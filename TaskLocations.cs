using System.Text.Json;
using System.Text.Json.Nodes;

namespace TarkovMapLite;

/// <summary>Supplement missing objective positions without replacing current task text or polygons.</summary>
internal static class TaskLocations
{
    internal sealed record Position(string TaskId, string ObjectiveId, string Map, string Type, double X, double Y, double Z);
    private sealed record SnapshotData(Dictionary<string, Position[]> Points, Position[] Excluded, string AsOf, Dictionary<string,string[]> RegionExclusions);
    private static readonly Lazy<SnapshotData> Snapshot = new(() =>
    {
        using var doc = JsonDocument.Parse(Assets.ReadText("task-locations.json") ?? "{\"points\":[]}");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var points = doc.RootElement.GetProperty("points").Deserialize<Position[]>(options) ?? [];
        var excluded = doc.RootElement.TryGetProperty("excluded", out var bad) ? bad.Deserialize<Position[]>(options) ?? [] : [];
        var regions = doc.RootElement.TryGetProperty("regionExclusions", out var fixes) ? fixes.Deserialize<Dictionary<string,string[]>>() ?? new() : new();
        return new(points.GroupBy(p => p.TaskId).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase),
            excluded, doc.RootElement.TryGetProperty("asOf", out var date) ? date.GetString() ?? "" : "", regions);
    });
    internal static IEnumerable<Position> All => Snapshot.Value.Points.Values.SelectMany(p => p);
    internal static TaskMetadata CorrectMetadata(string id, TaskMetadata meta) => meta.AsOf == Snapshot.Value.AsOf &&
        Snapshot.Value.RegionExclusions.TryGetValue(id, out var excluded) ? meta with { Regions = meta.Regions.Except(excluded, StringComparer.OrdinalIgnoreCase).ToArray() } : meta;
    private static bool Excluded(string taskId, string map, double x, double? y, double z) => Snapshot.Value.Excluded.Any(p => p.TaskId == taskId &&
        p.Map == map && Math.Abs(p.X-x) < .01 && y is { } height && Math.Abs(p.Y-height) < .01 && Math.Abs(p.Z-z) < .01);
    internal static IEnumerable<JsonObject> Objects(JsonNode? node) => node switch
    {
        JsonArray array => array.OfType<JsonObject>(),
        JsonObject obj when obj.ContainsKey("id") || obj.ContainsKey("type") || obj.ContainsKey("position") || obj.ContainsKey("x") || obj.ContainsKey("outline") || obj.ContainsKey("p") => [obj],
        JsonObject dictionary => dictionary.Select(p => p.Value).OfType<JsonObject>(),
        _ => [],
    };
    private static double? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var n) && double.IsFinite(n) ? n : null;
    private static string MapKey(JsonNode? value) => TaskMetadata.RegionKey(value is JsonObject obj
        ? obj["normalizedName"]?.ToString() ?? obj["id"]?.ToString() ?? obj["name"]?.ToString() : value?.ToString());

    /// <summary>Accept the API's outline/position zones and the snapshot's flattened x/y/z zones.</summary>
    internal static List<JsonObject> ReadApi(JsonObject task)
    {
        var result = new List<JsonObject>();
        foreach (var objective in Objects(task["objectives"]))
        foreach (var zone in Objects(objective["zones"]))
        {
            var map = MapKey(zone["mapId"] ?? zone["map"] ?? zone["normalizedName"]);
            if (map.Length == 0 || MapPack.Find(map) == null) continue;
            var type = objective["type"]?.ToString() ?? "";
            var id = objective["id"]?.ToString() ?? "";
            var polygon = new JsonArray();
            foreach (var point in Objects(zone["outline"]))
                if (Number(point["x"]) is { } x && Number(point["z"]) is { } z) polygon.Add(new JsonArray(x, z));
            var position = zone["position"] as JsonObject ?? zone;
            var bottom = Number(zone["bottom"]); var top = Number(zone["top"]);
            var height = bottom != null || top != null ? ((bottom ?? top!.Value) + (top ?? bottom!.Value)) / 2 : Number(position["y"]);
            JsonObject item;
            if (polygon.Count >= 3)
                item = new JsonObject { ["m"] = map, ["t"] = type, ["o"] = id, ["p"] = polygon, ["f"] = TarkovData.FloorFromZone(zone) };
            else if (Number(position["x"]) is { } x && Number(position["z"]) is { } z)
            {
                if (Excluded(task["id"]?.ToString() ?? "", map, x, height, z)) continue;
                item = PointZone(map, type, id, x, height, z);
            }
            else continue;
            if (height is { } y) item["y"] = y;
            result.Add(item);
        }
        return result;
    }
    private static JsonObject PointZone(string map, string type, string id, double x, double? height, double z)
    {
        const double radius = 4; // A display footprint for a position, not an upstream objective boundary.
        var known = MapPack.Find(map)?.Layers.Count == 0;
        var floor = 0;
        if (height is { } y && MapFloors.TryLayer(map, x, z, y, out var layer))
        { floor = MapFloors.FloorOf(map, layer); known = true; }
        var item = new JsonObject { ["m"] = map, ["t"] = type, ["o"] = id, ["f"] = floor, ["floorKnown"] = known, ["point"] = true,
            ["p"] = new JsonArray(new JsonArray(x-radius,z-radius), new JsonArray(x+radius,z-radius), new JsonArray(x+radius,z+radius), new JsonArray(x-radius,z+radius)) };
        if (height is { } h) item["y"] = h;
        return item;
    }
    internal static List<JsonObject> Merge(JsonObject task)
    {
        var result = Objects(task["zones"]).Select(z => (JsonObject)z.DeepClone()).ToList();
        var authoritative = result.Where(z => !string.IsNullOrEmpty(z["o"]?.ToString()) && (z["p"] as JsonArray)?.Count >= 3).ToArray();
        // Old caches have no objective IDs. New caches use them to avoid restoring retired objectives.
        var objectives = Objects(task["obj"]).ToArray();
        var objectiveIds = objectives.Select(o => o["id"]?.ToString() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var idsKnown = objectives.Length > 0 && !objectiveIds.Contains("");
        if (!Snapshot.Value.Points.TryGetValue(task["id"]?.ToString() ?? "", out var points)) return result;
        foreach (var point in points)
        {
            if (idsKnown && !objectiveIds.Contains(point.ObjectiveId)) continue;
            var map = TaskMetadata.RegionKey(point.Map);
            if (MapPack.Find(map) == null) continue;
            if (authoritative.Any(z => z["o"]!.ToString() == point.ObjectiveId && MapKey(z["m"]) == map)) continue;
            var existing = result.FirstOrDefault(zone => MapKey(zone["m"]) == map && zone["t"]?.ToString() == point.Type &&
                (string.IsNullOrEmpty(zone["o"]?.ToString()) || zone["o"]!.ToString() == point.ObjectiveId) && Represents(zone, point));
            if (existing != null)
            {
                // The old seed kept generic floor numbers but discarded world height.
                // Recover height only for the same area centre, preserving its exact outline.
                if (existing["y"] == null && existing["p"] is JsonArray polygon)
                {
                    var vertices = polygon.OfType<JsonArray>().ToArray();
                    if (vertices.Length >= 3 && Math.Abs(vertices.Average(p => Number(p[0]) ?? double.MaxValue)-point.X) < 1 &&
                        Math.Abs(vertices.Average(p => Number(p[1]) ?? double.MaxValue)-point.Z) < 1)
                    {
                        existing["y"] = point.Y;
                        if (MapFloors.TryLayer(map, point.X, point.Z, point.Y, out var layer))
                        { existing["f"] = MapFloors.FloorOf(map, layer); existing["floorKnown"] = true; }
                    }
                }
                continue;
            }
            result.Add(PointZone(map, point.Type, point.ObjectiveId, point.X, point.Y, point.Z));
        }
        return result;
    }
    private static bool Represents(JsonObject zone, Position point)
    {
        if (zone["point"]?.GetValue<bool>() == true && zone["p"] is JsonArray polygon)
        {
            var vertices = polygon.OfType<JsonArray>().ToArray();
            return vertices.Length > 0 && Math.Abs(vertices.Average(p => Number(p[0]) ?? double.MaxValue)-point.X) < .01 &&
                Math.Abs(vertices.Average(p => Number(p[1]) ?? double.MaxValue)-point.Z) < .01 &&
                (Number(zone["y"]) is not { } y || Math.Abs(y-point.Y) < .01);
        }
        return Covers(zone, point.X, point.Z);
    }
    internal static bool Covers(JsonObject zone, double x, double z)
    {
        var vertices = (zone["p"] as JsonArray)?.OfType<JsonArray>()
            .Where(p => p.Count >= 2 && Number(p[0]) != null && Number(p[1]) != null)
            .Select(p => (X: Number(p[0])!.Value, Z: Number(p[1])!.Value)).ToArray() ?? [];
        if (vertices.Length < 3) return false;
        var inside = false;
        for (int i = 0, j = vertices.Length - 1; i < vertices.Length; j = i++)
        {
            var a = vertices[i]; var b = vertices[j];
            var dx = b.X-a.X; var dz = b.Z-a.Z; var length = dx*dx+dz*dz;
            var t = length == 0 ? 0 : Math.Clamp(((x-a.X)*dx+(z-a.Z)*dz)/length, 0, 1);
            if (Math.Pow(x-a.X-t*dx,2)+Math.Pow(z-a.Z-t*dz,2) <= .01) return true;
            if ((a.Z > z) != (b.Z > z) && x < (b.X-a.X)*(z-a.Z)/(b.Z-a.Z)+a.X) inside = !inside;
        }
        return inside;
    }
}
