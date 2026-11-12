// ==== 点位楼层：优先使用 MapFloors 按建筑范围计算的结果，其余地图用高度聚类表 ===
// 职责：给出「某个点位的高度属于哪一层」，让容器/散落物资能跟着楼层按钮切换。
// 谁在用：MapCanvas 画点位时逐个判断。
// 改动须知：旧表的断点是 1D k-means 从真实 y 分布算出来的，改阈值就改那个脚本再重跑，
//           别在这里手写魔数——一张图四个数字，写死会很快和游戏版本对不上。
// 踩过的坑：
//  · bands 的上下界在 JSON 里可能是 null（表示正负无穷），必须当极小/极大处理，
//    否则第一段和最后一段永远匹配不上；
//  · 图层名是 svgLayer（Basement / Second_Floor），和地图菜单里显示的中文无关，
//    比较时别拿中文名去对；
//  · 没有分层数据的图（单层图、瓦片版老数据）一律返回 true，宁可多显示不可漏显示。
// ====

using System.Text.Json;

namespace TarkovMapLite;

internal static class MarkerFloors
{
    private static Dictionary<string, List<(float Low, float High, string Layer)>>? _bands;

    private static Dictionary<string, List<(float Low, float High, string Layer)>> Bands
    {
        get
        {
            if (_bands != null)
            {
                return _bands;
            }
            var table = new Dictionary<string, List<(float, float, string)>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var document = JsonDocument.Parse(Assets.ReadText("marker-floors.json"));
                if (document.RootElement.TryGetProperty("maps", out var maps))
                {
                    foreach (var map in maps.EnumerateObject())
                    {
                        var list = new List<(float, float, string)>();
                        if (map.Value.TryGetProperty("bands", out var bands))
                        {
                            foreach (var band in bands.EnumerateArray())
                            {
                                if (band.ValueKind != JsonValueKind.Array || band.GetArrayLength() < 3)
                                {
                                    continue;
                                }
                                var low = band[0].ValueKind == JsonValueKind.Null
                                    ? float.MinValue
                                    : band[0].GetSingle();
                                var high = band[1].ValueKind == JsonValueKind.Null
                                    ? float.MaxValue
                                    : band[1].GetSingle();
                                var layer = band[2].GetString() ?? "";
                                list.Add((low, high, layer));
                            }
                        }
                        if (list.Count > 0)
                        {
                            table[map.Name] = list;
                        }
                    }
                }
            }
            catch
            {
                // 没有这个资源就当所有点位都不分层
            }
            _bands = table;
            return table;
        }
    }

    private static string Normalize(string name)
    {
        var text = name.Replace("_Level", "").Replace("_Floor", "").Replace("_", "");
        return text.Trim();
    }

    /// <summary>判断是否属于当前层；已有建筑规则时地面也过滤，旧表维持原有地面总览。</summary>
    public static bool Matches(string mapKey, float y, string layer, string? floorLayer = null)
    {
        // 新的按建筑分区规则已在投影时计算。地面也要过滤，避免把地下/楼上物资混在一起。
        if (floorLayer != null)
        {
            return string.Equals(Normalize(floorLayer), Normalize(layer), StringComparison.OrdinalIgnoreCase);
        }
        if (string.IsNullOrEmpty(layer))
        {
            return true;
        }
        // 瓦片版和 SVG 版是两张地图条目（customs / customs-tiles），
        // 断点表按 SVG 版的 key 生成，所以查不到时去掉 -tiles 再试一次。
        if (!Bands.TryGetValue(mapKey, out var bands)
            && !(mapKey.EndsWith("-tiles", StringComparison.OrdinalIgnoreCase)
                 && Bands.TryGetValue(mapKey[..^6], out bands)))
        {
            return true;
        }
        foreach (var (low, high, name) in bands)
        {
            if (y >= low && y < high)
            {
                // 瓦片版和 SVG 版对同一层的叫法可能差一个后缀（Underground / Underground_Level），
                // 比较前统一去掉 _Level / _Floor 再忽略大小写。
                return string.Equals(Normalize(name), Normalize(layer), StringComparison.OrdinalIgnoreCase);
            }
        }
        return true;
    }
}
