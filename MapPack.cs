// ============================================================================
// MapPack.cs — 地图包与投影：世界坐标 (x,z) ↔ 图片像素
//
// 整个软件只有这一处知道"塔科夫的世界坐标怎么落到地图图片上"，核心是 ToImage()：
//   世界 (x,z) → 按地图的 rotation 旋转 → transform 的 [scale, margin] 缩放平移
//   → 用 bounds 反解出像素归一化比例 → 乘 viewBox 得到图片像素。
// 任务区域、撤离点、地名、容器、玩家位置全存世界坐标，画之前都过这一个函数，
// 所以同一份任务数据在 SVG 版和瓦片版上都能正确落位。
//
// 改动须知：
//  · maps.json 里的 transform / bounds / viewBox 是**标定数据**，写错整张图会整体偏移。
//    重新标定走 tools/shrink-tiles.ps1（裁边后它会重算 bounds/viewBox）。
//  · ImageFor() 返回的路径**不带扩展名**：瓦片底图编码成 .jpg，楼层图和 SVG 渲染图是
//    .png，由调用方按 {.jpg,.png} 依次试。别在这里写死扩展名。
//  · Key / DataKey / ImageKey 三个字段含义不同：
//      Key      这张图自己（投影参数、图片目录）
//      DataKey  任务/撤离点数据借谁的（瓦片版海关借 customs）
//      ImageKey 位图借谁的（夜间工厂借 factory）
//    混淆 → 点位整体跑到图外，这个 bug 已经犯过一次。
// ============================================================================
using System.Text.Json;

namespace TarkovMapLite;

/// <summary>一张地图的投影参数：世界 (x, z) 怎么落到地图图片的像素上。</summary>
public sealed class MapProjection
{
    public string Key { get; init; } = "";
    public string NormalizedName { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Svg { get; init; } = "";
    /// <summary>
    /// 共用别的图的位图（夜间工厂和工厂同一张图）。为空表示用自己的 Key 目录。
    /// </summary>
    public string ImageKey { get; init; } = "";
    /// <summary>共用别的图的任务/标记数据（瓦片版海关用 customs 的数据）。</summary>
    public string DataKey { get; init; } = "";
    /// <summary>
    /// 这条是别的图的另一种画法（瓦片版指向 SVG 版）。有值时顶栏出现「SVG 版 / 瓦片版」切换。
    /// </summary>
    public string VariantOf { get; init; } = "";
    /// <summary>[scaleX, marginX, scaleZ, marginZ]</summary>
    public double[] Transform { get; init; } = new double[4];
    public double Rotation { get; init; }
    public double[][] Bounds { get; init; } = Array.Empty<double[]>();
    public double[] ViewBox { get; init; } = new double[4];
    public IReadOnlyList<MapLayer> Layers { get; init; } = Array.Empty<MapLayer>();

    /// <summary>
    /// 这张图该默认用另一个条目（通常是它的瓦片版）。SVG 源里缺层的图会这么指：
    /// 网站的瓦片有 5 层、SVG 只有 1 层时，用瓦片版才能把层补全。
    /// </summary>
    public string PreferTiles { get; init; } = "";

    /// <summary>任务与标记从哪个地图 key 取（为空就是自己）。</summary>
    public string Data =>
        string.IsNullOrEmpty(DataKey) ? Key : DataKey;

    public double ViewBoxWidth => ViewBox.Length >= 4 ? ViewBox[2] : 0;
    public double ViewBoxHeight => ViewBox.Length >= 4 ? ViewBox[3] : 0;

    /// <summary>
    /// 地图图片在 Assets/maps 下的相对路径（按楼层），**不含扩展名**：
    /// 瓦片底图压缩成 .jpg，其余（SVG 渲染图、楼层图）仍是 .png，
    /// 由读图的地方依次尝试。
    /// </summary>
    public string ImageFor(string? svgLayer)
    {
        var folder = string.IsNullOrEmpty(ImageKey) ? Key : ImageKey;
        return string.IsNullOrEmpty(svgLayer) ? $"{folder}/base" : $"{folder}/{svgLayer}";
    }
}

public sealed class MapLayer
{
    public string Name { get; init; } = "";
    public string SvgLayer { get; init; } = "";
    public bool Show { get; init; }
    public int? Level { get; init; }
    public int Floor => Level ?? MapFloors.NumberOfLayer(SvgLayer, Name);
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

    /// <summary>自动选图可采用默认版本，用户显式切换时保留所选版本。</summary>
    public static MapProjection? Resolve(string key, bool preferDefault)
    {
        var map = Find(key);
        return preferDefault && !string.IsNullOrEmpty(map?.PreferTiles)
            ? Find(map.PreferTiles) ?? map : map;
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
        // 手动标定微调（Ctrl+方向键调整，见 MapCalibration）
        var (dx, dy) = MapCalibration.Of(map.Key);
        return (u + dx, v + dy);
    }
}
