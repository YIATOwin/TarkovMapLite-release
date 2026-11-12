// ============================================================================
// MobileState.cs — 手机端的状态快照（不可变 JSON）+ 手机动作的数据类型
//
// 契约（Assets/mobile/app.js 只认这些字段，改名要两端一起改）：
//   rev          每次内容变化 +1，手机端据此判断要不要重画
//   map{key,name,img,w,h,layer,floors[{l,n}]}
//   onlyActive / sel
//   player{x,y,dx,dy}      只在 PlayerMapKey == 当前地图时才有（否则手机上会画错图）
//   zones[{i,f,p:[x,y,...]}]  已投影成图片像素，多边形
//   marks[{n,k:"e"|"t",x,y,note}]   e=撤离点 t=转移点
//   tasks[{id,n,s,sub}]    s = TaskState 的整数（1 进行中 / 2 失败 / 3 完成）
//   detail{obj[],items[{id,n}]}    只针对当前选中的任务
//
// 性能设计：
//  · Flush() 先比 Signature()（地图/楼层/数量/玩家半像素/任务状态），没变就跳过重拼。
//    手机 4Hz 轮询，不做这层判断每秒会白拼 4 个 13 KB 的字符串。
//  · 坐标一律 1 位小数：4000px 的图上 0.1px 肉眼看不出来，但能省一半字节。
//  · 投影只在电脑端算一次，手机端不做任何标定计算。
// ============================================================================
using System.Globalization;
using System.Text;

namespace TarkovMapLite;

/// <summary>手机端发来的动作。HTTP 线程只入队，UI 定时器里再执行。</summary>
internal sealed class MobileAction
{
    public const string SetFloor = "setFloor";
    public const string SelectTask = "selectTask";
    public const string SetMap = "setMap";

    public string Type { get; init; } = "";
    public string Value { get; init; } = "";
}

/// <summary>手机端任务列表的一行（状态用枚举数字发过去，手机自己上色）。</summary>
internal sealed record MobileTask(string Id, string Name, string Subtitle, TaskState State);

    /// <summary>任务资料站链接（中文 Wiki / tarkov.dev / fandom），手机端照桌面那份渲染成按钮。</summary>
    internal sealed record MobileLink(string Label, string Url);

internal sealed record MobileItem(string Id, string Name);

/// <summary>
/// 手机端的只读快照。
///
/// 分工：UI 定时器（250ms）把当前状态塞进字段然后调 <see cref="Flush"/>；
/// 只有"看起来会变的东西"变了才重新拼一次 JSON，HTTP 线程直接拿
/// <see cref="Json"/> 这个不可变字符串，绝不碰 WinForms 控件。
///
/// 投影只在这里算一次——手机端收到的是图片像素坐标，不需要再实现一遍标定。
/// </summary>
internal sealed class MobileState
{
    private readonly object _gate = new();
    private readonly StringBuilder _sb = new(48 * 1024);

    private string _json = "{\"ready\":false}";
    private string _signature = "";
    private int _revision;

    public MapProjection? Map { get; set; }
    public string Layer { get; set; } = "";
    public IReadOnlyList<ZoneShape> Zones { get; set; } = Array.Empty<ZoneShape>();
    public IReadOnlyList<MapMarker> Extracts { get; set; } = Array.Empty<MapMarker>();
    public IReadOnlyList<MapMarker> Transits { get; set; } = Array.Empty<MapMarker>();
    public PointF? Player { get; set; }
    public PointF? PlayerForward { get; set; }
    /// <summary>玩家的世界坐标 (x, z)：手机上要直接念出来（多人报点用）。</summary>
    public PointF? PlayerWorld { get; set; }
    public string PlayerMapKey { get; set; } = "";
    public IReadOnlyList<MobileTask> Tasks { get; set; } = Array.Empty<MobileTask>();
    public string? SelectedTaskId { get; set; }
    public bool OnlyActive { get; set; }

    /// <summary>选中任务的目标文字（没有选中任务时是空的）。</summary>
    public IReadOnlyList<string> Objectives { get; set; } = Array.Empty<string>();
    public IReadOnlyList<MobileItem> Items { get; set; } = Array.Empty<MobileItem>();

    /// <summary>当前任务的资料站链接，和桌面「任务页」菜单同源（都由 MapForm.CurrentTaskLinks 给）。</summary>
    public IReadOnlyList<MobileLink> WikiLinks { get; set; } = Array.Empty<MobileLink>();

    /// <summary>最近一次拼 JSON 的时间，给"手机端"面板显示用。</summary>
    public DateTime BuiltAt { get; private set; }

    public string Json
    {
        get
        {
            lock (_gate)
            {
                return _json;
            }
        }
    }

