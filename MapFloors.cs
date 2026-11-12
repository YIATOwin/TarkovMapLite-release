using System.Text.Json;

namespace TarkovMapLite;

/// <summary>楼层语义与按建筑范围划分的高度规则；SVG 和瓦片共用世界坐标规则。</summary>
internal static class MapFloors
{
    private sealed class FloorMap
    {
        public FloorMap() { }
        public FloorLayer[] Layers { get; init; } = Array.Empty<FloorLayer>();
    }

    private sealed class FloorLayer
    {
        public FloorLayer() { }
        public string Layer { get; init; } = "";
        public int Level { get; init; }
        public FloorExtent[] Extents { get; init; } = Array.Empty<FloorExtent>();
    }

    private sealed class FloorExtent
    {
        public FloorExtent() { }
        public double[] Height { get; init; } = Array.Empty<double>();
        // 上游每个矩形的第三项是建筑名称，保留以便核对来源。
        public JsonElement[] Bounds { get; init; } = Array.Empty<JsonElement>();
    }

    private static readonly Lazy<Dictionary<string, FloorMap>> Rules = new(() =>
    {
        var json = Assets.ReadText("floor-extents.json");
        return json == null ? new() :
            JsonSerializer.Deserialize<Dictionary<string, FloorMap>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
    });

    /// <summary>有上游规则时返回 true；没有匹配附加层的点属于地面（空串）。</summary>
    public static bool TryLayer(string mapKey, double x, double z, double y, out string layer)
    {
        layer = "";
        var key = MapPack.Find(mapKey)?.Data ?? mapKey;
        if (!Rules.Value.TryGetValue(key, out var map))
        {
            return false;
        }
        foreach (var floor in map.Layers)
        {
            foreach (var extent in floor.Extents)
            {
                if (extent.Height.Length < 2 || y < extent.Height[0] || y >= extent.Height[1])
                {
                    continue;
                }
                if (extent.Bounds.Length == 0 || extent.Bounds.Any(rect => Contains(rect, x, z)))
                {
                    layer = floor.Layer;
                    return true;
                }
            }
        }
        return true;
    }

    private static bool Contains(JsonElement rect, double x, double z)
    {
        if (rect.ValueKind != JsonValueKind.Array || rect.GetArrayLength() < 2)
        {
            return false;
        }
        var a = rect[0];
        var b = rect[1];
        return x >= Math.Min(a[0].GetDouble(), b[0].GetDouble())
            && x <= Math.Max(a[0].GetDouble(), b[0].GetDouble())
            && z >= Math.Min(a[1].GetDouble(), b[1].GetDouble())
            && z <= Math.Max(a[1].GetDouble(), b[1].GetDouble());
    }

    public static int FloorOf(string mapKey, string layer)
    {
        var key = MapPack.Find(mapKey)?.Data ?? mapKey;
        return Rules.Value.TryGetValue(key, out var map)
            ? map.Layers.FirstOrDefault(l => l.Layer == layer)?.Level ?? 0
            : NumberOfLayer(layer);
    }

    public static int NumberOfLayer(string key, string name = "")
    {
        // 稳定的素材 ID 优先于显示名称，中文翻译不再改变楼层语义。
        var text = string.IsNullOrEmpty(key) ? name : key;
        if (new[] { "Under", "Bunker", "Basement", "Tunnel", "Garage", "Technical", "地下", "地堡", "隧道", "车库", "技术" }
            .Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase)))
        {
            return -1;
        }
        var match = System.Text.RegularExpressions.Regex.Match(text, @"^(\d+)");
        if (match.Success && int.TryParse(match.Value, out var number))
        {
            return Math.Max(0, number - 1);
        }
        if (text.StartsWith("Second", StringComparison.OrdinalIgnoreCase)) return 1;
        if (text.StartsWith("Third", StringComparison.OrdinalIgnoreCase)) return 2;
        if (text.StartsWith("Fourth", StringComparison.OrdinalIgnoreCase)) return 3;
        if (text.StartsWith("Fifth", StringComparison.OrdinalIgnoreCase)) return 4;
        return 0;
    }
}
