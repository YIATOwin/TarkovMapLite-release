// ============================================================================
// MapMarkers.cs — 地图静态标记：容器 / 散落物资 / 固定武器 / 出生点 / 危险区
//
// 数据不是实时接口，是 tools/build-markers.ps1 从 /pve/maps 导出的一次性快照
// （Assets/markers.json）——玩家不用联网，也不怕接口改版。
// 数据过时了：重跑脚本 → 重新打包。这个文件不用改。
//
// 和任务区域一样：存世界坐标，按当前地图投影；缓存 key 用 map.Key、取数据用 map.Data。
// 默认全部关闭（用户要求开图只看到区域和撤离点），在「图层」菜单里打开。
// ============================================================================
using System.Drawing;
using System.Text.Json.Nodes;

namespace TarkovMapLite;

/// <summary>一张地图上的静态标记（容器、散落物资、出生点、危险区）。</summary>
internal sealed class MarkerLayer
{
    public List<LootPoint> Loose { get; } = new();
    /// <summary>容器点：位置 + 类型（drawer / jacket / medbag ...）。</summary>
    public List<(PointF Point, string Kind)> Containers { get; } = new();
    public List<PointF> Statics { get; } = new();
    /// <summary>点状危险区（地雷等）：位置 + 类型。</summary>
    public List<(PointF Point, string Type)> Hazards { get; } = new();
    /// <summary>面状危险区（雷区轮廓）。</summary>
    public List<(PointF[] Ring, string Type)> HazardAreas { get; } = new();
    /// <summary>出生点：位置 + 类别串（p=玩家 b=AI o=Boss s=狙击手）。</summary>
    public List<(PointF Point, string Cats)> Spawns { get; } = new();

    public bool IsEmpty =>
        Loose.Count == 0 && Containers.Count == 0 && Statics.Count == 0
        && Hazards.Count == 0 && HazardAreas.Count == 0 && Spawns.Count == 0;
}

/// <summary>
/// 地图静态标记。数据来自 json.tarkov.dev 的 /pve/maps，用 tools/build-markers.ps1
/// 提前导出成 Assets/markers.json 打进 exe——玩家电脑上不需要联网，也不怕接口改版。
/// 数据过时了重新跑一次脚本、重新打包即可。
/// </summary>
internal static class MapMarkers
{
    private static readonly Dictionary<string, MarkerLayer> World = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, MarkerLayer> Image = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    public static bool Loaded => _loaded;

    /// <summary>标定改了之后要把投影缓存清掉。</summary>
    public static void ClearCache() => Image.Clear();

