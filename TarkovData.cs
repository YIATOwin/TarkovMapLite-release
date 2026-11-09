// ============================================================================
// TarkovData.cs — 数据层：任务 / 物品 / 撤离点 / 转移点 / BTR / 刷新点
//
// 数据来源：json.tarkov.dev（/pve/tasks、/pve/maps、中文名走 *_zh 接口）。
// 24 小时内只读本地缓存 %LOCALAPPDATA%\TarkovMapLite\data-v8.json；
// 缓存里的 schema 和 CurrentSchema 不一致会强制重拉——所以升级数据结构时
// **只改 CurrentSchema，不用改文件名**（文件名一直叫 v8，别被它骗了）。
// 首次运行没网时用内嵌的 Assets/data-seed.json 兜底（离线可用）。
//
// 填好的静态表（全在文件后半段的 Apply/Parse 里赋值）：
//   ZonesByMap    地图 data key → 任务区域（**世界坐标**，画之前才投影）
//   Extracts / Transits / BtrStops   同样按 data key 分组、世界坐标
//   TaskObjectives / TaskNames / ItemNames / TaskWiki / TaskSlug / TaskTrader  按任务 id
//   SpawnPoints   地图 data key → 物品 id → 世界坐标点（"要带的物品刷在哪"）
//   SceneToMap    游戏场景名 → 地图 key（TaskLog 靠它判断进的是哪张图）
//
// 改动须知：
//  · 坐标一律存世界坐标，投影延后到 MapCanvas / MapForm 按**当前展示的地图**做。
//    以前这里直接存像素坐标，共享数据的第二张图（海关瓦片版）就整体错位了。
//  · 加字段要同时改四处：CurrentSchema、RefreshAsync 的解析、ApplyCache 的回填、
//    Assets/data-seed.json。少改一处就会出现"在线正常、离线全空"。
//  · JSON 字段名是接口的英文驼峰，中文名有单独的接口，别自己翻译。
// ============================================================================
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TarkovMapLite;

/// <summary>撤离点 / 转移点：世界坐标 + 名字。</summary>
public sealed class MapMarker
{
    public required string Name { get; init; }
    public required double X { get; init; }
    public required double Z { get; init; }
    /// <summary>extract / transit</summary>
    public required string Kind { get; init; }
    /// <summary>撤离阵营（PMC/Scav/通用）或转移目标地图。</summary>
    public string Note { get; init; } = "";
    /// <summary>投影到图片像素坐标后的位置（由 <see cref="Project"/> 按当前地图算）。</summary>
    public PointF Screen { get; set; }

    /// <summary>
    /// 按 <paramref name="map"/> 的投影得到一份可以直接画的副本。
    /// 撤离点数据是按"数据地图 key"共享的，直接改 <see cref="Screen"/> 会让
    /// 海关 / 海关（瓦片）互相覆盖，所以每次切图都重新算一份。
    /// </summary>
    public MapMarker Project(MapProjection map)
    {
        var (u, v) = MapPack.ToImage(map, X, Z);
        return new MapMarker
        {
            Name = Name,
            X = X,
            Z = Z,
            Kind = Kind,
            Note = Note,
            Screen = new PointF((float)u, (float)v),
        };
    }
}

