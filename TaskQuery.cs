using System.Text.Json;
using System.Text.Json.Nodes;

namespace TarkovMapLite;

/// <summary>Unknown values stay null: old caches must never become false Kappa flags.</summary>
public sealed record TaskMetadata
{
    public string TraderName { get; init; } = "";
    public string[] Regions { get; init; } = [];
    public bool RegionKnown { get; init; }
    public int? MinLevel { get; init; }
    public string? Faction { get; init; }
    public bool? Kappa { get; init; }
    public bool? Lightkeeper { get; init; }
    public string AsOf { get; init; } = "";

    internal static TaskMetadata FromApi(JsonObject task)
    {
        var objectives = (task["objectives"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
        var regions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(JsonNode? node)
        {
            var value = node is JsonObject obj
                ? obj["normalizedName"]?.ToString() ?? obj["id"]?.ToString() ?? obj["name"]?.ToString()
                : node?.ToString();
            var key = RegionKey(value);
            if (key.Length > 0) regions.Add(key);
        }
        Add(task["map"]);
        foreach (var objective in objectives)
        {
            foreach (var map in objective["maps"] as JsonArray ?? []) Add(map);
            foreach (var zone in objective["zones"] as JsonArray ?? []) Add(zone?["map"]);
        }
        var trader = task["trader"];
        var traderId = trader is JsonObject tr ? tr["id"]?.ToString() : trader?.ToString();
        var traderName = trader is JsonObject named ? named["name"]?.ToString() : null;
        return new()
        {
            TraderName = traderName ?? TraderLabel(traderId ?? ""),
            Regions = regions.Order(StringComparer.Ordinal).ToArray(),
            RegionKnown = regions.Count > 0 || (task.ContainsKey("map") &&
                objectives.All(o => o.ContainsKey("maps"))),
            MinLevel = task["minPlayerLevel"]?.GetValue<int?>(),
            Faction = NormalizeFaction(task["factionName"]?.ToString()),
            Kappa = task["kappaRequired"]?.GetValue<bool?>(),
            Lightkeeper = task["lightkeeperRequired"]?.GetValue<bool?>(),
            AsOf = DateTime.UtcNow.ToString("yyyy-MM-dd"),
        };
    }

    public static string? NormalizeFaction(string? value) => value?.ToLowerInvariant() switch
    {
        "any" or "all" or "通用" => "Any", "usec" => "USEC", "bear" => "BEAR", _ => null,
    };

    public static string RegionKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("any", StringComparison.OrdinalIgnoreCase)) return "";
        if (TarkovData.TryMapKey(value, out var idKey)) value = idKey;
        var key = value.Trim().ToLowerInvariant().Replace(' ', '-').Replace("21+", "21");
        key = key switch { "streets" => "streets-of-tarkov", "labs" or "laboratory" => "the-lab",
            "the-lab-(dark)" => "the-lab-dark", _ => key };
        return MapPack.Find(key)?.Data ?? key;
    }

    public static string TraderLabel(string id) => id switch
    {
        "54cb50c76803fa8b248b4571" => "Prapor（俄商）",
        "54cb57776803fa99248b456e" => "Therapist（医疗商）",
        "58330581ace78e27b8b10cee" => "Skier（滑雪者）",
        "5935c25fb3acc3127c3d8cd9" => "Peacekeeper（美商）",
        "5a7c2eca46aef81a7ca2145d" => "Mechanic（机械师）",
        "5ac3b934156ae10c4430e83c" => "Ragman（服装商）",
        "5c0647fdd443bc2504c2d371" => "Jaeger（猎人）",
        "579dc571d53a0658a154fbec" => "Fence（黑商）",
        "638f541a29ffd1183d187f57" => "Lightkeeper（灯塔商人）",
        "656f0f98d80a697f855d34b1" => "BTR Driver（装甲车司机）",
        "6617beeaa9cfa777ca915b7c" => "Ref（裁判）",
        "" => "商人未知", _ => id,
    };
}

