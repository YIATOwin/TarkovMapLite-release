using System.Text.Json;
using System.Text.Json.Nodes;

namespace TarkovMapLite;

internal sealed class TaskEditorForm : Form
{
    private readonly JsonArray _base=TarkovData.BaseTasks();
    private JsonObject _document=TaskOverrides.Read();
    private JsonObject? _task;
    private readonly TextBox _search=new(){PlaceholderText="搜索任务名称或 ID",Dock=DockStyle.Top};
    private readonly ListBox _tasks=new(){Dock=DockStyle.Fill,DisplayMember="Name"};
    private readonly TextBox _name=new(),_trader=new(),_regions=new(),_level=new(),_source=new();
    private readonly ComboBox _faction=Choice("未知","Any","USEC","BEAR"),_kappa=Choice("未知","必需","非必需"),_lightkeeper=Choice("未知","必需","非必需");
    private readonly DataGridView _objectives=Grid(),_zones=Grid(),_vertices=Grid();
    private readonly TextBox _json=new(){Multiline=true,ScrollBars=ScrollBars.Both,WordWrap=false,Dock=DockStyle.Fill,Font=new("Consolas",10)};
    private readonly MapCanvas _canvas=new(){Dock=DockStyle.Fill,EditorEnabled=true};
    private readonly ComboBox _map=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=170,DisplayMember="DisplayName"};
    private readonly ComboBox _floor=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=110,DisplayMember="Name"};
    private readonly CheckBox _place=new(){Text="右键添加点位",AutoSize=true};
    private readonly Label _status=new(){Dock=DockStyle.Bottom,Height=32,AutoEllipsis=true};
    private string _path=TaskOverrides.FilePath;
    private bool _loading,_dirty,_taskDirty,_flushing,_flushingVertices,_reading;
    internal event Action? Saved;
    private void MarkDirty(){_dirty=true;_taskDirty=true;}
    private sealed record Row(string Id,string Name){public override string ToString()=>Name;}
    private sealed record Floor(string Layer,string Name,int Number);
    private static ComboBox Choice(params string[] options){var c=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};c.Items.AddRange(options);c.SelectedIndex=0;return c;}
    private static DataGridView Grid()=>new(){Dock=DockStyle.Fill,AllowUserToAddRows=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,RowHeadersVisible=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=false,BackgroundColor=Color.FromArgb(27,32,38)};
    internal TaskEditorForm()
    {
        Text="任务数据维护";ClientSize=new(1440,860);MinimumSize=new(1100,650);Font=new("Microsoft YaHei UI",9f);StartPosition=FormStartPosition.CenterParent;
        var toolbar=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true,Padding=new(8)};
        Button Action(string text,Action action){var b=new Button{Text=text,AutoSize=true};b.Click+=(_,_)=>Guard(action);toolbar.Controls.Add(b);return b;}
        Action("打开 JSON",Open);Action("保存",()=>Save(false));Action("另存为…",()=>Save(true));Action("新增任务",NewTask);Action("撤销此任务修订",ResetTask);
        var remote=new Button{Text="从 Git 地址读取",AutoSize=true};toolbar.Controls.Add(remote);
        remote.Click+=async(_,_)=>
        {
            if((_dirty || _json.Modified) && MessageBox.Show(this,"放弃尚未保存的草稿并读取远程文件？","读取任务修订",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;
            using var dialog=new Form{Text="任务修订 JSON 地址",ClientSize=new(680,125),StartPosition=FormStartPosition.CenterParent,MinimizeBox=false,MaximizeBox=false,FormBorderStyle=FormBorderStyle.FixedDialog};
            var input=new TextBox{Dock=DockStyle.Top,Text=PlayerLocator.GetSetting("taskOverridesUrl"),PlaceholderText="https://raw.githubusercontent.com/账号/仓库/main/task-overrides.json"};
            var ok=new Button{Text="读取",Dock=DockStyle.Bottom,Height=34,DialogResult=DialogResult.OK};dialog.Controls.Add(new Label{Dock=DockStyle.Fill,Text="使用 GitHub Raw / Pages 的公开 JSON 地址。读取并校验后成为草稿，点击保存后生效。",Padding=new(8)});dialog.Controls.Add(input);dialog.Controls.Add(ok);dialog.AcceptButton=ok;
            if(dialog.ShowDialog(this)!=DialogResult.OK)return;
            _reading=true;Enabled=false;SetStatus("正在读取远程 JSON…");
            try{var doc=await TaskOverrides.FetchAsync(input.Text.Trim());_document=doc;_task=null;_taskDirty=false;_dirty=true;_json.Modified=false;PlayerLocator.SetSetting("taskOverridesUrl",input.Text.Trim());FillTasks();SetStatus("远程 JSON 已读取并校验，请保存到本地使其生效。");}
            catch(Exception ex){SetStatus(ex.Message);MessageBox.Show(this,ex.Message,"读取失败");}
            finally{_reading=false;if(!IsDisposed)Enabled=true;}
        };
        var note=new Label{AutoSize=true,Text="修订优先于接口；保存后主窗口生效。拖动标记校准，中键/空白处拖动地图。",Margin=new(8,8,0,0)};toolbar.Controls.Add(note);
        var left=new Panel{Dock=DockStyle.Left,Width=235,Padding=new(8)};left.Controls.Add(_tasks);left.Controls.Add(_search);
        var right=new Panel{Dock=DockStyle.Right,Width=430,Padding=new(6)};
        var tabs=new TabControl{Dock=DockStyle.Fill};right.Controls.Add(tabs);
        var metadata=new TabPage("任务");var objectives=new TabPage("目标");var geometry=new TabPage("点位 / 区域");var json=new TabPage("修订 JSON");tabs.TabPages.AddRange([metadata,objectives,geometry,json]);
        var fields=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=2,Padding=new(6)};fields.ColumnStyles.Add(new(SizeType.Absolute,85));fields.ColumnStyles.Add(new(SizeType.Percent,100));
        void Field(string name,Control c){var n=fields.RowCount++;fields.RowStyles.Add(new(SizeType.Absolute,38));c.Dock=DockStyle.Fill;fields.Controls.Add(new Label{Text=name,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,n);fields.Controls.Add(c,1,n);}
        Field("名称",_name);Field("商人 ID",_trader);Field("地区",_regions);Field("最低等级",_level);Field("阵营",_faction);Field("Kappa",_kappa);Field("灯塔商人",_lightkeeper);Field("来源 / 备注",_source);
        metadata.Controls.Add(fields);metadata.Controls.Add(new Label{Text="地区使用地图 key，多个用逗号分隔；空等级表示未知。\n商人使用游戏 ID；目标和所需物品在“目标”页编辑。\n任务 ID、新增/删除字段可在“修订 JSON”中修改。",Dock=DockStyle.Bottom,Height=100,Padding=new(8)});
        foreach(var (key,title) in new[]{("id","目标 ID"),("d","目标描述"),("t","类型"),("c","数量"),("i","物品 ID（逗号分隔）"),("k","钥匙组 JSON")})_objectives.Columns.Add(key,title);
        _objectives.Columns["d"]!.FillWeight=250;
        var objectiveActions=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=36};
        var addObjective=new Button{Text="添加目标",AutoSize=true};var deleteObjective=new Button{Text="删除目标",AutoSize=true};objectiveActions.Controls.AddRange([addObjective,deleteObjective]);objectives.Controls.Add(_objectives);objectives.Controls.Add(objectiveActions);
        addObjective.Click+=(_,_)=>Guard(()=>{_objectives.Rows.Add("manual-"+Guid.NewGuid().ToString("N"),"新目标","visit","1","","[]");MarkDirty();});
        deleteObjective.Click+=(_,_)=>{if(_objectives.CurrentRow is {} r){_objectives.Rows.Remove(r);MarkDirty();}};
        foreach(var (key,title) in new[]{("m","地图 key"),("f","楼层编号"),("o","目标 ID"),("t","类型")})_zones.Columns.Add(key,title);
        _zones.Columns.Add(new DataGridViewCheckBoxColumn{Name="known",HeaderText="楼层已知"});
        _zones.Height=220;_zones.Dock=DockStyle.Top;
        _vertices.Columns.Add("x","世界 X");_vertices.Columns.Add("z","世界 Z");
        var zoneActions=new FlowLayoutPanel{Dock=DockStyle.Bottom,AutoSize=true};
        void ZoneAction(string title,Action action){var b=new Button{Text=title,AutoSize=true};b.Click+=(_,_)=>Guard(action);zoneActions.Controls.Add(b);}
        ZoneAction("删除位置",()=>{Flush();if(_zones.CurrentRow is {} r){TaskZones.RemoveAt(r.Index);Changed();}});
        ZoneAction("新增顶点",()=>{FlushVertices();if(CurrentZone is {} z){z["point"]=false;var p=z["p"]!.AsArray();p.Add(p.Last()!.DeepClone());MarkDirty();LoadVertices();Redraw();}});
        ZoneAction("删除顶点",()=>{FlushVertices();if(CurrentZone?["p"] is JsonArray p && _vertices.CurrentRow is {} r && p.Count>3){p.RemoveAt(r.Index);MarkDirty();LoadVertices();Redraw();}});
        geometry.Controls.Add(_vertices);geometry.Controls.Add(_zones);geometry.Controls.Add(zoneActions);
        var applyJson=new Button{Text="校验并应用 JSON 到草稿",Dock=DockStyle.Bottom,Height=36};json.Controls.Add(_json);json.Controls.Add(applyJson);
        applyJson.Click+=(_,_)=>Guard(()=>{_document=TaskOverrides.Parse(_json.Text);_json.Modified=false;_task=null;_taskDirty=false;_dirty=true;FillTasks();SetStatus("JSON 校验通过；保存后生效。");});
        tabs.Selecting+=(_,e)=>{try{if(e.TabPage==json){Flush();_json.Text=_document.ToJsonString(TaskOverrides.Pretty);_json.Modified=false;}else if(tabs.SelectedTab==json && _json.Modified)throw new InvalidDataException("请先点击“校验并应用 JSON 到草稿”，或撤回 JSON 文本修改。");}catch(Exception ex){e.Cancel=true;SetStatus(ex.Message);MessageBox.Show(this,ex.Message,"请检查输入");}};
        var center=new Panel{Dock=DockStyle.Fill,Padding=new(4)};var maps=new FlowLayoutPanel{Dock=DockStyle.Top,Height=40};maps.Controls.AddRange([_map,_floor,_place]);center.Controls.Add(_canvas);center.Controls.Add(maps);
        Controls.Add(center);Controls.Add(right);Controls.Add(left);Controls.Add(_status);Controls.Add(toolbar);
        _map.Items.AddRange(MapPack.Maps.ToArray());_map.SelectedIndexChanged+=(_,_)=>Guard(ChangeMap);
        _floor.SelectedIndexChanged+=(_,_)=>{if(_floor.SelectedItem is Floor f){_canvas.SetLayer(f.Layer);_canvas.Invalidate();}};
        _canvas.FloorRequested+=layer=>{for(var i=0;i<_floor.Items.Count;i++)if((_floor.Items[i] as Floor)?.Layer==layer){_floor.SelectedIndex=i;break;}};
        _canvas.EditorAdd+=p=>Guard(()=>AddPoint(p));_canvas.EditorMoved+=(i,p)=>Guard(()=>MovePoint(i,p));
        _zones.SelectionChanged+=(_,_)=>Guard(()=>{if(!_loading){FlushVertices();LoadVertices();_canvas.EditorSelectedIndex=_zones.CurrentRow?.Index??-1;_canvas.Invalidate();}});
        _zones.CellEndEdit+=(_,_)=>Guard(()=>{MarkDirty();Flush();Redraw();});_vertices.CellEndEdit+=(_,_)=>Guard(()=>{FlushVertices();MarkDirty();Redraw();});
        _zones.CurrentCellDirtyStateChanged+=(_,_)=>{if(_zones.IsCurrentCellDirty)_zones.CommitEdit(DataGridViewDataErrorContexts.Commit);};
        _objectives.CellEndEdit+=(_,_)=>MarkDirty();
        foreach(var c in new Control[]{_name,_trader,_regions,_level,_source})c.TextChanged+=(_,_)=>{if(!_loading)MarkDirty();};
        foreach(var c in new[]{_faction,_kappa,_lightkeeper})c.SelectedIndexChanged+=(_,_)=>{if(!_loading)MarkDirty();};
        _search.TextChanged+=(_,_)=>Guard(()=>{Flush();FillTasks();});_tasks.SelectedIndexChanged+=(_,_)=>Guard(SelectTask);
        _map.SelectedItem=MapPack.Find("lighthouse")??MapPack.Maps.First();FillTasks();
        SetStatus(TaskOverrides.Error.Length>0?TaskOverrides.Error:_path);
        Theme(this);
        FormClosing+=(_,e)=>{if(_reading){e.Cancel=true;return;}if(_dirty || _json.Modified){var answer=MessageBox.Show(this,"草稿尚未保存，是否保存？","任务数据维护",MessageBoxButtons.YesNoCancel);if(answer==DialogResult.Cancel)e.Cancel=true;else if(answer==DialogResult.Yes){try{Save(false);}catch(Exception ex){MessageBox.Show(this,ex.Message);e.Cancel=true;}if(_dirty)e.Cancel=true;}}};
    }
    private JsonArray TaskZones => _task!["zones"]!.AsArray();
    private JsonObject? CurrentZone => _task!=null && _zones.CurrentRow is {} r && r.Index<TaskZones.Count ? TaskZones[r.Index] as JsonObject:null;
    private int _vertexZone=-1;
    private void SetStatus(string text)=>_status.Text=text;
    private void Guard(Action action){try{action();}catch(Exception e){SetStatus(e.Message);MessageBox.Show(this,e.Message,"请检查输入",MessageBoxButtons.OK,MessageBoxIcon.Information);}}
    private JsonArray Catalog()=>TaskOverrides.Apply(_base,_document);
    private void FillTasks()
    {
        _loading=true;try{_tasks.Items.Clear();foreach(var t in Catalog().OfType<JsonObject>().Where(t=>(t["name"]?.ToString()+t["id"]?.ToString()).Contains(_search.Text,StringComparison.OrdinalIgnoreCase)).OrderBy(t=>t["name"]?.ToString()))_tasks.Items.Add(new Row(t["id"]!.ToString(),t["name"]?.ToString()??t["id"]!.ToString()));}finally{_loading=false;}
        if(_tasks.Items.Count>0)_tasks.SelectedIndex=0;else{_task=null;_zones.Rows.Clear();_vertices.Rows.Clear();_canvas.EditorZones=[];_canvas.Invalidate();}
    }
    private void SelectTask()
    {
        if(_loading || _tasks.SelectedItem is not Row row)return;Flush();
        _taskDirty=false;_task=(JsonObject)Catalog().OfType<JsonObject>().Single(t=>t["id"]!.ToString()==row.Id).DeepClone();
        _task["zones"]=new JsonArray(TaskLocations.Merge(_task).Select(z=>(JsonNode)z.DeepClone()).ToArray());
        _loading=true;
        try
        {
            var m=_task["meta"] as JsonObject??new();_name.Text=_task["name"]?.ToString()??"";_trader.Text=_task["trader"]?.ToString()??"";
            _regions.Text=string.Join(',',(m["Regions"] as JsonArray??[]).Select(v=>v!.ToString()));_level.Text=m["MinLevel"]?.ToString()??"";_source.Text=_task["source"]?.ToString()??"";
            _faction.SelectedItem=m["Faction"]?.ToString()??"未知";if(_faction.SelectedIndex<0)_faction.SelectedIndex=0;
            _kappa.SelectedIndex=Flag(m["Kappa"]);_lightkeeper.SelectedIndex=Flag(m["Lightkeeper"]);
            _objectives.Rows.Clear();foreach(var o in (_task["obj"] as JsonArray??[]).OfType<JsonObject>())_objectives.Rows.Add(o["id"]?.ToString()??"",o["d"]?.ToString()??"",o["t"]?.ToString()??"",o["c"]?.ToString()??"",string.Join(',',(o["i"] as JsonArray??[]).Select(v=>v!.ToString())),o["k"]?.ToJsonString()??"[]");
            LoadZones();
        }
        finally{_loading=false;}
        LoadVertices();Redraw();SetStatus($"{row.Id} · {_path}");
    }
    private static int Flag(JsonNode? node)=>node==null?0:node.GetValue<bool>()?1:2;
    private static JsonNode? FlagValue(ComboBox box)=>box.SelectedIndex==0?null:JsonValue.Create(box.SelectedIndex==1);
    private static string[] Parts(string value)=>value.Split([',','，'],StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
    private void Flush()
    {
        if(_loading || _task==null || _flushing)return;
        _flushing=true;
        try
        {
        _objectives.EndEdit();_zones.EndEdit();FlushVertices();
        _task["name"]=_name.Text.Trim();_task["trader"]=_trader.Text.Trim();_task["source"]=_source.Text.Trim();
        var m=_task["meta"] as JsonObject;if(m==null){m=new();_task["meta"]=m;}
        m["Regions"]=new JsonArray(Parts(_regions.Text).Select(s=>(JsonNode?)JsonValue.Create(s)).ToArray());m["RegionKnown"]=Parts(_regions.Text).Length>0 || m["RegionKnown"]?.GetValue<bool>()==true;
        m["MinLevel"]=string.IsNullOrWhiteSpace(_level.Text)?null:JsonValue.Create(int.Parse(_level.Text));m["Faction"]=_faction.SelectedIndex==0?null:JsonValue.Create(_faction.Text);m["Kappa"]=FlagValue(_kappa);m["Lightkeeper"]=FlagValue(_lightkeeper);
        var objectives=new JsonArray();foreach(DataGridViewRow r in _objectives.Rows)
        {string Cell(string k)=>r.Cells[k].Value?.ToString()??"";objectives.Add(new JsonObject{["id"]=Cell("id"),["d"]=Cell("d"),["t"]=Cell("t"),["c"]=Cell("c").Length==0?null:JsonValue.Create(int.Parse(Cell("c"))),["i"]=new JsonArray(Parts(Cell("i")).Select(s=>(JsonNode?)JsonValue.Create(s)).ToArray()),["k"]=JsonNode.Parse(Cell("k").Length==0?"[]":Cell("k"))});}
        _task["obj"]=objectives;
        for(var i=0;i<_zones.Rows.Count;i++)
        {var r=_zones.Rows[i];var z=TaskZones[i]!.AsObject();var map=r.Cells["m"].Value?.ToString();var floor=int.Parse(r.Cells["f"].Value?.ToString()??"0");if(map!=z["m"]?.ToString() || floor!=(z["f"]?.GetValue<int>()??0))z.Remove("y");z["m"]=map;z["f"]=floor;z["o"]=r.Cells["o"].Value?.ToString()??"";z["t"]=r.Cells["t"].Value?.ToString()??"visit";z["floorKnown"]=Convert.ToBoolean(r.Cells["known"].Value);}
        // Only a user change creates an override; browsing does not freeze all upstream tasks.
        if(_taskDirty){Stage();_taskDirty=false;}
        }
        finally{_flushing=false;}
    }
    private void Stage()
    {
        if(_task==null)return;_task["manualLocations"]=true;_task["updatedAt"]=DateTimeOffset.Now.ToString("o");
        var rows=_document["tasks"]!.AsArray();var old=rows.OfType<JsonObject>().FirstOrDefault(t=>t["id"]!.ToString()==_task["id"]!.ToString());if(old!=null)rows.Remove(old);rows.Add(_task.DeepClone());
    }
    private void LoadZones()
    {
        _vertexZone=-1;_zones.Rows.Clear();foreach(var z in TaskZones.OfType<JsonObject>())_zones.Rows.Add(z["m"]?.ToString()??"",z["f"]?.ToString()??"0",z["o"]?.ToString()??"",z["t"]?.ToString()??"visit",z["floorKnown"]?.GetValue<bool>()??true);
    }
    private void LoadVertices()
    {
        _vertexZone=-1;_vertices.Rows.Clear();if(CurrentZone?["p"] is not JsonArray p)return;
        foreach(var v in p.OfType<JsonArray>())_vertices.Rows.Add(v[0]!.GetValue<double>(),v[1]!.GetValue<double>());_vertexZone=_zones.CurrentRow!.Index;
    }
    private void FlushVertices()
    {
        if(_loading || _vertexZone<0 || _task==null || _vertexZone>=TaskZones.Count || _flushingVertices)return;
        _flushingVertices=true;
        try{_vertices.EndEdit();var p=new JsonArray();foreach(DataGridViewRow row in _vertices.Rows)p.Add(new JsonArray(Convert.ToDouble(row.Cells[0].Value),Convert.ToDouble(row.Cells[1].Value)));TaskZones[_vertexZone]!["p"]=p;}
        finally{_flushingVertices=false;}
    }
    private void Changed(){MarkDirty();Stage();_loading=true;try{LoadZones();}finally{_loading=false;}LoadVertices();Redraw();}
    private void Redraw()
    {
        if(_task==null)return;
        _canvas.EditorZones=TaskZones.OfType<JsonObject>().Select(z=>new MapCanvas.EditorArea(z["m"]!.ToString(),z["f"]?.GetValue<int>()??0,z["p"]!.AsArray().OfType<JsonArray>().Select(p=>new PointF((float)p[0]!.GetValue<double>(),(float)p[1]!.GetValue<double>())).ToArray())).ToList();_canvas.Invalidate();
    }
    private void ChangeMap()
    {
        if(_map.SelectedItem is not MapProjection map)return;_canvas.SetMap(map,map.DisplayName);_floor.Items.Clear();_floor.Items.Add(new Floor("","地面",0));foreach(var l in map.Layers)_floor.Items.Add(new Floor(l.SvgLayer,l.Name,MapFloors.FloorOf(map.Key,l.SvgLayer)));_floor.SelectedIndex=0;Redraw();
    }
    private void AddPoint(PointF point)
    {
        if(!_place.Checked || _task==null || _map.SelectedItem is not MapProjection map)return;Flush();
        var objective=_objectives.CurrentRow;var oid=objective?.Cells["id"].Value?.ToString()??"";var type=objective?.Cells["t"].Value?.ToString()??"visit";
        TaskZones.Add(new JsonObject{["m"]=map.Data,["f"]=(_floor.SelectedItem as Floor)?.Number??0,["floorKnown"]=true,["o"]=oid,["t"]=type,["point"]=true,["p"]=new JsonArray(new JsonArray((double)point.X-4,(double)point.Y-4),new JsonArray((double)point.X+4,(double)point.Y-4),new JsonArray((double)point.X+4,(double)point.Y+4),new JsonArray((double)point.X-4,(double)point.Y+4))});Changed();_zones.CurrentCell=_zones.Rows[^1].Cells[0];
    }
    private void MovePoint(int index,PointF point)
    {
        if(_task==null)return;Flush();var zone=TaskZones[index]!.AsObject();var p=zone["p"]!.AsArray();var pairs=p.OfType<JsonArray>().ToArray();var dx=point.X-pairs.Average(v=>v[0]!.GetValue<double>());var dz=point.Y-pairs.Average(v=>v[1]!.GetValue<double>());
        foreach(var v in pairs){v[0]=v[0]!.GetValue<double>()+dx;v[1]=v[1]!.GetValue<double>()+dz;}zone.Remove("y");MarkDirty();Stage();_zones.CurrentCell=_zones.Rows[index].Cells[0];LoadVertices();Redraw();
    }
    private void NewTask()
    {
        Flush();var id="manual-"+Guid.NewGuid().ToString("N");var task=new JsonObject{["id"]=id,["name"]="新任务",["trader"]="",["obj"]=new JsonArray(),["zones"]=new JsonArray(),["meta"]=new JsonObject()};_document["tasks"]!.AsArray().Add(task);_task=null;_taskDirty=false;_dirty=true;_search.Clear();FillTasks();for(var i=0;i<_tasks.Items.Count;i++)if(((Row)_tasks.Items[i]).Id==id){_tasks.SelectedIndex=i;break;}
        SetStatus("已添加草稿；可在修订 JSON 中将 manual-… 改为游戏真实任务 ID，以匹配日志。");
    }
    private void ResetTask()
    {
        if(_task==null)return;var id=_task["id"]!.ToString();var rows=_document["tasks"]!.AsArray();var old=rows.OfType<JsonObject>().FirstOrDefault(t=>t["id"]!.ToString()==id);if(old!=null)rows.Remove(old);_task=null;_taskDirty=false;_dirty=true;FillTasks();
    }
    private void Open()
    {
        if((_dirty || _json.Modified) && MessageBox.Show(this,"放弃尚未保存的草稿并打开其它文件？","打开 JSON",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;
        using var dialog=new OpenFileDialog{Filter="任务修订 JSON|*.json",Title="打开 Git 仓库中的 task-overrides.json"};if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        var document=TaskOverrides.Parse(File.ReadAllText(dialog.FileName));_path=dialog.FileName;_document=document;_task=null;_taskDirty=false;_dirty=false;FillTasks();SetStatus("已打开草稿；点击保存使主窗口使用此文件。");
    }
    private void Save(bool saveAs)
    {
        Flush();if(_json.Modified)throw new InvalidDataException("JSON 文本尚未应用，请先校验并应用。");
        if(saveAs){using var d=new SaveFileDialog{Filter="任务修订 JSON|*.json",FileName="task-overrides.json"};if(d.ShowDialog(this)!=DialogResult.OK)return;_path=d.FileName;}
        TaskOverrides.Save(_path,_document);PlayerLocator.SetSetting("taskOverridesFile",Path.GetFullPath(_path));TarkovData.ReloadTaskOverrides();_dirty=false;_taskDirty=false;
        if(_task!=null && _tasks.SelectedIndex>=0){var index=_tasks.SelectedIndex;_loading=true;try{_tasks.Items[index]=new Row(_task["id"]!.ToString(),_task["name"]!.ToString());_tasks.SelectedIndex=index;}finally{_loading=false;}}
        Saved?.Invoke();SetStatus($"已保存并应用：{_path}");
    }
    private static void Theme(Control parent)
    {
        if(parent is MapCanvas)return;
        parent.BackColor=Color.FromArgb(25,30,36);parent.ForeColor=Color.FromArgb(224,231,239);
        if(parent is TextBox or ComboBox or ListBox)parent.BackColor=Color.FromArgb(35,43,53);
        if(parent is Button b){b.FlatStyle=FlatStyle.Flat;b.FlatAppearance.BorderColor=Color.FromArgb(70,85,100);}
        if(parent is DataGridView g)
        {
            g.EnableHeadersVisualStyles=false;g.GridColor=Color.FromArgb(62,74,87);
            g.DefaultCellStyle.BackColor=Color.FromArgb(30,37,46);g.DefaultCellStyle.ForeColor=Color.FromArgb(224,231,239);
            g.ColumnHeadersDefaultCellStyle.BackColor=Color.FromArgb(42,53,65);g.ColumnHeadersDefaultCellStyle.ForeColor=Color.White;
        }
        foreach(Control c in parent.Controls)Theme(c);
    }
}
