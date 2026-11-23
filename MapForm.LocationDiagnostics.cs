using System.Text;

namespace TarkovMapLite;

internal sealed partial class MapForm
{
    internal string LocationDiagnostics()
    {
        var folder=_player.Folder.Length>0?_player.Folder:PlayerLocator.ResolveFolder();
        var text=new StringBuilder();
        text.AppendLine($"TarkovMapLite 截图定位诊断 · {DateTime.Now:O}");
        text.AppendLine($"版本：{AppUpdate.CurrentVersion}　轮询：{_timer.Enabled} / {_timer.Interval}ms");
        text.AppendLine($"最后轮询：{_player.LastPollAt:O}\n截图目录：{folder}\n读取下限：{_player.NotBefore:O}");
        text.AppendLine($"读取状态：{_player.Status}\n日志目录：{_log.LogsFolder}\n日志状态：{_log.Status}");
        text.AppendLine($"日志地图：{_log.LastSceneMapKey}　进图时间：{_log.LastSceneAt:O}\n定位绑定地图：{_raidMapKey}　浏览地图：{CurrentMap?.Key}");
        text.AppendLine($"上次有效位置：{_player.Current}\n测试位置：{_testFix}\n上次扫描：\n{_player.ScanReport}");
        text.AppendLine("\n目录内最近 12 个 PNG（仅读取文件名与时间，不读取图像）：");
        try
        {
            foreach(var file in Directory.EnumerateFiles(folder,"*.png").Select(p=>new FileInfo(p)).OrderByDescending(f=>f.LastWriteTimeUtc).Take(12))
                text.AppendLine($"{file.Name}\n  修改：{file.LastWriteTime:O}　创建：{file.CreationTime:O}　字节：{file.Length}　坐标可解析：{PlayerLocator.Parse(file)!=null}");
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){text.AppendLine(ex.Message);}
        return text.ToString();
    }
    private void ShowLocationDiagnostics()
    {
        using var dialog=new Form{Text="截图定位诊断",ClientSize=new(860,560),StartPosition=FormStartPosition.CenterParent,MinimumSize=new(620,360)};
        var box=new TextBox{Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Both,WordWrap=false,Dock=DockStyle.Fill,Font=new("Microsoft YaHei UI",9),Text=LocationDiagnostics()};
        var actions=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=42};
        var refresh=new Button{Text="刷新诊断",AutoSize=true};var copy=new Button{Text="复制",AutoSize=true};var save=new Button{Text="保存诊断…",AutoSize=true};
        refresh.Click+=(_,_)=>box.Text=LocationDiagnostics();
        copy.Click+=(_,_)=>{try{Clipboard.SetText(box.Text);}catch{MessageBox.Show(dialog,"剪贴板暂不可用，可保存为文本。");}};
        save.Click+=(_,_)=>{using var picker=new SaveFileDialog{Filter="诊断文本|*.txt",FileName="截图定位诊断.txt"};if(picker.ShowDialog(dialog)==DialogResult.OK)try{File.WriteAllText(picker.FileName,box.Text);}catch(Exception ex){MessageBox.Show(dialog,ex.Message);}};
        actions.Controls.AddRange([refresh,copy,save]);dialog.Controls.Add(box);dialog.Controls.Add(actions);dialog.ShowDialog(this);
    }
}
