// ============================================================================
// MapLabels.cs — 地名（Big Red / Dorms / Sawmill / 医疗楼 …）
//
// 数据不在接口里：来源是 tarkov-dev 站点的 maps[].labels，
// 由 tools/build-labels.ps1 导出成 Assets/labels.json（约 12 KB、303 个）打进 exe。
//
// 存世界坐标，按 map.Key 缓存投影结果——和任务区域是同一个坑：
// 缓存 key 必须是 map.Key（每张图投影不同），取数据才用 map.Data。
// 用错的话，瓦片版海关会拿到 SVG 版算出来的像素，地名整体飘走。
//
// 想更新地名：重跑 tools/build-labels.ps1 → 重新打包。这个文件不用改。
// ============================================================================
using System.Drawing;
using System.Text.Json.Nodes;

namespace TarkovMapLite;

/// <summary>地图上的地名（Big Red / Dorms / Sawmill …）。</summary>
internal sealed class MapLabel
{
    public required string Text { get; init; }
    /// <summary>世界坐标 (x, z)。</summary>
    public required PointF World { get; init; }
    /// <summary>站点上的字号（40~100），只用来决定叠放优先级。</summary>
    public int Size { get; init; } = 70;
}

/// <summary>
/// 地名来自 tarkov-dev 的 maps.json（接口里没有），由 tools/build-labels.ps1 导出成
/// Assets/labels.json 打进 exe。坐标是世界坐标，画之前按当前地图投影——
/// 这样 SVG 版和瓦片版共用同一份地名。
/// </summary>
internal static class MapLabels
{
    private static readonly Dictionary<string, List<MapLabel>> World = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<(PointF Point, MapLabel Label)>> Image = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    /// <summary>
    /// 当前地图的地名，已投影成图片像素坐标。缓存 key 用 <c>map.Key</c>：
    /// 同一份地名要按每张图各自的投影算一遍，不能用 Data 当 key。
    /// </summary>
    public static List<(PointF Point, MapLabel Label)> For(MapProjection? map)
    {
        if (map == null)
        {
            return new List<(PointF, MapLabel)>();
        }
        if (Image.TryGetValue(map.Key, out var cached))
        {
            return cached;
        }
        EnsureLoaded();
        var projected = new List<(PointF, MapLabel)>();
        if (World.TryGetValue(map.Data, out var source))
        {
            foreach (var label in source)
            {
                var (u, v) = MapPack.ToImage(map, label.World.X, label.World.Y);
                projected.Add((new PointF((float)u, (float)v), label));
            }
        }
        Image[map.Key] = projected;
        return projected;
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
            var text = Assets.ReadText("labels.json");
            if (string.IsNullOrWhiteSpace(text) || JsonNode.Parse(text)?["maps"] is not JsonObject maps)
            {
                return;
            }
            foreach (var (mapKey, node) in maps)
            {
                if (node is not JsonArray array)
                {
                    continue;
                }
                var list = new List<MapLabel>(array.Count);
                foreach (var item in array)
                {
                    if (item is not JsonObject entry)
                    {
                        continue;
                    }
                    var name = entry["t"]?.GetValue<string>() ?? "";
                    if (name.Length == 0)
                    {
                        continue;
                    }
                    list.Add(new MapLabel
                    {
                        Text = name,
                        World = new PointF(
                            (float)(entry["x"]?.GetValue<double>() ?? 0),
                            (float)(entry["z"]?.GetValue<double>() ?? 0)),
                        Size = entry["s"]?.GetValue<int>() ?? 70,
                    });
                }
                if (list.Count > 0)
                {
                    World[mapKey] = list;
                }
            }
        }
        catch
        {
            // 地名文件坏了不影响地图本身
        }
    }
}