    /// <summary>把当前字段拼成 JSON；内容没变就跳过（轮询 4Hz，不能每次都重拼）。</summary>
    public void Flush()
    {
        var signature = Signature();
        if (signature == _signature)
        {
            return;
        }
        var json = Build();
        _signature = signature;
        BuiltAt = DateTime.Now;
        lock (_gate)
        {
            _json = json;
            _revision++;
        }
    }

    /// <summary>
    /// 变化的指纹。玩家坐标按半像素取整——3 米内的小抖动不重发，
    /// 但走路时 4Hz 一定看得出在动。
    /// </summary>
    private string Signature()
    {
        var sb = new StringBuilder(160);
        sb.Append(Map?.Key).Append('|').Append(Layer).Append('|')
          .Append(Zones.Count).Append('|').Append(Extracts.Count).Append('|')
          .Append(Transits.Count).Append('|').Append(Tasks.Count).Append('|')
          .Append(SelectedTaskId).Append('|').Append(OnlyActive ? '1' : '0').Append('|')
          .Append(PlayerMapKey).Append('|');
        if (Player is { } p)
        {
            sb.Append((int)Math.Round(p.X * 2)).Append(',').Append((int)Math.Round(p.Y * 2)).Append(',');
        }
        if (PlayerWorld is { } w)
        {
            sb.Append((int)Math.Round(w.X * 10)).Append(',').Append((int)Math.Round(w.Y * 10)).Append(',');
        }
        if (PlayerForward is { } f)
        {
            sb.Append((int)Math.Round(f.X * 20)).Append(',').Append((int)Math.Round(f.Y * 20));
        }
        sb.Append('|').Append(Objectives.Count).Append('|').Append(Items.Count).Append('|');
        foreach (var marker in Extracts.Concat(Transits)) sb.Append(marker.FloorLayer ?? "?").Append('~');
        foreach (var link in WikiLinks)
        {
            sb.Append(link.Url).Append('~');
        }
        sb.Append('|');
        // 任务状态会变（完成/失败），数量和顺序不变，所以把状态一起算进指纹
        foreach (var task in Tasks)
        {
            sb.Append((int)task.State);
        }
        return sb.ToString();
    }

    private string Build()
    {
        var sb = _sb;
        sb.Clear();
        sb.Append("{\"rev\":").Append(_revision + 1);

        if (Map is { } map)
        {
            sb.Append(",\"map\":{\"key\":").Append(Quote(map.Key.Length > 0 ? map.Key : "-"))
              .Append(",\"name\":").Append(Quote(map.DisplayName.Length > 0 ? map.DisplayName : map.Key))
              .Append(",\"img\":").Append(Quote(string.IsNullOrEmpty(map.ImageKey) ? map.Key : map.ImageKey))
              .Append(",\"w\":").Append(Num(map.ViewBoxWidth))
              .Append(",\"h\":").Append(Num(map.ViewBoxHeight))
              .Append(",\"layer\":").Append(Quote(Layer))
              .Append(",\"floors\":[");
            var first = true;
            AppendFloor(sb, "", "地面", ref first);
            foreach (var layer in map.Layers)
            {
                if (string.IsNullOrWhiteSpace(layer.SvgLayer))
                {
                    continue;
                }
                AppendFloor(sb, layer.SvgLayer, MapCanvas.FloorLabel(layer.Name), ref first);
            }
            sb.Append(']');
            // 参考坐标轴：把世界 +X / +Z 投影到这张图上，得到两个方向向量。
            // 手机端不用懂投影，照着画箭头就行（地图旋转 180° 时方向自然反过来）。
            var origin = MapPack.ToImage(map, 0, 0);
            var plusX = MapPack.ToImage(map, 10, 0);
            var plusZ = MapPack.ToImage(map, 0, 10);
            sb.Append(",\"axes\":{\"x\":[")
              .Append(Num(plusX.X - origin.X)).Append(',').Append(Num(plusX.Y - origin.Y))
              .Append("],\"z\":[")
              .Append(Num(plusZ.X - origin.X)).Append(',').Append(Num(plusZ.Y - origin.Y))
              .Append("]}");
            sb.Append('}');
        }
        else
        {
            sb.Append(",\"map\":null");
        }

        sb.Append(",\"onlyActive\":").Append(OnlyActive ? "true" : "false");
        sb.Append(",\"sel\":").Append(SelectedTaskId is { Length: > 0 } selected ? Quote(selected) : "null");

        // 任务资料站：手机上直接渲染成按钮，点开就在手机浏览器里看
        sb.Append(",\"wiki\":[");
        for (var i = 0; i < WikiLinks.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }
            sb.Append("{\"l\":").Append(Quote(WikiLinks[i].Label))
              .Append(",\"u\":").Append(Quote(WikiLinks[i].Url)).Append('}');
        }
        sb.Append(']');

