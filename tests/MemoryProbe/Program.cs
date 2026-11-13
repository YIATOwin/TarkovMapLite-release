using System.Collections;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Reflection;
using System.Text.Json;
using TarkovMapLite;

internal static class Program
{
    private const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    private static readonly Assembly App=typeof(MapPack).Assembly;
    private static Type T(string name)=>App.GetType("TarkovMapLite."+name)!;
    private static object? Call(string type,object? target,string method,params object?[] args)=>T(type).GetMethod(method,Flags)!.Invoke(target,args);
    [STAThread]
    static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        TarkovData.LoadCached(); Call("MapMarkers",null,"KindsByCount");
        using var canvas=(Control)Activator.CreateInstance(T("MapCanvas"),true)!;
        canvas.Size=new Size(1000,800);canvas.CreateControl();
        foreach(var prop in new[]{"ShowContainers","ShowLoot","ShowSpawns"})T("MapCanvas").GetProperty(prop)!.SetValue(canvas,true);
        var samples=new List<object>();
        void Sample(string phase)
        {
            using var process=Process.GetCurrentProcess(); process.Refresh();
            var normal=process.PrivateMemorySize64;
            GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();process.Refresh();
            var images=(IDictionary)T("MapCanvas").GetField("_images",Flags)!.GetValue(canvas)!;
            var imageBytes=images.Values.Cast<object>().Select(value=>value as Image ?? (Image)value.GetType().GetProperty("Image")!.GetValue(value)!).Sum(i=>i.Width*i.Height*4L);
            var item=new{phase,privateBytes=process.PrivateMemorySize64,privateBeforeGC=normal,workingBytes=process.WorkingSet64,
                managedBytes=GC.GetTotalMemory(false),decodedMapBytes=imageBytes,images=images.Count,peakBytes=process.PeakWorkingSet64};
            samples.Add(item);Console.WriteLine(JsonSerializer.Serialize(item));
        }
        void Paint(){using var bitmap=new Bitmap(canvas.Width,canvas.Height);canvas.DrawToBitmap(bitmap,canvas.ClientRectangle);}
        void Map(string key,string layer="")
        {
            var map=MapPack.Find(key)!;Call("MapCanvas",canvas,"SetMap",map,map.DisplayName);
            Call("MapCanvas",canvas,"SetLayer",layer);Paint();
        }
        Sample("startup-data");
        Map("reserve");Sample("reserve-ground");
        Call("MapCanvas",canvas,"SetLayer","Bunkers");Sample("floor-animation-active");
        Thread.Sleep(180);Application.DoEvents();Paint();Sample("reserve-bunker");
        Map("woods-tiles");Sample("woods-after-reserve");
        Map("interchange-tiles","Second_Floor");Sample("interchange-upper");
        Map("factory");Sample("factory-after-large");
        foreach(var map in MapPack.Maps)Map(map.Key);
        Sample("after-all-maps");
        Map("reserve"); Sample("return-reserve-ground");
        using var fixture=new Bitmap(512,512);
        using(var g=Graphics.FromImage(fixture)){g.Clear(Color.Transparent);g.FillEllipse(Brushes.Gold,30,30,450,450);g.DrawRectangle(Pens.Black,50,50,400,400);}
        using var png=new MemoryStream();fixture.Save(png,ImageFormat.Png);var bytes=png.ToArray();
        var thumbnails=new List<Image>();
        for(var i=0;i<50;i++)thumbnails.Add((Image)Call("ItemImages",null,"Decode",bytes)!);
        Sample("50-synthetic-512px-thumbnails");
        GC.KeepAlive(thumbnails);foreach(var image in thumbnails)image.Dispose();
        if(args.Length>0)File.WriteAllText(args[0],JsonSerializer.Serialize(samples,new JsonSerializerOptions{WriteIndented=true}));
    }
}
