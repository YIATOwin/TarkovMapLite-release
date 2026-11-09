// ============================================================================
// TaskLog.cs — 读《逃离塔科夫》日志：任务状态 + "正在进哪张图"
//
// 日志目录由 ResolveLogsFolder() 猜（E:\Tarkov\Logs 之类，常见位置 + 注册表都试）。
// 启动时 ReplayAll() 整包回放一次（得到所有任务当前状态 + 最后一次进图），
// 之后 Poll() 只读每个文件**新追加的字节**（_offsets 记位置），不会重复触发切图。
//
// 事件：
//   Changed     任务状态变了 → 刷新任务列表、重画
//   MapLoading  进图了     → 自动切图、玩家定位重新计时
//
// 改动须知：
//  · 解析靠关键字/正则匹配游戏日志。游戏更新改了措辞就会静默失效，
//    表现是"进图不切图"或"任务状态不动"——先看 EventCount 有没有在涨。
//  · 回放期间不触发切图（_replaying 标志），否则会把几天前的老会话放一遍、地图乱跳。
//  · SceneToMap 由 TarkovData 从 /pve/maps 的 scenes 字段生成，不在这里写死。
// ============================================================================
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace TarkovMapLite;

public enum TaskState
{
    None = 0,
    Started,
    Failed,
    Finished,
}

/// <summary>
/// 从 EFT 日志里读出任务状态和"正在进哪张图"。
/// 启动时整包回放一次，之后只读每个文件新追加的字节，
/// 这样旧日志里的进图记录不会再触发自动切图。
/// </summary>
public sealed class TaskLog
{
    private readonly Dictionary<string, TaskState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _offsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>日志里最后一次出现的地图（回放也会记录，用于启动时已经在局内的情况）。</summary>
    public string LastSceneMapKey { get; private set; } = "";

    public event Action? Changed;
    public event Action<string>? MapLoading;

    public string LogsFolder { get; private set; } = "";
    public string Status { get; private set; } = "";
    public int EventCount { get; private set; }

    public TaskState StateOf(string taskId)
    {
        lock (_gate)
        {
            return _states.TryGetValue(taskId, out var state) ? state : TaskState.None;
        }
    }