        // 玩家：只有「截图所属地图」和当前地图一致时才发，手机就不会把点画到别的图上
        if (Player is { } player && string.Equals(PlayerMapKey, Map?.Key, StringComparison.OrdinalIgnoreCase))
        {
            var forward = PlayerForward ?? new PointF(0, -1);
            sb.Append(",\"player\":{\"x\":").Append(Num(player.X))
              .Append(",\"y\":").Append(Num(player.Y))
              .Append(",\"dx\":").Append(Num(forward.X))
              .Append(",\"dy\":").Append(Num(forward.Y));
            if (PlayerWorld is { } world)
            {
                sb.Append(",\"wx\":").Append(Num(world.X)).Append(",\"wz\":").Append(Num(world.Y));
            }
            sb.Append('}');
        }

        sb.Append(",\"zones\":[");
        var comma = false;
        foreach (var zone in Zones)
        {
            if (zone.Points.Length == 0)
            {
                continue;
            }
            AppendComma(sb, ref comma);
            sb.Append("{\"i\":").Append(Quote(zone.TaskId))
              .Append(",\"f\":").Append(zone.Floor)
              .Append(",\"p\":[");
            for (var i = 0; i < zone.Points.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                sb.Append(Num(zone.Points[i].X)).Append(',').Append(Num(zone.Points[i].Y));
            }
            sb.Append("]}");
        }
        sb.Append(']');

        sb.Append(",\"marks\":[");
        comma = false;
        foreach (var (list, kind) in new[] { (Extracts, "e"), (Transits, "t") })
        {
            foreach (var marker in list)
            {
                if (marker.Screen.IsEmpty && marker.X == 0 && marker.Z == 0)
                {
                    continue;
                }
                AppendComma(sb, ref comma);
                sb.Append("{\"n\":").Append(Quote(marker.Name))
                  .Append(",\"k\":\"").Append(kind).Append('"')
                  .Append(",\"x\":").Append(Num(marker.Screen.X))
                  .Append(",\"y\":").Append(Num(marker.Screen.Y))
                  .Append(",\"a\":").Append(MarkerFloors.OpacityForLayer(marker.FloorLayer, Layer)
                      .ToString("0.##", CultureInfo.InvariantCulture));
                if (marker.Note.Length > 0)
                {
                    sb.Append(",\"note\":").Append(Quote(marker.Note));
                }
                sb.Append('}');
            }
        }
        sb.Append(']');

        sb.Append(",\"tasks\":[");
        comma = false;
        foreach (var task in Tasks)
        {
            AppendComma(sb, ref comma);
            sb.Append("{\"id\":").Append(Quote(task.Id))
              .Append(",\"n\":").Append(Quote(task.Name))
              .Append(",\"s\":").Append((int)task.State)
              .Append(",\"sub\":").Append(Quote(task.Subtitle))
              .Append('}');
        }
        sb.Append(']');

        sb.Append(",\"detail\":{\"obj\":[");
        for (var i = 0; i < Objectives.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }
            sb.Append(Quote(Objectives[i]));
        }
        sb.Append("],\"items\":[");
        for (var i = 0; i < Items.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }
            sb.Append("{\"id\":").Append(Quote(Items[i].Id))
              .Append(",\"n\":").Append(Quote(Items[i].Name)).Append('}');
        }
        sb.Append("]}}");
        return sb.ToString();
    }

    private static void AppendFloor(StringBuilder sb, string layer, string label, ref bool first)
    {
        if (!first)
        {
            sb.Append(',');
        }
        first = false;
        sb.Append("{\"l\":").Append(Quote(layer)).Append(",\"n\":").Append(Quote(label)).Append('}');
    }

    private static void AppendComma(StringBuilder sb, ref bool comma)
    {
        if (comma)
        {
            sb.Append(',');
        }
        comma = true;
    }

    /// <summary>坐标保留 1 位小数：4000px 的图上是 0.1px，肉眼看不出，但省一半字节。</summary>
    private static string Num(double value)
    {
        var rounded = Math.Round(value, 1);
        return rounded.ToString("0.#", CultureInfo.InvariantCulture);
    }

    /// <summary>JSON 字符串转义。任务名里有引号/反斜杠的，直接抄过来会拼坏整个文档。</summary>
    private static string Quote(string text)
    {
        var sb = new StringBuilder(text.Length + 2);
        sb.Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ')
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>占位：客户端连上来但电脑端还没准备好时也能拿到一个合法 JSON。</summary>
    public void Reset()
    {
        Map = null;
        Zones = Array.Empty<ZoneShape>();
        Extracts = Array.Empty<MapMarker>();
        Transits = Array.Empty<MapMarker>();
        Tasks = Array.Empty<MobileTask>();
        Player = null;
        PlayerForward = null;
        PlayerMapKey = "";
        SelectedTaskId = null;
        Objectives = Array.Empty<string>();
        Items = Array.Empty<MobileItem>();
        Flush();
    }
}
