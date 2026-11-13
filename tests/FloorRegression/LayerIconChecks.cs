using System.Collections;
using System.Drawing.Imaging;
using System.Reflection;
using System.Security.Cryptography;
using TarkovMapLite;

internal static class LayerIconChecks
{
    private const BindingFlags Flags = BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    private static readonly Assembly App = typeof(MapPack).Assembly;
    private static Type T(string name) => App.GetType("TarkovMapLite."+name)!;
    private static object? Call(string type,object? target,string name,params object?[] args) => T(type).GetMethod(name,Flags)!.Invoke(target,args);
    private static long Alpha(Bitmap image)
    {
        long sum=0;
        for(var y=0;y<image.Height;y++) for(var x=0;x<image.Width;x++) sum+=image.GetPixel(x,y).A;
        return sum;
    }
    private static string Hash(Bitmap image)
    {
        using var stream=new MemoryStream(); image.Save(stream,ImageFormat.Png);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
    public static int Run(string? output)
    {
        var checks=0;
        void Check(bool value,string message) { if(!value)throw new Exception(message);checks++; }
        var icons=Enum.GetValues(T("LayerIcon")).Cast<object>().ToArray();
        using var sheet=new Bitmap(1120,80+((icons.Length+3)/4)*74);
        using var g=Graphics.FromImage(sheet);
        g.Clear(Color.FromArgb(20,23,26));
        using var title=new Font("Microsoft YaHei UI",14);
        using var font=new Font("Microsoft YaHei UI",9);
        using var ink=new SolidBrush(Color.FromArgb(231,235,239));
        using var dim=new SolidBrush(Color.FromArgb(152,162,171));
        g.DrawString("统一图层符号 · 菜单 / 地图 / 异层",title,ink,20,14);
        var shapes=new HashSet<string>();
        for(var i=0;i<icons.Length;i++)
        {
            var icon=icons[i];
            using var menu=(Bitmap)Call("LayerMenuArt",null,"Create",icon,Color.FromArgb(210,225,235),20)!;
            Check(Alpha(menu)>2000,icon+": blank or too faint menu icon");
            Check(shapes.Add(Hash(menu)),icon+": silhouette duplicates another symbol");
            foreach(var size in new[]{20,30,40,60,80})
            {
                using var scaled=(Bitmap)Call("LayerMenuArt",null,"Create",icon,Color.White,size)!;
                Check(scaled.Width==size&&Alpha(scaled)>2000,icon+": high DPI size failed");
            }
            using var full=(Bitmap)Call("MarkerIcons",null,"Create",icon,Color.FromArgb(210,225,235),16,true,1f)!;
            using var faded=(Bitmap)Call("MarkerIcons",null,"Create",icon,Color.FromArgb(210,225,235),16,true,.35f)!;
            var ratio=(double)Alpha(faded)/Alpha(full);
            Check(ratio>.32&&ratio<.38,icon+": glyph/halo did not fade together");
            var x=20+(i%4)*280;var y=75+(i/4)*74;
            g.DrawImageUnscaled(menu,x,y);g.DrawImageUnscaled(full,x+36,y+2);g.DrawImageUnscaled(faded,x+66,y+2);
            g.DrawString(icon.ToString(),font,ink,x+96,y);
            g.DrawString("20px     16px     35%",font,dim,x,y+27);
        }
        var loot=T("LootKinds").GetField("All",Flags)!.GetValue(null) as Array;
        var mappedLoot=new HashSet<object>();
        foreach(var entry in loot!)
            mappedLoot.Add(Call("MarkerIcons",null,"Loot",entry!.GetType().GetProperty("Bit")!.GetValue(entry)!)!);
        Check(mappedLoot.Count==7,"Loot categories need seven distinct shapes");
        Check(!Equals(Call("MarkerIcons",null,"Container","weapon-box"),Call("MarkerIcons",null,"Container","wooden-ammo-box")),"Weapon and ammo boxes use indistinguishable shapes");
        Check(!Equals(Call("MarkerIcons",null,"Container","safe"),Call("MarkerIcons",null,"Container","cash-register")),"Safe and cash register need distinct silhouettes");
        using (var normal=new Bitmap(80,80))
        using (var highDpi=new Bitmap(160,160))
        {
            highDpi.SetResolution(192,192);
            using(var normalG=Graphics.FromImage(normal)) Call("MarkerIcons",null,"Draw",normalG,icons[0],new PointF(40,40),16f,Color.White,1f);
            using(var highG=Graphics.FromImage(highDpi)) Call("MarkerIcons",null,"Draw",highG,icons[0],new PointF(80,80),16f,Color.White,1f);
            Check(Alpha(highDpi)>Alpha(normal)*3,"Map icon did not scale with actual drawing DPI");
            using var target=Graphics.FromImage(normal);
            for(var i=0;i<530;i++) Call("MarkerIcons",null,"Draw",target,icons[0],new PointF(40,40),16f,Color.FromArgb(255,i%256,i/256,50),1f);
            var sprites=(IDictionary)T("MarkerIcons").GetField("Sprites",Flags)!.GetValue(null)!;
            Check(sprites.Count<=512,"Map icon cache grew beyond its entry limit");
            var bytes=sprites.Values.Cast<Bitmap>().Sum(image=>image.Width*image.Height*4L);
            Check(bytes<=4*1024*1024&&bytes==(long)T("MarkerIcons").GetField("_spriteBytes",Flags)!.GetValue(null)!,"Icon cache exceeded or lost its memory accounting");
        }

        using var form=(Form)Activator.CreateInstance(T("MapForm"),true)!;
        _=form.Handle;
        var menuStrip=(ContextMenuStrip)T("MapForm").GetField("_layerMenu",Flags)!.GetValue(form)!;
        Check(menuStrip.ShowCheckMargin&&menuStrip.ShowImageMargin,"Visibility checks and category symbols need separate columns");
        var rows=menuStrip.Items.OfType<ToolStripMenuItem>().ToArray();
        var lootMenu=rows.Single(row=>row.Text=="散落物资");
        var containerMenu=rows.Single(row=>row.Text=="容器");
        foreach(var parent in new[]{lootMenu,containerMenu})
        {
            Check(((ToolStripDropDownMenu)parent.DropDown).ShowCheckMargin,"Submenu check/icon columns overlap");
            foreach(var row in parent.DropDownItems.OfType<ToolStripMenuItem>().Skip(2))
                Check(row.Tag!.GetType().GetProperty("Kind")!.GetValue(row.Tag)!.ToString()!="Dot","A category still uses an anonymous dot");
        }
        var oldImage=lootMenu.Image!;
        Call("MapForm",form,"DisposeLayerIcons",menuStrip.Items);
        Check(lootMenu.Image==null,"Menu-owned icon was not detached on cleanup");
        Call("MapForm",form,"RefreshLayerIconSizes",menuStrip.Items);
        Check(lootMenu.Image!=null&&!ReferenceEquals(oldImage,lootMenu.Image),"Menu icon recreation after DPI/cleanup failed");
        // Native menu layout, not a hand-drawn approximation.
        if(output!=null)
        {
            sheet.Save(Path.Combine(output,"layer-icon-sheet.png"));
            foreach(var (name,strip) in new[]{("layer-menu",(ToolStripDropDown)menuStrip),("loot-menu",lootMenu.DropDown),("container-menu",containerMenu.DropDown)})
            {
                strip.Show(new Point(40,40)); Application.DoEvents();
                using var frame=new Bitmap(strip.Width,strip.Height);
                strip.DrawToBitmap(frame,strip.ClientRectangle);
                frame.Save(Path.Combine(output,name+".png"));strip.Close();
            }
            var canvas=(Control)T("MapForm").GetField("_canvas",Flags)!.GetValue(form)!;
            canvas.Size=new Size(1000,800);
            foreach(var mapKey in new[]{"reserve","reserve-tiles"})
            {
                var map=MapPack.Find(mapKey)!;Call("MapCanvas",canvas,"SetMap",map,map.DisplayName);
                foreach(var prop in new[]{"ShowContainers","ShowLoot","ShowSpawns","ShowHazards","ShowStatics","ShowDoors","ShowSwitches"})
                    T("MapCanvas").GetProperty(prop)!.SetValue(canvas,true);
                var pair=Call("MapDoors",null,"For",map)!;
                T("MapCanvas").GetProperty("Doors")!.SetValue(canvas,pair.GetType().GetField("Item1")!.GetValue(pair));
                T("MapCanvas").GetProperty("Switches")!.SetValue(canvas,pair.GetType().GetField("Item2")!.GetValue(pair));
                using var frame=new Bitmap(canvas.Width,canvas.Height);canvas.DrawToBitmap(frame,canvas.ClientRectangle);
                frame.Save(Path.Combine(output,mapKey+"-icons-overview.png"));
                Call("MapCanvas",canvas,"ZoomBy",3d,null!);
                using var zoomed=new Bitmap(canvas.Width,canvas.Height);canvas.DrawToBitmap(zoomed,canvas.ClientRectangle);
                zoomed.Save(Path.Combine(output,mapKey+"-icons-detail.png"));
            }
        }
        Console.WriteLine($"PASS: {checks} shared icon shapes, DPI, opacity, category mappings and menu lifecycle checks.");
        return checks;
    }
}
