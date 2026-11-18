using System.Reflection;
using TarkovMapLite;

internal static class TaskMapVisualChecks
{
    internal static int Run(string? output)
    {
        var checks=0;
        void Check(bool ok,string text){if(!ok)throw new Exception(text);checks++;}
        ZoneShape Zone(string task,float x,float y,int floor=0,string type="visit",string objective="") => new()
        { TaskId=task,TaskName=task=="a"?"多地点任务":"另一项任务",Type=type,ObjectiveId=objective,Floor=floor,FloorKnown=true,
          Center=new(x,y),WorldPoints=[new(x-4,y-4),new(x+4,y-4),new(x+4,y+4),new(x-4,y+4)],
          Points=[new(x-4,y-4),new(x+4,y-4),new(x+4,y+4),new(x-4,y+4)] };
        var zones=new List<ZoneShape>{Zone("a",80,110),Zone("a",400,110),Zone("b",80,250),Zone("b",400,250)};
        using var canvas=new MapCanvas{Size=new(640,360),OnlyActive=false};
        var flags=BindingFlags.NonPublic|BindingFlags.Instance;
        Bitmap Render(List<ZoneShape> rows,string? selected)
        {
            typeof(MapCanvas).GetField("_projected",flags)!.SetValue(canvas,rows);canvas.SelectedTaskId=selected;
            var bitmap=new Bitmap(640,360);using var g=Graphics.FromImage(bitmap);
            g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            typeof(MapCanvas).GetMethod("DrawZones",flags)!.Invoke(canvas,[g,1f,PointF.Empty]);return bitmap;
        }
        int Pixels(Bitmap image,Rectangle box){var n=0;for(var y=box.Top;y<box.Bottom;y++)for(var x=box.Left;x<box.Right;x++)if(image.GetPixel(x,y).A>0)n++;return n;}
        using var overview=Render(zones,null);
        Check(Pixels(overview,new(180,107,130,7))==0,"Overview still draws task connections");
        Check(Pixels(overview,new(180,247,130,7))==0,"Other task overview still draws connections");
        Check(Pixels(overview,new(412,85,150,20))>20,"Far second task place has no independent name/number label");
        using var selected=Render(zones,"a");
        Check(Pixels(selected,new(180,107,130,7))>20,"Selected task lost association line");
        Check(Pixels(selected,new(70,240,30,30))==0,"Selecting task leaks another task's point");
        using var reset=Render(zones,null);
        Check(Pixels(reset,new(180,107,130,7))==0,"Deselecting did not remove lines");
        using var candidates=Render([Zone("a",80,110,type:"findQuestItem",objective:"item"),Zone("a",400,110,type:"findQuestItem",objective:"item")],"a");
        Check(Pixels(candidates,new(180,107,130,7))==0,"Alternative quest-item spawns are still linked");
        using var crossFloor=Render([Zone("a",80,110),Zone("a",400,110,1)],"a");
        Check(Pixels(crossFloor,new(180,107,130,7))==0,"Association line crosses floors");
        var clustered=TaskMapLayout.Build([Zone("a",80,110),Zone("a",85,115),Zone("a",85,115,1)],1,PointF.Empty,0);
        Check(clustered.Count==2 && clustered[0].Count==2,"Nearby grouping merged floors or failed to collapse nearby same-floor points");
        Check(TaskMapLayout.Build([Zone("a",80,110),Zone("a",100,110)],2,PointF.Empty,0).Count==2,"Zooming in did not reveal nearby places");
        Check(TaskMapLayout.Label("任务",1,2,false)=="任务 · 2" && TaskMapLayout.Label("任务",1,2,true)=="任务 · 候选 2","Labels lost matching task identity or candidate semantics");
        if(output!=null){Directory.CreateDirectory(output);overview.Save(Path.Combine(output,"task-points-overview.png"));selected.Save(Path.Combine(output,"task-points-selected.png"));candidates.Save(Path.Combine(output,"task-points-candidates.png"));}
        Console.WriteLine($"PASS: {checks} task overview labels, selection-only connections, candidates, floors, zoom and deselection rendering checks.");
        return checks;
    }
}
