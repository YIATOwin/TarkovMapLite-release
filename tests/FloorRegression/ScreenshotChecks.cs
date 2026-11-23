using System.Reflection;
using TarkovMapLite;

internal static class ScreenshotChecks
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public;
    static object? Call(object o,string n,params object?[] args)=>o.GetType().GetMethod(n,Flags)!.Invoke(o,args);
    static T Field<T>(object o,string n)=>(T)o.GetType().GetField(n,Flags)!.GetValue(o)!;
    static void Set(object o,string n,object value)=>o.GetType().GetField(n,Flags)!.SetValue(o,value);
    internal static int Run()
    {
        var checks=0;void Check(bool ok,string text){if(!ok)throw new Exception(text);checks++;}
        var root=Path.Combine(Path.GetTempPath(),"shot-checks-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var previous=Environment.GetEnvironmentVariable("TARKOVMAPLITE_LOGS");var missing=Path.Combine(root,"missing-logs");Environment.SetEnvironmentVariable("TARKOVMAPLITE_LOGS",missing);
        var now=DateTime.Now;var lower=now.AddMinutes(-2);
        string Shot(string x,DateTime time,string suffix="",string rotation="0, 0, 0, 1")
        {var path=Path.Combine(root,$"2026-11-23[12-34]_{x}, 2, 3_{rotation}_0{suffix}.png");File.WriteAllText(path,"test");File.SetLastWriteTime(path,time);return path;}
        try
        {
            var first=Shot("10",now.AddSeconds(-9));var unrelated=Path.Combine(root,"desktop.png");File.WriteAllText(unrelated,"not a game screenshot");File.SetLastWriteTime(unrelated,now);
            var player=new PlayerLocator{NotBefore=lower};player.UseFolder(root);var changes=0;player.Changed+=()=>changes++;
            player.Poll();Check(player.Current?.X==10,"Unrelated newer PNG masked valid position");Check(changes==1,"First position did not signal change");
            player.Poll();Check(changes==1 && player.Current?.X==10,"Unchanged screenshot was republished");
            Check(player.NewestInFolder()?.X==10,"Manual screenshot test still picks non-coordinate PNG");
            var second=Shot("20",now.AddSeconds(-5));player.Poll();Check(player.Current?.X==20,"New screenshot did not refresh position");
            var future=Shot("999",now.AddDays(10));player.Poll();Check(player.Current?.X==20,"Future-dated screenshot locked position");
            var restarted=new PlayerLocator{NotBefore=lower};restarted.UseFolder(root);restarted.Poll();Check(restarted.Current?.X==20,"Bad timestamp survived restart and blocked valid shot");
            File.AppendAllText(second,"changed");File.SetLastWriteTime(second,now.AddSeconds(-3));player.Poll();Check(changes==3,"Same-path screenshot rewrite was ignored");
            var scientific=Shot("+3e1",now.AddSeconds(-1),rotation:"-2.87E-05, +0.001, .5, 1.");player.Poll();
            Check(player.Current?.X==30 && double.IsFinite(player.Current.ForwardX),"Scientific quaternion or signed coordinate not parsed");
            var invalid=Shot("1e999",now);Check(PlayerLocator.Parse(new FileInfo(invalid))==null,"Non-finite position accepted");player.Poll();Check(player.Current?.X==30,"Malformed newest coordinates masked valid shot");
            Check(player.ScanReport.Contains("格式无效") && player.ScanReport.Contains("未来时间"),"Diagnostic scan counts missing");
            player.NotBefore=now.AddSeconds(1);Check(player.Current==null,"New raid retained old position");player.Poll();Check(player.Current==null && player.ReadProblem,"Old screenshot passed new-raid cutoff");
            File.SetLastWriteTime(scientific,now.AddSeconds(2));player.Poll();Check(player.Current?.X==30,"Same path could not be consumed in new raid");
            player.UseFolder(Path.Combine(root,"no-such-folder"));Check(player.Current==null,"Folder change retained old position");player.Poll();Check(player.Status.Contains("不存在"),"Missing folder diagnostic lost");player.UseFolder(root);
            // Test the form chain without the network task refresh or any installed game.
            TarkovData.LoadCached();using var form=new MapForm();_=form.Handle;Call(form,"FillMapCombo");Call(form,"ApplyMap",false);
            var locator=Field<PlayerLocator>(form,"_player");locator.NotBefore=lower;locator.UseFolder(root);Call(form,"StartLocationPolling");
            Check(Field<System.Windows.Forms.Timer>(form,"_timer").Enabled && locator.NotBefore==lower,"Starting polling moved screenshot cutoff or failed to enable timer");
            locator.Poll();Call(form,"UpdateStatus");Check(Field<Label>(form,"_status").Text.Contains("等待游戏日志识别地图"),"Missing map binding disguised as successful position");
            var log=Field<TaskLog>(form,"_log");var eventAt=now.AddSeconds(-2);
            Call(log,"ApplyApplication",$"{eventAt:yyyy-MM-dd HH:mm:ss.fff zzz}|Info|scene preset path:maps/lighthouse_preset.bundle\n",true);
            Check(Field<string>(form,"_raidMapKey")=="lighthouse" && locator.Current==null,"Map-loading reset was deferred or retained stale fix");
            Check(log.LastSceneAt is {} time && Math.Abs((time-eventAt).TotalSeconds)<.01,"Log map event time was not preserved");
            locator.Poll();Call(form,"UpdatePlayerMarker");Check(locator.Current?.X==30 && Field<MapCanvas>(form,"_canvas").PlayerPosition!=null,"Screenshot after logged map event but before poll was discarded");
            Call(form,"SelectMap","customs",false);Call(form,"TestNewestScreenshot");
            Check(Field<MapCanvas>(form,"_canvas").PlayerMapKey=="customs","Manual screenshot preview was masked by old real fix");
            Set(form,"_raidMapKey","");Set(form,"_testFix",null!);Call(form,"PollLocation");Check(Field<string>(form,"_raidMapKey")=="lighthouse","Late log replay did not recover map binding");
            Check(form.LocationDiagnostics().Contains(Path.GetFileName(scientific)) && form.LocationDiagnostics().Contains("轮询"),"Export diagnostic missing files or timer state");
            // A truncated log file must not stay beyond EOF forever.
            var application=Path.Combine(root,"application.log");File.WriteAllText(application,new string(' ',500)+"scene preset path:maps/customs_preset.bundle\n");
            var tail=new TaskLog();Call(tail,"ReadAndDispatch",application,false,false);File.WriteAllText(application,"scene preset path:maps/woods_preset.bundle\n");Call(tail,"ReadAndDispatch",application,false,false);Check(tail.LastSceneMapKey=="woods","Truncated log permanently stopped map tracking");
        }
        finally{Environment.SetEnvironmentVariable("TARKOVMAPLITE_LOGS",previous);foreach(var file in Directory.GetFiles(root))File.Delete(file);Directory.Delete(root);}
        Console.WriteLine($"PASS: {checks} screenshot parsing, selection, timestamp, restart, rewrite, raid lifecycle, diagnostics and map-binding checks.");return checks;
    }
}
