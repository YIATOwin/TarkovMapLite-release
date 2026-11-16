using System.Text.Json;

namespace TarkovMapLite;

/// <summary>Offline story targets. Synthetic IDs deliberately cannot match game-log quest IDs.</summary>
public static class StoryTasks
{
    public const string TraderId = "story";
    public const string DefaultSeries = "tour";
    public sealed record Series(string Id, string Name, int Order, string Wiki, StoryTarget[] Tasks);
    public sealed record StoryTarget(string Id, string SourceQuestId, int Step, string Description,
        string Section, bool Optional, string[] Maps, StoryZone[] Zones);
    public sealed record StoryZone(string Map, string? Layer, int SourceFloorIndex, double[][] Points);
    private sealed class Document
    {
        public string ChapterAsOf { get; init; } = "";
        public string PositionAsOf { get; init; } = "";
        public Series[] Chapters { get; init; } = [];
    }
    public sealed record TargetInfo(Series Series, StoryTarget Target);
    private static readonly Lazy<Document> Data = new(() => JsonSerializer.Deserialize<Document>(
        Assets.ReadText("story-tasks.json") ?? "{}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new());
    private static readonly Lazy<Dictionary<string, TargetInfo>> Targets = new(() => Data.Value.Chapters
        .SelectMany(c => c.Tasks.Select(t => new KeyValuePair<string, TargetInfo>("story:" + t.Id, new(c, t))))
        .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
    private static readonly Lazy<TaskEntry[]> Catalogue = new(() => AllSeries.OrderBy(c => c.Order)
        .SelectMany(series => series.Tasks.Select(task =>
        {
            var regions = task.Maps.Concat(task.Zones.Select(z => z.Map)).Select(TaskMetadata.RegionKey)
                .Where(k => k.Length > 0).Distinct().Order().ToArray();
            var meta = new TaskMetadata { TraderName = "主线任务", Regions = regions, RegionKnown = true,
                AsOf = Data.Value.ChapterAsOf };
            var name = $"{task.Step:00}. {task.Description}";
            return new TaskEntry("story:" + task.Id, name, TraderId, meta, ["主线子任务"],
                string.Join('\n', "主线任务", series.Name, task.Section, task.Description, TaskQuery.RegionLabel(meta)),
                series.Id, task.Step);
        })).ToArray());
    private static readonly Lazy<Dictionary<string, List<ZoneShape>>> Shapes = new(() =>
    {
        var result = new Dictionary<string, List<ZoneShape>>(StringComparer.Ordinal);
        foreach (var series in AllSeries)
        foreach (var task in series.Tasks)
        foreach (var zone in task.Zones)
        {
            var key = TaskMetadata.RegionKey(zone.Map);
            var map = MapPack.Maps.FirstOrDefault(m => m.Data == key);
            if (map == null || zone.Points.Length == 0) continue;
            var points = zone.Points.Where(p => p.Length >= 2 && double.IsFinite(p[0]) && double.IsFinite(p[1]))
                .Select(p => new PointF((float)p[0], (float)p[1])).ToArray();
            if (points.Length == 0) continue;
            if (points.Length < 3)
            {
                var center = points[0]; const float radius = 3;
                points = [new(center.X-radius, center.Y-radius), new(center.X+radius, center.Y-radius),
                    new(center.X+radius, center.Y+radius), new(center.X-radius, center.Y+radius)];
            }
            var floor = FloorOf(map, zone.Layer);
            var label = task.Description.Length > 48 ? task.Description[..48] + "…" : task.Description;
            var shape = new ZoneShape { TaskId = "story:" + task.Id,
                TaskName = $"{series.Name} · {task.Step:00} · {label}", ObjectiveId = task.Id,
                Type = "story", IsStory = true, WorldPoints = points, Floor = floor ?? 0, FloorKnown = floor.HasValue };
            var index = series.Id + "|" + key;
            if (!result.TryGetValue(index, out var list)) result[index] = list = [];
            list.Add(shape);
        }
        return result;
    });

    public static IReadOnlyList<Series> AllSeries => Data.Value.Chapters;
    public static IReadOnlyList<TaskEntry> Entries => Catalogue.Value;
    public static bool IsTask(string? id) => id?.StartsWith("story:", StringComparison.Ordinal) == true;
    public static TargetInfo? Get(string? id) => IsTask(id) ? Targets.Value.GetValueOrDefault(id!) : null;
    public static IReadOnlyList<ZoneShape> Zones(string series, string map) => Shapes.Value.GetValueOrDefault(series + "|" + map) ?? [];
    public static bool HasCoordinates(string id) => Get(id)?.Target.Zones.Length > 0;
    public static int? FloorOf(MapProjection map, string? sourceLayer)
    {
        if (sourceLayer == null) return null;
        if (sourceLayer.Length == 0) return 0; // source -1 means BASE, never basement
        var exact = map.Layers.FirstOrDefault(l => l.SvgLayer == sourceLayer);
        if (exact != null) return exact.Floor;
        var level = MapFloors.NumberOfLayer(sourceLayer);
        return map.Layers.Any(l => l.Floor == level) ? level : null;
    }
}
