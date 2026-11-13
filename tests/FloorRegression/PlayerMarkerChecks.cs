using System.Drawing.Imaging;
using System.Reflection;
using System.Security.Cryptography;
using TarkovMapLite;

internal static class PlayerMarkerChecks
{
    private const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
    private static readonly Type CanvasType=typeof(MapPack).Assembly.GetType("TarkovMapLite.MapCanvas")!;
    private static object? Call(Control canvas,string name,params object?[] args)=>CanvasType.GetMethod(name,Flags)!.Invoke(canvas,args);
    private static void Set(Control canvas,string name,object? value)=>CanvasType.GetProperty(name)!.SetValue(canvas,value);
    private static object? Get(Control canvas,string name)=>CanvasType.GetProperty(name,Flags)!.GetValue(canvas);
    private static void Phase(Control canvas,float phase)=>CanvasType.GetField("_pulsePhase",Flags)!.SetValue(canvas,phase);
    private static string Hash(Bitmap image)
    {
        using var stream=new MemoryStream();image.Save(stream,ImageFormat.Png);return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
    private static Bitmap Frame(Control canvas)
    {
        var frame=new Bitmap(canvas.Width,canvas.Height,PixelFormat.Format32bppArgb);
        using var g=Graphics.FromImage(frame);
        Call(canvas,"OnPaint",new PaintEventArgs(g,canvas.ClientRectangle));
        return frame;
    }
    private static long Alpha(Bitmap image)
    {
        long sum=0;for(var y=0;y<image.Height;y++)for(var x=0;x<image.Width;x++)sum+=image.GetPixel(x,y).A;return sum;
    }
    public static int Run(string? output)
    {
        var checks=0;
        void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
        using var canvas=(Control)Activator.CreateInstance(CanvasType,true)!;
        canvas.Size=new Size(1000,760);canvas.CreateControl();
        var map=MapPack.Find("reserve-tiles")!;
        Call(canvas,"SetMap",map,map.DisplayName);
        Set(canvas,"PlayerMapKey",map.Key);Set(canvas,"PlayerPosition",PointF.Empty);
        Set(canvas,"PlayerForward",new PointF(0,-1));Set(canvas,"PlayerFloor",0);
        var timer=(System.Windows.Forms.Timer)CanvasType.GetField("_pulseTimer",Flags)!.GetValue(canvas)!;
        timer.Stop();
        Bitmap Glyph(Color background,float scale=1,int dpi=96)
        {
            var frame=new Bitmap(160*dpi/96,160*dpi/96,PixelFormat.Format32bppArgb);
            frame.SetResolution(dpi,dpi);
            using var g=Graphics.FromImage(frame);g.Clear(background);
            Call(canvas,"DrawPlayer",g,scale,new PointF(frame.Width/2f,frame.Height/2f));return frame;
        }
        Phase(canvas,.2f);
        using var dark=Glyph(Color.FromArgb(14,16,18));
        using var light=Glyph(Color.White);
        int Count(Bitmap image,Func<Color,bool> predicate)
        {
            var sum=0;for(var y=image.Height/2-28;y<image.Height/2+28;y++)for(var x=image.Width/2-18;x<image.Width/2+18;x++)if(predicate(image.GetPixel(x,y)))sum++;
            return sum;
        }
        Check(Count(dark,c=>c.R<140&&c.G>185&&c.B>205)>120,"Player cyan body is too faint on dark terrain");
        Check(Count(dark,c=>c.R>225&&c.G>225&&c.B>225)>40,"Player white outline is missing on dark terrain");
        Check(Count(light,c=>c.R<35&&c.G<45&&c.B<50)>60,"Player dark outline is missing on light terrain");
        using var zoomed=Glyph(Color.FromArgb(14,16,18),6);
        Check(Hash(dark)==Hash(zoomed),"Player glyph shrank or enlarged with map zoom");
        using var highDpi=Glyph(Color.Transparent,1,192);
        using var normalDpi=Glyph(Color.Transparent);
        Check(Alpha(highDpi)>Alpha(normalDpi)*3,"Player glyph did not scale with screen DPI");
        Set(canvas,"PlayerFloor",-1);
        using var offFloor=Glyph(Color.Transparent);
        Check(Alpha(offFloor)<Alpha(normalDpi)*.7,"Off-floor player emphasis did not remain dimmed");
        Set(canvas,"PlayerFloor",0);Set(canvas,"PlayerForward",new PointF(1,0));
        using var east=Glyph(Color.FromArgb(14,16,18));
        Check(Hash(east)!=Hash(dark),"Player heading did not rotate the marker");
        Set(canvas,"PlayerForward",PointF.Empty);
        using var zero=Glyph(Color.FromArgb(14,16,18));
        Check(Hash(zero)==Hash(dark),"Zero heading should fall back to north");
        Set(canvas,"PlayerForward",new PointF(0,-1));
        Phase(canvas,0);using var initial=Glyph(Color.FromArgb(14,16,18));
        Phase(canvas,.55f);using var expanded=Glyph(Color.FromArgb(14,16,18));
        using(var a=initial.Clone(new Rectangle(73,73,14,14),PixelFormat.Format32bppArgb))
        using(var b=expanded.Clone(new Rectangle(73,73,14,14),PixelFormat.Format32bppArgb))
            Check(Hash(a)==Hash(b),"Arrow body flashed with the pulse instead of remaining stable");
        Check(Hash(initial)!=Hash(expanded),"Pulse did not expand or fade");

        // Native map + point layers exercise dirty repaint restoration, including labels and HUD.
        Set(canvas,"PlayerPosition",new PointF((float)map.ViewBoxWidth*.56f,(float)map.ViewBoxHeight*.48f));
        Set(canvas,"ShowContainers",true);Set(canvas,"ShowLoot",true);Set(canvas,"ShowLabels",true);
        Phase(canvas,0);using var scene0=Frame(canvas);
        var bounds=(Rectangle)Call(canvas,"PlayerAnimationBounds")!;
        Check(bounds.Width* bounds.Height<canvas.Width*canvas.Height/20,"Pulse invalidates too much of the viewport");
        Phase(canvas,.55f);using var scene1=Frame(canvas);
        using var partial=(Bitmap)scene0.Clone();
        using(var g=Graphics.FromImage(partial))Call(canvas,"OnPaint",new PaintEventArgs(g,bounds));
        var outside=0;var maximumDelta=0;
        for(var yy=0;yy<partial.Height;yy++)for(var xx=0;xx<partial.Width;xx++)
        {
            var a=partial.GetPixel(xx,yy);var b=scene1.GetPixel(xx,yy);
            if(!bounds.Contains(xx,yy)&&a!=b)outside++;
            maximumDelta=Math.Max(maximumDelta,Math.Max(Math.Max(Math.Abs(a.R-b.R),Math.Abs(a.G-b.G)),Math.Max(Math.Abs(a.B-b.B),Math.Abs(a.A-b.A))));
        }
        Check(outside==0,"Pulse repaint changed pixels outside its bounds");
        // GDI+ bicubic image drawing rounds some RGB channels by one when clipped.
        Check(maximumDelta<=1,"Partial pulse repaint left trails instead of restoring the map");
        var invalidations=new List<Rectangle>();canvas.Invalidated+=(_,e)=>invalidations.Add(e.InvalidRect);
        Call(canvas,"PlayerPulseTick");
        Check(invalidations.Count==1&&invalidations[0]==bounds,"Pulse timer did not request only the player area");
        invalidations.Clear();Set(canvas,"PlayerMapKey","woods");Call(canvas,"PlayerPulseTick");
        Check(invalidations.Count==0,"Other-map player still caused repaint work");
        Set(canvas,"PlayerMapKey",map.Key);Set(canvas,"PlayerPosition",new PointF(-100000,-100000));
        Call(canvas,"PlayerPulseTick");
        Check(invalidations.Count==0,"Offscreen player still caused repaint work");
        Set(canvas,"PlayerPosition",null);Check(!timer.Enabled,"Missing player left pulse timer running");
        Set(canvas,"PlayerPosition",new PointF((float)map.ViewBoxWidth*.56f,(float)map.ViewBoxHeight*.48f));
        canvas.Visible=false;Check(!timer.Enabled,"Hidden player canvas left its timer running");
        canvas.Visible=true;Check(timer.Enabled,"Showing player canvas did not resume its timer");
        Call(canvas,"RecreateHandle");Check(timer.Enabled&&canvas.IsHandleCreated,"Recreating player canvas handle lost the pulse timer");
        timer.Stop();

        if(output!=null)
        {
            dark.Save(Path.Combine(output,"player-dark.png"));light.Save(Path.Combine(output,"player-light.png"));
            Set(canvas,"PlayerPosition",new PointF((float)map.ViewBoxWidth*.56f,(float)map.ViewBoxHeight*.48f));
            Set(canvas,"PlayerForward",new PointF(.35f,-1));timer.Stop();
            for(var i=0;i<15;i++)
            {
                Phase(canvas,i/15f);using var frame=Frame(canvas);frame.Save(Path.Combine(output,"player-pulse-"+i.ToString("00")+".png"));
            }
        }
        Console.WriteLine($"PASS: {checks} player contrast, stable body, heading, DPI and partial pulse repaint checks.");
        return checks;
    }
}
