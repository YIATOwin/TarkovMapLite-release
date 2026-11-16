using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using TarkovMapLite;

internal static class TaskLocationChecks
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Flags)!.Invoke(target, args);
    private static V Field<V>(object target, string name) => (V)target.GetType().GetField(name, Flags)!.GetValue(target)!;
    private static void Apply(string text) => typeof(TarkovData).GetMethod("ApplyCache", Flags)!.Invoke(null, [text, true]);
    public static int Run(string? output)
    {
        var checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        var seedText = Assets.ReadText("data-seed.json")!;
        var seed = JsonNode.Parse(seedText)!.AsObject();
        Apply(seedText);
        var tasks = seed["tasks"]!.AsArray().OfType<JsonObject>().ToDictionary(t => t["id"]!.ToString());
        var allZones = TarkovData.ZonesByMap.Values.SelectMany(z => z).ToArray();
        var reports = new List<object>();
        var noCoordinatesBefore = tasks.Values.Count(t => (t["zones"] as JsonArray)?.Count == 0);
        var restored = tasks.Values.Where(t => (t["zones"] as JsonArray)?.Count == 0 && allZones.Any(z => z.TaskId == t["id"]!.ToString())).ToArray();
        Check(restored.Length == 56, "Coordinate-empty tasks on supported maps were not all recovered: " + restored.Length + "; zones: " + allZones.Length);
        foreach (var position in TaskLocations.All.Where(p => tasks.ContainsKey(p.TaskId) && MapPack.Find(p.Map) != null))
        {
            var zones = TarkovData.ZonesByMap.GetValueOrDefault(position.Map) ?? [];
            var matching = zones.Where(z => z.TaskId == position.TaskId && z.Type == position.Type).ToArray();
            Check(matching.Any(z => Covers(z, position.X, position.Z)), "Snapshot coordinate missing: " + position.TaskId + "/" + position.Map);
            foreach (var map in MapPack.Maps.Where(m => m.Data == position.Map))
            {
                var projected = matching.Select(z => z.Project(map)).ToArray();
                Check(projected.All(z => float.IsFinite(z.Center.X) && float.IsFinite(z.Center.Y) && z.Points.All(p => float.IsFinite(p.X) && float.IsFinite(p.Y))), "Invalid projected task points: " + map.Key);
                var (x,y) = MapPack.ToImage(map, position.X, position.Z);
                Check(x >= -map.ViewBoxWidth*.01 && y >= -map.ViewBoxHeight*.01 && x <= map.ViewBoxWidth*1.01 && y <= map.ViewBoxHeight*1.01,
                    "Task point is outside the map image: " + position.TaskId + "/" + map.Key);
            }
        }
        foreach (var task in tasks.Values)
        {
            var merged = TaskLocations.Merge(task);
            var twice = (JsonObject)task.DeepClone(); twice["zones"] = new JsonArray(merged.Select(z => (JsonNode)z.DeepClone()).ToArray());
            Check(TaskLocations.Merge(twice).Count == merged.Count, "Repeated cache merge duplicated points: " + task["id"]);
            foreach (var original in (task["zones"] as JsonArray ?? []).OfType<JsonObject>())
                Check(merged.Any(z => z["m"]?.ToString() == original["m"]?.ToString() && z["t"]?.ToString() == original["t"]?.ToString() &&
                    JsonNode.DeepEquals(z["p"], original["p"])), "Original outline was replaced");
            var locations = allZones.Where(z => z.TaskId == task["id"]!.ToString()).ToArray();
            reports.Add(new { id = task["id"]!.ToString(), name = task["name"]!.ToString(), before = (task["zones"] as JsonArray)?.Count ?? 0,
                after = locations.Length, maps = TarkovData.ZonesByMap.Where(kv => kv.Value.Any(z => z.TaskId == task["id"]!.ToString())).Select(kv => kv.Key).ToArray() });
        }
        const string postman = "59675ea386f77414b32bded2";
        foreach (var key in new[] { "factory", "night-factory" })
        {
            var zone = TarkovData.ZonesByMap[key].Single(z => z.TaskId == postman);
            Check(zone.FloorKnown && zone.Floor == 0 && zone.ObjectiveId == "596895f986f7740d14064722", "Postman point lost real floor/objective");
        }
        Check(TarkovData.TaskMetadata[postman].Regions.Contains("factory"), "Supplemented points absent from task regions");
        Check(TarkovData.ZonesByMap["customs"].Count(z => z.TaskId == "597a0b2986f77426d66c0633") == 2, "Different objectives near each other were merged");
        Check(TarkovData.ZonesByMap["factory"].Count(z => z.TaskId == "597a0e5786f77426d66c0636") == 3, "Nearby quest item alternatives were lost");
        Check(TarkovData.ZonesByMap["the-labyrinth"].Any(z => z.TaskId == "67a09724972c11a3f5077324"), "Labyrinth points were dropped");
        const string oilRun = "59c124d686f774189b3c843f";
        Check(!TarkovData.ZonesByMap["reserve"].Any(z => z.TaskId == oilRun) && TarkovData.ZonesByMap["customs"].Count(z => z.TaskId == oilRun) == 4,
            "Conflicting Reserve duplicate was imported from snapshot");
        Check(TarkovData.TaskMetadata[oilRun].Regions.SequenceEqual(["customs"]), "Bad snapshot map restriction survived correction");
        Check(TarkovData.ZonesByMap["customs"].Where(z=>z.TaskId==oilRun).All(z=>z.FloorKnown && z.Floor==0), "Legacy seed's absolute-height floor guesses were not corrected for the real Customs tanker points");
        Check(TaskLocations.CorrectMetadata(oilRun,new TaskMetadata { AsOf="2099-01-01", Regions=["reserve"] }).Regions.Contains("reserve"), "Snapshot correction overwrites future live map metadata");

        // Current API geometry wins; missing coordinates merge by objective, not by whole task.
        var current = (JsonObject)tasks[postman].DeepClone();
        current["obj"] = JsonNode.Parse("""[{"id":"retired-objective-replacement","t":"visit","d":"New target"}]""");
        Check(TaskLocations.Merge(current).Count == 0, "Removed live objective was resurrected by stale snapshot");
        current["obj"] = JsonNode.Parse("""[{"id":"596895f986f7740d14064722","t":"findQuestItem","d":"Letter"}]""");
        Check(TaskLocations.Merge(current).Count == 2, "Current objective without coordinates did not use fallback");
        var authoritative = (JsonObject)current.DeepClone();
        authoritative["zones"] = JsonNode.Parse("""[{"m":"factory","t":"findQuestItem","o":"596895f986f7740d14064722","p":[[99,99],[101,99],[101,101],[99,101]],"f":0}]""");
        Check(TaskLocations.Merge(authoritative).Count(z => z["m"]!.ToString() == "factory") == 1, "Old snapshot revived a location replaced by current objective geometry");
        var online = JsonNode.Parse("""
            {"objectives":[{"id":"a","type":"findQuestItem","zones":[
                {"map":"55f2d3fd4bdc2d5f408b4567","position":{"x":-19.9610023,"y":0.047,"z":21.9430027}},
                {"map":"factory","x":-19,"y":0,"z":22},
                {"map":{"normalizedName":"factory"},"x":-19,"y":8,"z":22},
                {"map":"factory","position":{"x":1}},
                {"map":"unknown-map","x":1,"y":1,"z":1}]},
              {"id":"b","type":"visit","zones":{"one":{"map":"5b0fc42d86f7744a585f9105","outline":[{"x":0,"z":0},{"x":5,"z":0},{"x":5,"z":5}],"bottom":0,"top":2}}}]}
            """)!.AsObject();
        var parsed = TaskLocations.ReadApi(online);
        Check(parsed.Count == 4 && parsed.Count(z => z["m"]!.ToString() == "factory") == 3, "API position/flat/map-object/dictionary zones not normalized");
        Check(parsed.Single(z => z["m"]!.ToString() == "the-lab")["y"]!.GetValue<double>() == 1, "Lab map ID or zone midpoint not parsed");
        var singleton = JsonNode.Parse("""{"objectives":{"id":"a","type":"findQuestItem","zones":{"map":"factory","x":1,"y":0,"z":2}}}""")!.AsObject();
        Check(TaskLocations.ReadApi(singleton).Count == 1, "Singleton objective or zone was ignored");
        var conflicting = JsonNode.Parse("""{"id":"59c124d686f774189b3c843f","objectives":{"id":"59c128b986f77415037680df","type":"mark","zones":{"map":"reserve","x":-334.93,"y":-101.46,"z":-163.46}}}""")!.AsObject();
        Check(TaskLocations.ReadApi(conflicting).Count == 0, "Known conflicting point returned through live parser");

        var old = JsonNode.Parse(seedText)!.AsObject(); old["schema"] = 12;
        Apply(old.ToJsonString());
        Check(TarkovData.ZonesByMap["factory"].Any(z => z.TaskId == postman), "Existing cache did not gain offline coordinates");
        Apply(seedText);
        PlayerLocator.SetSetting("taskFilter", ""); PlayerLocator.SetSetting("view", "");
        using (var form = new MapForm())
        {
            _ = form.Handle; Call(form, "FillMapCombo"); Call(form, "SelectMap", "factory", false);
            Call(form, "ApplyMap", false);
            Call(form, "ChangeTaskFilter", new TaskFilter { Region = "current", Status = -1 });
            Call(form, "SelectTaskFromMobile", postman);
            var canvas = Field<Control>(form, "_canvas");
            var type = canvas.GetType();
            var projected = (IReadOnlyList<ZoneShape>)type.GetProperty("ProjectedZones")!.GetValue(canvas)!;
            var zone = projected.Single(z => z.TaskId == postman);
            type.GetProperty("OnlyActive")!.SetValue(canvas, true);
            Check((bool)Call(canvas, "IsZoneVisible", zone, TaskState.None)!, "Selecting unlogged task still hides it under OnlyActive");
            Check((bool)Call(canvas, "IsZoneVisible", zone, TaskState.Finished)!, "Selecting completed task still hides it under OnlyActive");
            type.GetProperty("SelectedTaskId")!.SetValue(canvas, null);
            Check(!(bool)Call(canvas, "IsZoneVisible", zone, TaskState.None)!, "Deselecting did not restore active-only visibility");
            type.GetProperty("SelectedTaskId")!.SetValue(canvas, postman);
            if (output != null)
            {
                Directory.CreateDirectory(output);
                canvas.Size = new Size(920, 700);
                using var without = new Bitmap(canvas.Width, canvas.Height);
                type.GetProperty("ShowZones")!.SetValue(canvas, false);
                canvas.DrawToBitmap(without, new Rectangle(Point.Empty, canvas.Size));
                type.GetProperty("ShowZones")!.SetValue(canvas, true);
                using var image = new Bitmap(canvas.Width, canvas.Height);
                canvas.DrawToBitmap(image, new Rectangle(Point.Empty, canvas.Size));
                var difference = 0;
                for (var y=0;y<image.Height;y++) for (var x=0;x<image.Width;x++)
                    if (image.GetPixel(x,y) != without.GetPixel(x,y)) difference++;
                Check(difference > 50, "Selected unlogged Postman task has no visible pixels on Factory");
                image.Save(Path.Combine(output, "postman-factory.png"));
                var mapCanvas = (MapCanvas)canvas;
                var state = new MobileState { Map = mapCanvas.Map, Zones = mapCanvas.ProjectedZones, SelectedTaskId = postman, OnlyActive = true,
                    TaskStatesJson = "{\""+postman+"\":0}" };
                state.Flush(); File.WriteAllText(Path.Combine(output, "locations-state.json"), state.Json);
            }
        }
        if (output != null)
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output,"task-locations-audit.json"),JsonSerializer.Serialize(new { snapshotPoints=TaskLocations.All.Count(), tasksRestored=restored.Length,
                withoutCoordinatesBefore=noCoordinatesBefore, withoutCoordinatesAfter=tasks.Count-allZones.Select(z=>z.TaskId).Distinct().Count(), tasks=reports },new JsonSerializerOptions { WriteIndented=true }));
        }
        PlayerLocator.SetSetting("taskFilter", ""); PlayerLocator.SetSetting("view", ""); Apply(seedText);
        Console.WriteLine($"PASS: {checks} snapshot coverage, preserved polygons, cache fallback, map variants, floors, nearby alternatives, API parsing and selected-task visibility checks; {restored.Length} empty tasks restored.");
        return checks;
    }
    private static bool Covers(ZoneShape zone, double x, double z)
    {
        var polygon = new JsonArray(zone.WorldPoints.Select(p => (JsonNode)new JsonArray((double)p.X,(double)p.Y)).ToArray());
        return TaskLocations.Covers(new JsonObject { ["p"] = polygon }, x, z);
    }
}
