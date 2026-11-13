using System.Diagnostics;
using System.Drawing.Imaging;
using System.Reflection;
using System.Security.Cryptography;
using TarkovMapLite;

internal static class FloorTransitionChecks
{
    private static readonly Type CanvasType = typeof(MapPack).Assembly.GetType("TarkovMapLite.MapCanvas")!;
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static object? Call(Control canvas, string method, params object?[] args) =>
        CanvasType.GetMethod(method, Flags)!.Invoke(canvas, args);
    private static object? Field(Control canvas, string name) => CanvasType.GetField(name, Flags)!.GetValue(canvas);
    private static void Field(Control canvas, string name, object value) => CanvasType.GetField(name, Flags)!.SetValue(canvas, value);
    private static object? Property(Control canvas, string name) => CanvasType.GetProperty(name, Flags)!.GetValue(canvas);
    private static void Property(Control canvas, string name, object value) => CanvasType.GetProperty(name, Flags)!.SetValue(canvas, value);
    private static Bitmap Scene(Control canvas)
    {
        var frame = new Bitmap(canvas.Width, canvas.Height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(frame);
        Call(canvas, "DrawMapScene", g);
        return frame;
    }
    private static Bitmap Blend(Control canvas, float progress)
    {
        var frame = new Bitmap(canvas.Width, canvas.Height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(frame);
        Call(canvas, "DrawFloorTransition", g, progress);
        return frame;
    }
    private static void Paint(Control canvas)
    {
        using var frame = new Bitmap(canvas.Width, canvas.Height);
        canvas.DrawToBitmap(frame, canvas.ClientRectangle);
    }
    private static string Hash(Bitmap frame)
    {
        using var stream = new MemoryStream();
        frame.Save(stream, ImageFormat.Png);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
    private static bool Disposed(Bitmap frame)
    {
        try { _ = frame.Width; return false; }
        catch (ArgumentException) { return true; }
    }

    public static int Run(string? output)
    {
        var checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new Exception(message);
            checks++;
        }
        foreach (var key in new[] { "reserve", "reserve-tiles" })
        {
            using var canvas = (Control)Activator.CreateInstance(CanvasType, true)!;
            canvas.Size = new Size(700, 500);
            canvas.CreateControl();
            var map = MapPack.Find(key)!;
            Call(canvas, "SetMap", map, map.DisplayName);
            Call(canvas, "SetLayer", "Bunkers");
            Check(Field(canvas, "_floorTransitionFrom") == null, "First floor before any paint should display directly");
            Call(canvas, "SetLayer", "");
            Property(canvas, "Extracts", new List<MapMarker> {
                new() { Name = "地下撤离点", X = 0, Z = 0, Kind = "extract", FloorLayer = "Bunkers",
                    Screen = new PointF((float)map.ViewBoxWidth * .6f, (float)map.ViewBoxHeight * .5f) }
            });
            Paint(canvas);
            using var oldScene = Scene(canvas);
            var origin = (PointF)Property(canvas, "Origin")!;
            var scale = (double)Property(canvas, "Scale")!;
            Call(canvas, "SetLayer", "Bunkers");
            var from = (Bitmap)Field(canvas, "_floorTransitionFrom")!;
            var to = (Bitmap)Field(canvas, "_floorTransitionTo")!;
            Check(((System.Windows.Forms.Timer)Field(canvas,"_floorTransitionTimer")!).Enabled, key + ": visible floor switch did not start animation");
            Check(from!.Size == canvas.ClientSize && to!.Size == canvas.ClientSize, "Snapshots must be viewport-sized");
            Check((string)Property(canvas, "CurrentLayer")! == "Bunkers", "Selected floor did not update immediately");
            Check((PointF)Property(canvas, "Origin")! == origin && (double)Property(canvas, "Scale")! == scale,
                "Floor transition changed map position or scale");
            using var target = Scene(canvas);
            Check(Hash(from) == Hash(oldScene), "Old floor snapshot differs from old scene");
            Check(Hash(to) == Hash(target), "Target snapshot differs from actual target scene");
            using var start = Blend(canvas, 0);
            using var half = Blend(canvas, .5f);
            using var end = Blend(canvas, 1);
            Check(Hash(start) == Hash(oldScene), "Transition start jumped to the new floor");
            Check(Hash(end) == Hash(target), "Transition endpoint differs from final live rendering");
            var differing = 0;
            for (var y = 10; y < canvas.Height; y += 17)
            for (var x = 10; x < canvas.Width; x += 17)
            {
                var a = start.GetPixel(x, y); var b = end.GetPixel(x, y); var c = half.GetPixel(x, y);
                if (a.ToArgb() != b.ToArgb()) differing++;
                if (Math.Abs(c.R - (a.R+b.R)/2f) > 2 || Math.Abs(c.G - (a.G+b.G)/2f) > 2 ||
                    Math.Abs(c.B - (a.B+b.B)/2f) > 2 || c.A != 255)
                    throw new Exception("Crossfade darkened the scene or failed to interpolate colors");
            }
            Check(differing > 100, "Floor scenes did not differ enough to verify interpolation");
            var point = new Point((int)(origin.X + map.ViewBoxWidth*.6*scale), (int)(origin.Y + map.ViewBoxHeight*.5*scale));
            var pointChanges = 0;
            for (var y = point.Y-8; y <= point.Y+8; y++)
            for (var x = point.X-8; x <= point.X+8; x++)
                if (start.GetPixel(x,y) != end.GetPixel(x,y) && half.GetPixel(x,y) != start.GetPixel(x,y) && half.GetPixel(x,y) != end.GetPixel(x,y)) pointChanges++;
            Check(pointChanges > 20, "Exit marker did not interpolate with its floor scene");
            if (output != null)
            {
                start.Save(Path.Combine(output,key+"-transition-0.png"));
                half.Save(Path.Combine(output,key+"-transition-50.png"));
                end.Save(Path.Combine(output,key+"-transition-100.png"));
                for (var step = 0; step <= 10; step++)
                {
                    using var frame = Blend(canvas, step/10f);
                    using (var g = Graphics.FromImage(frame)) Call(canvas,"DrawFloors",g);
                    frame.Save(Path.Combine(output,key+"-animation-"+step.ToString("00")+".png"));
                }
            }
            var began = Field(canvas, "_floorTransitionStarted");
            Call(canvas, "SetLayer", "Bunkers");
            Call(canvas, "SetLayer", "not-a-floor");
            Check(ReferenceEquals(from,Field(canvas,"_floorTransitionFrom")) && Equals(began,Field(canvas,"_floorTransitionStarted")),
                "Same or invalid floor restarted the current animation");
            Field(canvas,"_floorTransitionProgress",.5f);
            Call(canvas,"SetLayer","Third_Floor");
            Check(Hash((Bitmap)Field(canvas,"_floorTransitionFrom")!) == Hash(half), "Rapid switch flashed back instead of continuing from displayed blend");
            Check(Disposed(from) && Disposed(to), "Interrupted animation retained old snapshots");
            Field(canvas,"_floorTransitionStarted",Stopwatch.GetTimestamp()-Stopwatch.Frequency);
            Paint(canvas);
            Check(Field(canvas,"_floorTransitionFrom") == null && !((System.Windows.Forms.Timer)Field(canvas,"_floorTransitionTimer")!).Enabled,
                "Completed animation did not release snapshots and stop its timer");
            Check((string)Property(canvas,"CurrentLayer")! == "Third_Floor", "Interrupted transition settled on an earlier selection");

            // Preserve an actual zoomed/panned view, then handle user input during the fade.
            Call(canvas,"ZoomBy",2d,null!);
            Call(canvas,"OnMouseDown",new MouseEventArgs(MouseButtons.Left,1,300,250,0));
            Call(canvas,"OnMouseMove",new MouseEventArgs(MouseButtons.Left,0,330,230,0));
            Call(canvas,"OnMouseUp",new MouseEventArgs(MouseButtons.Left,1,330,230,0));
            Paint(canvas);
            origin = (PointF)Property(canvas,"Origin")!; scale = (double)Property(canvas,"Scale")!;
            Call(canvas,"SetLayer","");
            Check((PointF)Property(canvas,"Origin")! == origin && (double)Property(canvas,"Scale")! == scale,
                "Floor switch reset a user zoomed/panned view");
            Call(canvas,"ZoomBy",1.15d,null!);
            Check(Field(canvas,"_floorTransitionFrom") == null, "Zoom used stale viewport snapshots");
            Paint(canvas); Call(canvas,"SetLayer","Bunkers");
            Call(canvas,"OnMouseDown",new MouseEventArgs(MouseButtons.Left,1,300,250,0));
            Call(canvas,"OnMouseMove",new MouseEventArgs(MouseButtons.Left,0,340,240,0));
            Call(canvas,"OnMouseUp",new MouseEventArgs(MouseButtons.Left,1,340,240,0));
            Check(Field(canvas,"_floorTransitionFrom") == null, "Dragging used stale viewport snapshots");
            Paint(canvas); Call(canvas,"SetLayer","");
            canvas.Size = new Size(720,500);
            Check(Field(canvas,"_floorTransitionFrom") == null, "Resize retained incorrectly sized snapshots");
            Paint(canvas); Call(canvas,"SetLayer","Bunkers");
            Call(canvas,"SetMap",MapPack.Find("customs")!,"海关");
            Check(Field(canvas,"_floorTransitionFrom") == null && !(bool)Field(canvas,"_floorScenePainted")!,
                "Map change should cancel floor fade and never animate between maps");
            Paint(canvas); Call(canvas,"SetLayer","Second_Floor");
            canvas.Visible = false;
            Check(Field(canvas,"_floorTransitionFrom") == null, "Hidden canvas retained animation buffers");
            canvas.Visible = true; Paint(canvas); Call(canvas,"SetLayer","");
            Thread.Sleep(180); Application.DoEvents();
            Check(Field(canvas,"_floorTransitionFrom") == null && !((System.Windows.Forms.Timer)Field(canvas,"_floorTransitionTimer")!).Enabled,
                "Real timer did not finish/release the transition");
            Paint(canvas); Call(canvas,"SetLayer","Second_Floor");
            var disposalFrom = (Bitmap)Field(canvas,"_floorTransitionFrom")!;
            var disposalTo = (Bitmap)Field(canvas,"_floorTransitionTo")!;
            canvas.Dispose();
            Check(Disposed(disposalFrom) && Disposed(disposalTo), "Closing canvas leaked animation buffers");
        }
        Console.WriteLine($"PASS: {checks} floor crossfade, marker blend, interruption, viewport and lifecycle checks.");
        return checks;
    }
}