public sealed record TaskFilter
{
    public string Search { get; init; } = "";
    /// <summary>current / all / any / unknown / a canonical map data key.</summary>
    public string Region { get; init; } = "current";
    public int Status { get; init; } = (int)TaskState.Started;
    public string Trader { get; init; } = "";
    public string Category { get; init; } = "";
    public int? PlayerLevel { get; init; }
    public string Faction { get; init; } = "";
    public bool? Kappa { get; init; }
    public bool? Lightkeeper { get; init; }
    public bool ShowStory { get; init; }
    public string StorySeries { get; init; } = StoryTasks.DefaultSeries;

    public static TaskFilter Load(bool onlyActive, bool anyRegion)
    {
        try
        {
            var text = PlayerLocator.GetSetting("taskFilter");
            if (!string.IsNullOrWhiteSpace(text))
            {
                var saved = (JsonSerializer.Deserialize<TaskFilter>(text) ?? new()).Normalize();
                return saved with { Search = "", ShowStory = false,
                    Trader = saved.Trader == StoryTasks.TraderId ? "" : saved.Trader,
                    Status = saved.Trader == StoryTasks.TraderId ? 1 : saved.Status };
            }
        }
        catch (JsonException) { }
        return new() { Region = anyRegion ? "any" : "current", Status = onlyActive ? 1 : -1 };
    }
    public TaskFilter Normalize() => this with
    {
        Search = (Search ?? "")[..Math.Min((Search ?? "").Length, 256)],
        Region = string.IsNullOrEmpty(Region) ? "current" : Region,
        Status = Status is >= -1 and <= 3 ? Status : 1,
        Trader = Trader ?? "", Category = TaskQuery.Categories.Contains(Category ?? "") ? Category! : "",
        Faction = Faction is "Any" or "USEC" or "BEAR" ? Faction : "",
        PlayerLevel = PlayerLevel is >= 1 and <= 100 ? PlayerLevel : null,
        StorySeries = StoryTasks.AllSeries.Any(c => c.Id == StorySeries) ? StorySeries : StoryTasks.DefaultSeries,
    };
    public void Save() => PlayerLocator.SetSetting("taskFilter", JsonSerializer.Serialize(this with { Search = "", ShowStory = false,
        Trader = Trader == StoryTasks.TraderId ? "" : Trader, Status = Trader == StoryTasks.TraderId ? 1 : Status }));
}

public sealed record TaskEntry(string Id, string Name, string TraderId, TaskMetadata Metadata,
    string[] Categories, string SearchText, string StorySeries = "", int StoryStep = 0)
{
    public bool IsStory => StoryTasks.IsTask(Id);
}

/// <summary>One catalogue and one predicate serve desktop and mobile; no network on filter changes.</summary>
public static class TaskQuery
{
    public static readonly string[] Categories = ["击杀", "探索", "收集", "交付", "放置", "标记", "撤离", "改枪", "使用", "成长／交易", "其他", "主线子任务"];
    public static string CategoryOf(string type) => type switch
    {
        "shoot" => "击杀", "visit" => "探索", "findItem" or "findQuestItem" => "收集",
        "giveItem" or "giveQuestItem" => "交付", "plantItem" or "plantQuestItem" => "放置",
        "mark" => "标记", "extract" => "撤离", "buildWeapon" => "改枪", "useItem" => "使用",
        "traderLevel" or "traderStanding" or "skill" or "experience" or "sellItem" => "成长／交易", _ => "其他",
    };
    public static string StateLabel(TaskState state) => state switch
    {
        TaskState.Started => "进行中", TaskState.Finished => "已完成", TaskState.Failed => "已失败", _ => "日志未记录",
    };
    public static string MapLabel(string key) => MapPack.Maps.FirstOrDefault(m => m.Key == key)?.DisplayName ??
        (key == "icebreaker" ? "破冰船（Icebreaker）" : key);
    public static string RegionLabel(TaskMetadata m) => m.Regions.Length > 0
        ? string.Join("、", m.Regions.Select(MapLabel)) : m.RegionKnown ? "任意地区" : "地区未知";

