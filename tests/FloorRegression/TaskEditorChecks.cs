using System.Reflection;
using System.Text.Json.Nodes;
using TarkovMapLite;

internal static class TaskEditorChecks
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public;
    static object? Call(object o,string n,params object?[] args)=>o.GetType().GetMethod(n,Flags)!.Invoke(o,args);
    static T Field<T>(object o,string n)=>(T)o.GetType().GetField(n,Flags)!.GetValue(o)!;
    internal static int Run(string? output)
    {
        int checks=0;void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;}
        var setting=PlayerLocator.GetSetting("taskOverridesFile");var path=Path.Combine(Path.GetTempPath(),"task-edits-"+Guid.NewGuid().ToString("N")+".json");
        var seed=Assets.ReadText("data-seed.json")!;
        void Apply(string text)=>typeof(TarkovData).GetMethod("ApplyCache",Flags)!.Invoke(null,[text,true]);
        try
        {
            PlayerLocator.SetSetting("taskOverridesFile",path);Apply(seed);
            foreach(var map in MapPack.Maps)
            foreach(var pt in new[]{new PointF(0,0),new PointF(80,-120)})
            {
                var image=MapPack.ToImage(map,pt.X,pt.Y);var world=MapPack.FromImage(map,image.X,image.Y);
                Check(Math.Abs(world.X-pt.X)<1e-6 && Math.Abs(world.Z-pt.Y)<1e-6,"Inverse projection mismatch: "+map.Key);
            }
            const string id="6a763f35ce9a947b4d0918b6";
            var patch=TaskOverrides.Parse("""
                {"schema":1,"tasks":[{"id":"6a763f35ce9a947b4d0918b6","name":"圈地行动（维护测试）","trader":"58330581ace78e27b8b10cee",
                "meta":{"Regions":["lighthouse"],"RegionKnown":true,"MinLevel":null,"Faction":null,"Kappa":null,"Lightkeeper":null},
                "obj":[{"id":"manual-camera-1","d":"安装摄像头","t":"plantItem","c":1,"i":[],"k":[]}],
                "zones":[{"m":"lighthouse","t":"plantItem","o":"manual-camera-1","f":0,"point":true,"p":[[-4,-4],[4,-4],[4,4],[-4,4]]}]}]}
                """);
            TaskOverrides.Save(path,patch);TarkovData.ReloadTaskOverrides();
            Check(TarkovData.TaskNames[id].Contains("维护测试") && TarkovData.ZonesByMap["lighthouse"].Count(z=>z.TaskId==id)==1,"New task or geometry not loaded");
            Check(TarkovData.TaskMetadata[id].Kappa==null && TarkovData.TaskMetadata[id].MinLevel==null,"Unknown metadata invented");
            Apply(seed);Check(TarkovData.TaskNames.ContainsKey(id),"Refreshing upstream removed manual task");
            var task=patch["tasks"]![0]!.AsObject();task["zones"]=new JsonArray();TaskOverrides.Save(path,patch);Apply(seed);
            Check(!TarkovData.ZonesByMap.Values.SelectMany(z=>z).Any(z=>z.TaskId==id),"Explicit point removal failed");
            const string postman="59675ea386f77414b32bded2";
            patch["tasks"]!.AsArray().Add(new JsonObject{["id"]=postman,["zones"]=new JsonArray()});TaskOverrides.Save(path,patch);Apply(seed);
            Check(!TarkovData.ZonesByMap.Values.SelectMany(z=>z).Any(z=>z.TaskId==postman),"Old coordinate snapshot revived intentionally removed point");
            Check(File.Exists(path+".bak"),"Atomic save lost backup");
            File.WriteAllText(path,"not json");Apply(seed);
            Check(TarkovData.TaskNames.Count==499 && TaskOverrides.Error.Length>0,"Broken edit file prevented original tasks from loading");
            foreach(var bad in new[]{"""{"schema":1,"tasks":[{"id":"x"},{"id":"x"}]}""","""{"schema":1,"tasks":[{"id":"x","zones":[{"m":"lighthouse","p":[[0,0],[1,1]]}]}]}"""})
            {bool rejected=false;try{TaskOverrides.Parse(bad);}catch{rejected=true;}Check(rejected,"Invalid edit accepted");}
            TaskOverrides.Save(path,TaskOverrides.Empty());Apply(seed);
            using(var editor=new TaskEditorForm())
            {
                editor.Show();Application.DoEvents();
                var list=Field<ListBox>(editor,"_tasks");
                list.SelectedIndex=2;list.SelectedIndex=3;Call(editor,"Flush");
                Check(Field<JsonObject>(editor,"_document")["tasks"]!.AsArray().Count==0,"Browsing created overrides");
                Call(editor,"NewTask");Field<TextBox>(editor,"_name").Text="手工测试任务";Call(editor,"Flush");
                Field<CheckBox>(editor,"_place").Checked=true;Call(editor,"AddPoint",new PointF(100,200));
                var doc=Field<JsonObject>(editor,"_document");var created=doc["tasks"]!.AsArray().OfType<JsonObject>().Single();var createdId=created["id"]!.ToString();
                Check(created["name"]!.ToString()=="手工测试任务" && created["zones"]!.AsArray().Count==1,"Editor new task / map placement failed");
                Call(editor,"MovePoint",0,new PointF(123,234));Call(editor,"Flush");
                var edited=Field<JsonObject>(editor,"_task");var p=edited["zones"]![0]!["p"]!.AsArray().OfType<JsonArray>().ToArray();
                Check(Math.Abs(p.Average(v=>v[0]!.GetValue<double>())-123)<.001 && Math.Abs(p.Average(v=>v[1]!.GetValue<double>())-234)<.001,"Map drag did not move world geometry");
                Call(editor,"Save",false);Check(TarkovData.TaskNames[createdId]=="手工测试任务","Saved GUI task not visible to app");
                Check(list.SelectedItem?.ToString()=="手工测试任务","Saving renamed task did not update editor list");
                list.SelectedIndex=0;list.SelectedIndex=1;Call(editor,"Flush");
                Check(Field<JsonObject>(editor,"_document")["tasks"]!.AsArray().Count==1,"Browsing after save froze unrelated upstream tasks");
                for(var i=0;i<list.Items.Count;i++)if(list.Items[i]!.GetType().GetProperty("Id")!.GetValue(list.Items[i])?.ToString()==createdId){list.SelectedIndex=i;break;}
                var canvas=Field<MapCanvas>(editor,"_canvas");Application.DoEvents();
                var origin=(PointF)canvas.GetType().GetProperty("Origin",Flags)!.GetValue(canvas)!;
                var projected=MapPack.ToImage(canvas.Map!,123,234);var at=new Point((int)Math.Round(origin.X+projected.X*canvas.Scale),(int)Math.Round(origin.Y+projected.Y*canvas.Scale));
                var to=new Point(at.X+30,at.Y+20);var wanted=MapPack.FromImage(canvas.Map!,(to.X-origin.X)/canvas.Scale,(to.Y-origin.Y)/canvas.Scale);
                Call(canvas,"OnMouseDown",new MouseEventArgs(MouseButtons.Left,1,at.X,at.Y,0));Call(canvas,"OnMouseMove",new MouseEventArgs(MouseButtons.Left,0,to.X,to.Y,0));Call(canvas,"OnMouseUp",new MouseEventArgs(MouseButtons.Left,1,to.X,to.Y,0));Call(editor,"Flush");
                var moved=Field<JsonObject>(editor,"_task")["zones"]![0]!["p"]!.AsArray().OfType<JsonArray>().ToArray();
                Check(Math.Abs(moved.Average(v=>v[0]!.GetValue<double>())-wanted.X)<.001 && Math.Abs(moved.Average(v=>v[1]!.GetValue<double>())-wanted.Z)<.001,"Actual map input drag projection failed");
                Call(editor,"Save",false);
                if(output!=null){Directory.CreateDirectory(output);using var bitmap=new Bitmap(editor.Width,editor.Height);editor.DrawToBitmap(bitmap,new Rectangle(Point.Empty,editor.Size));bitmap.Save(Path.Combine(output,"task-editor.png"));}
                Call(editor,"ResetTask");
                Check(Field<JsonObject>(editor,"_document")["tasks"]!.AsArray().Count==0,"Reset did not remove new manual task");
                // Test window closes without a user prompt; disk persistence is checked independently above.
                editor.GetType().GetField("_dirty",Flags)!.SetValue(editor,false);
                editor.Close();
            }
        }
        finally
        {
            PlayerLocator.SetSetting("taskOverridesFile",setting);
            foreach(var file in new[]{path,path+".bak",path+".tmp"})if(File.Exists(file))File.Delete(file);
            Apply(seed);
        }
        Console.WriteLine($"PASS: {checks} authoring, inverse projection, persistence, refresh, deletion and isolation checks.");return checks;
    }
}
