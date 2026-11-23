using System.Drawing.Drawing2D;

namespace TarkovMapLite;

internal sealed partial class MapCanvas
{
    internal sealed record EditorArea(string Map,int Floor,PointF[] WorldPoints);
    internal bool EditorEnabled {get;set;}
    internal List<EditorArea> EditorZones {get;set;}=[];
    internal int EditorSelectedIndex {get;set;}=-1;
    internal event Action<int,PointF>? EditorMoved;
    internal event Action<PointF>? EditorAdd;
    private int _editorDrag=-1;
    private PointF EditorWorld(Point point)
    {
        var origin=Origin;var world=MapPack.FromImage(_map!, (point.X-origin.X)/Scale,(point.Y-origin.Y)/Scale);
        return new((float)world.X,(float)world.Z);
    }
    private bool EditorDown(MouseEventArgs e)
    {
        if(!EditorEnabled || _map==null)return false;
        if(e.Button==MouseButtons.Right){EditorAdd?.Invoke(EditorWorld(e.Location));return true;}
        if(e.Button!=MouseButtons.Left)return false;
        var origin=Origin;var best=18.0*DeviceDpi/96;var found=-1;
        for(var i=0;i<EditorZones.Count;i++)
        {
            var z=EditorZones[i];if(MapPack.Find(z.Map)?.Data!=_map.Data)continue;
            var (x,y)=MapPack.ToImage(_map,z.WorldPoints.Average(p=>p.X),z.WorldPoints.Average(p=>p.Y));
            var distance=Math.Sqrt(Math.Pow(origin.X+x*Scale-e.X,2)+Math.Pow(origin.Y+y*Scale-e.Y,2));
            if(distance<best){best=distance;found=i;}
        }
        if(found<0)return false;
        _editorDrag=found;EditorSelectedIndex=found;Capture=true;Cursor=Cursors.SizeAll;Invalidate();return true;
    }
    private bool EditorMove(MouseEventArgs e)
    {
        if(_editorDrag<0)return false;
        EditorMoved?.Invoke(_editorDrag,EditorWorld(e.Location));Invalidate();return true;
    }
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if(!Capture)_editorDrag=-1;
    }
    private void DrawEditor(Graphics g)
    {
        if(!EditorEnabled || _map==null)return;
        var origin=Origin;var floor=MapFloors.FloorOf(_map.Key,_layer);
        for(var i=0;i<EditorZones.Count;i++)
        {
            var z=EditorZones[i];if(MapPack.Find(z.Map)?.Data!=_map.Data)continue;
            var color=z.Floor==floor?Color.OrangeRed:Color.FromArgb(80,255,100,50);
            var pts=z.WorldPoints.Select(v=>{var p=MapPack.ToImage(_map,v.X,v.Y);return new PointF(origin.X+(float)(p.X*Scale),origin.Y+(float)(p.Y*Scale));}).ToArray();
            using var pen=new Pen(color,i==EditorSelectedIndex?3:1.5f);using var fill=new SolidBrush(Color.FromArgb(z.Floor==floor?55:15,color));
            g.FillPolygon(fill,pts);g.DrawPolygon(pen,pts);
            var cx=pts.Average(v=>v.X);var cy=pts.Average(v=>v.Y);
            using var dot=new SolidBrush(color);g.FillEllipse(dot,cx-6,cy-6,12,12);
            TextRenderer.DrawText(g,(i+1).ToString(),Font,new Point((int)cx+10,(int)cy-8),color,Color.FromArgb(20,24,28));
        }
    }
}