    public int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                return _states.Values.Count(s => s == TaskState.Started);
            }
        }
    }

    public static string ResolveLogsFolder()
    {
        var custom = Environment.GetEnvironmentVariable("TARKOVMAPLITE_LOGS");
        if (!string.IsNullOrWhiteSpace(custom) && Directory.Exists(custom))
        {
            return custom;
        }

        try
        {
            var process = System.Diagnostics.Process.GetProcessesByName("EscapeFromTarkov").FirstOrDefault();
            if (process != null)
            {
                var exe = process.MainModule?.FileName;
                if (!string.IsNullOrEmpty(exe))
                {
                    var dir = Path.GetDirectoryName(exe);
                    while (dir != null)
                    {
                        var candidate = Path.Combine(dir, "Logs");
                        if (Directory.Exists(candidate))
                        {
                            return candidate;
                        }
                        dir = Path.GetDirectoryName(dir);
                    }
                }
            }
        }
        catch
        {
            // 进程访问被拒绝时继续往下找
        }

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\EscapeFromTarkov");
                var location = key?.GetValue("InstallLocation") as string;
                if (string.IsNullOrWhiteSpace(location))
                {
                    continue;
                }
                foreach (var candidate in new[]
                         {
                             Path.Combine(location, "Logs"),
                             Path.Combine(location, "build", "Logs"),
                         })
                {
                    if (Directory.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            catch
            {
                // 忽略无权限的注册表项
            }
        }

        foreach (var candidate in new[]
                 {
                     @"C:\Battlestate Games\EFT\Logs",
                     @"D:\Battlestate Games\EFT\Logs",
                     @"E:\Battlestate Games\EFT\Logs",
                 })
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }
        return "";
    }

    public void ReplayAll()
    {
        Status = "回放日志…";
        var folder = ResolveLogsFolder();
        if (string.IsNullOrEmpty(folder))
        {
            LogsFolder = "";
            Status = "找不到 EFT 日志目录";
            return;
        }
        LogsFolder = folder;
        lock (_gate)
        {
            _states.Clear();
            EventCount = 0;
        }
        _offsets.Clear();
        _pending.Clear();

        var sessions = Directory.GetDirectories(folder, "log_*")
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var session in sessions)
        {
            foreach (var file in SafeGetFiles(session))
            {
                // 回放阶段不发布"进图"事件
                ReadAndDispatch(file, publishMapLoading: false);
            }
        }
        Status = $"日志 {sessions.Count} 个会话 / {EventCount} 条事件";
        Changed?.Invoke();
    }

    public void Poll()
    {
        if (string.IsNullOrEmpty(LogsFolder))
        {
            var folder = ResolveLogsFolder();
            if (string.IsNullOrEmpty(folder))
            {
                return;
            }
            LogsFolder = folder;
            ReplayAll();
            return;
        }

        string newest;
        try
        {
            newest = Directory.GetDirectories(LogsFolder, "log_*")
                .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault() ?? "";
        }
        catch
        {
            return;
        }
        if (newest.Length == 0)
        {
            return;
        }

        var changed = false;
        foreach (var file in SafeGetFiles(newest))
        {
            if (ReadAndDispatch(file, publishMapLoading: true, countChange: true))
            {
                changed = true;
            }
        }
        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private static IEnumerable<string> SafeGetFiles(string folder)
    {
        try
        {
            return Directory.GetFiles(folder);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>读文件里新追加的部分并处理；返回是否有新内容。</summary>
    private bool ReadAndDispatch(string path, bool publishMapLoading, bool countChange = false)
    {
        var name = Path.GetFileName(path);
        var isNotification = name.Contains("push-notifications", StringComparison.OrdinalIgnoreCase);
        var isApplication = name.Contains("application", StringComparison.OrdinalIgnoreCase);
        if (!isNotification && !isApplication)
        {
            return false;
        }

        long offset;
        lock (_gate)
        {
            offset = _offsets.TryGetValue(path, out var known) ? known : 0;
        }

        long length;
        try
        {
            length = new FileInfo(path).Length;
        }
        catch
        {
            return false;
        }
        if (length <= offset)
        {
            return false;
        }

        string chunk;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[length - offset];
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                return false;
            }
            lock (_gate)
            {
                _offsets[path] = offset + read;
            }
            chunk = Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch
        {
            return false;
        }

        // 只处理完整的行，剩下的半个行留到下次
        lock (_gate)
        {
            if (_pending.TryGetValue(path, out var tail) && tail.Length > 0)
            {
                chunk = tail + chunk;
                _pending.Remove(path);
            }
            var lastNewline = chunk.LastIndexOf('\n');
            if (lastNewline < 0)
            {
                _pending[path] = chunk;
                return false;
            }
            if (lastNewline < chunk.Length - 1)
            {
                _pending[path] = chunk[(lastNewline + 1)..];
            }
            chunk = chunk[..(lastNewline + 1)];
        }

        if (isNotification)
        {
            return ApplyNotifications(chunk);
        }
        ApplyApplication(chunk, publishMapLoading);
        return countChange;
    }

    private bool ApplyNotifications(string text)
    {
        var lines = text.Split('\n');
        var applied = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("Got notification |", StringComparison.Ordinal))
            {
                continue;
            }
            var start = i + 1;
            while (start < lines.Length && lines[start].Trim().Length == 0)
            {
                start++;
            }
            if (start >= lines.Length || !lines[start].TrimStart().StartsWith('{'))
            {
                continue;
            }
            var depth = 0;
            var builder = new StringBuilder();
            var end = -1;
            for (var j = start; j < lines.Length; j++)
            {
                builder.AppendLine(lines[j]);
                foreach (var ch in lines[j])
                {
                    if (ch == '{')
                    {
                        depth++;
                    }
                    else if (ch == '}')
                    {
                        depth--;
                    }
                }
                if (depth <= 0)
                {
                    end = j;
                    break;
                }
            }
            if (end < 0)
            {
                // JSON 还没写完，交给下一次读取（保守起见直接放弃这段）
                break;
            }
            if (ApplyNotification(builder.ToString()))
            {
                applied = true;
            }
            i = end;
        }
        return applied;
    }

    private bool ApplyNotification(string json)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch
        {
            return false;
        }
        if (root?["message"] is not JsonObject message)
        {
            return false;
        }
        if (!int.TryParse(message["type"]?.ToString(), out var type))
        {
            return false;
        }
        var state = type switch
        {
            10 => TaskState.Started,
            11 => TaskState.Failed,
            12 => TaskState.Finished,
            _ => TaskState.None,
        };
        if (state == TaskState.None)
        {
            return false;
        }
        var templateId = message["templateId"]?.ToString() ?? "";
        var taskId = templateId.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return false;
        }
        lock (_gate)
        {
            _states[taskId] = state;
            EventCount++;
        }
        return true;
    }

    /// <summary>
    /// 场景名 → 地图。优先用 tarkov.dev 数据里抓下来的 scenePath 精确表
    /// （streets_preset → streets-of-tarkov），再退回按名字猜。
    /// 之前只按 key 前缀匹配，"streets" 匹配不到任何地图，于是一直停在上一张图上。
    /// </summary>
    private static MapProjection? ResolveScene(string scene)
    {
        if (TarkovData.SceneToMap.TryGetValue(scene, out var mapped))
        {
            var exact = MapPack.Find(mapped);
            if (exact != null)
            {
                return exact;
            }
        }
        var key = scene.Replace("_preset", "").Replace("-preset", "");
        var direct = MapPack.Find(key);
        if (direct != null)
        {
            return direct;
        }
        // 去掉分隔符再比，覆盖 streets / streets_of_tarkov / StreetsOfTarkov 这类写法
        static string Normalize(string value) =>
            new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var wanted = Normalize(key);
        return MapPack.Maps.FirstOrDefault(m =>
        {
            var candidate = Normalize(m.Key);
            var normalizedName = Normalize(m.NormalizedName);
            return candidate == wanted
                   || normalizedName == wanted
                   || candidate.StartsWith(wanted, StringComparison.Ordinal)
                   || wanted.StartsWith(candidate, StringComparison.Ordinal);
        });
    }

    private void ApplyApplication(string text, bool publish)
    {
        foreach (var line in text.Split('\n'))
        {
            const string marker = "scene preset path:maps/";
            var at = line.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }
            var rest = line[(at + marker.Length)..];
            var dot = rest.IndexOf(".bundle", StringComparison.Ordinal);
            if (dot <= 0)
            {
                continue;
            }
            var scene = rest[..dot];                       // customs_preset / streets_preset
            var map = ResolveScene(scene);
            if (map != null)
            {
                LastSceneMapKey = map.Key;
                if (publish)
                {
                    MapLoading?.Invoke(map.Key);
                }
            }
        }
    }
}
