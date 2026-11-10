// ============================================================================
// LayerMenuArt.cs — 图层面板每一项的小图标（GDI+ 现画，不带图片资源）
//
// 为什么要有图标：菜单以前是"给文字上色"来区分类别，彩色文字压在深色底上、
// 再叠悬浮底色，很容易糊成一片看不清。现在**文字颜色固定**，分类信息交给图标：
// 颜色只出现在 14×14 的图标上，文字永远是 UI-SPEC 里的 TextMain / Accent。
//
// 所有图标按 16×16 画（留 2px 边距），用同一套线宽 1.6f，视觉重量一致。
// 加新图层：在 LayerIcon 里加一项，在 Draw 里加一个 case，然后 Add(...) 里传进去。
// ============================================================================
using System.Drawing.Drawing2D;

namespace TarkovMapLite;

internal enum LayerIcon
{
    Label,       // 地名
    Btr,         // BTR 站点
    Loot,        // 散落物资（父项）
    Dot,         // 子项的纯色圆点（物资分类、容器分类）
    ClearMarks,  // 清空已完成标记
    Spawn,       // 出生点
    Hazard,      // 危险区
    Weapon,      // 固定武器
    Door,        // 门锁
    Switch,      // 闸点
    Container,   // 容器（父项）
}

internal static class LayerMenuArt
{
    private const int Size = 16;

    /// <summary>生成一个 16×16 图标。<paramref name="color"/> 就是这个类别的颜色。</summary>
    public static Bitmap Create(LayerIcon kind, Color color)
    {
        var bmp = new Bitmap(Size, Size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var pen = new Pen(color, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(color);

        switch (kind)
        {
            case LayerIcon.Dot:
                g.FillEllipse(brush, 5f, 5f, 6f, 6f);
                break;

            case LayerIcon.Label:
                // 图钉：圆头 + 尖脚
                g.DrawEllipse(pen, 4.5f, 3f, 7f, 7f);
                g.DrawLine(pen, 8f, 10.2f, 8f, 13.2f);
                break;

            case LayerIcon.Btr:
                // 巴士：车身 + 两个轮子
                g.DrawRectangle(pen, 2.6f, 4.4f, 10.8f, 6.4f);
                g.FillEllipse(brush, 4.6f, 10.4f, 2.4f, 2.4f);
                g.FillEllipse(brush, 9f, 10.4f, 2.4f, 2.4f);
                g.DrawLine(pen, 7.4f, 4.6f, 7.4f, 10.6f);
                break;

            case LayerIcon.Loot:
                // 三颗散落的点
                g.FillEllipse(brush, 2.4f, 6.8f, 3.4f, 3.4f);
                g.FillEllipse(brush, 8.2f, 4.2f, 3.4f, 3.4f);
                g.FillEllipse(brush, 7.4f, 9.4f, 3.4f, 3.4f);
                break;

            case LayerIcon.ClearMarks:
                // 橡皮：斜方块 + 一条底线
                g.DrawLine(pen, 4.4f, 11.4f, 10.6f, 5.2f);
                g.DrawLine(pen, 6.6f, 13.2f, 12.8f, 7f);
                g.DrawLine(pen, 4.4f, 11.4f, 6.6f, 13.2f);
                g.DrawLine(pen, 10.6f, 5.2f, 12.8f, 7f);
                g.DrawLine(pen, 3.2f, 13.6f, 12.8f, 13.6f);
                break;

            case LayerIcon.Spawn:
                // 人：头 + 肩
                g.DrawEllipse(pen, 6f, 2.6f, 4f, 4f);
                g.DrawArc(pen, 3.2f, 7.4f, 9.6f, 9f, 190f, 160f);
                break;

            case LayerIcon.Hazard:
                // 三角警告 + 感叹号
                g.DrawPolygon(pen, new[]
                {
                    new PointF(8f, 2.6f), new PointF(14f, 13f), new PointF(2f, 13f),
                });
                g.DrawLine(pen, 8f, 6.4f, 8f, 9.4f);
                g.FillEllipse(brush, 7.4f, 10.6f, 1.4f, 1.4f);
                break;

            case LayerIcon.Weapon:
                // 准星：圆 + 四向短线
                g.DrawEllipse(pen, 4.2f, 4.2f, 7.6f, 7.6f);
                g.DrawLine(pen, 8f, 1.8f, 8f, 3.6f);
                g.DrawLine(pen, 8f, 12.4f, 8f, 14.2f);
                g.DrawLine(pen, 1.8f, 8f, 3.6f, 8f);
                g.DrawLine(pen, 12.4f, 8f, 14.2f, 8f);
                break;

            case LayerIcon.Door:
                // 钥匙：环 + 杆 + 齿
                g.DrawEllipse(pen, 2.4f, 5.4f, 5.6f, 5.6f);
                g.DrawLine(pen, 7.6f, 8.2f, 13.4f, 8.2f);
                g.DrawLine(pen, 10.6f, 8.2f, 10.6f, 11f);
                g.DrawLine(pen, 12.8f, 8.2f, 12.8f, 10.6f);
                break;

            case LayerIcon.Switch:
                // 闪电
                g.FillPolygon(brush, new[]
                {
                    new PointF(9.6f, 1.8f), new PointF(4.4f, 8.6f), new PointF(7.6f, 8.6f),
                    new PointF(6.4f, 14.2f), new PointF(11.8f, 6.8f), new PointF(8.4f, 6.8f),
                });
                break;

            case LayerIcon.Container:
                // 箱子：方框 + 对角线
                g.DrawRectangle(pen, 2.6f, 3.6f, 10.8f, 8.8f);
                g.DrawLine(pen, 2.8f, 12.2f, 13.2f, 3.8f);
                g.DrawLine(pen, 8f, 3.8f, 8f, 12.2f);
                break;
        }

        return bmp;
    }
}
