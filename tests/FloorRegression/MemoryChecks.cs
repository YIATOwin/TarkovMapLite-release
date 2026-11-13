using System.Collections;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using TarkovMapLite;

internal static class MemoryChecks
{
    private const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    private static Type T(string name)=>typeof(MapPack).Assembly.GetType("TarkovMapLite."+name)!;
    private static object? Call(string type,object? target,string method,params object?[] args)=>T(type).GetMethod(method,Flags)!.Invoke(target,args);
    private static object? Field(string type,object? target,string field)=>T(type).GetField(field,Flags)!.GetValue(target);
    private static bool Disposed(Image image){try{_=image.Width;return false;}catch(ArgumentException){return true;}}
    public static int Run()
    {
        var checks=0;
        void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
        var asset=Call("Assets",null,"ReadImage","maps/reserve/base.png")!;
        var source=(Stream)Field("AssetImage",asset,"_source")!;
        var image=(Image)asset.GetType().GetProperty("Image")!.GetValue(asset)!;
        Check(source.CanRead&&image.Width==4000,"Asset stream must remain open while GDI image is alive");
        using(var pixels=new Bitmap(320,240))
        using(var g=Graphics.FromImage(pixels))
        {
            g.DrawImage(image,pixels.ClientRect());
            Check(pixels.GetPixel(160,120).A>0,"Owned resource image failed to render");
        }
        ((IDisposable)asset).Dispose();
        Check(!source.CanRead&&Disposed(image),"Resource stream and image must release together");

        using var canvas=(Control)Activator.CreateInstance(T("MapCanvas"),true)!;
        canvas.Size=new Size(500,400);canvas.CreateControl();
        void Paint(){using var frame=new Bitmap(canvas.Width,canvas.Height);canvas.DrawToBitmap(frame,canvas.ClientRectangle);}
        Call("MapCanvas",canvas,"SetMap",MapPack.Find("reserve")!,"储备站");Paint();
        var cache=(IDictionary)Field("MapCanvas",canvas,"_images")!;
        var previous=cache.Values.Cast<object>().Single();
        var previousImage=(Image)previous.GetType().GetProperty("Image")!.GetValue(previous)!;
        Call("MapCanvas",canvas,"SetMap",MapPack.Find("woods-tiles")!,"森林");
        Check(cache.Count==0&&Disposed(previousImage),"Changing map retained old large decoded image");Paint();
        Check(cache.Count==1,"Ground view unnecessarily retained two large maps");
        var pair=(from a in MapPack.Maps from b in MapPack.Maps where a.Key!=b.Key&&a.ImageFor("")==b.ImageFor("") select(a,b)).First();
        Call("MapCanvas",canvas,"SetMap",pair.a,pair.a.DisplayName);Paint();
        var shared=cache.Values.Cast<object>().Single();
        Call("MapCanvas",canvas,"SetMap",pair.b,pair.b.DisplayName);Paint();
        Check(ReferenceEquals(shared,cache.Values.Cast<object>().Single()),"Shared-image variants should reuse the same resource");
        Call("MapCanvas",canvas,"SetMap",null!,"");
        Check(cache.Count==0,"Empty map retained decoded resources");

        byte[] Fixture(int w,int h)
        {
            using var bitmap=new Bitmap(w,h);using(var g=Graphics.FromImage(bitmap))g.Clear(Color.Gold);
            using var bytes=new MemoryStream();bitmap.Save(bytes,ImageFormat.Png);return bytes.ToArray();
        }
        using var thumb=(Image)Call("ItemImages",null,"Decode",Fixture(512,256))!;
        Check(thumb.Width==128&&thumb.Height==64,"Thumbnail did not reduce pixels while preserving aspect ratio");
        using var small=(Image)Call("ItemImages",null,"Decode",Fixture(24,12))!;
        Check(small.Width==24&&small.Height==12,"Small source image was unnecessarily enlarged");
        Image? first=null;
        for(var i=0;i<140;i++)
        {
            var bitmap=new Bitmap(128,128);if(i==0)first=bitmap;
            Call("ItemImages",null,"Store","memory-check-"+i,bitmap);
        }
        var thumbnails=(IDictionary)Field("ItemImages",null,"Loaded")!;
        Check(thumbnails.Count<=128&&Disposed(first!),"Thumbnail cache is unbounded or did not dispose evictions");
        using(var destination=new Bitmap(32,32))
        using(var g=Graphics.FromImage(destination))
        {
            Check((bool)Call("ItemImages",null,"Draw",g,"memory-check-20",new Rectangle(0,0,24,24))!,"Cached thumbnail failed to draw");
            Call("ItemImages",null,"Store","memory-check-140",new Bitmap(128,128));
            Check(thumbnails.Contains("memory-check-20"),"Recently drawn thumbnail was not kept by LRU");
            var worker=Task.Run(()=>{for(var i=0;i<160;i++)Call("ItemImages",null,"Store","concurrent",new Bitmap(32,32));});
            for(var i=0;i<160;i++)Call("ItemImages",null,"Draw",g,"concurrent",new Rectangle(0,0,24,24));
            worker.GetAwaiter().GetResult();
            Check(true,"Concurrent thumbnail cache replacement and drawing completed");
        }
        foreach(var key in thumbnails.Keys.Cast<string>().ToArray())Call("ItemImages",null,"Store",key,null!);
        Call("ItemImages",null,"Store","failed-thumbnail",null!);
        Call("ItemImages",null,"Request","failed-thumbnail",null!);
        Check(!((IEnumerable)Field("ItemImages",null,"Pending")!).Cast<string>().Contains("failed-thumbnail"),"Missing image caused immediate network retries while repainting");

        using var server=(IDisposable)Activator.CreateInstance(T("LocalServer"),true)!;
        T("LocalServer").GetField("_listener",Flags)!.SetValue(server,new TcpListener(IPAddress.Loopback,0));
        Call("LocalServer",server,"CacheImage","reserve/base",new byte[1024*1024],"image/png");
        var mobileCache=(IDictionary)Field("LocalServer",server,"_images")!;
        Check(mobileCache.Count==1,"Mobile image cache fixture failed");
        Call("LocalServer",server,"Stop");
        Check(mobileCache.Count==0&&((IList)Field("LocalServer",server,"_imageOrder")!).Count==0,"Stopping mobile server retained encoded map bytes");
        Call("LocalServer",server,"CacheImage","late/base",new byte[1024],"image/png");
        Check(mobileCache.Count==0,"Late request refilled cache after stop");
        var raw=(byte[])Call("LocalServer",null,"ReadAsset","mobile/icons.json")!;
        Check(raw.Length>1000&&raw[0]=='{',"Exact-length resource read truncated mobile assets");
        Console.WriteLine($"PASS: {checks} map image lifetime, shared assets, thumbnail bounds/concurrency and mobile stop checks.");
        return checks;
    }
    private static Rectangle ClientRect(this Image image)=>new(0,0,image.Width,image.Height);
}
