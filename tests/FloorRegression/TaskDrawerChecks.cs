using System.Diagnostics;
using System.Reflection;
using TarkovMapLite;

internal static class TaskDrawerChecks
{
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    private static T Field<T>(object target,string name)=>(T)target.GetType().GetField(name,Flags)!.GetValue(target)!;
    private static object? Call(object target,string name,params object?[] args)=>target.GetType().GetMethod(name,Flags)!.Invoke(target,args);
    private static void Pump(int milliseconds=240){var watch=Stopwatch.StartNew();while(watch.ElapsedMilliseconds<milliseconds){Application.DoEvents();Thread.Sleep(8);}}
    public static int Run(string? output)
    {
        var count=0;void Check(bool ok,string message){if(!ok)throw new Exception(message);count++;}
        typeof(TarkovData).GetMethod("ApplyCache",Flags)!.Invoke(null,[Assets.ReadText("data-seed.json")!,true]);
        PlayerLocator.SetSetting("view","");PlayerLocator.SetSetting("taskFilter","");
        using var form=new MapForm();_=form.Handle;Call(form,"FillMapCombo");Call(form,"ApplyMap",false);
        Call(form,"ChangeTaskFilter",new TaskFilter {Region="all",Status=-1});
        using var host=new Form{ClientSize=new(1120,800),Text="任务侧滑详情验证"};
        foreach(var control in form.Controls.Cast<Control>().ToArray())host.Controls.Add(control);
        host.Show();Application.DoEvents();
        var drawer=Field<Panel>(form,"_taskDrawerViewport");var canvas=Field<Control>(form,"_canvas");
        var sidebar=Field<Control>(form,"_taskSidebar");var list=Field<TaskListControl>(form,"_taskList");
        var detail=Field<RichTextBox>(form,"_detail");var items=Field<ListBox>(form,"_itemList");
        Check(!drawer.Visible,"Unselected task drawer appears at startup");
        Check(list.Parent!.Height>sidebar.Height/2,"Task list still shares half its space with details");
        var mapBounds=canvas.Bounds;var listBounds=list.Bounds;
        const string postman="59675ea386f77414b32bded2";
        Call(form,"SelectTaskFromMobile",postman);Pump(40);
        Check(drawer.Visible && drawer.Width>0 && drawer.Width<=sidebar.Width,"Drawer did not begin sliding on task selection");
        Pump();
        Check(drawer.Width==sidebar.Width && drawer.Right==sidebar.Left && drawer.Top==canvas.Top,"Drawer is not aligned left of the task list");
        Check(canvas.Bounds==mapBounds && list.Bounds==listBounds,"Drawer resized map or task list");
        Check(detail.Text.Contains("信") && items.Items.Count>0,"Drawer lost task description or items");
        Check(detail.RectangleToScreen(detail.ClientRectangle).Left<sidebar.PointToScreen(Point.Empty).X,"Description still sits below the task list");
        var selected=list.SelectedItem;
        Call(form,"RefreshTaskList");Pump();
        Check(drawer.Visible && list.SelectedItem?.ToString()==selected?.ToString(),"Refreshing collapsed valid selection");
        var other=TaskQuery.Build().First(t=>!t.IsStory && t.Id!=postman && TarkovData.TaskObjectives.GetValueOrDefault(t.Id)?.Any(o=>o.Items.Count>0)==true);
        Call(form,"SelectTaskFromMobile",other.Id);Pump();
        Check(Field<string>(form,"_currentTaskId")==other.Id && drawer.Visible,"Switching task failed to update open drawer");
        foreach(var size in new[]{new Size(760,480),new Size(1680,1050)})
        {
            host.ClientSize=size;Application.DoEvents();
            Check(drawer.Right==sidebar.Left && drawer.Left>=canvas.Left && drawer.Bottom<=canvas.Bottom,"Resizing left drawer outside map area");
            Check(detail.Height>60 && items.Height>60,"Description or item scroll viewport collapsed");
        }
        host.ClientSize=new(1120,800);Application.DoEvents();
        Check(drawer.Visible && Field<bool>(form,"_taskDrawerOpen"),"Resizing cleared the open drawer");
        Check(host.Controls.GetChildIndex(drawer)==0,"Drawer no longer covers the map after layout");
        if(output!=null)
        {
            Directory.CreateDirectory(output);using var image=new Bitmap(host.Width,host.Height);
            host.DrawToBitmap(image,new Rectangle(Point.Empty,host.Size));
            // WinForms DrawToBitmap reverses overlapping child paint order; composite the verified topmost panel last.
            var origin=host.PointToScreen(Point.Empty);var offset=new Point(origin.X-host.Left,origin.Y-host.Top);
            drawer.DrawToBitmap(image,new Rectangle(drawer.Left+offset.X,drawer.Top+offset.Y,drawer.Width,drawer.Height));
            image.Save(Path.Combine(output,"task-drawer-open.png"));
        }
        Call(form,"SelectTaskFromMobile","");Pump(30);
        Call(form,"SelectTaskFromMobile",postman);Pump();
        Check(drawer.Visible && Field<string>(form,"_currentTaskId")==postman,"Rapid close/reopen completed stale close animation");
        Call(form,"OnKeyDown",new KeyEventArgs(Keys.Escape));Pump();
        Check(!drawer.Visible && list.SelectedIndex==-1 && detail.TextLength==0,"Escape did not clear selection and hide details");
        Check(!Field<System.Windows.Forms.Timer>(form,"_taskDrawerTimer").Enabled,"Hidden drawer still animates");
        Call(form,"SelectTaskFromMobile",postman);Pump();
        var close=drawer.Controls[0].Controls.Cast<Control>().SelectMany(c=>c.Controls.Cast<Control>()).OfType<Button>()
            .Single(b=>b.AccessibleName=="取消选择并关闭任务详情");
        close.PerformClick();Pump();
        Check(!drawer.Visible && list.SelectedIndex==-1,"Close button did not clear selection");
        Call(form,"SelectTaskFromMobile",postman);Pump();
        Call(form,"ChangeTaskFilter",new TaskFilter{Region="all",Status=-1,Search="no-task-drawer-result-349835"});Pump();
        Check(!drawer.Visible && Field<string>(form,"_currentTaskId")=="","Filtering selected task out did not hide drawer");
        if(output!=null){using var image=new Bitmap(host.Width,host.Height);host.DrawToBitmap(image,new Rectangle(Point.Empty,host.Size));image.Save(Path.Combine(output,"task-drawer-closed.png"));}
        PlayerLocator.SetSetting("view","");PlayerLocator.SetSetting("taskFilter","");
        Console.WriteLine($"PASS: {count} task drawer slide, selection, refresh, resize, scrolling space, reversal, Escape and filter lifecycle checks.");
        return count;
    }
}
