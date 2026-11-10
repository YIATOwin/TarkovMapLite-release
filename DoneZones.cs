// ============================================================================
// DoneZones.cs — 手动标记"这个任务**在这张地图上**已经做完了"
//
// 为什么是手动的：游戏日志只给任务级状态（开始/失败/完成），子目标级进度在
// 日志和公开接口里都不存在。而一个任务经常横跨好几张图，玩家在森林做完了，
// 海关那张图上的区域还亮着——只能自己标一下。
//
// 语义（用户定的）：在任务列表里右键某个任务 = "本图这部分我做完了"：
//   · 这张图上该任务的所有区域和点位**不再显示**
//   · 列表里该任务的副标题写「本图已完成」，右键再点一次取消
//
// 存储：%LOCALAPPDATA%\TarkovMapLite\done.json
//   { "marks": ["<taskId>|<地图 data key>", ...] }
// 用地图的 **Data key**（不是 Key）当维度：海关的 SVG 版和瓦片版共用同一份
// 任务数据，标了一次两边都算做完。
//
// 文件坏了/写不进去都当空处理：这个功能不能影响地图本身。
// ============================================================================
using System.Text.Json.Nodes;

namespace TarkovMapLite;

internal static class DoneZones
{
    private static readonly HashSet<string> Marks = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    /// <summary>标记变化时触发（窗口据此刷新列表和画布）。</summary>
    public static event Action? Changed;

    private static string FilePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TarkovMapLite");
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch
            {
                // 建不出目录就只在内存里记
            }
            return Path.Combine(dir, "done.json");
        }
    }

    private static string Key(string taskId, string mapData) => $"{taskId}|{mapData}";

    /// <summary>这个任务在这张图上标过"已完成"了吗。</summary>
    public static bool IsDone(string taskId, string mapData)
    {
        if (string.IsNullOrEmpty(mapData))
        {
            return false;
        }
        EnsureLoaded();
        return Marks.Contains(Key(taskId, mapData));
    }

    /// <summary>切换标记；返回切换后的状态（true = 本图已完成）。</summary>
    public static bool Toggle(string taskId, string mapData)
    {
        if (string.IsNullOrEmpty(mapData))
        {
            return false;
        }
        EnsureLoaded();
        var key = Key(taskId, mapData);
        var now = !Marks.Contains(key);
        if (now)
        {
            Marks.Add(key);
        }
        else
        {
            Marks.Remove(key);
        }
        Save();
        Changed?.Invoke();
        return now;
    }

    /// <summary>这张图上所有"已完成"的任务 id（画布据此隐藏它们的区域）。</summary>
    public static HashSet<string> TasksForMap(string mapData)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(mapData))
        {
            return result;
        }
        EnsureLoaded();
        var suffix = "|" + mapData;
        foreach (var key in Marks)
        {
            if (key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(key[..^suffix.Length]);
            }
        }
        return result;
    }

    public static int Count
    {
        get
        {
            EnsureLoaded();
            return Marks.Count;
        }
    }

    public static void ClearAll()
    {
        EnsureLoaded();
        if (Marks.Count == 0)
        {
            return;
        }
        Marks.Clear();
        Save();
        Changed?.Invoke();
    }

    private static void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;
        try
        {
            var path = FilePath;
            if (!File.Exists(path))
            {
                return;
            }
            if (JsonNode.Parse(File.ReadAllText(path))?["marks"] is JsonArray marks)
            {
                foreach (var item in marks)
                {
                    var text = item?.GetValue<string>();
                    // 旧格式是 "taskId|objectiveId"，没有地图维度，读进来也没用，
                    // 直接跳过（它会自然过期，不影响新标记）
                    if (!string.IsNullOrWhiteSpace(text) && text.Split('|').Length == 2)
                    {
                        Marks.Add(text);
                    }
                }
            }
        }
        catch
        {
            // 文件坏了就当没有标记
        }
    }

    private static void Save()
    {
        try
        {
            var array = new JsonArray();
            foreach (var key in Marks)
            {
                array.Add(key);
            }
            var root = new JsonObject { ["marks"] = array };
            File.WriteAllText(FilePath, root.ToJsonString());
        }
        catch
        {
            // 写不进去就只活在内存里
        }
    }
}
