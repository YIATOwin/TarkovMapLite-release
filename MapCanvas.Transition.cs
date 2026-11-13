using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace TarkovMapLite;

internal sealed partial class MapCanvas
{
    private const double FloorTransitionDuration = 150;
    private readonly System.Windows.Forms.Timer _floorTransitionTimer = new() { Interval = 15 };
    private Bitmap? _floorTransitionFrom;
    private Bitmap? _floorTransitionTo;
    private long _floorTransitionStarted;
    private float _floorTransitionProgress;
    private bool _floorScenePainted;

    private double FloorTransitionElapsed =>
        Stopwatch.GetElapsedTime(_floorTransitionStarted).TotalMilliseconds;

    private void ChangeFloor(string layer)
    {
        Bitmap? from = null;
        if (_floorScenePainted && !_fitPending && IsHandleCreated && Visible &&
            ClientSize.Width > 16 && ClientSize.Height > 16)
        {
            try
            {
                // 连续切层时保留上次真正绘出的混合比例，避免闪回旧楼层。
                from = CaptureFloorScene(displayed: true);
            }
            catch (OutOfMemoryException)
            {
                // 快照只是动效，内存不足时仍允许直接切层。
            }
        }
        StopFloorTransition();
        _layer = layer;
        _image = null;
        if (from == null)
        {
            return;
        }
        _floorTransitionFrom = from;
        try
        {
            _floorTransitionTo = CaptureFloorScene(displayed: false);
            // 冷加载和标记绘制完成以后才开始计时，避免准备图片消耗动画时长。
            _floorTransitionStarted = Stopwatch.GetTimestamp();
            _floorTransitionProgress = 0;
            _floorTransitionTimer.Start();
        }
        catch (OutOfMemoryException)
        {
            StopFloorTransition();
        }
        catch
        {
            StopFloorTransition();
            throw;
        }
    }

    private Bitmap CaptureFloorScene(bool displayed)
    {
        var frame = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppPArgb);
        try
        {
            frame.SetResolution(DeviceDpi,DeviceDpi);
            using var g = Graphics.FromImage(frame);
            if (displayed && _floorTransitionFrom != null && _floorTransitionTo != null)
            {
                DrawFloorTransition(g, _floorTransitionProgress);
            }
            else
            {
                DrawMapScene(g);
            }
            return frame;
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    private bool TryDrawFloorTransition(Graphics g)
    {
        if (_floorTransitionFrom == null || _floorTransitionTo == null)
        {
            return false;
        }
        if (_floorTransitionFrom.Size != ClientSize || FloorTransitionElapsed >= FloorTransitionDuration)
        {
            StopFloorTransition();
            return false;
        }
        _floorTransitionProgress = (float)Math.Clamp(FloorTransitionElapsed / FloorTransitionDuration, 0, 1);
        DrawFloorTransition(g, _floorTransitionProgress);
        return true;
    }

    private void DrawFloorTransition(Graphics g, float progress)
    {
        if (_floorTransitionFrom == null || _floorTransitionTo == null)
        {
            return;
        }
        var state = g.Save();
        try
        {
            g.CompositingMode = CompositingMode.SourceOver;
            g.PixelOffsetMode = PixelOffsetMode.Default;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            // 快照含不透明背景。旧帧完整铺底，再叠加新帧，亮度不会在中间变暗。
            g.DrawImageUnscaled(_floorTransitionFrom, Point.Empty);
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(new ColorMatrix { Matrix33 = Math.Clamp(progress, 0, 1) });
            g.DrawImage(_floorTransitionTo, new Rectangle(Point.Empty, ClientSize),
                0, 0, _floorTransitionTo.Width, _floorTransitionTo.Height, GraphicsUnit.Pixel, attributes);
        }
        finally
        {
            g.Restore(state);
        }
    }

    private void StopFloorTransition()
    {
        _floorTransitionTimer.Stop();
        _floorTransitionFrom?.Dispose();
        _floorTransitionTo?.Dispose();
        _floorTransitionFrom = _floorTransitionTo = null;
        _floorTransitionProgress = 0;
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        if (!Visible)
        {
            StopFloorTransition();
            _floorScenePainted = false;
        }
        base.OnVisibleChanged(e);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        StopFloorTransition();
        _floorScenePainted = false;
        base.OnHandleDestroyed(e);
    }
}
