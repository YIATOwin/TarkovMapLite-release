// ============================================================================
// MapCalibration.cs — 手动微调某张地图的标定（点位整体平移）
//
// 为什么需要：SVG 渲染版和瓦片版的标定不一致（储备站横跨差 9.5%），
// 而自动配准在这两种画风之间不可靠（归一化互相关只有 0.25，全是假匹配）。
// 与其在离线数据里试凑，不如让用户看着地图按方向键微调，对齐后自动记住。
//
// 用法：在画布上 Ctrl + 方向键 = 当前地图的点位/区域整体移动（每次 2 个
// viewBox 单位，约 2~8 图像像素）；Ctrl+0 清零。存在
//   %LOCALAPPDATA%\TarkovMapLite\calibration.json
// 形如 { "<地图 key>": [dx, dy] }，只影响投影，不动 maps.json。
// ============================================================================
using System.Text.Json.Nodes;

namespace TarkovMapLite;

internal static class MapCalibration
{
    private static readonly Dictionary<string, (double Dx, double Dy)> Offsets =
        new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

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
                // 建不出目录就只活在内存里
            }
            return Path.Combine(dir, "calibration.json");
        }
    }

    /// <summary>这张图当前的平移量（viewBox 单位）。</summary>
    public static (double Dx, double Dy) Of(string mapKey)
    {
        EnsureLoaded();
        return Offsets.TryGetValue(mapKey, out var value) ? value : (0, 0);
    }

    /// <summary>调整并保存；返回调整后的值。</summary>
    public static (double Dx, double Dy) Nudge(string mapKey, double dx, double dy)
    {
        EnsureLoaded();
        var (x, y) = Of(mapKey);
        x = Math.Round(x + dx, 1);
        y = Math.Round(y + dy, 1);
        if (Math.Abs(x) < 0.05)
        {
            x = 0;
        }
        if (Math.Abs(y) < 0.05)
        {
            y = 0;
        }
        Offsets[mapKey] = (x, y);
        Save();
        return (x, y);
    }

    public static void Clear(string mapKey)
    {
        EnsureLoaded();
        if (Offsets.Remove(mapKey))
        {
            Save();
        }
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
            if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject root)
            {
                foreach (var (key, node) in root)
                {
                    if (node is JsonArray pair && pair.Count >= 2)
                    {
                        Offsets[key] = (pair[0]?.GetValue<double>() ?? 0, pair[1]?.GetValue<double>() ?? 0);
                    }
                }
            }
        }
        catch
        {
            // 文件坏了就当没调过
        }
    }

    private static void Save()
    {
        try
        {
            var root = new JsonObject();
            foreach (var (key, value) in Offsets)
            {
                if (value.Dx == 0 && value.Dy == 0)
                {
                    continue;
                }
                root[key] = new JsonArray(value.Dx, value.Dy);
            }
            File.WriteAllText(FilePath, root.ToJsonString());
        }
        catch
        {
            // 写不进去就只活在内存里
        }
    }
}
