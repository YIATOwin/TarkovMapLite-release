// ============================================================================
// MapCanvas.cs — 画布（本项目最大的绘制文件）
//
// 负责：底图/楼层合成、地名、静态标记、任务区域+编号连线、撤离点/转移点、BTR、
//       玩家箭头、左下角参考坐标轴、缩放拖动、楼层按钮。
//
// 绘制顺序写在 OnPaint 里，顺序本身就是设计（改顺序会互相遮挡）：
//   底图 → 楼层 → 地名 → 静态标记 → 任务区域 → 撤离点 → BTR → 玩家 → 角标
//   地名必须最早（否则会盖住任务标签），缩放/楼层角标必须最后。
//
// 改动须知：
//  · 区域数据（Zones）是世界坐标，SetMap() 里统一投影一次存进 _projected；
//    直接改 _map 而不重算投影 → 区域整体跑到图外（踩过一次）。
//  · 图片缓存上限 120 MB（ImageCacheBudget）：4000px 的图解码后一张 40~76 MB，
//    这个软件的立身之本就是省内存，别随手调大。
//  · 只画屏幕内的东西，culling 一律用**屏幕坐标**判断；用图片坐标判断会在缩放时漏画。
//  · 楼层图不存在时（很多图只有地面）自动回落到底图，不重复解码一份一样的大图。
//  · 拖动手感：OnMouseDown 先判断是否点中左上角楼层按钮，再进入拖动。
// ============================================================================
using System.Drawing.Drawing2D;

namespace TarkovMapLite;

/// <summary>画布：底图 + 任务区域 + 点位 + 标签，负责平移缩放。</summary>
internal sealed class MapCanvas : Control
{
    private readonly Dictionary<string, Image> _images = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>图片缓存的最近使用顺序（末尾最新），用来淘汰旧图。</summary>
    private readonly List<string> _imageOrder = new();
    /// <summary>每张缓存图解码后大约占多少字节（Width * Height * 4）。</summary>
    private readonly Dictionary<string, long> _imageBytes = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// 解码后图片的总预算。一张 4000 px 的地图约 40 MB，一张 4352×4352 的瓦片图约 76 MB，
    /// 不设上限的话逛十张图就是几百 MB——这个软件的出发点就是省内存。
    /// 至少留 2 张（底图 + 当前楼层），有余量才继续留。
    /// </summary>
    private const long ImageCacheBudget = 120L * 1024 * 1024;
    private const int ImageCacheMinimum = 2;
    /// <summary>图片扩展名，按顺序尝试：瓦片底图压缩成了 jpg，楼层和矢量图还是 png。</summary>
    private static readonly string[] ImageExtensions = { ".jpg", ".png" };
    private MapProjection? _map;
    private string _layer = "";
    private Image? _image;

    private double _zoom = 1;
    private double _fitScale = 1;
    private bool _fitPending;
    private PointF _pan;
    private bool _dragging;
    private Point _dragStart;
    private PointF _panStart;

    public bool OnlyActive { get; set; }
    public TaskLog? Log { get; set; }
    public bool ShowZones { get; set; } = true;

    private List<ZoneShape> _zones = new();
    private List<ZoneShape> _projected = new();

    /// <summary>
    /// 当前地图上所有区域（世界坐标，来自 TarkovData）。
    /// 赋值时按当前地图投影一次，绘制只用 <c>_projected</c>。
    /// </summary>
    public List<ZoneShape> Zones
    {
        get => _zones;
        set
        {
            _zones = value ?? new List<ZoneShape>();
            ProjectZones();
        }
    }

    /// <summary>把世界坐标的区域投影到当前地图的像素空间；切图时重算。</summary>
    private void ProjectZones()
    {
        _projected = _map == null
            ? new List<ZoneShape>()
            : _zones.Select(z => z.Project(_map)).ToList();
    }

    /// <summary>选中任务所需物品的候选刷新点（图片像素坐标）。</summary>
    public List<PointF> SpawnPoints { get; set; } = new();

    /// <summary>只显示这个任务的区域；null 表示显示所有符合条件的任务。</summary>
    public string? SelectedTaskId { get; set; }

    /// <summary>是否显示撤离点 / 转移点。</summary>
    public bool ShowExtracts { get; set; } = true;

    /// <summary>是否显示地名（Big Red / Dorms / Sawmill …）。</summary>
    public bool ShowLabels { get; set; }

    /// <summary>
    /// 是否显示 BTR 停车点。默认关：它和地名大量重叠（森林的「锯木厂」就是 Sawmill），
    /// 只有在真的在坐 BTR 时才需要打开。
    /// </summary>
    public bool ShowBtr { get; set; }

    /// <summary>静态图层开关：容器 / 散落物资 / 出生点 / 危险区 / 固定武器。</summary>
    public bool ShowContainers { get; set; }
    /// <summary>被隐藏的容器类型（在「图层」菜单里取消勾选）。</summary>
    public HashSet<string> HiddenContainerKinds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ShowLoot { get; set; }
    public bool ShowSpawns { get; set; }
    public bool ShowHazards { get; set; }
    public bool ShowStatics { get; set; }

    public List<MapMarker> Extracts { get; set; } = new();
    public List<MapMarker> Transits { get; set; } = new();

    /// <summary>BTR 停车点（图片像素坐标，由 MapForm 按当前地图投影）。</summary>
    public List<MapMarker> BtrStops { get; set; } = new();

    /// <summary>玩家位置（图片像素坐标）与朝向；只有 PlayerMapKey 和当前地图一致时才画。</summary>
    public PointF? PlayerPosition { get; set; }
    public PointF? PlayerForward { get; set; }
    public string PlayerMapKey { get; set; } = "";

    private static readonly (string Key, string Label)[] FloorButtons = Array.Empty<(string, string)>();

    public event Action? ViewChanged;
    public event Action<string>? FloorRequested;

