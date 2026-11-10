// ============================================================================
// LayerMenuRenderer.cs — 「图层」下拉的绘制规则
//
// 三条规则，逐条对应提出来的问题：
//
//  1. **悬浮不铺底色，只描边**
//     以前 ToolStripProfessionalRenderer 的悬浮是把整行刷成蓝色，压在彩色文字上
//     就看不清了。现在悬浮只在行内画 1px 强调色描边，底色保持面板色。
//
//  2. **文字颜色固定**
//     文字只有三种：常态 TextMain、勾选 Accent、禁用 TextFaint。
//     分类颜色不再上文字，而是画在左边 16×16 的小图标上（见 LayerMenuArt）。
//
//  3. **勾选另用一套底色**
//     勾选 = 强调色 14% 实心底 + 强调色文字 + 图标全彩；
//     未勾选 = 透明底 + 常态文字 + 图标半透明（45%）。
//     这样"开着的层"和"关掉的层"一眼分得开，且不依赖彩色文字。
//
// 另外把系统默认的白色图片边距、蓝色箭头、渐变边框全部换掉，否则深色菜单里
// 会漏出几块亮色。
// ============================================================================
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

    /// <summary>系统的勾选方块和图标打架，勾选状态已经由底色 + 文字色表达，这里不画。</summary>
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
    }

    /// <summary>未勾选的项把图标压淡，表示"这一层现在是关的"。</summary>
    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        var image = e.Image;
        if (image == null)
        {
            return;
        }
        var alpha = StateOf(e.Item) is CheckState.Checked or CheckState.Indeterminate ? 1f : 0.62f;
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