    public static TaskEntry[] Build()
    {
        return TarkovData.TaskNames.Select(pair =>
        {
            var id = pair.Key;
            var trader = TarkovData.TaskTrader.GetValueOrDefault(id, "");
            var meta = TarkovData.TaskMetadata.GetValueOrDefault(id) ?? new TaskMetadata();
            if (string.IsNullOrEmpty(meta.TraderName)) meta = meta with { TraderName = TaskMetadata.TraderLabel(trader) };
            var objectives = TarkovData.TaskObjectives.GetValueOrDefault(id) ?? [];
            var types = objectives.Select(o => CategoryOf(o.Type)).Distinct().ToArray();
            if (types.Length == 0) types = ["其他"];
            var items = objectives.SelectMany(o => o.Items.Concat(o.Keys.SelectMany(k => k)))
                .Distinct().Select(i => TarkovData.ItemNames.GetValueOrDefault(i, i));
            var search = string.Join('\n', new[] { pair.Value, meta.TraderName,
                TarkovData.TaskSlug.GetValueOrDefault(id, ""), string.Join(' ', objectives.Select(o => o.Description)),
                string.Join(' ', items), string.Join(' ', types), RegionLabel(meta) });
            return new TaskEntry(id, pair.Value, trader, meta, types, search);
        }).OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).Concat(StoryTasks.Entries).ToArray();
    }

    public static bool Matches(TaskEntry task, TaskFilter filter, string currentMap, TaskState state)
    {
        var m = task.Metadata;
        if (task.IsStory && (!filter.ShowStory || filter.StorySeries != task.StorySeries)) return false;
        var region = filter.Region == "current" ? currentMap : filter.Region;
        if (filter.Region != "all" && !(region switch
        {
            "any" => m.RegionKnown && m.Regions.Length == 0,
            "unknown" => !m.RegionKnown && m.Regions.Length == 0,
            _ => m.Regions.Contains(region, StringComparer.OrdinalIgnoreCase),
        })) return false;
        if (!task.IsStory && filter.Status >= 0 && (int)state != filter.Status) return false;
        if (filter.Trader.Length > 0 && filter.Trader != task.TraderId) return false;
        if (filter.Category.Length > 0 && !task.Categories.Contains(filter.Category)) return false;
        if (!task.IsStory)
        {
            if (filter.PlayerLevel is { } level && (m.MinLevel is not { } min || min > level)) return false;
            if (filter.Faction.Length > 0 && m.Faction != filter.Faction) return false;
            if (filter.Kappa is { } kappa && m.Kappa != kappa) return false;
            if (filter.Lightkeeper is { } lightkeeper && m.Lightkeeper != lightkeeper) return false;
        }
        return filter.Search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .All(word => task.SearchText.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    public static string[] Tags(TaskEntry task, TaskState state, bool doneOnMap)
    {
        var m = task.Metadata;
        if (StoryTasks.Get(task.Id) is { } story)
        {
            var result = new List<string> { "主线任务", story.Series.Name, "手动显示",
                story.Target.Optional ? "可选目标" : "主线目标", RegionLabel(m),
                story.Target.Zones.Length == 0 ? "暂无坐标" : story.Target.Zones.Any(z =>
                    MapPack.Maps.Any(m => m.Data == TaskMetadata.RegionKey(z.Map))) ? "有地图点位" : "底图未收录" };
            if (story.Target.Section.Length > 0) result.Add(story.Target.Section);
            if (doneOnMap) result.Add("本图已手动隐藏");
            return result.ToArray();
        }
        var tags = new List<string> { m.TraderName, StateLabel(state) };
        tags.AddRange(task.Categories);
        tags.Add(RegionLabel(m));
        if (m.MinLevel is { } level) tags.Add(level == 0 ? "无等级要求" : $"最低 {level} 级");
        if (m.Faction is { } faction) tags.Add(faction == "Any" ? "通用阵营" : faction);
        if (m.Kappa == true) tags.Add("Kappa 必需");
        if (m.Lightkeeper == true) tags.Add("灯塔商人必需");
        if (doneOnMap) tags.Add("本图已完成");
        return tags.ToArray();
    }
}
