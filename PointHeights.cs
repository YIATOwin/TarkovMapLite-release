using System.Text.Json;

namespace TarkovMapLite;

/// <summary>离线高度补充表，为旧快照/缓存中丢失高度的点位补齐真实 y；不猜测未知点。</summary>
internal static class PointHeights
{
    private sealed record FloorOverride(string Map, string Kind, string Name, string Layer);
    private static readonly Lazy<FloorOverride[]> Overrides = new(() =>
        JsonSerializer.Deserialize<FloorOverride[]>(Assets.ReadText("point-floor-overrides.json") ?? "[]",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? Array.Empty<FloorOverride>());
    private static readonly Lazy<Dictionary<string, Dictionary<string, Dictionary<(int, int), double?>>>> Heights = new(() =>
    {
        var result = new Dictionary<string, Dictionary<string, Dictionary<(int, int), double?>>>();
        var json = Assets.ReadText("point-heights.json");
        if (json == null) return result;
        using var doc = JsonDocument.Parse(json);
        foreach (var map in doc.RootElement.GetProperty("maps").EnumerateObject())
        {
            var kinds = new Dictionary<string, Dictionary<(int, int), double?>>();
            foreach (var kind in map.Value.EnumerateObject())
            {
                var points = new Dictionary<(int, int), double?>();
                foreach (var row in kind.Value.EnumerateArray())
                {
                    var key = Key(row[0].GetDouble(), row[1].GetDouble());
                    var y = row[2].GetDouble();
                    // 同一平面坐标存在多个高度时，缺少 ID 的旧点不能安全消歧。
                    if (points.TryGetValue(key, out var previous) && previous != y) points[key] = null;
                    else points[key] = y;
                }
                kinds[kind.Name] = points;
            }
            result[map.Name] = kinds;
        }
        return result;
    });

    private static (int, int) Key(double x, double z) =>
        ((int)Math.Round(x * 10, MidpointRounding.AwayFromZero), (int)Math.Round(z * 10, MidpointRounding.AwayFromZero));

    public static double? Find(MapProjection map, string kind, double x, double z)
    {
        var key = Heights.Value.ContainsKey(map.Data) ? map.Data : map.ImageKey;
        return Heights.Value.TryGetValue(key, out var kinds) && kinds.TryGetValue(kind, out var points)
            && points.TryGetValue(Key(x, z), out var y) ? y : null;
    }

    public static string? LayerAt(MapProjection map, string kind, double x, double z, double? y = null, string? name = null)
    {
        var annotation = Overrides.Value.FirstOrDefault(p => p.Map == map.Data && p.Kind == kind
            && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (annotation != null) return annotation.Layer;
        y ??= Find(map, kind, x, z);
        if (!y.HasValue || !double.IsFinite(y.Value))
            return MapFloors.IsGroundOutsideLayerBounds(map.Data, x, z) ? "" : null;
        return MapFloors.TryLayer(map.Data, x, z, y.Value, out var layer)
            ? layer : MarkerFloors.LayerOfHeight(map.Data, (float)y.Value);
    }
}
