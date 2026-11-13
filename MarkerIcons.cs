using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;

namespace TarkovMapLite;

/// <summary>菜单和地图共用 24 单位矢量轮廓；地图图标缓存成小位图，避免每个点重新造路径。</summary>
internal static class MarkerIcons
{
    private static readonly Dictionary<LayerIcon, GraphicsPath> Paths = Load();
    private readonly record struct SpriteKey(LayerIcon Icon, int Color, int Size, int Alpha, bool Halo);
    private static readonly Dictionary<SpriteKey, Bitmap> Sprites = new();
    private static readonly Queue<SpriteKey> Order = new();
    private const int CacheLimit = 512;
    private const long CacheBytes = 4L * 1024 * 1024;
    private static long _spriteBytes;

    private static Dictionary<LayerIcon, GraphicsPath> Load()
    {
        using var doc = JsonDocument.Parse(Assets.ReadText("mobile/icons.json")!);
        var result = new Dictionary<LayerIcon, GraphicsPath>();
        foreach (var icon in Enum.GetValues<LayerIcon>())
        {
            var data = doc.RootElement.GetProperty(icon.ToString());
            var path = new GraphicsPath();
            if (data.TryGetProperty("lines", out var lines))
                foreach (var line in lines.EnumerateArray())
                {
                    var values = line.EnumerateArray().Select(n=>n.GetSingle()).ToArray();
                    var points = Enumerable.Range(0,values.Length/2).Select(i=>new PointF(values[i*2],values[i*2+1])).ToArray();
                    path.StartFigure(); path.AddLines(points);
                    if (points[0] == points[^1]) path.CloseFigure();
                }
            foreach (var key in new[] { "rects", "circles", "ellipses" })
                if (data.TryGetProperty(key,out var shapes))
                    foreach (var shape in shapes.EnumerateArray())
                    {
                        var v = shape.EnumerateArray().Select(n=>n.GetSingle()).ToArray();
                        path.StartFigure();
                        if (key == "rects") path.AddRectangle(new RectangleF(v[0],v[1],v[2],v[3]));
                        else if (key == "circles") path.AddEllipse(v[0]-v[2],v[1]-v[2],v[2]*2,v[2]*2);
                        else path.AddEllipse(v[0],v[1],v[2],v[3]);
                    }
            result.Add(icon,path);
        }
        return result;
    }

    public static LayerIcon Loot(int bit) => bit switch
    {
        LootKinds.Keys => LayerIcon.Key, LootKinds.Meds => LayerIcon.Meds,
        LootKinds.Food => LayerIcon.Food, LootKinds.Poster => LayerIcon.Poster,
        LootKinds.Storage => LayerIcon.Storage, LootKinds.Junk => LayerIcon.Junk,
        _ => LayerIcon.Docs,
    };

    public static LayerIcon Container(string slug) => slug switch
    {
        "weapon-box" => LayerIcon.Weapon, "wooden-ammo-box" => LayerIcon.Ammo,
        "grenade-box" => LayerIcon.Grenade, "toolbox" or "technical-supply-crate" => LayerIcon.Toolbox,
        "ration-supply-crate" => LayerIcon.Food, "buried-barrel-cache" => LayerIcon.Barrel,
        "ground-cache" or "shturmans-stash" => LayerIcon.Cache,
        "cash-register" or "bank-cash-register" => LayerIcon.Register,
        _ => ContainerKinds.Resolve(slug).Glyph switch
        {
            ContainerKinds.Icon.Bag => LayerIcon.Bag, ContainerKinds.Icon.Drawer => LayerIcon.Drawer,
            ContainerKinds.Icon.Hanger => LayerIcon.Hanger, ContainerKinds.Icon.Dial => LayerIcon.Safe,
            ContainerKinds.Icon.Monitor => LayerIcon.Computer, ContainerKinds.Icon.Cross => LayerIcon.MedicalBag,
            ContainerKinds.Icon.Body => LayerIcon.Body, _ => LayerIcon.Crate,
        }
    };

    public static Bitmap Create(LayerIcon icon, Color color, int size = 20, bool halo = false, float opacity = 1)
    {
        size = Math.Clamp(size,8,128);
        using var large = new Bitmap(size*3,size*3,PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(large))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // 1 单位外侧留白，避免尖角与光晕在图标边界被裁掉。
            g.TranslateTransform(size*.25f,size*.25f);
            g.ScaleTransform(size*2.5f/24,size*2.5f/24);
            var width = Math.Max(2.1f,24f/size*1.05f);
            if (halo)
            {
                using var outline = new Pen(UiTokens.IconOutline,width+24f/size*1.5f) { LineJoin=LineJoin.Round,StartCap=LineCap.Round,EndCap=LineCap.Round };
                g.DrawPath(outline,Paths[icon]);
            }
            using var pen = new Pen(color,width) { LineJoin=LineJoin.Round,StartCap=LineCap.Round,EndCap=LineCap.Round };
            g.DrawPath(pen,Paths[icon]);
        }
        var result = new Bitmap(size,size,PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(result))
        using (var attributes = new ImageAttributes())
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            attributes.SetColorMatrix(new ColorMatrix { Matrix33 = Math.Clamp(opacity,0,1) });
            g.DrawImage(large,new Rectangle(0,0,size,size),0,0,large.Width,large.Height,GraphicsUnit.Pixel,attributes);
        }
        return result;
    }

    public static void Draw(Graphics g, LayerIcon icon, PointF point, float size, Color color, float opacity = 1)
    {
        var pixels = Math.Clamp((int)Math.Round(size*g.DpiX/96f),8,96);
        var alpha = Math.Clamp((int)Math.Round(opacity*255),0,255);
        var key = new SpriteKey(icon,color.ToArgb(),pixels,alpha,true);
        // UI 绘制单线程使用；图标数量有硬上限，切换地图不会无限积累 GDI 对象。
        if (!Sprites.TryGetValue(key,out var sprite))
        {
            sprite = Create(icon,color,pixels,true,alpha/255f);
            Sprites.Add(key,sprite); Order.Enqueue(key);
            _spriteBytes += pixels*pixels*4L;
            while (Sprites.Count > CacheLimit || _spriteBytes > CacheBytes)
                if (Sprites.Remove(Order.Dequeue(),out var victim))
                {
                    _spriteBytes -= victim.Width*victim.Height*4L;
                    victim.Dispose();
                }
        }
        g.DrawImageUnscaled(sprite,(int)Math.Round(point.X-pixels/2f),(int)Math.Round(point.Y-pixels/2f));
    }

}