/// <summary>从 /pve/maps 里一次性抓下来的东西。</summary>
public sealed class MapExtras
{
    public Dictionary<string, Dictionary<string, List<double[]>>> Spawns { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>场景名（streets_preset）→ 地图 key。</summary>
    public Dictionary<string, string> Scenes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<MapMarker>> Extracts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>BTR 停车点（森林 / 塔科夫街区）。</summary>
    public Dictionary<string, List<MapMarker>> BtrStops { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<MapMarker>> Transits { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>任务的一个目标：描述、类型、数量，以及需要的物品/钥匙（id）。</summary>
public sealed class ObjectiveInfo
{
    public required string Description { get; init; }
    public required string Type { get; init; }
    public int? Count { get; init; }
    public IReadOnlyList<string> Items { get; init; } = Array.Empty<string>();
    /// <summary>外层是"任一把"，内层是"要一起带"。</summary>
    public IReadOnlyList<IReadOnlyList<string>> Keys { get; init; } = Array.Empty<IReadOnlyList<string>>();
}

/// <summary>
/// 一个任务区域。坐标只保留世界坐标（x,z），画到哪张图由 MapCanvas 按当前地图投影。
/// 以前这里直接存"图片像素坐标"，于是共享同一份数据的第二张图（海关 / 海关瓦片）
/// 会拿到另一套投影下的像素，区域就整体跑到图外去了。
/// </summary>
public sealed class ZoneShape
{
    public required string TaskId { get; init; }
    public required string TaskName { get; init; }
    public required string Type { get; init; }
    /// <summary>世界坐标（x,z）。同一份数据被多张图共享时只有这一份。</summary>
    public required PointF[] WorldPoints { get; init; }
    /// <summary>投影到某张图的像素坐标；由 <see cref="Project"/> 填好，供绘制用。</summary>
    public PointF[] Points { get; init; } = Array.Empty<PointF>();
    public PointF Center { get; init; }
    public RectangleF Bounds { get; init; }
    /// <summary>推断出来的楼层：0 地面，1 二层，2 三层，-1 地下。</summary>
    public int Floor { get; init; }

    /// <summary>按 <paramref name="map"/> 的投影算出一份可以直接画的副本（世界坐标不变）。</summary>
    public ZoneShape Project(MapProjection map)
    {
        var points = new PointF[WorldPoints.Length];
        for (var i = 0; i < points.Length; i++)
        {
            var (u, v) = MapPack.ToImage(map, WorldPoints[i].X, WorldPoints[i].Y);
            points[i] = new PointF((float)u, (float)v);
        }
        var minX = points.Min(p => p.X);
        var minY = points.Min(p => p.Y);
        return new ZoneShape
        {
            TaskId = TaskId,
            TaskName = TaskName,
            Type = Type,
            WorldPoints = WorldPoints,
            Points = points,
            Center = new PointF(points.Average(p => p.X), points.Average(p => p.Y)),
            Bounds = new RectangleF(minX, minY,
                points.Max(p => p.X) - minX, points.Max(p => p.Y) - minY),
            Floor = Floor,
        };
    }
}

/// <summary>
/// 任务数据：从 json.tarkov.dev 拉一次（中英各一份），投影缓存到本地，
/// 之后 24 小时内只用缓存，启动不联网。
/// </summary>
public static class TarkovData
{
    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri("https://json.tarkov.dev"),
        // 默认 100 秒；网络不通时会让"正在获取…"卡很久，对玩家来说就是软件假死
        Timeout = TimeSpan.FromSeconds(25),
    };

    /// <summary>tarkov.dev 的地图 id → 本地地图 key。地图 id 是稳定的，所以写死。</summary>
    private static readonly Dictionary<string, string> MapIdToKey = new(StringComparer.OrdinalIgnoreCase)
    {
        ["55f2d3fd4bdc2d5f408b4567"] = "factory",
        ["59fc81d786f774390775787e"] = "night-factory",
        ["6a8dd1325e15a5ea2e010820"] = "infected-factory",
        ["56f40101d2720b2a4d8b45d6"] = "customs",
        ["5704e3c2d2720bac5b8b4567"] = "woods",
        ["5704e4dad2720bb55b8b4567"] = "lighthouse",
        ["5704e554d2720bac5b8b456e"] = "shoreline",
        ["5704e5fad2720bc05b8b4567"] = "reserve",
        ["5714dbc024597771384a510d"] = "interchange",
        ["5714dc692459777137212e12"] = "streets-of-tarkov",
        ["653e6760052c01c1c805532f"] = "ground-zero",
        ["65b8d6f5cdde2479cb2a3125"] = "ground-zero-21",
        ["68236e8153654e8c1200798a"] = "ground-zero-tutorial",
        ["65cc8f81a9aac3e77d0cfd3e"] = "terminal",
    };

    public static Dictionary<string, List<ZoneShape>> ZonesByMap { get; private set; } = new();
    public static Dictionary<string, List<ObjectiveInfo>> TaskObjectives { get; private set; } = new();
    public static Dictionary<string, string> TaskNames { get; private set; } = new();
    /// <summary>物品 id → 中文名（只包含任务目标里引用到的，几百条）</summary>
    public static Dictionary<string, string> ItemNames { get; private set; } = new();
    /// <summary>地图 key → 物品 id → 可能的散落生成点（世界坐标 x,z）。</summary>
    public static Dictionary<string, Dictionary<string, List<PointF>>> SpawnPoints { get; private set; } = new();
    /// <summary>场景名（streets_preset）→ 地图 key，用来判断"进的是哪张图"。</summary>
    public static Dictionary<string, string> SceneToMap { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public static Dictionary<string, List<MapMarker>> Extracts { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public static Dictionary<string, List<MapMarker>> Transits { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>BTR 停车点（只有森林和塔科夫街区有）。</summary>
    public static Dictionary<string, List<MapMarker>> BtrStops { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public static Dictionary<string, string> TaskTrader { get; private set; } = new();
    /// <summary>任务 id → wiki 链接（地点照片都在 wiki 页上）。</summary>
    public static Dictionary<string, string> TaskWiki { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>任务 id → tarkov.dev 的 slug（任务页地址 /task/&lt;slug&gt;）。</summary>
    public static Dictionary<string, string> TaskSlug { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public static DateTime FetchedAt { get; private set; }
    public static string Status { get; private set; } = "";
    /// <summary>最近一次是否成功从接口拉到数据。</summary>
    public static bool Online { get; private set; }
    /// <summary>当前用的是打包进 exe 的静态数据（没有缓存、也没联网时）。</summary>
    public static bool FromSeed { get; private set; }
    /// <summary>缓存格式版本：老的内置数据升到这里就要强制刷新一次。</summary>
    public const int CurrentSchema = 9;
    public static int Schema { get; private set; }

    private static string CachePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TarkovMapLite");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "data-v8.json");
        }
    }

    /// <summary>启动时先用缓存；没有缓存就用打包进去的静态数据，缓存超过一天再后台刷新。</summary>
    public static void LoadCached()
    {
        try
        {
            if (File.Exists(CachePath))
            {
                ApplyCache(File.ReadAllText(CachePath), seed: false);
                return;
            }
            // 第一次运行（或者别人电脑上刚解压）：先用静态数据，界面立刻有内容
            if (!LoadSeed())
            {
                Status = "无任务数据";
            }
        }
        catch (Exception ex)
        {
            Status = "缓存读取失败：" + ex.Message;
        }
    }

    /// <summary>读 exe 里打包的那份数据（我这边抓取时的快照）。</summary>
    private static bool LoadSeed()
    {
        var text = Assets.ReadText("data-seed.json");
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        ApplyCache(text, seed: true);
        return true;
    }

    private static void ApplyCache(string text, bool seed)
    {
        var doc = JsonNode.Parse(text);
        if (doc is not JsonObject root)
        {
            return;
        }
        FetchedAt = DateTime.TryParse(root["fetched"]?.GetValue<string>(), out var at) ? at : DateTime.MinValue;
        Schema = root["schema"]?.GetValue<int?>() ?? 0;
        ParseRecords(root["tasks"] as JsonArray);
        var items = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root["items"] is JsonObject itemNodes)
        {
            foreach (var pair in itemNodes)
            {
                var name = pair.Value?.GetValue<string>();
                if (!string.IsNullOrEmpty(name))
                {
                    items[pair.Key] = name;
                }
            }
        }
        ItemNames = items;
        SpawnPoints = PackSpawns(ReadSpawns(root["spawns"]));
        SceneToMap = ReadScenes(root["scenes"]);
        Extracts = ProjectMarkers(ReadMarkers(root["extracts"]));
        Transits = ProjectMarkers(ReadMarkers(root["transits"]));
        BtrStops = ProjectMarkers(ReadMarkers(root["btrStops"]));
        FromSeed = seed;
        Status = seed
            ? $"内置数据 {FetchedAt:MM-dd}（未联网）"
            : $"任务数据 {FetchedAt:MM-dd HH:mm}";
    }

    public static async Task<bool> RefreshAsync()
    {
        try
        {
            Status = "正在获取任务数据…";
            var data = await FetchTranslatedAsync("/pve/tasks");
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var records = new List<object>();
            var names = new Dictionary<string, string>();
            var traders = new Dictionary<string, string>();
            // data.tasks 是以任务 id 为键的对象（不是数组）
            var taskNodes = (data["tasks"] as JsonObject)?.Select(pair => pair.Value)
                            ?? (data["tasks"] as JsonArray)?.Cast<JsonNode?>()
                            ?? Enumerable.Empty<JsonNode?>();
            foreach (var node in taskNodes)
            {
                if (node is not JsonObject t)
                {
                    continue;
                }
                var id = t["id"]?.GetValue<string>() ?? "";
                if (id.Length == 0)
                {
                    continue;
                }
                var name = t["name"]?.GetValue<string>() ?? id;
                names[id] = name;
                traders[id] = t["trader"]?.ToString() ?? "";

                var zones = new List<object>();
                var objectives = new List<object>();
                foreach (var objective in (t["objectives"] as JsonArray) ?? new JsonArray())
                {
                    var type = objective?["type"]?.GetValue<string>() ?? "";
                    var description = objective?["description"]?.GetValue<string>() ?? "";
                    var itemIds = ReadStringArray(objective?["items"]);
                    foreach (var also in ReadStringArray(objective?["useAny"]))
                    {
                        if (!itemIds.Contains(also))
                        {
                            itemIds.Add(also);
                        }
                    }
                    var questItem = objective?["questItem"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(questItem) && !itemIds.Contains(questItem))
                    {
                        itemIds.Add(questItem);
                    }
                    var keys = new List<List<string>>();
                    foreach (var group in (objective?["requiredKeys"] as JsonArray) ?? new JsonArray())
                    {
                        var inner = ReadStringArray(group);
                        if (inner.Count > 0)
                        {
                            keys.Add(inner);
                        }
                    }
                    foreach (var itemId in itemIds)
                    {
                        referenced.Add(itemId);
                    }
                    foreach (var group in keys)
                    {
                        foreach (var keyId in group)
                        {
                            referenced.Add(keyId);
                        }
                    }
                    if (description.Length > 0)
                    {
                        objectives.Add(new
                        {
                            d = description,
                            t = type,
                            c = objective?["count"]?.GetValue<int?>(),
                            i = itemIds,
                            k = keys,
                        });
                    }
                    foreach (var zone in (objective?["zones"] as JsonArray) ?? new JsonArray())
                    {
                        var mapId = zone?["map"]?.GetValue<string>() ?? "";
                        if (!MapIdToKey.TryGetValue(mapId, out var mapKey))
                        {
                            continue;
                        }
                        var pts = new List<double[]>();
                        foreach (var p in (zone?["outline"] as JsonArray) ?? new JsonArray())
                        {
                            pts.Add(new[]
                            {
                                p?["x"]?.GetValue<double>() ?? 0,
                                p?["z"]?.GetValue<double>() ?? 0,
                            });
                        }
                        if (pts.Count == 0 && zone?["position"] is JsonObject pos)
                        {
                            var x = pos["x"]?.GetValue<double>() ?? 0;
                            var z = pos["z"]?.GetValue<double>() ?? 0;
                            const double r = 4;
                            pts.Add(new[] { x - r, z - r });
                            pts.Add(new[] { x + r, z - r });
                            pts.Add(new[] { x + r, z + r });
                            pts.Add(new[] { x - r, z + r });
                        }
                        if (pts.Count < 3)
                        {
                            continue;
                        }
                        zones.Add(new
                        {
                            m = mapKey,
                            t = type,
                            p = pts,
                            f = FloorFromZone(zone),
                        });
                    }
                }
                if (zones.Count > 0 || objectives.Count > 0)
                {
                    records.Add(new { id, name, trader = traders[id], wiki = t["wikiLink"]?.GetValue<string>() ?? "", slug = t["normalizedName"]?.GetValue<string>() ?? "", zones, obj = objectives });
                }
            }

            // 只保留任务目标引用到的物品名字。这里**不**把 5498 条的 items JSON 解析成
            // 对象树（实测会让进程常驻到 300MB），而是按 "id":{"id":"id","name":"..." 的形式
            // 直接抽取，内存只是那个原始字符串。
            var itemNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (referenced.Count > 0)
            {
                try
                {
                    itemNames = await FetchItemNamesAsync(referenced);
                }
                catch
                {
                    // 物品名拿不到不影响地图本身
                }
            }

            // 地图附加数据：散落生成点（只留引用到的物品）、场景名、撤离点、转移点
            var extras = new MapExtras();
            try
            {
                extras = await FetchMapDataAsync(referenced);
            }
            catch
            {
                // 拿不到不影响任务本身
            }

            var cache = new JsonObject
            {
                ["fetched"] = DateTime.Now.ToString("o"),
                ["schema"] = CurrentSchema,
                ["tasks"] = JsonSerializer.SerializeToNode(records),
                ["items"] = JsonSerializer.SerializeToNode(itemNames),
                ["spawns"] = JsonSerializer.SerializeToNode(extras.Spawns),
                ["scenes"] = JsonSerializer.SerializeToNode(extras.Scenes),
                ["extracts"] = SerializeMarkers(extras.Extracts),
                ["transits"] = SerializeMarkers(extras.Transits),
                ["btrStops"] = SerializeMarkers(extras.BtrStops),
            };
            File.WriteAllText(CachePath, cache.ToJsonString());

            FetchedAt = DateTime.Now;
            ParseRecords(cache["tasks"] as JsonArray);
            ItemNames = itemNames;
            SpawnPoints = PackSpawns(extras.Spawns);
            SceneToMap = extras.Scenes;
            Extracts = ProjectMarkers(extras.Extracts);
            Transits = ProjectMarkers(extras.Transits);
            BtrStops = ProjectMarkers(extras.BtrStops);
            // 物品表有 16.5MB，解析成 JsonNode 后是几百 MB 的临时对象；
            // 这里手动回收一次，否则要等到下次分配压力才会释放。
            data = null;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            Status = $"任务数据 {FetchedAt:MM-dd HH:mm}（已更新）";
            Online = true;
            FromSeed = false;
            return true;
        }
        catch (Exception ex)
        {
            Online = false;
            // 有缓存/内置数据就继续用，只是明确告诉用户"现在是离线的"
            Status = (FromSeed || FetchedAt != DateTime.MinValue)
                ? "在线获取失败 · 用内置数据"
                : "在线获取失败：" + ex.Message;
            return false;
        }
    }

    private static void ParseRecords(JsonArray? tasks)
    {
        var zones = new Dictionary<string, List<ZoneShape>>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>();
        var traders = new Dictionary<string, string>();
        var objectivesByTask = new Dictionary<string, List<ObjectiveInfo>>();
        var wikis = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var slugs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in tasks ?? new JsonArray())
        {
            if (node is not JsonObject t)
            {
                continue;
            }
            var id = t["id"]?.GetValue<string>() ?? "";
            var name = t["name"]?.GetValue<string>() ?? id;
            names[id] = name;
            traders[id] = t["trader"]?.GetValue<string>() ?? "";
            if (t["wiki"]?.GetValue<string>() is { Length: > 0 } wiki)
            {
                wikis[id] = wiki;
            }
            if (t["slug"]?.GetValue<string>() is { Length: > 0 } slug)
            {
                slugs[id] = slug;
            }

            if ((t["obj"] as JsonArray) is { Count: > 0 } objectiveNodes)
            {
                var list = new List<ObjectiveInfo>();
                foreach (var o in objectiveNodes)
                {
                    var description = o?["d"]?.GetValue<string>() ?? "";
                    if (description.Length == 0)
                    {
                        continue;
                    }
                    list.Add(new ObjectiveInfo
                    {
                        Description = description,
                        Type = o?["t"]?.GetValue<string>() ?? "",
                        Count = o?["c"]?.GetValue<int?>(),
                        Items = ReadStringArray(o?["i"]),
                        Keys = (o?["k"] as JsonArray)?
                            .Select(group => (IReadOnlyList<string>)ReadStringArray(group))
                            .Where(inner => inner.Count > 0)
                            .ToList() ?? new List<IReadOnlyList<string>>(),
                    });
                }
                if (list.Count > 0)
                {
                    objectivesByTask[id] = list;
                }
            }
            foreach (var zone in (t["zones"] as JsonArray) ?? new JsonArray())
            {
                var mapKey = zone?["m"]?.GetValue<string>() ?? "";
                // 只保留我们有地图的那些；不支持的地图直接丢掉，省内存
                if (MapPack.Find(mapKey) == null)
                {
                    continue;
                }
                var world = new List<PointF>();
                foreach (var p in (zone?["p"] as JsonArray) ?? new JsonArray())
                {
                    var pair = p!.AsArray();
                    world.Add(new PointF(
                        (float)pair[0]!.GetValue<double>(),
                        (float)pair[1]!.GetValue<double>()));
                }
                if (world.Count < 3)
                {
                    continue;
                }
                // 这里只存世界坐标；投影留到切图时按当前地图做（见 ZoneShape.Project）
                var shape = new ZoneShape
                {
                    TaskId = id,
                    TaskName = name,
                    Type = zone?["t"]?.GetValue<string>() ?? "",
                    WorldPoints = world.ToArray(),
                    Floor = zone?["f"]?.GetValue<int?>() ?? 0,
                };
                if (!zones.TryGetValue(mapKey, out var list))
                {
                    list = new List<ZoneShape>();
                    zones[mapKey] = list;
                }
                list.Add(shape);
            }
        }
        ZonesByMap = zones;
        TaskObjectives = objectivesByTask;
        TaskNames = names;
        TaskTrader = traders;
        TaskWiki = wikis;
        TaskSlug = slugs;
    }

    /// <summary>
    /// 推断区域所在楼层。数据里没有"每层高度区间"，只能靠高度估：
    /// 用区域高度**中点**相对地形的高度，除以约 3 米层高。
    /// 用底板高度会出错——室外区域的底板常在地形以下，会被误判成地下；
    /// 同理 |偏移| 小于 1.8 米时按地面处理。
    /// </summary>
    private static int FloorFromZone(JsonNode? zone)
    {
        if (zone == null)
        {
            return 0;
        }
        var bottom = zone["bottom"]?.GetValue<double?>();
        var top = zone["top"]?.GetValue<double?>();
        if (bottom is not { } low && top is not { } high)
        {
            return 0;
        }
        var lowValue = bottom ?? top!.Value;
        var highValue = top ?? bottom!.Value;
        var mid = (lowValue + highValue) / 2.0;
        var terrain = zone["terrainElevation"]?.GetValue<double?>() ?? 0;
        var above = mid - terrain;
        if (Math.Abs(above) < 1.8)
        {
            return 0;
        }
        var floor = (int)Math.Round(above / 3.0, MidpointRounding.AwayFromZero);
        return Math.Clamp(floor, -2, 5);
    }

    private static List<string> ReadStringArray(JsonNode? node)    {
        var list = new List<string>();
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                string? text;
                try
                {
                    text = item?.GetValue<string>();
                }
                catch
                {
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(text))
                {
                    list.Add(text);
                }
            }
        }
        else if (node is JsonValue value && value.GetValueKind() == JsonValueKind.String)
        {
            list.Add(value.GetValue<string>());
        }
        return list;
    }

    /// <summary>
    /// 从 /pve/maps 里读出「散落战利品生成点」：每个点带坐标和可能生成的物品 id。
    /// 只保留任务目标引用到的物品，并且用 JsonDocument（比 JsonNode 省得多）流式解析。
    /// </summary>
    private static async Task<MapExtras> FetchMapDataAsync(HashSet<string> wanted)
    {
        var extras = new MapExtras();
        using var response = await Http.GetAsync("/pve/maps", HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        if (!doc.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("maps", out var maps)
            || maps.ValueKind != JsonValueKind.Object)
        {
            return extras;
        }
        var wantedKeys = new HashSet<string>(
            MapPack.Maps.Select(m => m.Key), StringComparer.OrdinalIgnoreCase);

        // 撤离点名/转移点名的中文对照表（文件很小，24KB）
        var zh = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var zhResponse = await Http.GetAsync("/pve/maps_zh");
            var zhText = await zhResponse.Content.ReadAsStringAsync();
            if (JsonNode.Parse(zhText)?["data"] is JsonObject zhNode)
            {
                foreach (var pair in zhNode)
                {
                    if (pair.Value is JsonValue value && value.GetValueKind() == JsonValueKind.String)
                    {
                        zh[pair.Key] = value.GetValue<string>();
                    }
                }
            }
        }
        catch
        {
            // 翻译拿不到就显示原始名
        }
        foreach (var mapEntry in maps.EnumerateObject())
        {
            var map = mapEntry.Value;
            if (map.ValueKind != JsonValueKind.Object
                || !map.TryGetProperty("normalizedName", out var normalized)
                || normalized.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            var key = normalized.GetString() ?? "";
            if (!wantedKeys.Contains(key))
            {
                continue;
            }

            // 场景名：maps/streets_preset.bundle → streets_preset
            if (map.TryGetProperty("scenePath", out var scene) && scene.ValueKind == JsonValueKind.String)
            {
                var sceneName = scene.GetString() ?? "";
                var slash = sceneName.LastIndexOf('/');
                if (slash >= 0)
                {
                    sceneName = sceneName[(slash + 1)..];
                }
                if (sceneName.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
                {
                    sceneName = sceneName[..^7];
                }
                if (sceneName.Length > 0)
                {
                    extras.Scenes[sceneName] = key;
                }
            }

            // 撤离点 / 转移点
            var extracts = new List<MapMarker>();
            foreach (var extract in ArrayOf(map, "extracts"))
            {
                var marker = ReadMarker(extract, "extract", zh);
                if (marker != null)
                {
                    extracts.Add(marker);
                }
            }
            if (extracts.Count > 0)
            {
                extras.Extracts[key] = extracts;
            }
            var transits = new List<MapMarker>();
            foreach (var transit in ArrayOf(map, "transits"))
            {
                var marker = ReadMarker(transit, "transit", zh);
                if (marker != null)
                {
                    transits.Add(marker);
                }
            }
            if (transits.Count > 0)
            {
                extras.Transits[key] = transits;
            }

            // BTR 停车点：名字是本地化 key（Trading/Dialog/PlayerTaxi/Woods/p5/Name）
            var btrStops = new List<MapMarker>();
            foreach (var stop in ArrayOf(map, "btrStops"))
            {
                var rawName = stop.TryGetProperty("name", out var stopName)
                    ? stopName.GetString() ?? ""
                    : "";
                btrStops.Add(new MapMarker
                {
                    Name = BtrStopLabel(rawName, zh),
                    X = stop.TryGetProperty("x", out var bx) ? bx.GetDouble() : 0,
                    Z = stop.TryGetProperty("z", out var bz) ? bz.GetDouble() : 0,
                    Kind = "btr",
                });
            }
            if (btrStops.Count > 0)
            {
                extras.BtrStops[key] = btrStops;
            }

            if (!map.TryGetProperty("lootLoose", out var loose) || loose.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            var perItem = new Dictionary<string, List<double[]>>(StringComparer.OrdinalIgnoreCase);
            foreach (var point in loose.EnumerateArray())
            {
                if (!point.TryGetProperty("position", out var position)
                    || !point.TryGetProperty("items", out var items)
                    || items.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                var x = position.TryGetProperty("x", out var px) ? px.GetDouble() : 0;
                var z = position.TryGetProperty("z", out var pz) ? pz.GetDouble() : 0;
                foreach (var item in items.EnumerateArray())
                {
                    var id = item.GetString();
                    if (id == null || !wanted.Contains(id))
                    {
                        continue;
                    }
                    if (!perItem.TryGetValue(id, out var list))
                    {
                        list = new List<double[]>();
                        perItem[id] = list;
                    }
                    list.Add(new[] { x, z });
                }
            }
            if (perItem.Count > 0)
            {
                extras.Spawns[key] = perItem;
            }
        }
        return extras;
    }

    private static IEnumerable<JsonElement> ArrayOf(JsonElement map, string name) =>
        map.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

    /// <summary>
    /// BTR 站点的名字是本地化 key（Trading/Dialog/PlayerTaxi/Woods/p5/Name）。
    /// 翻译表里有就用中文，没有就退化成 "BTR p5"，至少不会显示一长串路径。
    /// </summary>
    private static string BtrStopLabel(string raw, Dictionary<string, string> zh)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return "BTR";
        }
        if (zh.TryGetValue(raw, out var text) && !string.IsNullOrWhiteSpace(text))
        {
            return text;
        }
        var parts = raw.Split('/');
        var tail = parts.Length >= 2 ? parts[^2] : raw;
        return tail.Length > 0 ? "BTR " + tail : "BTR";
    }

    private static MapMarker? ReadMarker(JsonElement element, string kind, Dictionary<string, string> zh)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("position", out var position))
        {
            return null;
        }
        var name = element.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString() ?? ""
            : "";
        if (name.Length == 0)
        {
            name = kind == "extract" ? "撤离点" : "转移点";
        }
        else if (zh.TryGetValue(name, out var translated))
        {
            name = translated;
        }
        var note = "";
        if (kind == "extract"
            && element.TryGetProperty("faction", out var faction)
            && faction.ValueKind == JsonValueKind.String)
        {
            note = faction.GetString() ?? "";
        }
        else if (kind == "transit")
        {
            if (element.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.String)
            {
                note = target.GetString() ?? "";
            }
        }
        return new MapMarker
        {
            Name = name,
            X = position.TryGetProperty("x", out var x) ? x.GetDouble() : 0,
            Z = position.TryGetProperty("z", out var z) ? z.GetDouble() : 0,
            Kind = kind,
            Note = note,
        };
    }

    private static JsonObject SerializeMarkers(Dictionary<string, List<MapMarker>> markers)
    {
        var result = new JsonObject();
        foreach (var (mapKey, list) in markers)
        {
            var array = new JsonArray();
            foreach (var marker in list)
            {
                array.Add(new JsonObject
                {
                    ["n"] = marker.Name,
                    ["x"] = marker.X,
                    ["z"] = marker.Z,
                    ["k"] = marker.Kind,
                    ["t"] = marker.Note,
                });
            }
            result[mapKey] = array;
        }
        return result;
    }

    private static Dictionary<string, List<MapMarker>> ReadMarkers(JsonNode? node)
    {
        var result = new Dictionary<string, List<MapMarker>>(StringComparer.OrdinalIgnoreCase);
        if (node is not JsonObject maps)
        {
            return result;
        }
        foreach (var (mapKey, listNode) in maps)
        {
            if (listNode is not JsonArray array)
            {
                continue;
            }
            var list = new List<MapMarker>();
            foreach (var item in array)
            {
                if (item is not JsonObject marker)
                {
                    continue;
                }
                list.Add(new MapMarker
                {
                    Name = marker["n"]?.GetValue<string>() ?? "",
                    X = marker["x"]?.GetValue<double>() ?? 0,
                    Z = marker["z"]?.GetValue<double>() ?? 0,
                    Kind = marker["k"]?.GetValue<string>() ?? "extract",
                    Note = marker["t"]?.GetValue<string>() ?? "",
                });
            }
            if (list.Count > 0)
            {
                result[mapKey] = list;
            }
        }
        return result;
    }

    private static Dictionary<string, string> ReadScenes(JsonNode? node)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (node is JsonObject scenes)
        {
            foreach (var (scene, key) in scenes)
            {
                var value = key?.GetValue<string>();
                if (!string.IsNullOrEmpty(value))
                {
                    result[scene] = value;
                }
            }
        }
        return result;
    }

    private static Dictionary<string, List<MapMarker>> ProjectMarkers(
        Dictionary<string, List<MapMarker>> markers)
    {
        // 只保留有地图的；坐标保持世界坐标，画之前由 MapMarker.Project 按当前地图投影
        return markers
            .Where(pair => MapPack.Find(pair.Key) != null)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 生成点保留世界坐标（x,z），画之前再按当前地图投影。
    /// 和区域同理：共享数据的第二张图不能沿用第一张图的像素坐标。
    /// </summary>
    private static Dictionary<string, Dictionary<string, List<PointF>>> PackSpawns(
        Dictionary<string, Dictionary<string, List<double[]>>> raw)
    {
        var result = new Dictionary<string, Dictionary<string, List<PointF>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (mapKey, perItem) in raw)
        {
            if (MapPack.Find(mapKey) == null)
            {
                continue;
            }
            var packed = new Dictionary<string, List<PointF>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (itemId, points) in perItem)
            {
                var list = new List<PointF>(points.Count);
                foreach (var point in points)
                {
                    list.Add(new PointF((float)point[0], (float)point[1]));
                }
                packed[itemId] = list;
            }
            result[mapKey] = packed;
        }
        return result;
    }

    private static Dictionary<string, Dictionary<string, List<double[]>>> ReadSpawns(JsonNode? node)
    {
        var result = new Dictionary<string, Dictionary<string, List<double[]>>>(StringComparer.OrdinalIgnoreCase);
        if (node is not JsonObject maps)
        {
            return result;
        }
        foreach (var (mapKey, perItemNode) in maps)
        {
            if (perItemNode is not JsonObject perItem)
            {
                continue;
            }
            var inner = new Dictionary<string, List<double[]>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (itemId, pointsNode) in perItem)
            {
                if (pointsNode is not JsonArray points)
                {
                    continue;
                }
                var list = new List<double[]>(points.Count);
                foreach (var point in points)
                {
                    if (point is JsonArray pair && pair.Count >= 2)
                    {
                        list.Add(new[] { pair[0]!.GetValue<double>(), pair[1]!.GetValue<double>() });
                    }
                }
                if (list.Count > 0)
                {
                    inner[itemId] = list;
                }
            }
            if (inner.Count > 0)
            {
                result[mapKey] = inner;
            }
        }
        return result;
    }

