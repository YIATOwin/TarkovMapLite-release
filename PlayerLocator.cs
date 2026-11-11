// ============================================================================
// PlayerLocator.cs — 从游戏截图文件名里读出玩家坐标 / 朝向
//
// 游戏内截图的名字形如：
//   2026-09-04[18-33]_7.86, 38.06, -27.57_-0.03307, -0.13322, 0.00384, -0.99053_21.87 (0).png
// 前三个数是世界坐标 (x,y,z)，后四个是朝向四元数（Unity 约定）。
// 截图里**没有地图名**，所以"这是哪张图"由 TaskLog 的最后一次进图决定
// （MapForm._raidMapKey）；两者对不上时画布会主动不画玩家。
//
// 改动须知：
//  · 只认应用启动之后新建的截图（NotBefore），否则会把几天前的老图当当前位置。
//  · 环境变量 TARKOVMAPLITE_SHOTS 可以指定别的截图目录（测试用）。
//  · 文件名格式是游戏定的，它一变这里就哑了；表现是底栏一直"等待游戏内截图…"。
//  · 想要更实时，可以在这里加"监听文件系统变化"，现在是 3 秒轮询。
// ============================================================================
using System.Globalization;
using System.Text.RegularExpressions;

namespace TarkovMapLite;

/// <summary>
/// 从塔科夫截图的文件名里读出玩家位置和朝向。
/// 文件名形如：
/// <c>2026-09-04[18-33]_7.86, 38.06, -27.57_-0.03307, -0.13322, 0.00384, -0.99053_21.87 (0).png</c>
/// 前面三个数是世界坐标 (x, y, z)，后面四个是朝向四元数。截图里不含地图名，
/// 所以地图由"当时那次进图"决定（由 TaskLog 提供）。
/// </summary>
public sealed class PlayerLocator
{
    private static readonly Regex NamePattern = new(
        @"^\d{4}-\d{2}-\d{2}\[\d{2}-\d{2}\]_(-?\d+(?:\.\d+)?),\s*(-?\d+(?:\.\d+)?),\s*(-?\d+(?:\.\d+)?)_" +
        @"(-?\d+(?:\.\d+)?),\s*(-?\d+(?:\.\d+)?),\s*(-?\d+(?:\.\d+)?),\s*(-?\d+(?:\.\d+)?)_",
        RegexOptions.Compiled);

    private string _lastFile = "";

    public sealed record Fix(double X, double Y, double Z, double ForwardX, double ForwardZ, DateTime CapturedAt, string File);

    /// <summary>最新一次定位；没有就是 null。</summary>
    public Fix? Current { get; private set; }

    public string Folder { get; private set; } = "";
    public string Status { get; private set; } = "";

    public event Action? Changed;

    private static string SettingsFile
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TarkovMapLite");
            try { Directory.CreateDirectory(dir); } catch { }
            return Path.Combine(dir, "settings.json");
        }
    }

    /// <summary>用户在界面里手动选的截图目录；没选过返回空串。</summary>
    public static string SavedFolder
    {
        get
        {
            try
            {
                var path = SettingsFile;
                if (!File.Exists(path))
                {
                    return "";
                }
                if (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))
                    is System.Text.Json.Nodes.JsonObject root
                    && root["screenshots"]?.GetValue<string>() is { Length: > 0 } value
                    && Directory.Exists(value))
                {
                    return value;
                }
            }
            catch
            {
                // 设置文件坏了就当没选过
            }
            return "";
        }
    }

    /// <summary>记住用户选的截图目录（写 settings.json，重启后仍生效）。</summary>
    public static void SaveFolder(string folder)
    {
        try
        {
            var root = new System.Text.Json.Nodes.JsonObject { ["screenshots"] = folder };
            File.WriteAllText(SettingsFile, root.ToJsonString());
        }
        catch
        {
            // 写不进去就只对本次运行生效
        }
    }

    /// <summary>立刻切到某个目录（界面里选完文件夹后调用）。</summary>
    public void UseFolder(string folder)
    {
        Folder = folder;
        _lastFile = "";
        Status = "已切换截图目录，进游戏按截图键即可定位";
        Changed?.Invoke();
    }

    /// <summary>环境变量优先（测试用），其次用户手动选的，最后是文档目录下的默认位置。</summary>
    public static string ResolveFolder()
    {
        var custom = Environment.GetEnvironmentVariable("TARKOVMAPLITE_SHOTS");
        if (!string.IsNullOrWhiteSpace(custom) && Directory.Exists(custom))
        {
            return custom;
        }
        var saved = SavedFolder;
        if (saved.Length > 0)
        {
            return saved;
        }
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(documents, "Escape From Tarkov", "Screenshots");
    }

    /// <summary>只认应用启动之后新建的截图，避免把几天前的老图当成当前位置。</summary>
    public DateTime NotBefore { get; set; } = DateTime.Now;

    public void Poll()
    {
        if (string.IsNullOrEmpty(Folder))
        {
            Folder = ResolveFolder();
        }
        if (!Directory.Exists(Folder))
        {
            Status = "截图目录不存在（游戏内按截图键后自动出现）";
            return;
        }

        FileInfo? newest = null;
        try
        {
            foreach (var path in Directory.EnumerateFiles(Folder, "*.png"))
            {
                var info = new FileInfo(path);
                if (info.LastWriteTime < NotBefore)
                {
                    continue;
                }
                if (newest == null || info.LastWriteTime > newest.LastWriteTime)
                {
                    newest = info;
                }
            }
        }
        catch
        {
            Status = "读取截图目录失败";
            return;
        }

        if (newest == null || newest.FullName == _lastFile)
        {
            Status = Current == null ? "等待游戏内截图…" : $"已定位：{Current.File}";
            return;
        }

        _lastFile = newest.FullName;
        var match = NamePattern.Match(Path.GetFileNameWithoutExtension(newest.Name));
        if (!match.Success)
        {
            Status = "最新截图文件名里没有坐标";
            return;
        }

        var x = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        // 第二个数是高度（世界 y）：判断玩家在哪一层要用它
        var y = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var z = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        var (forwardX, forwardZ) = Forward(
            double.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture),
            double.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture),
            double.Parse(match.Groups[6].Value, CultureInfo.InvariantCulture),
            double.Parse(match.Groups[7].Value, CultureInfo.InvariantCulture));

        Current = new Fix(x, y, z, forwardX, forwardZ, newest.LastWriteTime, newest.Name);
        Status = $"已定位：{newest.Name}";
        Changed?.Invoke();
    }

    /// <summary>把朝向四元数（Unity 的 qx,qy,qz,qw）换成世界坐标里的前方向量（只看水平面）。</summary>
    private static (double X, double Z) Forward(double qx, double qy, double qz, double qw)
    {
        var length = Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
        if (length < 1e-9)
        {
            return (0, 1);
        }
        qx /= length;
        qy /= length;
        qz /= length;
        qw /= length;
        var fx = 2 * (qx * qz + qw * qy);
        var fz = 1 - 2 * (qx * qx + qy * qy);
        var flat = Math.Sqrt(fx * fx + fz * fz);
        return flat < 1e-6 ? (0, 1) : (fx / flat, fz / flat);
    }
}