    public MapCanvas()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(13, 15, 17);
    }

    public MapProjection? Map => _map;

    /// <summary>当前楼层（空串 = 地面）；手机端快照要带过去。</summary>
    public string CurrentLayer => _layer;

    /// <summary>已经按当前地图投影好的区域（手机端直接拿去发，不用再算一遍）。</summary>
    public IReadOnlyList<ZoneShape> ProjectedZones => _projected;

    public void SetMap(MapProjection? map, string displayName)
    {
        _map = map;
        _image = null;
        _layer = "";
        _zoom = 1;
        _pan = PointF.Empty;
        _fitPending = true;      // 等拿到真实画布尺寸再算适配比例
        ProjectZones();          // 换图要按新图的投影重算区域
        Invalidate();
    }

    public void SetLayer(string layer)
    {
        _layer = layer;
        _image = null;
        Invalidate();
    }

    /// <summary>窗口大小变化时，如果用户没手动缩放过，重新适配一次。</summary>
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_zoom <= 1.0001)
        {
            Fit();
        }
    }

    public void Fit()
    {
        if (_map == null || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return;
        }
        _fitScale = Math.Min((double)ClientSize.Width / _map.ViewBoxWidth,
                             (double)ClientSize.Height / _map.ViewBoxHeight);
        _zoom = 1;
        _pan = PointF.Empty;
        _fitPending = false;
        Invalidate();
    }

    /// <summary>
    /// 以 <paramref name="anchor"/>（屏幕点，默认视口中心）为锚点缩放：
    /// 锚点下方的那个图像点必须前后不动。所以要先用**缩放前**的比例算出锚点对应的
    /// 图像坐标，再反推出缩放后的平移量。
    /// </summary>
    public void ZoomBy(double factor, Point? anchor = null)
    {
        if (_map == null)
        {
            return;
        }
        var point = anchor ?? new Point(ClientSize.Width / 2, ClientSize.Height / 2);

        var before = Scale;
        var originX = (ClientSize.Width - _map.ViewBoxWidth * before) / 2 + _pan.X;
        var originY = (ClientSize.Height - _map.ViewBoxHeight * before) / 2 + _pan.Y;

        _zoom = Math.Clamp(_zoom * factor, 1, 12);
        var after = Scale;
        if (Math.Abs(after - before) < 1e-9)
        {
            return;
        }

        var ratio = after / before;
        // 锚点对应的图像坐标在缩放后仍要落在同一个屏幕点
        var nextOriginX = point.X - (point.X - originX) * ratio;
        var nextOriginY = point.Y - (point.Y - originY) * ratio;
        _pan.X = (float)(nextOriginX - (ClientSize.Width - _map.ViewBoxWidth * after) / 2);
        _pan.Y = (float)(nextOriginY - (ClientSize.Height - _map.ViewBoxHeight * after) / 2);

        if (_zoom <= 1)
        {
            _pan = PointF.Empty;
        }
        Invalidate();
        ViewChanged?.Invoke();
    }

    public double Zoom => _zoom;
    /// <summary>缩放倍数（1 = 适配窗口）。名字和 Control.Scale(float) 撞车，所以标 new。</summary>
    public new double Scale => _fitScale * _zoom;

    private PointF Origin
    {
        get
        {
            if (_map == null)
            {
                return PointF.Empty;
            }
            var s = Scale;
            return new PointF(
                (float)((ClientSize.Width - _map.ViewBoxWidth * s) / 2 + _pan.X),
                (float)((ClientSize.Height - _map.ViewBoxHeight * s) / 2 + _pan.Y));
        }
    }

    private Image? ImageFor(string? layer)
    {
        if (_map == null)
        {
            return null;
        }
        // 瓦片底图是 .jpg（裁边+压缩过），楼层和 SVG 渲染图是 .png
        var stem = _map.ImageFor(layer);
        foreach (var extension in ImageExtensions)
        {
            if (_images.TryGetValue(stem + extension, out var hit))
            {
                Touch(stem + extension);
                return hit;
            }
        }
        string? path = null;
        Image? image = null;
        foreach (var extension in ImageExtensions)
        {
            image = Assets.ReadImage("maps/" + stem + extension);
            if (image != null)
            {
                path = stem + extension;
                break;
            }
        }
        if (image == null && !string.IsNullOrEmpty(layer))
        {
            // 该楼层没有独立图片：直接复用底图，不要再解码一份一样的图（一张就 40 MB）
            return ImageFor(null);
        }
        if (image == null || path == null)
        {
            return null;
        }
        _images[path] = image;
        _imageBytes[path] = 4L * image.Width * image.Height;
        _imageOrder.Add(path);
        TrimImages();
        return image;
    }

    /// <summary>标记为"刚用过"，淘汰时优先丢最久没看的。</summary>
    private void Touch(string path)
    {
        if (_imageOrder.Count > 0 && _imageOrder[^1] == path)
        {
            return;
        }
        _imageOrder.Remove(path);
        _imageOrder.Add(path);
    }

    /// <summary>
    /// 超出内存预算就释放最久没用过的图；正在显示的图刚 Touch 过，不会被选中，
    /// 而且无论多大都至少保留 <see cref="ImageCacheMinimum"/> 张。
    /// </summary>
    private void TrimImages()
    {
        while (_imageOrder.Count > ImageCacheMinimum)
        {
            long total = 0;
            foreach (var path in _imageOrder)
            {
                total += _imageBytes.TryGetValue(path, out var bytes) ? bytes : 0;
            }
            if (total <= ImageCacheBudget)
            {
                return;
            }
            var victim = _imageOrder[0];
            _imageOrder.RemoveAt(0);
            _imageBytes.Remove(victim);
            if (_images.Remove(victim, out var image))
            {
                image.Dispose();
            }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_map == null)
        {
            return;
        }
        if (_fitPending && ClientSize.Width > 16 && ClientSize.Height > 16)
        {
            Fit();
        }
        // 每次都取一遍：一是保证底图留在缓存里（不被当最旧淘汰掉），
        // 二是 ImageFor 会顺手把它的"最近使用"顺序刷到最新，代价只是一次字典查找。
        _image = ImageFor(null);
        var s = (float)Scale;
        var origin = Origin;
        var rect = new RectangleF(origin.X, origin.Y,
            (float)(_map.ViewBoxWidth * s), (float)(_map.ViewBoxHeight * s));
        g.InterpolationMode = _zoom > 1.6 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        if (_image != null)
        {
            g.DrawImage(_image, rect);
        }
        // 非地面楼层：把地面压暗，再叠加当前楼层
        if (!string.IsNullOrEmpty(_layer) && ImageFor(_layer) is { } floor)
        {
            using (var dim = new SolidBrush(Color.FromArgb(130, 5, 7, 9)))
            {
                g.FillRectangle(dim, rect);
            }
            g.DrawImage(floor, rect);
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        // 地名压在最底层：它只是背景参照，任务标记、点位、撤离点都要盖在它上面
        if (ShowLabels)
        {
            DrawLabels(g, s, origin);
        }
        // 静态标记（容器/散落物资/出生点/危险区）压在地图上面、任务区域下面，
        // 这样任务的多边形和点位不会被物资点盖住
        if (ShowContainers || ShowLoot || ShowSpawns || ShowHazards || ShowStatics)
        {
            DrawStaticMarkers(g, s, origin);
        }
        if (ShowZones && SpawnPoints.Count > 0)
        {
            DrawSpawnPoints(g, s, origin);
        }
        if (ShowZones && Zones.Count > 0)
        {
            DrawZones(g, s, origin);
            DrawOffscreenArrows(g, s, origin);
        }
        // 地名压在最底层：它只是背景参照，任务标记、点位、撤离点都要盖在它上面
        if (ShowExtracts)
        {
            DrawMarkers(g, s, origin);
        }
        if (ShowBtr && BtrStops.Count > 0)
        {
            DrawBtrStops(g, s, origin);
        }
        DrawPlayer(g, s, origin);
        DrawFloors(g);
        DrawZoom(g);
        DrawAxes(g);
    }

    /// <summary>容器 / 散落物资 / 出生点 / 危险区 / 固定武器。</summary>
    private void DrawStaticMarkers(Graphics g, float scale, PointF origin)
    {
        var layer = MapMarkers.For(_map);
        if (layer.IsEmpty)
        {
            return;
        }
        // 屏幕坐标从 (0,0) 起算。之前错写成从 origin 起算：缩放/平移后 origin 变成负数，
        // 所有标记都被判成"在视野外"，所以放大一点标记就整片消失。
        var visible = new RectangleF(-40, -40, ClientSize.Width + 80, ClientSize.Height + 80);

        PointF At(PointF p) => new(origin.X + p.X * scale, origin.Y + p.Y * scale);

        // 雷区：半透明红面 + 红边
        if (ShowHazards)
        {
            foreach (var (ring, _) in layer.HazardAreas)
            {
                var pts = ring.Select(At).ToArray();
                if (!pts.Any(p => visible.Contains(p)))
                {
                    continue;
                }
                using var fill = new SolidBrush(Color.FromArgb(46, 220, 70, 70));
                using var line = new Pen(Color.FromArgb(150, 220, 70, 70), 1.2f);
                g.FillPolygon(fill, pts);
                g.DrawPolygon(line, pts);
            }
        }

        // 容器：每种类型一个颜色 + 形状（图例在「图层」菜单里）
        if (ShowContainers)
        {
            foreach (var (point, kind) in layer.Containers)
            {
                if (HiddenContainerKinds.Contains(kind))
                {
                    continue;
                }
                var p = At(point);
                if (!visible.Contains(p))
                {
                    continue;
                }
                DrawContainer(g, p, ContainerKinds.Resolve(kind), scale);
            }
        }

        // 散落物资：琥珀色小圆点
        if (ShowLoot)
        {
            using var fill = new SolidBrush(Color.FromArgb(200, 255, 186, 88));
            foreach (var point in layer.Loose)
            {
                var p = At(point);
                if (!visible.Contains(p))
                {
                    continue;
                }
                g.FillEllipse(fill, p.X - 2.1f, p.Y - 2.1f, 4.2f, 4.2f);
            }
        }

        // 危险点（非雷区）：红色三角 + 感叹号太挤，就用三角
        if (ShowHazards)
        {
            using var fill = new SolidBrush(Color.FromArgb(210, 235, 90, 80));
            foreach (var (point, _) in layer.Hazards)
            {
                var p = At(point);
                if (!visible.Contains(p))
                {
                    continue;
                }
                g.FillPolygon(fill, new[]
                {
                    new PointF(p.X, p.Y - 4.5f),
                    new PointF(p.X + 4.5f, p.Y + 3.5f),
                    new PointF(p.X - 4.5f, p.Y + 3.5f),
                });
            }
        }

        // 出生点：空心圈，颜色区分类别（玩家蓝 / AI 橙 / Boss 紫 / 狙击手深红）
        if (ShowSpawns)
        {
            using var playerPen = new Pen(Color.FromArgb(170, 120, 200, 255), 1.3f);
            using var botPen = new Pen(Color.FromArgb(150, 235, 165, 90), 1.3f);
            using var bossPen = new Pen(Color.FromArgb(190, 200, 120, 255), 1.8f);
            using var sniperPen = new Pen(Color.FromArgb(170, 240, 90, 90), 1.3f);
            foreach (var (point, cats) in layer.Spawns)
            {
                var p = At(point);
                if (!visible.Contains(p))
                {
                    continue;
                }
                var pen = cats.Contains('o') ? bossPen
                    : cats.Contains('p') ? playerPen
                    : cats.Contains('s') ? sniperPen
                    : botPen;
                var r = cats.Contains('o') ? 6f : 4.5f;
                g.DrawEllipse(pen, p.X - r, p.Y - r, r * 2, r * 2);
            }
        }

        // 固定武器：灰色方块加叉
        if (ShowStatics)
        {
            using var fill = new SolidBrush(Color.FromArgb(180, 150, 160, 175));
            foreach (var point in layer.Statics)
            {
                var p = At(point);
                if (!visible.Contains(p))
                {
                    continue;
                }
                g.FillRectangle(fill, p.X - 3.5f, p.Y - 3.5f, 7, 7);
            }
        }

    }

    /// <summary>圆角矩形，容器图标的底板。</summary>
    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// 一种容器：圆角底 + 内部象形记号。点太小（缩得很远）时只留纯色块，
    /// 因为 5px 里画笔画反而糊成一团。
    /// </summary>
    private static void DrawContainer(Graphics g, PointF p, ContainerKinds.Kind kind, float scale)
    {
        var s = Math.Clamp(scale * 30f, 3.6f, 7.2f);      // 半边长，约 7~14 px
        var rect = new RectangleF(p.X - s, p.Y - s, s * 2, s * 2);
        using var fill = new SolidBrush(Color.FromArgb(228, kind.Color));
        using var edge = new Pen(Color.FromArgb(210, 18, 20, 24), 1f);
        if (s < 4.6f)
        {
            g.FillRectangle(fill, rect);
            g.DrawRectangle(edge, rect.X, rect.Y, rect.Width, rect.Height);
            return;
        }

        using var body = RoundedRect(rect, s * 0.42f);
        g.FillPath(fill, body);
        g.DrawPath(edge, body);

        using var ink = new Pen(Color.FromArgb(240, 20, 22, 26), Math.Max(1f, s * 0.19f));
        var u = s;
        switch (kind.Glyph)
        {
            case ContainerKinds.Icon.Crate:
                g.DrawLine(ink, p.X - u * 0.52f, p.Y - u * 0.52f, p.X + u * 0.52f, p.Y + u * 0.52f);
                g.DrawLine(ink, p.X + u * 0.52f, p.Y - u * 0.52f, p.X - u * 0.52f, p.Y + u * 0.52f);
                break;
            case ContainerKinds.Icon.Bag:
                g.DrawArc(ink, p.X - u * 0.34f, p.Y - u * 0.78f, u * 0.68f, u * 0.6f, 195, 150);
                g.DrawLine(ink, p.X - u * 0.6f, p.Y + u * 0.08f, p.X + u * 0.6f, p.Y + u * 0.08f);
                break;
            case ContainerKinds.Icon.Drawer:
                g.DrawLine(ink, p.X - u * 0.52f, p.Y - u * 0.26f, p.X + u * 0.52f, p.Y - u * 0.26f);
                g.DrawLine(ink, p.X - u * 0.52f, p.Y + u * 0.28f, p.X + u * 0.52f, p.Y + u * 0.28f);
                break;
            case ContainerKinds.Icon.Hanger:
                g.DrawArc(ink, p.X - u * 0.17f, p.Y - u * 0.8f, u * 0.34f, u * 0.34f, 200, 200);
                g.DrawLine(ink, p.X, p.Y - u * 0.44f, p.X - u * 0.52f, p.Y + u * 0.46f);
                g.DrawLine(ink, p.X, p.Y - u * 0.44f, p.X + u * 0.52f, p.Y + u * 0.46f);
                g.DrawLine(ink, p.X - u * 0.52f, p.Y + u * 0.46f, p.X + u * 0.52f, p.Y + u * 0.46f);
                break;
            case ContainerKinds.Icon.Dial:
                g.DrawEllipse(ink, p.X - u * 0.42f, p.Y - u * 0.42f, u * 0.84f, u * 0.84f);
                g.DrawLine(ink, p.X, p.Y, p.X, p.Y - u * 0.3f);
                break;
            case ContainerKinds.Icon.Monitor:
                g.DrawRectangle(ink, p.X - u * 0.54f, p.Y - u * 0.52f, u * 1.08f, u * 0.72f);
                g.DrawLine(ink, p.X, p.Y + u * 0.2f, p.X, p.Y + u * 0.48f);
                g.DrawLine(ink, p.X - u * 0.3f, p.Y + u * 0.48f, p.X + u * 0.3f, p.Y + u * 0.48f);
                break;
            case ContainerKinds.Icon.Cross:
                g.DrawLine(ink, p.X - u * 0.44f, p.Y, p.X + u * 0.44f, p.Y);
                g.DrawLine(ink, p.X, p.Y - u * 0.44f, p.X, p.Y + u * 0.44f);
                break;
            case ContainerKinds.Icon.Body:
                g.DrawEllipse(ink, p.X - u * 0.52f, p.Y - u * 0.46f, u * 0.42f, u * 0.42f);
                g.DrawLine(ink, p.X - u * 0.1f, p.Y + u * 0.18f, p.X + u * 0.56f, p.Y + u * 0.18f);
                break;
        }
    }

    /// <summary>当前区域是否应该显示：要过"仅进行中"和"只看选中任务"两道过滤。</summary>
    private bool IsZoneVisible(ZoneShape zone, TaskState state)
    {
        if (SelectedTaskId != null && !string.Equals(zone.TaskId, SelectedTaskId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return !OnlyActive || state == TaskState.Started;
    }

    /// <summary>撤离点（绿）和转移点（紫），带名字。</summary>
    private void DrawMarkers(Graphics g, float scale, PointF origin)
    {
        using var font = new Font("Microsoft YaHei UI", 7.5f);
        using var textBrush = new SolidBrush(Color.FromArgb(235, 235, 245, 255));
        using var halo = new SolidBrush(Color.FromArgb(200, 0, 0, 0));
        foreach (var (list, color) in new[]
                 {
                     (Extracts, Color.FromArgb(120, 220, 150)),
                     (Transits, Color.FromArgb(190, 150, 245)),
                 })
        {
            foreach (var marker in list)
            {
                var x = origin.X + marker.Screen.X * scale;
                var y = origin.Y + marker.Screen.Y * scale;
                if (x < -20 || y < -20 || x > ClientSize.Width + 20 || y > ClientSize.Height + 20)
                {
                    continue;
                }
                using var fill = new SolidBrush(color);
                using var ring = new Pen(Color.FromArgb(230, 20, 24, 20), 1.2f);
                if (marker.Kind == "extract")
                {
                    // 方块
                    g.FillRectangle(fill, x - 4.5f, y - 4.5f, 9, 9);
                    g.DrawRectangle(ring, x - 4.5f, y - 4.5f, 9, 9);
                }
                else
                {
                    // 菱形
                    g.FillPolygon(fill, new[]
                    {
                        new PointF(x, y - 6), new PointF(x + 6, y),
                        new PointF(x, y + 6), new PointF(x - 6, y),
                    });
                    g.DrawPolygon(ring, new[]
                    {
                        new PointF(x, y - 6), new PointF(x + 6, y),
                        new PointF(x, y + 6), new PointF(x - 6, y),
                    });
                }
                var text = marker.Name;
                var size = g.MeasureString(text, font);
                var tx = x + 8;
                var ty = y - size.Height / 2;
                if (tx + size.Width > ClientSize.Width)
                {
                    tx = x - size.Width - 8;
                }
                g.FillRectangle(halo, tx - 1, ty, size.Width + 2, size.Height);
                g.DrawString(text, font, textBrush, tx, ty);
            }
        }
    }

    /// <summary>
    /// 地名：站点底图上写着的那些名字。屏幕上会重叠的直接跳过——立交桥有 77 个
    /// 店铺名，全画出来糊成一片，所以按站点字号从大到小去抢位置。
    /// </summary>
    private void DrawLabels(Graphics g, float scale, PointF origin)
    {
        var labels = MapLabels.For(_map);
        if (labels.Count == 0)
        {
            return;
        }
        using var font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold);
        using var halo = new SolidBrush(Color.FromArgb(205, 0, 0, 0));
        using var ink = new SolidBrush(Color.FromArgb(240, 236, 228, 198));
        var placed = new List<RectangleF>();
        foreach (var (point, label) in labels
                     .OrderByDescending(entry => entry.Label.Size)
                     .ThenBy(entry => entry.Label.Text, StringComparer.Ordinal))
        {
            if (placed.Count >= 90)
            {
                break;
            }
            var x = origin.X + point.X * scale;
            var y = origin.Y + point.Y * scale;
            if (x < -80 || y < -20 || x > ClientSize.Width + 80 || y > ClientSize.Height + 20)
            {
                continue;
            }
            var size = g.MeasureString(label.Text, font);
            var box = new RectangleF(x - size.Width / 2 - 3, y - size.Height / 2 - 2,
                size.Width + 6, size.Height + 4);
            if (placed.Any(other => other.IntersectsWith(box)))
            {
                continue;
            }
            placed.Add(box);
            var left = x - size.Width / 2;
            var top = y - size.Height / 2;
            foreach (var (dx, dy) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
            {
                g.DrawString(label.Text, font, halo, left + dx, top + dy);
            }
            g.DrawString(label.Text, font, ink, left, top);
        }
    }

    /// <summary>BTR 停车点：青色小方块 + 名字（只有森林和塔科夫街区有）。</summary>
    private void DrawBtrStops(Graphics g, float scale, PointF origin)
    {
        using var font = new Font("Microsoft YaHei UI", 7.5f);
        using var fill = new SolidBrush(Color.FromArgb(235, 90, 200, 210));
        using var ring = new Pen(Color.FromArgb(235, 15, 40, 45), 1.2f);
        using var ink = new SolidBrush(Color.FromArgb(235, 235, 250, 252));
        using var halo = new SolidBrush(Color.FromArgb(205, 0, 0, 0));
        foreach (var stop in BtrStops)
        {
            var x = origin.X + stop.Screen.X * scale;
            var y = origin.Y + stop.Screen.Y * scale;
            if (x < -20 || y < -20 || x > ClientSize.Width + 20 || y > ClientSize.Height + 20)
            {
                continue;
            }
            g.FillRectangle(fill, x - 4f, y - 4f, 8, 8);
            g.DrawRectangle(ring, x - 4f, y - 4f, 8, 8);
            var size = g.MeasureString(stop.Name, font);
            var tx = x + 7;
            var ty = y - size.Height / 2;
            if (tx + size.Width > ClientSize.Width)
            {
                tx = x - size.Width - 7;
            }
            g.FillRectangle(halo, tx - 1, ty, size.Width + 2, size.Height);
            g.DrawString(stop.Name, font, ink, tx, ty);
        }
    }

    private void DrawZones(Graphics g, float scale, PointF origin)
    {
        var log = Log;
        foreach (var zone in _projected)
        {
            var state = log?.StateOf(zone.TaskId) ?? TaskState.None;
            if (!IsZoneVisible(zone, state))
            {
                continue;
            }
            var dim = state is TaskState.Finished or TaskState.Failed;
            var color = ColorOf(state);
            using var path = new GraphicsPath();
            var pts = zone.Points.Select(p => new PointF(origin.X + p.X * scale, origin.Y + p.Y * scale)).ToArray();
            path.AddPolygon(pts);
            using var fill = new SolidBrush(Color.FromArgb(dim ? 26 : 56, color));
            g.FillPath(fill, path);
            using var stroke = new Pen(Color.FromArgb(dim ? 128 : 255, 255, 255), 1.6f);
            g.DrawPath(stroke, path);
            using var inner = new Pen(Color.FromArgb(dim ? 140 : 255, color), 1f);
            g.DrawPath(inner, path);
        }

        // 点位 + 连线。
        // 一个任务经常有好几个分散区域（"去这几个地方"类任务），只画点很难看出是一件事，
        // 所以同任务的多个"地方"之间用虚线连起来，并给每处标上序号。
        // 屏幕上挨得很近的区域（同一个房间里的 5 个 zone）先合并成一处，
        // 否则圆点和序号会叠成一团，什么也看不清。
        var currentFloor = CurrentFloor;
        using var dotPen = new Pen(Color.White, 2f);
        using var arrowBrush = new SolidBrush(Color.White);
        var visible = _projected
            .Where(z => IsZoneVisible(z, log?.StateOf(z.TaskId) ?? TaskState.None))
            .ToList();
        var groups = visible.GroupBy(z => z.TaskId).ToList();

        // 任务 → 合并后的若干"处"（屏幕坐标 + 代表楼层）
        var placesByTask = new Dictionary<string, List<(PointF Point, int Floor)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var places = new List<(PointF Point, int Floor, int Count)>();
            foreach (var zone in group)
            {
                var p = new PointF(origin.X + zone.Center.X * scale, origin.Y + zone.Center.Y * scale);
                var hit = -1;
                for (var i = 0; i < places.Count; i++)
                {
                    if (Math.Abs(places[i].Point.X - p.X) <= 22 && Math.Abs(places[i].Point.Y - p.Y) <= 22)
                    {
                        hit = i;
                        break;
                    }
                }
                if (hit < 0)
                {
                    places.Add((p, zone.Floor, 1));
                }
                else
                {
                    // 多点平均，合并后的标记落在它们中间
                    var (old, floor, count) = places[hit];
                    var merged = new PointF((old.X * count + p.X) / (count + 1), (old.Y * count + p.Y) / (count + 1));
                    places[hit] = (merged, floor, count + 1);
                }
            }
            placesByTask[group.Key] = places.Select(x => (x.Point, x.Floor)).ToList();
        }

        // 先画连线，压在多边形上面、点位下面
        foreach (var (taskId, places) in placesByTask)
        {
            if (places.Count < 2)
            {
                continue;
            }
            var color = ColorOf(log?.StateOf(taskId) ?? TaskState.None);
            using var link = new Pen(Color.FromArgb(205, color), 2.1f)
            {
                DashStyle = DashStyle.Custom,
                DashPattern = new[] { 6f, 4f },
            };
            g.DrawLines(link, places.Select(p => p.Point).ToArray());
        }

        using var numFont = new Font("Segoe UI", 6.5f, FontStyle.Bold);
        foreach (var (taskId, places) in placesByTask)
        {
            var state = log?.StateOf(taskId) ?? TaskState.None;
            var color = ColorOf(state);
            var multi = places.Count > 1;
            using var dotBrush = new SolidBrush(color);
            // 序号角标用深一点的颜色，压在圆点右上角，和箭头互不干扰
            using var badgeBrush = new SolidBrush(Color.FromArgb(255,
                Math.Max(0, color.R - 70), Math.Max(0, color.G - 50), Math.Max(0, color.B - 40)));
            using var badgePen = new Pen(Color.FromArgb(220, 12, 16, 20), 1f);
            for (var index = 0; index < places.Count; index++)
            {
                var (point, floor) = places[index];
                var x = point.X;
                var y = point.Y;
                const float radius = 7.5f;
                g.FillEllipse(dotBrush, x - radius, y - radius, radius * 2, radius * 2);
                g.DrawEllipse(dotPen, x - radius, y - radius, radius * 2, radius * 2);

                if (floor != currentFloor)
                {
                    // 目标在当前层上/下方：圆圈里画一个白色三角箭头
                    var up = floor > currentFloor;
                    var apex = up ? y - 4.6f : y + 4.6f;
                    var baseY = up ? y + 2.8f : y - 2.8f;
                    g.FillPolygon(arrowBrush, new[]
                    {
                        new PointF(x, apex),
                        new PointF(x - 3.8f, baseY),
                        new PointF(x + 3.8f, baseY),
                    });
                }

                if (multi)
                {
                    // 角标：右上角一个小圆 + 序号，箭头/普通点都能带上编号
                    const float badge = 6.5f;
                    var bx = x + 6.5f;
                    var by = y - 8.5f;
                    g.FillEllipse(badgeBrush, bx - badge, by - badge, badge * 2, badge * 2);
                    g.DrawEllipse(badgePen, bx - badge, by - badge, badge * 2, badge * 2);
                    TextRenderer.DrawText(g, (index + 1).ToString(), numFont,
                        new Rectangle((int)bx - 6, (int)by - 7, 12, 14), Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
            }
        }

        // 标签：每个任务一个，字号固定为屏幕像素；重叠的就让位，
        // 优先显示进行中的任务。多区域的任务在名字后面标出总共有几处。
        using var label = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
        using var textBrush = new SolidBrush(Color.White);
        using var halo = new SolidBrush(Color.FromArgb(220, 0, 0, 0));
        var placed = new List<RectangleF>();
        var ordered = groups
            .OrderByDescending(g => log?.StateOf(g.Key) == TaskState.Started)
            .ThenByDescending(g => placesByTask.TryGetValue(g.Key, out var pl) ? pl.Count : 0)
            .ToList();
        foreach (var group in ordered)
        {
            var zone = group.First();
            var state = log?.StateOf(zone.TaskId) ?? TaskState.None;
            if (!IsZoneVisible(zone, state))
            {
                continue;
            }
            var places = placesByTask.TryGetValue(zone.TaskId, out var list) && list.Count > 0
                ? list
                : new List<(PointF Point, int Floor)> { (new PointF(origin.X + zone.Center.X * scale, origin.Y + zone.Center.Y * scale), zone.Floor) };
            var text = places.Count > 1 ? $"{zone.TaskName}（{places.Count} 处）" : zone.TaskName;
            var x = places[0].Point.X + 10;
            var y = places[0].Point.Y - 8;
            var size = g.MeasureString(text, label);
            var box = new RectangleF(x - 2, y - 2, size.Width + 4, size.Height + 4);
            if (!box.IntersectsWith(new RectangleF(0, 0, ClientSize.Width, ClientSize.Height)))
            {
                continue;
            }
            var blocked = placed.Any(other => other.IntersectsWith(box));
            if (blocked)
            {
                continue;
            }
            placed.Add(box);
            foreach (var (dx, dy) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
            {
                g.DrawString(text, label, halo, x + dx, y + dy);
            }
            g.DrawString(text, label, textBrush, x, y);
        }
    }

    /// <summary>任务状态对应的颜色，画区域、点位、连线都共用。</summary>
    private static Color ColorOf(TaskState state) => state switch
    {
        TaskState.Started => Color.FromArgb(76, 154, 255),
        TaskState.Finished => Color.FromArgb(120, 200, 140),
        TaskState.Failed => Color.FromArgb(220, 110, 110),
        _ => Color.FromArgb(150, 150, 160),
    };

    private void DrawFloors(Graphics g)
    {
        if (_map == null || _map.Layers.Count == 0)
        {
            return;
        }
        var entries = new List<(string Key, string Label)> { ("", "地面") };
        foreach (var layer in _map.Layers)
        {
            if (!string.IsNullOrWhiteSpace(layer.SvgLayer))
            {
                entries.Add((layer.SvgLayer, FloorLabel(layer.Name)));
            }
        }
        var y = 10;
        using var font = new Font("Microsoft YaHei UI", 8.5f);
        foreach (var (key, label) in entries)
        {
            var rect = new Rectangle(10, y, 84, 24);
            var active = string.Equals(key, _layer, StringComparison.OrdinalIgnoreCase);
            using var back = new SolidBrush(active ? Color.FromArgb(46, 96, 170) : Color.FromArgb(34, 39, 46));
            using var border = new Pen(Color.FromArgb(74, 82, 92));
            g.FillRectangle(back, rect);
            g.DrawRectangle(border, rect);
            TextRenderer.DrawText(g, label, font, rect, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            y += 28;
        }
    }

    /// <summary>
    /// 玩家位置：青色圆点 + 指向朝向的三角。只有「截图所属地图」和当前显示的地图
    /// 一致时才画——玩家手动切到别的地图时，位置就不会被错误地标上去。
    /// </summary>
    private void DrawPlayer(Graphics g, float scale, PointF origin)
    {
        if (_map == null || PlayerPosition is not { } position)
        {
            return;
        }
        if (!string.Equals(PlayerMapKey, _map.Key, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        var x = origin.X + position.X * scale;
        var y = origin.Y + position.Y * scale;
        if (x < -30 || y < -30 || x > ClientSize.Width + 30 || y > ClientSize.Height + 30)
        {
            return;
        }
        var forward = PlayerForward ?? new PointF(0, -1);
        var length = (float)Math.Sqrt(forward.X * forward.X + forward.Y * forward.Y);
        if (length > 1e-4f)
        {
            forward = new PointF(forward.X / length, forward.Y / length);
        }
        else
        {
            forward = new PointF(0, -1);
        }

        // 朝向三角
        var tipX = x + forward.X * 17;
        var tipY = y + forward.Y * 17;
        var leftX = x + (-forward.Y) * 6 + forward.X * 4;
        var leftY = y + forward.X * 6 + forward.Y * 4;
        var rightX = x + forward.Y * 6 + forward.X * 4;
        var rightY = y + (-forward.X) * 6 + forward.Y * 4;
        using var arrow = new SolidBrush(Color.FromArgb(230, 90, 240, 255));
        g.FillPolygon(arrow, new[]
        {
            new PointF(tipX, tipY),
            new PointF(leftX, leftY),
            new PointF(rightX, rightY),
        });

        // 位置点
        using var fill = new SolidBrush(Color.FromArgb(240, 40, 210, 235));
        using var ring = new Pen(Color.FromArgb(235, 255, 255, 255), 2f);
        g.FillEllipse(fill, x - 7, y - 7, 14, 14);
        g.DrawEllipse(ring, x - 7, y - 7, 14, 14);
    }

    /// <summary>候选刷新点：小琥珀色圆点，带白边，方便和任务区域区分。</summary>
    private void DrawSpawnPoints(Graphics g, float scale, PointF origin)
    {
        using var fill = new SolidBrush(Color.FromArgb(235, 255, 196, 64));
        using var ring = new Pen(Color.FromArgb(200, 40, 32, 8), 1.2f);
        foreach (var point in SpawnPoints)
        {
            var x = origin.X + point.X * scale;
            var y = origin.Y + point.Y * scale;
            if (x < -8 || y < -8 || x > ClientSize.Width + 8 || y > ClientSize.Height + 8)
            {
                continue;
            }
            g.FillEllipse(fill, x - 3.5f, y - 3.5f, 7, 7);
            g.DrawEllipse(ring, x - 3.5f, y - 3.5f, 7, 7);
        }
    }

    /// <summary>跑到视口外的任务：在边缘画一个指向它的箭头和名字。</summary>
    private void DrawOffscreenArrows(Graphics g, float scale, PointF origin)
    {
        var log = Log;
        var inset = 30f;
        var view = new RectangleF(0, 0, ClientSize.Width, ClientSize.Height);
        var inner = RectangleF.Inflate(view, -inset, -inset);
        var centre = new PointF(view.Width / 2, view.Height / 2);
        using var font = new Font("Microsoft YaHei UI", 8f);
        using var back = new SolidBrush(Color.FromArgb(150, 30, 40, 55));
        using var ring = new Pen(Color.White, 1.4f);

        foreach (var zone in _projected.GroupBy(z => z.TaskId).Select(g2 => g2.First()))
        {
            var state = log?.StateOf(zone.TaskId) ?? TaskState.None;
            if (!IsZoneVisible(zone, state))
            {
                continue;
            }
            var x = origin.X + zone.Center.X * scale;
            var y = origin.Y + zone.Center.Y * scale;
            if (view.Contains(x, y))
            {
                continue;
            }
            var dx = x - centre.X;
            var dy = y - centre.Y;
            if (Math.Abs(dx) < 1e-3 && Math.Abs(dy) < 1e-3)
            {
                continue;
            }
            var factor = (float)Math.Min(
                Math.Abs(inner.Width / 2 / (dx == 0 ? 1e-4 : dx)),
                Math.Abs(inner.Height / 2 / (dy == 0 ? 1e-4 : dy)));
            var ax = Math.Clamp(centre.X + dx * factor, inset, view.Width - inset);
            var ay = Math.Clamp(centre.Y + dy * factor, inset, view.Height - inset);

            var angle = (float)(Math.Atan2(dy, dx) * 180 / Math.PI);
            g.FillEllipse(back, ax - 10, ay - 10, 20, 20);
            g.DrawEllipse(ring, ax - 10, ay - 10, 20, 20);
            var state2 = g.Save();
            g.TranslateTransform(ax, ay);
            g.RotateTransform(angle);
            using var arrowBrush = new SolidBrush(Color.FromArgb(220, 120, 180, 255));
            g.FillPolygon(arrowBrush, new[]
            {
                new PointF(6, 0),
                new PointF(-4, -5),
                new PointF(-4, 5),
            });
            g.Restore(state2);

            var size = g.MeasureString(zone.TaskName, font);
            var tx = Math.Clamp(ax + 14, 2f, view.Width - size.Width - 4);
            var ty = Math.Clamp(ay - size.Height / 2, 2f, view.Height - size.Height - 2);
            using var labelBack = new SolidBrush(Color.FromArgb(170, 10, 14, 20));
            g.FillRectangle(labelBack, tx - 2, ty, size.Width + 4, size.Height);
            g.DrawString(zone.TaskName, font, Brushes.White, tx, ty);
        }
    }

    /// <summary>当前浏览的楼层（0 地面，1 二层，-1 地下）。</summary>
    public int CurrentFloor
    {
        get
        {
            if (_map == null || string.IsNullOrEmpty(_layer))
            {
                return 0;
            }
            var layer = _map.Layers.FirstOrDefault(l =>
                string.Equals(l.SvgLayer, _layer, StringComparison.OrdinalIgnoreCase));
            return layer == null ? 0 : FloorOfLayerName(layer.Name);
        }
    }

    /// <summary>把楼层名（2nd Floor / Underground / Bunkers…）转成楼层号。</summary>
    public static int FloorOfLayerName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return 0;
        }
        var match = System.Text.RegularExpressions.Regex.Match(name, @"^(\d)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var n) && n >= 1)
        {
            return n - 1;
        }
        if (name.Contains("Underground", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Bunker", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Basement", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Tunnel", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Garage", StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }
        return 0;
    }

    /// <summary>切到指定楼层；该层不存在时返回 false。</summary>
    public bool ShowFloor(int floor)
    {
        if (_map == null)
        {
            return false;
        }
        if (floor <= 0 && floor != -1)
        {
            SetLayer("");
            return true;
        }
        var layer = _map.Layers.FirstOrDefault(l =>
            !string.IsNullOrWhiteSpace(l.SvgLayer) && FloorOfLayerName(l.Name) == floor);
        if (layer == null)
        {
            return false;
        }
        SetLayer(layer.SvgLayer);
        return true;
    }

    public static string FloorLabel(string name) => name switch
    {
        "2nd Floor" => "2 层",
        "3rd Floor" => "3 层",
        "4th Floor" => "4 层",
        "5th Floor" => "5 层",
        "Underground" => "地下",
        "Bunkers" => "地堡",
        "Tunnels" => "隧道",
        "Garage" => "车库",
        "Ground Level" => "地面",
        _ => name,
    };

    /// <summary>
    /// 左下角的参考坐标轴：把世界 +X / +Z 两个方向投影到当前这张图上，
    /// 于是不管地图旋转了 90/180/270 度，都能看出坐标往哪边长。
    /// 多人游玩报坐标全靠它。
    /// </summary>
    private void DrawAxes(Graphics g)
    {
        if (_map == null)
        {
            return;
        }
        var origin = MapPack.ToImage(_map, 0, 0);
        var plusX = MapPack.ToImage(_map, 10, 0);
        var plusZ = MapPack.ToImage(_map, 0, 10);
        var dx = new PointF((float)(plusX.X - origin.X), (float)(plusX.Y - origin.Y));
        var dz = new PointF((float)(plusZ.X - origin.X), (float)(plusZ.Y - origin.Y));
        if (Math.Abs(dx.X) + Math.Abs(dx.Y) < 1e-6 || Math.Abs(dz.X) + Math.Abs(dz.Y) < 1e-6)
        {
            return;
        }

        // 无底板：只在箭头下面描一层深色，保证在地图上也看得清，
        // 但不占一块方块，免得盖住地图内容
        var centre = new PointF(28, ClientSize.Height - 76);
        const float length = 16f;
        var xColor = Color.FromArgb(245, 240, 112, 98);
        var zColor = Color.FromArgb(245, 112, 180, 244);
        using var shadow = new Pen(Color.FromArgb(170, 0, 0, 0), 4.4f)
        {
            EndCap = LineCap.ArrowAnchor,
            LineJoin = LineJoin.Round,
        };
        using var xPen = new Pen(xColor, 1.8f) { EndCap = LineCap.ArrowAnchor };
        using var zPen = new Pen(zColor, 1.8f) { EndCap = LineCap.ArrowAnchor };
        using var font = new Font("Segoe UI", 7f, FontStyle.Bold);

        void Arrow(PointF direction, Pen pen, Color color, string label)
        {
            var norm = (float)Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
            if (norm < 1e-6f)
            {
                return;
            }
            var ux = direction.X / norm;
            var uy = direction.Y / norm;
            var tip = new PointF(centre.X + ux * length, centre.Y + uy * length);
            g.DrawLine(shadow, centre, tip);
            g.DrawLine(pen, centre, tip);
            using var brush = new SolidBrush(color);
            using var halo = new SolidBrush(Color.FromArgb(190, 0, 0, 0));
            var size = g.MeasureString(label, font);
            var tx = tip.X + ux * 6 - size.Width / 2;
            var ty = tip.Y + uy * 6 - size.Height / 2;
            foreach (var (ox, oy) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
            {
                g.DrawString(label, font, halo, tx + ox, ty + oy);
            }
            g.DrawString(label, font, brush, tx, ty);
        }

        Arrow(dx, xPen, xColor, "X");
        Arrow(dz, zPen, zColor, "Z");
    }


    private void DrawZoom(Graphics g)
    {
        using var font = new Font("Consolas", 9f);
        var floorName = string.IsNullOrEmpty(_layer) ? "地面" : FloorLabel(
            _map?.Layers.FirstOrDefault(l => string.Equals(l.SvgLayer, _layer, StringComparison.OrdinalIgnoreCase))?.Name ?? "");
        var text = $"{_zoom * _fitScale * 100:F0}%   {floorName}";
        TextRenderer.DrawText(g, text, font,
            new Rectangle(10, ClientSize.Height - 26, 160, 20), Color.FromArgb(180, 200, 220));
    }


    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ZoomBy(e.Delta > 0 ? 1.15 : 1 / 1.15, e.Location);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && e.Y < 10 + (FloorCount() * 28) && e.X < 76)
        {
            var index = (e.Y - 10) / 28;
            var entries = new List<string> { "" };
            entries.AddRange(_map?.Layers.Where(l => !string.IsNullOrWhiteSpace(l.SvgLayer))
                .Select(l => l.SvgLayer) ?? Enumerable.Empty<string>());
            if (index >= 0 && index < entries.Count)
            {
                FloorRequested?.Invoke(entries[index]);
                return;
            }
        }
        if (e.Button is MouseButtons.Left or MouseButtons.Middle)
        {
            _dragging = true;
            _dragStart = e.Location;
            _panStart = _pan;
            Cursor = Cursors.SizeAll;
        }
    }

    private int FloorCount() =>
        1 + (_map?.Layers.Count(l => !string.IsNullOrWhiteSpace(l.SvgLayer)) ?? 0);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging)
        {
            return;
        }
        _pan = new PointF(_panStart.X + (e.X - _dragStart.X), _panStart.Y + (e.Y - _dragStart.Y));
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Cursor = Cursors.Default;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        Fit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var image in _images.Values)
            {
                image.Dispose();
            }
            _images.Clear();
            _imageBytes.Clear();
            _imageOrder.Clear();
        }
        base.Dispose(disposing);
    }
}
