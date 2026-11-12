namespace TarkovMapLite;

/// <summary>复用一组画笔，将图标、轮廓、文字与文字底板一起按楼层淡化。</summary>
internal sealed class MarkerPaint
{
    private readonly (object Paint, Color Color)[] _paints;
    private float _opacity = 1;

    public MarkerPaint(params object[] paints) => _paints = paints.Select(p =>
        (p, p is Pen pen ? pen.Color : ((SolidBrush)p).Color)).ToArray();

    public static Color Fade(Color color, float opacity) =>
        Color.FromArgb((int)Math.Round(color.A * opacity), color);

    public void SetOpacity(float opacity)
    {
        if (_opacity == opacity) return;
        _opacity = opacity;
        foreach (var (paint, color) in _paints)
        {
            if (paint is Pen pen) pen.Color = Fade(color, opacity);
            else ((SolidBrush)paint).Color = Fade(color, opacity);
        }
    }
}
