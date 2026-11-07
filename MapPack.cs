using System.Text.Json;

namespace TarkovMapLite;

/// <summary>一张地图的投影参数：世界 (x, z) 怎么落到地图图片的像素上。</summary>
public sealed class MapProjection
{
    public string Key { get; init; } = "";
    public string NormalizedName { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Svg { get; init; } = "";
    /// <summary>[scaleX, marginX, scaleZ, marginZ]</summary>
    public double[] Transform { get; init; } = new double[4];
    public double Rotation { get; init; }
    public double[][] Bounds { get; init; } = Array.Empty<double[]>();
    public double[] ViewBox { get; init; } = new double[4];
    public IReadOnlyList<MapLayer> Layers { get; init; } = Array.Empty<MapLayer>();

    public double ViewBoxWidth => ViewBox.Length >= 4 ? ViewBox[2] : 0;
    public double ViewBoxHeight => ViewBox.Length >= 4 ? ViewBox[3] : 0;

    /// <summary>地图图片在 Assets/maps 下的相对路径（按楼层）。</summary>
    public string ImageFor(string? svgLayer) =>
        string.IsNullOrEmpty(svgLayer) ? $"{Key}/base.png" : $"{Key}/{svgLayer}.png";
}

public sealed class MapLayer
{
    public string Name { get; init; } = "";
    public string SvgLayer { get; init; } = "";
    public bool Show { get; init; }
}

internal sealed class MapPackDocument
{
    public int Version { get; init; }
    public MapProjection[] Maps { get; init; } = Array.Empty<MapProjection>();
}

/// <summary>离线地图包：读 Assets/maps/maps.json，做世界坐标 ↔ 图片坐标的换算。</summary>
public static class MapPack
{
    private static MapPackDocument? _document;

    public static IReadOnlyList<MapProjection> Maps => Document.Maps;

    private static MapPackDocument Document
    {
        get
        {
            if (_document != null)
            {
                return _document;
            }
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var json = Assets.ReadText("maps/maps.json");
            _document = json == null
                ? new MapPackDocument()
                : JsonSerializer.Deserialize<MapPackDocument>(json, options) ?? new MapPackDocument();
            return _document;
        }
    }

    public static MapProjection? Find(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }
        return Maps.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase))
            ?? Maps.FirstOrDefault(m => string.Equals(m.NormalizedName, key, StringComparison.OrdinalIgnoreCase));
    }

    private static (double Px, double Py) ToPixel(MapProjection map, double x, double z)
    {
        var r = map.Rotation * Math.PI / 180.0;
        var cos = Math.Cos(r);
        var sin = Math.Sin(r);
        var lng = x * cos - z * sin;
        var lat = x * sin + z * cos;
        var a = map.Transform[0];
        var b = map.Transform[1];
        var c = -map.Transform[2];
        var d = map.Transform[3];
        return (a * lng + b, c * lat + d);
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) PixelRect(MapProjection map)
    {
        var corners = map.Bounds
            .Where(pair => pair.Length >= 2)
            .Select(pair => ToPixel(map, pair[0], pair[1]))
            .ToArray();
        if (corners.Length == 0)
        {
            return (0, 0, 0, 0);
        }
        return (corners.Min(p => p.Px), corners.Min(p => p.Py), corners.Max(p => p.Px), corners.Max(p => p.Py));
    }

    /// <summary>世界 (x, z) → 图片像素坐标（与 viewBox 同尺度）。</summary>
    public static (double X, double Y) ToImage(MapProjection map, double x, double z)
    {
        var (px, py) = ToPixel(map, x, z);
        var (minX, minY, maxX, maxY) = PixelRect(map);
        var width = maxX - minX;
        var height = maxY - minY;
        var u = width == 0 ? 0 : (px - minX) / width * map.ViewBoxWidth;
        var v = height == 0 ? 0 : (py - minY) / height * map.ViewBoxHeight;
        return (u, v);
    }
}