    /// <summary>所有出现过的容器类型（按点位数量从多到少），给「图层」菜单当图例。</summary>
    public static List<(string Kind, int Count)> KindsByCount()
    {
        EnsureLoaded();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in World.Values)
        {
            foreach (var (_, kind) in layer.Containers)
            {
                counts[kind] = counts.TryGetValue(kind, out var n) ? n + 1 : 1;
            }
        }
        return counts
            .OrderByDescending(pair => pair.Value)
            .Select(pair => (pair.Key, pair.Value))
            .ToList();
    }

    /// <summary>
    /// 地图 → 图片像素坐标的标记层，按地图缓存。
    /// 缓存 key 必须是 <c>map.Key</c> 而不是 <c>map.Data</c>：海关和海关（瓦片）共用数据，
    /// 但投影完全不同，用 Data 当 key 会让第二张图拿到第一张图的像素坐标。
    /// </summary>
    public static MarkerLayer For(MapProjection? map)
    {
        if (map == null)
        {
            return new MarkerLayer();
        }
        if (Image.TryGetValue(map.Key, out var cached))
        {
            return cached;
        }
        EnsureLoaded();
        var layer = new MarkerLayer();
        if (World.TryGetValue(map.Data, out var source))
        {
            void Project(List<PointF> from, List<PointF> to)
            {
                foreach (var point in from)
                {
                    var (u, v) = MapPack.ToImage(map, point.X, point.Y);
                    to.Add(new PointF((float)u, (float)v));
                }
            }
            foreach (var loot in source.Loose)
            {
                var (u, v) = MapPack.ToImage(map, loot.Point.X, loot.Point.Y);
                layer.Loose.Add(new LootPoint(new PointF((float)u, (float)v), loot.Mask));
            }
            foreach (var (point, kind) in source.Containers)
            {
                var (u, v) = MapPack.ToImage(map, point.X, point.Y);
                layer.Containers.Add((new PointF((float)u, (float)v), kind));
            }
            Project(source.Statics, layer.Statics);
            foreach (var (point, type) in source.Hazards)
            {
                var (u, v) = MapPack.ToImage(map, point.X, point.Y);
                layer.Hazards.Add((new PointF((float)u, (float)v), type));
            }
            foreach (var (ring, type) in source.HazardAreas)
            {
                var projected = new PointF[ring.Length];
                for (var i = 0; i < ring.Length; i++)
                {
                    var (u, v) = MapPack.ToImage(map, ring[i].X, ring[i].Y);
                    projected[i] = new PointF((float)u, (float)v);
                }
                layer.HazardAreas.Add((projected, type));
            }
            foreach (var (point, cats) in source.Spawns)
            {
                var (u, v) = MapPack.ToImage(map, point.X, point.Y);
                layer.Spawns.Add((new PointF((float)u, (float)v), cats));
            }
        }
        Image[map.Key] = layer;
        return layer;
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
            var text = Assets.ReadText("markers.json");
            if (string.IsNullOrWhiteSpace(text) || JsonNode.Parse(text)?["maps"] is not JsonObject maps)
            {
                return;
            }
            foreach (var (mapKey, node) in maps)
            {
                if (node is not JsonObject entry)
                {
                    continue;
                }
                var layer = new MarkerLayer();
                ReadLootPoints(entry["loose"], layer.Loose);
                ReadContainerPoints(entry["containers"], layer.Containers);
                ReadPoints(entry["statics"], layer.Statics);
                if (entry["hazards"] is JsonArray hazards)
                {
                    foreach (var item in hazards)
                    {
                        if (item is JsonObject hazard
                            && hazard["x"]?.GetValue<double>() is { } x
                            && hazard["z"]?.GetValue<double>() is { } z)
                        {
                            layer.Hazards.Add((new PointF((float)x, (float)z),
                                hazard["t"]?.GetValue<string>() ?? "hazard"));
                        }
                    }
                }
                if (entry["hazardAreas"] is JsonArray areas)
                {
                    foreach (var item in areas)
                    {
                        if (item is not JsonObject area || area["p"] is not JsonArray ring)
                        {
                            continue;
                        }
                        var points = new List<PointF>();
                        foreach (var pair in ring)
                        {
                            if (pair is JsonArray coords && coords.Count >= 2)
                            {
                                points.Add(new PointF(
                                    (float)(coords[0]?.GetValue<double>() ?? 0),
                                    (float)(coords[1]?.GetValue<double>() ?? 0)));
                            }
                        }
                        if (points.Count >= 3)
                        {
                            layer.HazardAreas.Add((points.ToArray(), area["t"]?.GetValue<string>() ?? "hazard"));
                        }
                    }
                }
                if (entry["spawns"] is JsonArray spawns)
                {
                    foreach (var item in spawns)
                    {
                        if (item is JsonArray row && row.Count >= 2)
                        {
                            layer.Spawns.Add((
                                new PointF(
                                    (float)(row[0]?.GetValue<double>() ?? 0),
                                    (float)(row[1]?.GetValue<double>() ?? 0)),
                                row.Count >= 3 ? row[2]?.GetValue<string>() ?? "" : ""));
                        }
                    }
                }
                if (!layer.IsEmpty)
                {
                    World[mapKey] = layer;
                }
            }
        }
        catch
        {
            // 标记文件坏了不影响地图本身
        }
    }

    private static void ReadContainerPoints(JsonNode? node, List<(PointF Point, string Kind)> target)
    {
        if (node is not JsonArray array)
        {
            return;
        }
        foreach (var item in array)
        {
            if (item is not JsonArray row || row.Count < 2)
            {
                continue;
            }
            var kind = row.Count >= 3 ? row[2]?.GetValue<string>() ?? "container" : "container";
            target.Add((
                new PointF(
                    (float)(row[0]?.GetValue<double>() ?? 0),
                    (float)(row[1]?.GetValue<double>() ?? 0)),
                kind));
        }
    }

    /// <summary>
    /// 散落物资：[x, z, 分类掩码]。第三列是 tools/build-markers.ps1 算好的
    /// （老文件没有第三列时按「文件/其他」处理，不会崩）。
    /// </summary>
    private static void ReadLootPoints(JsonNode? node, List<LootPoint> target)
    {
        if (node is not JsonArray array)
        {
            return;
        }
        foreach (var item in array)
        {
            if (item is JsonArray row && row.Count >= 2)
            {
                var mask = row.Count > 2 ? row[2]?.GetValue<int>() ?? LootKinds.Docs : LootKinds.Docs;
                target.Add(new LootPoint(
                    new PointF(
                        (float)(row[0]?.GetValue<double>() ?? 0),
                        (float)(row[1]?.GetValue<double>() ?? 0)),
                    mask));
            }
        }
    }

    private static void ReadPoints(JsonNode? node, List<PointF> target)
    {
        if (node is not JsonArray array)
        {
            return;
        }
        foreach (var item in array)
        {
            if (item is JsonArray row && row.Count >= 2)
            {
                target.Add(new PointF(
                    (float)(row[0]?.GetValue<double>() ?? 0),
                    (float)(row[1]?.GetValue<double>() ?? 0)));
            }
        }
    }
}
