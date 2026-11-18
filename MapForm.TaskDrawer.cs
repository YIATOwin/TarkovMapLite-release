using System.Diagnostics;

namespace TarkovMapLite;

internal sealed partial class MapForm
{
    private readonly Panel _taskDrawerViewport = new() { Visible=false, BackColor=UiTokens.Line, Padding=new Padding(1) };
    private readonly System.Windows.Forms.Timer _taskDrawerTimer = new() { Interval=16 };
    private readonly Stopwatch _taskDrawerWatch = new();
    private Control? _taskSidebar, _taskDrawerBody, _taskDrawerItems;
    private bool _taskDrawerOpen;
    private double _taskDrawerProgress, _taskDrawerFrom;

    private void BuildTaskDrawer(Control sidebar, Control body, Control items)
    {
        _taskSidebar=sidebar;_taskDrawerBody=body;_taskDrawerItems=items;
        body.Dock=DockStyle.None;_taskDrawerViewport.Controls.Add(body);
        Controls.Add(_taskDrawerViewport);
        _taskDrawerViewport.AccessibleName="任务详情";
        _taskDrawerTimer.Tick+=(_,_)=>AdvanceTaskDrawer();
        _canvas.SizeChanged+=(_,_)=>LayoutTaskDrawer();
        _canvas.LocationChanged+=(_,_)=>LayoutTaskDrawer();
        sidebar.SizeChanged+=(_,_)=>LayoutTaskDrawer();
        sidebar.LocationChanged+=(_,_)=>LayoutTaskDrawer();
        _taskDrawerViewport.ParentChanged+=(_,_)=>LayoutTaskDrawer();
        DpiChanged+=(_,_)=>LayoutTaskDrawer();
    }
    private void SetTaskDrawerVisible(bool open)
    {
        if(_taskDrawerBody==null || IsDisposed || Disposing)return;
        if(_taskDrawerOpen==open){LayoutTaskDrawer();return;}
        _taskDrawerOpen=open;_taskDrawerFrom=_taskDrawerProgress;
        _taskDrawerWatch.Restart();
        if(open){_taskDrawerViewport.Visible=true;_taskDrawerViewport.BringToFront();}
        _taskDrawerTimer.Start();LayoutTaskDrawer();
    }
    private void AdvanceTaskDrawer()
    {
        var t=Math.Clamp(_taskDrawerWatch.Elapsed.TotalMilliseconds/180,0,1);
        var eased=1-Math.Pow(1-t,3);
        _taskDrawerProgress=_taskDrawerFrom+((_taskDrawerOpen?1:0)-_taskDrawerFrom)*eased;
        if(t>=1){_taskDrawerTimer.Stop();_taskDrawerWatch.Stop();if(!_taskDrawerOpen)_taskDrawerViewport.Visible=false;}
        LayoutTaskDrawer();
    }
    private void LayoutTaskDrawer()
    {
        if(_taskSidebar==null || _taskDrawerBody==null || _taskDrawerViewport.Parent!=_canvas.Parent)return;
        // Overlay the map, keeping the task list and map projection unchanged throughout the animation.
        var width=Math.Min(_taskSidebar.Width,Math.Max(0,_canvas.Width));
        var height=_canvas.Height;
        if(width<1 || height<1)return;
        var reveal=(int)Math.Round(width*_taskDrawerProgress);
        _taskDrawerViewport.SetBounds(_canvas.Right-reveal,_canvas.Top,reveal,height);
        _taskDrawerBody.SetBounds(1,1,Math.Max(1,width-2),Math.Max(1,height-2));
        if(_taskDrawerItems!=null)_taskDrawerItems.Height=Math.Clamp(height/3,TaskPx(128),TaskPx(220));
    }
}
