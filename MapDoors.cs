// ============================================================================
// MapDoors.cs — 需要钥匙的门 + 闸点（拉杆 / 按钮）
//
// 数据不是实时接口，是 tools/build-doors.ps1 从 /pve/maps 导出的一次性快照
// （Assets/doors.json，12.5 KB）：237 个门、39 个闸点、钥匙名 0 缺失。
//
// 门锁的"名字"就是**开它需要的钥匙名**（数据里 locks[].key 是物品 id，
// 脚本在构建时把它换成中文名）。所以：
//   门 → 钥匙：直接有
//   钥匙 → 门：同一张表反查（以后做"搜钥匙看能开哪些门"就靠它）
//
// 闸点没有中文名，接口里也没有：名字是内部资源名（switch_00409_power），
// 这里用关键词表翻译成人话，后面缀上编号区分。别指望接口会给现成的名字。
//
// NOTE: extracts[].switches[] 不能用来判断"这个闸开哪几个撤离点"——
// 海关 28 个撤离点全都引用同一个闸 id，那个字段没有信息量。
//
// 和区域/地名一样：存世界坐标，按 map.Key 缓存投影结果，取数据用 map.Data。
// ============================================================================
using System.Drawing;
using System.Text.Json.Nodes;

namespace TarkovMapLite;

/// <summary>一扇需要钥匙的门（世界坐标）：位置 + 开它要的钥匙名。</summary>
internal sealed class DoorMarker
{
    public required string KeyName { get; init; }
    public bool NeedsPower { get; init; }
    public required PointF World { get; init; }
    /// <summary>投影后的图片像素位置，由 <see cref="MapDoors.For"/> 填好。</summary>
    public PointF Screen { get; set; }
}

/// <summary>一个闸点（拉杆/按钮/控制台）：位置 + 可读标签。</summary>
internal sealed class SwitchMarker
{
    /// <summary>接口里的内部名，例如 switch_00409_power。</summary>
    public required string RawName { get; init; }
    public required string Label { get; init; }
    public required PointF World { get; init; }
    public PointF Screen { get; set; }
}

internal static class MapDoors
{
    /// <summary>内部名 → 可读中文。顺序有意义：先匹配更具体的词。</summary>
    private static readonly (string Word, string Label)[] SwitchWords =
    {
        ("Disable_alarm", "关闭警报"),
        ("Alarm_Kiba", "Kiba 警报"),
        ("Alarm_Console", "警报控制台"),
        ("Start_Exfiltration", "开始撤离"),
        ("EXFIL_Console", "撤离控制台"),
        ("EXFIL_Power", "撤离电源"),
        ("Pump_button", "抽水泵"),
        ("Water_plane", "排水"),
        ("floor_button", "楼层按钮"),
        ("call_button", "呼叫按钮"),
        ("disable_traps", "陷阱开关"),
        ("Toggle_Trap", "陷阱开关"),
        ("set_valve", "阀门"),
        ("Gaz_cutter", "燃气切割"),
        ("GatesButton", "大门按钮"),
        ("OuterSwitcher", "外圈开关"),
        ("InnerSwitcher", "内圈开关"),
        ("urinal", "冲洗阀"),
        ("Interactive_button", "按钮"),
        ("switcher_lever", "拉杆"),
        ("_power", "电源"),
        ("_Power", "电源"),
    };

    private static readonly Dictionary<string, (List<DoorMarker> Doors, List<SwitchMarker> Switches)> Cache =
        new(StringComparer.OrdinalIgnoreCase);
    private static JsonObject? _document;
    private static bool _loaded;

    /// <summary>当前地图的门和闸，已投影成图片像素；同一张图只算一次。</summary>
    public static (List<DoorMarker> Doors, List<SwitchMarker> Switches) For(MapProjection? map)
    {
        if (map == null)
        {
            return (new List<DoorMarker>(), new List<SwitchMarker>());
        }
        if (Cache.TryGetValue(map.Key, out var hit))
        {
            return hit;
        }
        EnsureLoaded();
        var doors = new List<DoorMarker>();
        var switches = new List<SwitchMarker>();
        if (_document?[map.Data] is JsonObject node)
        {
            foreach (var door in (node["doors"] as JsonArray) ?? new JsonArray())
            {
                if (door is not JsonArray item || item.Count < 2)
                {
                    continue;
                }
                var world = new PointF((float)(item[0]?.GetValue<double>() ?? 0),
                    (float)(item[1]?.GetValue<double>() ?? 0));
                var (u, v) = MapPack.ToImage(map, world.X, world.Y);
                doors.Add(new DoorMarker
                {
                    KeyName = item.Count > 2 ? item[2]?.GetValue<string>() ?? "" : "",
                    NeedsPower = item.Count > 3 && (item[3]?.GetValue<int>() ?? 0) == 1,
                    World = world,
                    Screen = new PointF((float)u, (float)v),
                });
            }
            foreach (var sw in (node["switches"] as JsonArray) ?? new JsonArray())
            {
                if (sw is not JsonArray item || item.Count < 2)
                {
                    continue;
                }
                var world = new PointF((float)(item[0]?.GetValue<double>() ?? 0),
                    (float)(item[1]?.GetValue<double>() ?? 0));
                var raw = item.Count > 2 ? item[2]?.GetValue<string>() ?? "" : "";
                var (u, v) = MapPack.ToImage(map, world.X, world.Y);
                switches.Add(new SwitchMarker
                {
                    RawName = raw,
                    Label = SwitchLabel(raw),
                    World = world,
                    Screen = new PointF((float)u, (float)v),
                });
            }
        }
        var result = (doors, switches);
        Cache[map.Key] = result;
        return result;
    }

    /// <summary>
    /// switch_00409_power → 「电源 00409」。
    /// 认不出来的就原样显示，至少不会是空白。
    /// </summary>
    internal static string SwitchLabel(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "闸";
        }
        var label = "";
        foreach (var (word, text) in SwitchWords)
        {
            if (raw.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                label = text;
                break;
            }
        }
        // 名字里那段数字是关卡里的编号，留着能把同名的两个闸分开
        var digits = System.Text.RegularExpressions.Regex.Match(raw, @"\d{3,}");
        var code = digits.Success ? digits.Value : "";
        if (label.Length == 0)
        {
            return raw;
        }
        return code.Length > 0 ? $"{label} {code}" : label;
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
            var text = Assets.ReadText("doors.json");
            _document = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text) as JsonObject;
        }
        catch
        {
            // 数据文件坏了就当没有门/闸，不影响地图本身
            _document = null;
        }
    }
}
