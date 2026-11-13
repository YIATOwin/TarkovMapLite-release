// 深色菜单：图标和勾选分别占列，类别靠形状+颜色，开关靠勾选与半选横线。
// 关闭项仍保留 82% 的图标亮度，避免与真正禁用项混淆；命令不按开关状态压暗。
namespace TarkovMapLite;

internal sealed class LayerMenuRenderer : ToolStripProfessionalRenderer
{
    public LayerMenuRenderer()
        : base(new DarkMenuColors())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(UiTokens.Raised);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(UiTokens.Line);
        var rect = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        e.Graphics.DrawRectangle(pen, rect);
    }

    /// <summary>左侧图标区：默认会刷成白色渐变，必须干掉。</summary>
    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(UiTokens.Raised);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var item = e.Item;
        var state = StateOf(item);
        if (!item.Selected && state == CheckState.Unchecked)
        {
            // 常态：铺回面板色，擦掉上一次留下的描边/底色
            using var flat = new SolidBrush(UiTokens.Raised);
            e.Graphics.FillRectangle(flat, new Rectangle(Point.Empty, item.Size));
            return;
        }

        var rect = new Rectangle(2, 1, item.Width - 5, item.Height - 3);
        if (state == CheckState.Checked)
        {
            using var fill = new SolidBrush(UiTokens.AccentSelection);
            e.Graphics.FillRectangle(fill, rect);
        }
        else if (state == CheckState.Indeterminate)
        {
            using var fill = new SolidBrush(Color.FromArgb(34, UiTokens.Warn));
            using var solid = new SolidBrush(Blend(UiTokens.Raised, UiTokens.Warn, 0.14f));
            e.Graphics.FillRectangle(solid, rect);
        }
        else
        {
            using var flat = new SolidBrush(UiTokens.Raised);
            e.Graphics.FillRectangle(flat, rect);
        }

        if (item.Selected)
        {
            // 悬浮只描边，不改底色
            using var pen = new Pen(UiTokens.Accent, 1f);
            e.Graphics.DrawRectangle(pen, rect);
        }
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        if (!e.Item.Enabled)
        {
            e.TextColor = UiTokens.TextFaint;
        }
        else if (StateOf(e.Item) == CheckState.Checked)
        {
            e.TextColor = UiTokens.Accent;
        }
        else if (StateOf(e.Item) == CheckState.Indeterminate)
        {
            e.TextColor = UiTokens.Warn;
        }
        else
        {
            e.TextColor = UiTokens.TextMain;
        }
        // 自己画文字：交给 base 时会被 item.ForeColor 覆盖，勾选的强调色就丢了
        TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont ?? e.Item.Font, e.TextRectangle,
            e.TextColor, e.TextFormat);
    }

    /// <summary>独立勾选列中绘制勾和半选横线，保持类别图标完整。</summary>
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        if (e.Item.Tag is not LayerIconInfo { Toggle: true } || StateOf(e.Item) == CheckState.Unchecked) return;
        var r = e.ImageRectangle;
        var cx = r.Left + r.Width/2f;
        var cy = r.Top + r.Height/2f;
        var scale = e.Graphics.DpiX/96f;
        using var pen = new Pen(StateOf(e.Item) == CheckState.Indeterminate ? UiTokens.Warn : UiTokens.Accent,1.8f*scale)
        { StartCap=System.Drawing.Drawing2D.LineCap.Round,EndCap=System.Drawing.Drawing2D.LineCap.Round,LineJoin=System.Drawing.Drawing2D.LineJoin.Round };
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        if (StateOf(e.Item) == CheckState.Indeterminate) e.Graphics.DrawLine(pen,cx-4*scale,cy,cx+4*scale,cy);
        else e.Graphics.DrawLines(pen,new[] { new PointF(cx-4*scale,cy),new PointF(cx-scale,cy+3*scale),new PointF(cx+5*scale,cy-4*scale) });
    }

    /// <summary>未勾选的项把图标压淡，表示"这一层现在是关的"。</summary>
    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        var image = e.Image;
        if (image == null)
        {
            return;
        }
        var alpha = !e.Item.Enabled ? .4f : e.Item.Tag is LayerIconInfo { Toggle: false } ? 1f
            : StateOf(e.Item) is CheckState.Checked or CheckState.Indeterminate ? 1f : .82f;
        var bounds = e.ImageRectangle;
        if (alpha >= 0.99f)
        {
            e.Graphics.DrawImage(image, bounds);
            return;
        }
        using var attrs = new System.Drawing.Imaging.ImageAttributes();
        var matrix = new System.Drawing.Imaging.ColorMatrix { Matrix33 = alpha };
        attrs.SetColorMatrix(matrix, System.Drawing.Imaging.ColorMatrixFlag.Default,
            System.Drawing.Imaging.ColorAdjustType.Bitmap);
        e.Graphics.DrawImage(image, bounds, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attrs);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        // 子菜单箭头：常态暗灰，悬浮/展开时用强调色
        e.ArrowColor = e.Item is { Selected: true } ? UiTokens.Accent : UiTokens.TextDim;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Height / 2;
        using var pen = new Pen(UiTokens.LineSubtle);
        e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
    }

    /// <summary>只有菜单项才有勾选状态，普通 ToolStripItem 一律当未勾选。</summary>
    private static CheckState StateOf(ToolStripItem item)
        => item is ToolStripMenuItem menu ? menu.CheckState : CheckState.Unchecked;

    private static Color Blend(Color under, Color over, float amount)
        => Color.FromArgb(
            255,
            (int)(under.R + (over.R - under.R) * amount),
            (int)(under.G + (over.G - under.G) * amount),
            (int)(under.B + (over.B - under.B) * amount));

    /// <summary>
    /// ProfessionalRenderer 的颜色表。整体压成深色，避免系统蓝色漏出来；
    /// 具体每一行怎么画已经被上面的重写接管了，这里主要管展开的子菜单。
    /// </summary>
    private sealed class DarkMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => UiTokens.Raised;
        public override Color ImageMarginGradientBegin => UiTokens.Raised;
        public override Color ImageMarginGradientMiddle => UiTokens.Raised;
        public override Color ImageMarginGradientEnd => UiTokens.Raised;
        public override Color MenuBorder => UiTokens.Line;
        public override Color MenuItemBorder => UiTokens.Accent;
        public override Color MenuItemSelected => UiTokens.Raised;
        public override Color MenuItemSelectedGradientBegin => UiTokens.Raised;
        public override Color MenuItemSelectedGradientEnd => UiTokens.Raised;
        public override Color MenuItemPressedGradientBegin => UiTokens.Raised;
        public override Color MenuItemPressedGradientEnd => UiTokens.Raised;
        public override Color SeparatorDark => UiTokens.Line;
        public override Color SeparatorLight => UiTokens.Line;
    }
}
