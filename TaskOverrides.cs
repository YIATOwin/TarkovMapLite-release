using System.Text.Json;
using System.Text.Json.Nodes;

namespace TarkovMapLite;

/// <summary>Explicit author corrections, applied after online/cache data; never written into that cache.</summary>
internal static class TaskOverrides
{
    private static readonly HttpClient Http=new(){Timeout=TimeSpan.FromSeconds(25)};
    internal static async Task<JsonObject> FetchAsync(string url)
    {
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri) || uri.Scheme!=Uri.UriSchemeHttps)throw new InvalidDataException("请填写 HTTPS 的 JSON 原始文件地址，例如 GitHub Raw 或 GitHub Pages。");
        using var response=await Http.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        using var stream=await response.Content.ReadAsStreamAsync();using var data=new MemoryStream();var buffer=new byte[16384];
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(25));
        int read;while((read=await stream.ReadAsync(buffer,timeout.Token))>0){if(data.Length+read>4*1024*1024)throw new InvalidDataException("任务修订文件超过 4MB。");await data.WriteAsync(buffer.AsMemory(0,read),timeout.Token);}
        return Parse(System.Text.Encoding.UTF8.GetString(data.ToArray()).TrimStart('\uFEFF'));
    }
    internal static readonly JsonSerializerOptions Pretty = new() { WriteIndented=true, Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal static string Error { get; private set; } = "";
    internal static string FilePath => PlayerLocator.GetSetting("taskOverridesFile") is { Length:>0 } path ? path :
        Path.Combine(Environment.GetEnvironmentVariable("TARKOVMAPLITE_SETTINGS") is { Length:>0 } test
            ? Path.GetDirectoryName(Path.GetFullPath(test))! : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"TarkovMapLite"),"task-overrides.json");
    internal static JsonObject Empty() => new() { ["schema"]=1,["tasks"]=new JsonArray() };
    internal static JsonObject Parse(string text)
    {
        var root=JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("根节点必须是 JSON 对象。");
        if(root["schema"]?.GetValue<int>()!=1 || root["tasks"] is not JsonArray tasks)throw new InvalidDataException("需要 schema: 1 和 tasks 数组。");
        var ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var node in tasks)
        {
            if(node is not JsonObject t || t["id"] is not JsonValue id || !id.TryGetValue<string>(out var key) || string.IsNullOrWhiteSpace(key) || !ids.Add(key))
                throw new InvalidDataException("任务 ID 不能为空或重复。");
            foreach(var field in new[]{"name","trader","wiki","slug"})if(t[field]!=null && (t[field] is not JsonValue value || !value.TryGetValue<string>(out _)))throw new InvalidDataException($"{key}: {field} 必须是文本。");
            if(t["name"]?.ToString() is "")throw new InvalidDataException($"{key}: 任务名不能为空。");
            if(t["manualLocations"]!=null)_=t["manualLocations"]!.GetValue<bool>();
            if(t["meta"]!=null)
            {
                var meta=t["meta"]!.Deserialize<TaskMetadata>() ?? throw new InvalidDataException("元数据无效。");
                if(meta.MinLevel<0 || meta.Regions==null || meta.Regions.Any(string.IsNullOrWhiteSpace))throw new InvalidDataException("等级或地区无效。");
                if(meta.Faction!=null && TaskMetadata.NormalizeFaction(meta.Faction)==null)throw new InvalidDataException("阵营应为 Any、USEC、BEAR 或 null。");
            }
            if(t["obj"]!=null)
            {
                if(t["obj"] is not JsonArray objectives)throw new InvalidDataException("obj 必须是数组。");
                var objectiveIds=new HashSet<string>();
                foreach(var o in objectives.OfType<JsonObject>())
                {
                    foreach(var f in new[]{"id","d","t"})if(o[f]!=null && (o[f] is not JsonValue v || !v.TryGetValue<string>(out _)))throw new InvalidDataException($"目标的 {f} 必须是文本。");
                    if(string.IsNullOrWhiteSpace(o["d"]?.GetValue<string>()))throw new InvalidDataException("目标描述不能为空。");
                    var oid=o["id"]?.GetValue<string>()??"";
                    if(oid.Length>0 && !objectiveIds.Add(oid))throw new InvalidDataException("目标 ID 重复。");
                    if(o["c"]!=null && o["c"]!.GetValue<int>()<0)throw new InvalidDataException("目标数量不能为负。");
                    foreach(var f in new[]{"i","k"})if(o[f]!=null && o[f] is not JsonArray)throw new InvalidDataException($"{f} 必须是数组。");
                    static bool IdArray(JsonArray array)=>array.All(x=>x is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text));
                    if(o["i"] is JsonArray items && !IdArray(items))throw new InvalidDataException("物品 ID 必须是文本数组。");
                    if(o["k"] is JsonArray keys && keys.Any(k=>k is not JsonArray group || !IdArray(group)))throw new InvalidDataException("钥匙组必须是二维 ID 文本数组。");
                }
                if(objectives.Any(o=>o is not JsonObject))throw new InvalidDataException("目标必须是对象。");
            }
            if(t["zones"]!=null)
            {
                if(t["zones"] is not JsonArray zones)throw new InvalidDataException("zones 必须是数组。");
                foreach(var nodeZone in zones)
                {
                    if(nodeZone is not JsonObject z || MapPack.Find(z["m"]?.GetValue<string>()??"")==null)throw new InvalidDataException("点位地图无效。");
                    foreach(var f in new[]{"o","t"})if(z[f]!=null && (z[f] is not JsonValue v || !v.TryGetValue<string>(out _)))throw new InvalidDataException($"区域的 {f} 必须是文本。");
                    if(z["p"] is not JsonArray polygon || polygon.Count<3)throw new InvalidDataException("区域至少需要三个坐标；单点也使用小方框保存。");
                    foreach(var point in polygon)
                        if(point is not JsonArray pair || pair.Count!=2 || pair.Any(v=>v is not JsonValue n || !n.TryGetValue<double>(out var x) || !double.IsFinite(x)))throw new InvalidDataException("坐标必须是有限的 [世界X, 世界Z] 数值。");
                    if(z["f"]!=null)_=z["f"]!.GetValue<int>();
                    foreach(var flag in new[]{"floorKnown","point"})if(z[flag]!=null)_=z[flag]!.GetValue<bool>();
                    if(z["y"]!=null && !double.IsFinite(z["y"]!.GetValue<double>()))throw new InvalidDataException("高度无效。");
                }
            }
        }
        return root;
    }
    internal static JsonObject Read()
    {
        try
        {
            var root=File.Exists(FilePath)?Parse(File.ReadAllText(FilePath)):Empty();
            Error="";return root;
        }
        catch(Exception e){Error=$"手工任务数据读取失败：{e.Message}";return Empty();}
    }
    internal static void Save(string path,JsonObject document)
    {
        var normalized=Parse(document.ToJsonString());
        normalized["tasks"]=new JsonArray(normalized["tasks"]!.AsArray().OfType<JsonObject>().OrderBy(t=>t["id"]!.ToString(),StringComparer.Ordinal).Select(t=>(JsonNode)t.DeepClone()).ToArray());
        var text=normalized.ToJsonString(Pretty)+Environment.NewLine;
        path=Path.GetFullPath(path);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp=path+".tmp";File.WriteAllText(temp,text);
        if(File.Exists(path))File.Copy(path,path+".bak",true);
        File.Move(temp,path,true);
    }
    internal static JsonArray Apply(JsonArray original,JsonObject? document=null)
    {
        // Bundled corrections travel with the exe; the chosen local file can override them.
        JsonObject bundled;
        try{bundled=Parse(Assets.ReadText("task-overrides.json")??Empty().ToJsonString());}
        catch(Exception e){Error="内置任务修订读取失败："+e.Message;bundled=Empty();}
        var local=document??Read();
        if(((JsonArray)bundled["tasks"]!).Count==0 && ((JsonArray)local["tasks"]!).Count==0)return original;
        var result=(JsonArray)original.DeepClone();
        foreach(var patch in ((JsonArray)bundled["tasks"]!).Concat((JsonArray)local["tasks"]!).OfType<JsonObject>())
        {
            var id=patch["id"]!.ToString();
            var task=result.OfType<JsonObject>().FirstOrDefault(t=>string.Equals(t["id"]?.ToString(),id,StringComparison.OrdinalIgnoreCase));
            if(task==null){task=new JsonObject{["id"]=id};result.Add(task);}
            foreach(var field in patch)
            {
                if(field.Key=="meta" && field.Value is JsonObject meta)
                {
                    var merged=task["meta"] as JsonObject;
                    if(merged==null){merged=new();task["meta"]=merged;}
                    foreach(var value in meta)merged[value.Key]=value.Value?.DeepClone();
                }
                else task[field.Key]=field.Value?.DeepClone();
            }
            // A supplied zones array is a deliberate replacement, including an empty array.
            if(patch.ContainsKey("zones"))task["manualLocations"]=true;
        }
        return result;
    }
}
