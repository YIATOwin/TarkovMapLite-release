// ============================================================================
// PlayerLocator.cs — 从游戏截图文件名里读出玩家坐标 / 朝向
//
// 游戏内截图的名字形如：
//   2026-09-04[18-33]_7.86, 38.06, -27.57_-0.03307, -0.13322, 0.00384, -0.99053_21.87 (0).png
// 前三个数是世界坐标 (x,y,z)，后四个是朝向四元数（Unity 约定）。
// 截图里**没有地图名**，所以"这是哪张图"由 TaskLog 的最后一次进图决定
// （MapForm._raidMapKey）；两者对不上时画布会主动不画玩家。
//
// 改动须知：
//  · 只认应用启动之后新建的截图（NotBefore），否则会把几天前的老图当当前位置。
//  · 环境变量 TARKOVMAPLITE_SHOTS 可以指定别的截图目录（测试用）。
//  · 文件名格式是游戏定的，它一变这里就哑了；表现是底栏一直"等待游戏内截图…"。
//  · 想要更实时，可以在这里加"监听文件系统变化"，现在是 3 秒轮询。
// ============================================================================
using System.Globalization;
using System.Text.RegularExpressions;

namespace TarkovMapLite;

/// <summary>
/// 从塔科夫截图的文件名里读出玩家位置和朝向。
/// 文件名形如：
/// <c>2026-09-04[18-33]_7.86, 38.06, -27.57_-0.03307, -0.13322, 0.00384, -0.99053_21.87 (0).png</c>
/// 前面三个数是世界坐标 (x, y, z)，后面四个是朝向四元数。截图里不含地图名，
/// 所以地图由"当时那次进图"决定（由 TaskLog 提供）。
/// </summary>
public sealed class PlayerLocator
{
    private const string Number = @"[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?";
    private static readonly Regex NamePattern = new(
        @"^\d{4}-\d{2}-\d{2}\[\d{2}-\d{2}(?:-\d{2})?\]_" +
        $@"({Number}),\s*({Number}),\s*({Number})_({Number}),\s*({Number}),\s*({Number}),\s*({Number})_",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private string _lastFile = "";
    private long _lastWriteTicks, _lastLength;
    private DateTime _notBefore = DateTime.Now;
    public DateTime LastPollAt {get;private set;}
    public string ScanReport {get;private set;} = "尚未扫描";
    public bool ReadProblem {get;private set;}

    public sealed record Fix(double X, double Y, double Z, double ForwardX, double ForwardZ, DateTime CapturedAt, string File);

    /// <summary>最新一次定位；没有就是 null。</summary>
    public Fix? Current { get; private set; }

    public string Folder { get; private set; } = "";
    public string Status { get; private set; } = "等待游戏内截图…";

    public event Action? Changed;

    private static string SettingsFile
    {
        get
        {
            // 回归测试使用独立设置文件，避免读写真实用户的界面偏好。
            var custom = Environment.GetEnvironmentVariable("TARKOVMAPLITE_SETTINGS");
            if (!string.IsNullOrWhiteSpace(custom))
            {
                var path = Path.GetFullPath(custom);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                return path;
            }
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TarkovMapLite");
            try { Directory.CreateDirectory(dir); } catch { }
            return Path.Combine(dir, "settings.json");
        }
    }

    /// <summary>用户在界面里手动选的截图目录；没选过返回空串。</summary>
    public static string SavedFolder
    {
        get
        {
            try
            {
                var path = SettingsFile;
                if (!File.Exists(path))
                {
                    return "";
                }
                if (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))
                    is System.Text.Json.Nodes.JsonObject root
                    && root["screenshots"]?.GetValue<string>() is { Length: > 0 } value
                    && Directory.Exists(value))
                {
                    return value;
                }
            }
            catch
            {
                // 设置文件坏了就当没选过
            }
            return "";
        }
    }

    /// <summary>记住用户选的截图目录（写 settings.json，重启后仍生效）。</summary>
    public static void SaveFolder(string folder) => Write(settings => settings["screenshots"] = folder);

    /// <summary>读一个字符串设置（settings.json 里的键），没有就返回空串。</summary>
    public static string GetSetting(string key) =>
        Read(settings => settings[key]?.GetValue<string>() ?? "");

    /// <summary>写一个字符串设置（其他键保留）。</summary>
    public static void SetSetting(string key, string value) => Write(settings => settings[key] = value);

    /// <summary>对局结束时要不要删掉本局的截图。默认关——这是删用户的文件，必须显式打开。</summary>
    public static bool CleanupShots
    {
        get => Read(settings => settings["cleanupShots"]?.GetValue<bool>() ?? false);
        set => Write(settings => settings["cleanupShots"] = value);
    }

    /// <summary>读 settings.json 后交给调用方改，改完整体写回（其他键保留）。</summary>
    private static void Write(Action<System.Text.Json.Nodes.JsonObject> change)
    {
        try
        {
            var path = SettingsFile;
            var root = File.Exists(path)
                       && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))
                           is System.Text.Json.Nodes.JsonObject existing
                ? existing
                : new System.Text.Json.Nodes.JsonObject();
            change(root);
            File.WriteAllText(path, root.ToJsonString());
        }
        catch
        {
            // 写不进去就只对本次运行生效
        }
    }

    private static T Read<T>(Func<System.Text.Json.Nodes.JsonObject, T> read)
    {
        try
        {
            var path = SettingsFile;
            if (File.Exists(path)
                && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))
                    is System.Text.Json.Nodes.JsonObject root)
            {
                return read(root);
            }
        }
        catch
        {
            // 坏文件当没设过
        }
        return default!;
    }

    /// <summary>立刻切到某个目录（界面里选完文件夹后调用）。</summary>
    public void UseFolder(string folder)
    {
        Folder = folder;
        _lastFile = "";
        Current = null;
        ReadProblem=false;
        Status = "已切换截图目录，进游戏按截图键即可定位";
        Changed?.Invoke();
    }

    /// <summary>环境变量优先（测试用），其次用户手动选的，最后是文档目录下的默认位置。</summary>
    public static string ResolveFolder()
    {
        var custom = Environment.GetEnvironmentVariable("TARKOVMAPLITE_SHOTS");
        if (!string.IsNullOrWhiteSpace(custom) && Directory.Exists(custom))
        {
            return custom;
        }
        var saved = SavedFolder;
        if (saved.Length > 0)
        {
            return saved;
        }
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(documents, "Escape From Tarkov", "Screenshots");
    }

    /// <summary>只认应用启动之后新建的截图，避免把几天前的老图当成当前位置。</summary>
    public DateTime NotBefore
    {
        get => _notBefore;
        set { _notBefore=value; Current=null; _lastFile=""; Status="等待本局新截图…"; }
    }

    public void Poll()
    {
        LastPollAt=DateTime.Now;
        if(string.IsNullOrEmpty(Folder))Folder=ResolveFolder();
        var selected=FindNewest(Folder,NotBefore,LastPollAt,out var note);
        if(selected==null){ReadProblem=true;Status=note;return;}
        ReadProblem=false;
        var info=selected.Value.File;var fix=selected.Value.Fix;
        // A path alone is not a file version: overwritten screenshots must be observed again.
        if(info.FullName==_lastFile && info.LastWriteTimeUtc.Ticks==_lastWriteTicks && info.Length==_lastLength)
        { Status=$"位置截图 {fix.CapturedAt:HH:mm:ss}"+note;return; }
        _lastFile=info.FullName;_lastWriteTicks=info.LastWriteTimeUtc.Ticks;_lastLength=info.Length;
        Current=fix;Status=$"位置截图 {fix.CapturedAt:HH:mm:ss}"+note;Changed?.Invoke();
    }

    // Evaluate each PNG independently. A non-coordinate image, inaccessible entry or bad clock
    // cannot mask every valid screenshot in the directory, including after a restart.
    private (FileInfo File,Fix Fix)? FindNewest(string folder,DateTime notBefore,DateTime now,out string note)
    {
        int count=0,invalid=0,old=0,future=0,unreadable=0;
        (FileInfo File,Fix Fix)? best=null;
        try
        {
            if(!Directory.Exists(folder)){note="截图目录不存在";ScanReport=$"目录：{folder}\n{note}";return null;}
            foreach(var path in Directory.EnumerateFiles(folder,"*.png"))
            {
                count++;
                try
                {
                    var info=new FileInfo(path);var time=info.LastWriteTime;
                    if(time>now.AddSeconds(30)){future++;continue;}
                    if(time<notBefore){old++;continue;}
                    var fix=Parse(info);if(fix==null){invalid++;continue;}
                    if(best==null || time>best.Value.File.LastWriteTime ||
                        (time==best.Value.File.LastWriteTime && (info.CreationTimeUtc>best.Value.File.CreationTimeUtc ||
                        (info.CreationTimeUtc==best.Value.File.CreationTimeUtc && string.CompareOrdinal(info.Name,best.Value.File.Name)>0))))best=(info,fix);
                }
                catch(IOException){unreadable++;}
                catch(UnauthorizedAccessException){unreadable++;}
            }
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {note="读取截图目录失败："+ex.Message;ScanReport=note;return null;}
        note=best!=null ? (invalid+future+unreadable>0?$" · 忽略 {invalid+future+unreadable} 个无效/异常文件":"") :
            count==0?"等待游戏内截图（目录内无 PNG）":
            future>0?"截图文件时间异常，等待新截图":
            invalid>0?"新截图文件名无法解析坐标":
            unreadable>0?"截图文件暂不可读取，将自动重试":"等待本局新截图（已有截图早于开始时间）";
        ScanReport=$"目录：{folder}\n本次扫描：{now:O}\n读取下限：{notBefore:O}\nPNG：{count}，过早：{old}，格式无效：{invalid}，未来时间：{future}，读取失败：{unreadable}\n选中：{best?.File.Name??"无"}\n{note}";
        return best;
    }

    /// <summary>Manual diagnostic preview: newest parseable screenshot, regardless of launch time.</summary>
    public Fix? NewestInFolder(string? folder=null)
    {
        var dir=!string.IsNullOrWhiteSpace(folder)?folder:(Folder.Length>0?Folder:ResolveFolder());
        var best=FindNewest(dir,DateTime.MinValue,DateTime.Now,out var note);
        if(best==null)Status=note;
        return best?.Fix;
    }

    /// <summary>文件名是否符合游戏截图的坐标命名（清理时只认这种，避免误删别的 PNG）。</summary>
    public static bool LooksLikeScreenshot(string fileName) =>
        NamePattern.IsMatch(Path.GetFileNameWithoutExtension(fileName));
    /// <summary>从一张截图的文件名解析坐标和朝向；文件名不符合格式返回 null。</summary>
    public static Fix? Parse(FileInfo file)
    {
        var match = NamePattern.Match(Path.GetFileNameWithoutExtension(file.Name));
        if (!match.Success)
        {
            return null;
        }
        var values=new double[7];
        for(var i=0;i<values.Length;i++)
            if(!double.TryParse(match.Groups[i+1].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out values[i]) || !double.IsFinite(values[i]))return null;
        var x=values[0];var y=values[1];var z=values[2];
        if(Math.Abs(x)>float.MaxValue || Math.Abs(y)>float.MaxValue || Math.Abs(z)>float.MaxValue)return null;
        var (forwardX,forwardZ)=Forward(values[3],values[4],values[5],values[6]);
        return new Fix(x, y, z, forwardX, forwardZ, file.LastWriteTime, file.Name);
    }
    /// <summary>把朝向四元数（Unity 的 qx,qy,qz,qw）换成世界坐标里的前方向量（只看水平面）。</summary>
    private static (double X, double Z) Forward(double qx, double qy, double qz, double qw)
    {
        var largest=Math.Max(Math.Max(Math.Abs(qx),Math.Abs(qy)),Math.Max(Math.Abs(qz),Math.Abs(qw)));
        if(largest>0){qx/=largest;qy/=largest;qz/=largest;qw/=largest;}
        var length = Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
        if (length < 1e-9)
        {
            return (0, 1);
        }
        qx /= length;
        qy /= length;
        qz /= length;
        qw /= length;
        var fx = 2 * (qx * qz + qw * qy);
        var fz = 1 - 2 * (qx * qx + qy * qy);
        var flat = Math.Sqrt(fx * fx + fz * fz);
        return flat < 1e-6 ? (0, 1) : (fx / flat, fz / flat);
    }
}
