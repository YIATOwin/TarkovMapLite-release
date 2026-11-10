// ============================================================================
// UiButton.cs — 顶栏按钮 / 下拉框的自绘控件
//
// 为什么不用系统 Button：WinForms 的 Button 画不了圆角，ComboBox 更是自带一条
// 白色边框（FlatStyle 也去不掉），在深色顶栏上特别扎眼。这里统一自绘：
//
//  · 圆角固定 UiTokens.RadiusControl(7px)，和胶囊、菜单保持同一套观感，
//    不会出现"方不方圆不圆"的混搭
//  · **边框颜色 = 内部底色**：方块靠"比父容器亮一档"来区分，不靠描边，
//    所以深色底上看不到白线
//  · 悬浮/按下只改底色（亮一档 / 暗一档），不加边框
//  · 带箭头时（下拉框）在右侧画一个小 V 形
//  · Active（激活态）用低饱和沙色底 + 沙色字，属于"状态"不是装饰
// ============================================================================
namespace TarkovMapLite;

internal sealed class UiButton : Control
{
    private bool _hover;
    private bool _pressed;
    private bool _active;

    public UiButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        TabStop = false;
        Font = new Font("Microsoft YaHei UI", UiTokens.FontBody);
        ForeColor = UiTokens.TextMain;
        Height = 28;
    }

    /// <summary>右侧要不要画下拉箭头。</summary>
    public bool ShowArrow { get; set; }

    /// <summary>激活态（对应"这个层开着"这类状态），不是普通按钮的默认外观。</summary>
    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value)
            {
                return;
            }
            _active = value;
            Invalidate();
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);

        var background = BackgroundColor();
        using (var path = UiTokens.RoundedRect(rect, UiTokens.RadiusControl))
        {
            using (var fill = new SolidBrush(background))
            {
                g.FillPath(fill, path);
            }
            // 边框色和底色一致：只为了让抗锯齿边缘干净，不制造对比线
            using var edge = new Pen(background);
            g.DrawPath(edge, path);
        }

        var arrowSpace = ShowArrow ? 16 : 0;
        var textRect = new Rectangle(11, 0, Math.Max(4, Width - 11 - 9 - arrowSpace), Height);
        var color = !Enabled ? UiTokens.TextFaint
            : Active ? UiTokens.Accent
            : UiTokens.TextMain;
        TextRenderer.DrawText(g, Text, Font, textRect, color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix
            | TextFormatFlags.EndEllipsis);

        if (ShowArrow)
        {
            var cx = Width - 13;
            var cy = Height / 2f - 1;
            using var pen = new Pen(Active ? UiTokens.Accent : UiTokens.TextDim, 1.6f);
            g.DrawLines(pen, new[]
            {
                new PointF(cx - 3.5f, cy - 1.5f),
                new PointF(cx, cy + 2f),
                new PointF(cx + 3.5f, cy - 1.5f),
            });
        }
    }

    /// <summary>底色：按下 &lt; 悬浮 &lt; 激活 &lt; 常态 的优先级。</summary>
    /// <summary>底色优先级：按下 < 悬浮 < 激活 < 常态。</summary>
    private Color BackgroundColor()
    {
        if (_active)
        {
            return _pressed ? Blend(UiTokens.AccentTint, Color.Black, 0.18f)
                : _hover ? Blend(UiTokens.AccentTint, Color.White, 0.06f)
                : UiTokens.AccentTint;
        }
        if (_pressed)
        {
            return Blend(UiTokens.ButtonBack, Color.Black, 0.18f);
        }
        return _hover ? UiTokens.ButtonHover : UiTokens.ButtonBack;
    }

    private static Color Blend(Color under, Color over, float amount)
    {
        var a = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            255,
            (int)(under.R + (over.R - under.R) * a),
            (int)(under.G + (over.G - under.G) * a),
            (int)(under.B + (over.B - under.B) * a));
    }
}