    /// <summary>取物品中文名，只针对 <paramref name="wanted"/> 里的 id。</summary>
    private static async Task<Dictionary<string, string>> FetchItemNamesAsync(HashSet<string> wanted)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> zh;
        using (var zhResponse = await Http.GetAsync("/pve/items_zh"))
        {
            var zhText = await zhResponse.Content.ReadAsStringAsync();
            zh = new Dictionary<string, string>(StringComparer.Ordinal);
            if (JsonNode.Parse(zhText)?["data"] is JsonObject zhNode)
            {
                foreach (var pair in zhNode)
                {
                    if (pair.Value is JsonValue value && value.GetValueKind() == JsonValueKind.String)
                    {
                        zh[pair.Key] = value.GetValue<string>();
                    }
                }
            }
        }

        var pattern = new System.Text.RegularExpressions.Regex(
            "\"([0-9a-f]{24})\":\\{\"id\":\"\\1\",\"name\":\"((?:[^\"\\\\]|\\\\.)*)\"",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        using var response = await Http.GetAsync("/pve/items", HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        var buffer = new char[64 * 1024];
        var carry = "";
        while (true)
        {
            var read = await reader.ReadAsync(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }
            var text = carry + new string(buffer, 0, read);
            foreach (System.Text.RegularExpressions.Match match in pattern.Matches(text))
            {
                var id = match.Groups[1].Value;
                if (!wanted.Contains(id))
                {
                    continue;
                }
                var english = Unescape(match.Groups[2].Value);
                names[id] = zh.TryGetValue(english, out var translated) ? translated : english;
            }
            carry = text.Length > 512 ? text[^512..] : text;
        }
        return names;
    }

    private static string Unescape(string value)
    {
        if (!value.Contains('\\'))
        {
            return value;
        }
        try
        {
            return JsonSerializer.Deserialize<string>("\"" + value + "\"") ?? value;
        }
        catch
        {
            return value;
        }
    }

    /// <summary>取一个 json.tarkov.dev 数据文件，并用同名的 _zh 文件把文案换成中文。</summary>
    private static async Task<JsonObject> FetchTranslatedAsync(string path)
    {
        var raw = await Http.GetStringAsync(path);
        var zhRaw = await Http.GetStringAsync(path + "_zh");
        var root = JsonNode.Parse(raw) as JsonObject
                   ?? throw new InvalidDataException($"{path}: unexpected response");
        var zh = (JsonNode.Parse(zhRaw)?["data"] as JsonObject) ?? new JsonObject();
        var data = root["data"] as JsonObject ?? new JsonObject();
        var translations = (root["translations"] as JsonArray)?
            .Select(n => n?.GetValue<string>() ?? "")
            .Where(s => s.Length > 0) ?? Enumerable.Empty<string>();
        foreach (var translation in translations)
        {
            ApplyTranslations(data, translation, zh);
        }
        return data;
    }

    /// <summary>把 zip 里的 $.data.a[*].b 这样的翻译路径应用到已经解析好的节点上。</summary>
    private static void ApplyTranslations(JsonNode data, string path, JsonObject zh)
    {
        var segments = path.Replace("$.data.", "").Replace("$.", "").Split('.', StringSplitOptions.RemoveEmptyEntries);
        var nodes = new List<JsonNode> { data };
        foreach (var segment in segments)
        {
            // 兼容两种通配写法：tarkov.dev 用 tasks.*，也支持 tasks[*]
            var wildcard = segment == "*" || segment.EndsWith("[*]", StringComparison.Ordinal);
            var name = segment == "*" ? "" : wildcard ? segment[..^3] : segment;
            var next = new List<JsonNode>();
            foreach (var node in nodes)
            {
                if (name.Length > 0)
                {
                    if (node is not JsonObject obj || obj[name] == null)
                    {
                        continue;
                    }
                    if (!wildcard)
                    {
                        next.Add(obj[name]!);
                        continue;
                    }
                    Collect(obj[name]!, next);
                }
                else
                {
                    Collect(node, next);
                }
            }
            nodes = next;
            if (nodes.Count == 0)
            {
                return;
            }
        }
        foreach (var node in nodes)
        {
            if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
            {
                continue;
            }
            var text = value.GetValue<string>();
            if (zh[text] is JsonValue translated)
            {
                node.ReplaceWith(JsonValue.Create(translated.GetValue<string>()));
            }
        }
    }

    private static void Collect(JsonNode node, List<JsonNode> into)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item != null)
                {
                    into.Add(item);
                }
            }
        }
        else if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                if (pair.Value != null)
                {
                    into.Add(pair.Value);
                }
            }
        }
    }
}
