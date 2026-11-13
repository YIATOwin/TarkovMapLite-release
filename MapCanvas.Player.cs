using System.Drawing.Drawing2D;
using System.Diagnostics;

namespace TarkovMapLite;

internal sealed partial class MapCanvas
{
    private const float PlayerPulseSeconds = 1.5f;
    private const float PlayerPaintRadius = 48f;
    private long _playerPulseStarted;

    /// <summary>只有扩散圈变化，固定引导线不需要每 50ms 重绘整张地图。</summary>
    private void PlayerPulseTick()
    {
        if (!Visible || IsDisposed || !IsHandleCreated) return;
        var bounds = PlayerAnimationBounds();
        if (bounds.IsEmpty) return;
        _pulsePhase = (float)(Stopwatch.GetElapsedTime(_playerPulseStarted).TotalSeconds / PlayerPulseSeconds % 1);
        Invalidate(bounds);
    }

    private Rectangle PlayerAnimationBounds()
    {
        if (_map == null || PlayerPosition is not { } position ||
            !string.Equals(PlayerMapKey,_map.Key,StringComparison.OrdinalIgnoreCase)) return Rectangle.Empty;
        var radius = PlayerPaintRadius * DeviceDpi / 96f;
        var x = Origin.X + position.X * (float)Scale;
        var y = Origin.Y + position.Y * (float)Scale;
        var bounds = Rectangle.Intersect(ClientRectangle,Rectangle.Ceiling(new RectangleF(x-radius,y-radius,radius*2,radius*2)));
        return bounds.Width > 0 && bounds.Height > 0 ? bounds : Rectangle.Empty;
    }

    /// <summary>稳定高亮的朝向箭头、黑白双描边与一圈柔和扩散；按屏幕 DPI 定尺寸。</summary>
    private void DrawPlayer(Graphics g, float scale, PointF origin)
    {
        if (_map == null || PlayerPosition is not { } position ||
            !string.Equals(PlayerMapKey,_map.Key,StringComparison.OrdinalIgnoreCase)) return;
        var x = origin.X + position.X*scale;
        var y = origin.Y + position.Y*scale;
        var dpi = g.DpiX/96f;
        var radius = PlayerPaintRadius*dpi;
        if (x < -radius || y < -radius || x > ClientSize.Width+radius || y > ClientSize.Height+radius) return;
        var offFloor = PlayerFloor.HasValue && PlayerFloor.Value != CurrentFloor;
        var opacity = offFloor ? 90f/255 : 1f;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var half = 32f*dpi;
        var box = new RectangleF(x-half,y-half,half*2,half*2);
        using (var frame = new Pen(Color.FromArgb(offFloor ? 35 : 75,PlayerGuideColor),dpi))
            g.DrawRectangle(frame,box.X,box.Y,box.Width,box.Height);
        using (var guide = new Pen(Color.FromArgb(offFloor ? 18 : 38,PlayerGuideColor),dpi))
        {
            g.DrawLine(guide,x,0,x,box.Top);
            g.DrawLine(guide,x,box.Bottom,x,ClientSize.Height);
            g.DrawLine(guide,0,y,box.Left,y);
            g.DrawLine(guide,box.Right,y,ClientSize.Width,y);
        }

        var phase = Math.Clamp(_pulsePhase,0,1);
        var ringRadius = (22 + 20*phase)*dpi;
        var ringAlpha = (int)Math.Round(140*Math.Pow(1-phase,1.3)*opacity);
        if (ringAlpha > 0)
        {
            using var under = new Pen(Color.FromArgb(ringAlpha/2,UiTokens.PlayerOutline),3.5f*dpi);
            using var ring = new Pen(Color.FromArgb(ringAlpha,PlayerMarkerColor),1.7f*dpi);
            g.DrawEllipse(under,x-ringRadius,y-ringRadius,ringRadius*2,ringRadius*2);
            g.DrawEllipse(ring,x-ringRadius,y-ringRadius,ringRadius*2,ringRadius*2);
        }

        var forward = PlayerForward ?? new PointF(0,-1);
        var length = (float)Math.Sqrt(forward.X*forward.X+forward.Y*forward.Y);
        forward = length > 1e-4f ? new PointF(forward.X/length,forward.Y/length) : new PointF(0,-1);
        var right = new PointF(-forward.Y,forward.X);
        PointF At(float f,float r) => new(x+(forward.X*f+right.X*r)*dpi,y+(forward.Y*f+right.Y*r)*dpi);
        var tip = At(21.6f,0);
        var left = At(-8.4f,9.6f);
        var rightWing = At(-8.4f,-9.6f);
        var tail = At(-20.4f,0);
        using var outline = new GraphicsPath();
        outline.AddPolygon(new[] { tip,left,tail,rightWing });
        using var dark = new Pen(MarkerPaint.Fade(UiTokens.PlayerOutline,opacity),5.6f*dpi) { LineJoin=LineJoin.Round };
        using var white = new Pen(MarkerPaint.Fade(UiTokens.PlayerEdge,opacity),2.8f*dpi) { LineJoin=LineJoin.Round };
        using var head = new SolidBrush(MarkerPaint.Fade(PlayerMarkerColor,opacity));
        using var fin = new SolidBrush(MarkerPaint.Fade(UiTokens.PlayerTail,opacity));
        g.DrawPath(dark,outline);
        g.DrawPath(white,outline);
        g.FillPolygon(head,new[] { tip,left,rightWing });
        g.FillPolygon(fin,new[] { left,tail,rightWing });
        using var spine = new Pen(MarkerPaint.Fade(Color.FromArgb(110,UiTokens.PlayerEdge),opacity),dpi);
        g.DrawLine(spine,tip,tail);
        g.DrawLine(spine,left,rightWing);
    }
}
