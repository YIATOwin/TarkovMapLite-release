using System.Reflection;
using System.Text.Json.Nodes;
using TarkovMapLite;

internal static class StoryTaskChecks
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static Type T(string name) => typeof(MapPack).Assembly.GetType("TarkovMapLite." + name)!;
    private static object? Call(object target, string method, params object?[] args) => target.GetType().GetMethod(method, Flags)!.Invoke(target, args);
    private static V Field<V>(object target, string name) => (V)target.GetType().GetField(name, Flags)!.GetValue(target)!;
    public static int Run(string? output)
    {
        var checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        Check(StoryTasks.AllSeries.Count == 10 && StoryTasks.Entries.Count == 522, "Story sections were imported as tasks or targets were lost");
        Check(StoryTasks.Entries.Select(t => t.Id).Distinct().Count() == 522, "Story target identity collision");
        Check(StoryTasks.Entries.Count(t => StoryTasks.HasCoordinates(t.Id)) == 116, "Story coordinate join failed");
        var all = new TaskFilter { Region = "all", Status = -1 };
        var target = StoryTasks.Entries.First();
        Check(!TaskQuery.Matches(target, all, "reserve", TaskState.Started), "Story was enabled by ordinary all-task filters");
        var on = all with { ShowStory = true, StorySeries = target.StorySeries };
        Check(TaskQuery.Matches(target, on with { Status = 1 }, "reserve", TaskState.None), "Manual story is blocked by active-task/log state");
        Check(!TaskQuery.Matches(target, on with { StorySeries = "the-ticket" }, "reserve", TaskState.None), "Another series leaked into the list");
        Check(TaskQuery.Tags(target, TaskState.Finished, false).Contains("手动显示") &&
            !TaskQuery.Tags(target, TaskState.Finished, false).Contains("已完成"), "Story displayed automatic log progress");
        Check(StoryTasks.FloorOf(MapPack.Find("factory")!, "Basement") == -1 &&
            StoryTasks.FloorOf(MapPack.Find("factory")!, "") == 0, "Source base-layer -1 confused with actual basement");
        Check(StoryTasks.FloorOf(MapPack.Find("interchange")!, "First_Floor") == 1, "Story floor semantic conversion failed");
        Check(StoryTasks.AllSeries.SelectMany(s => s.Tasks).SelectMany(t => t.Zones)
            .Where(z => z.SourceFloorIndex == -1).All(z => z.Layer == ""), "Source floor indexes were treated as storeys");
        Check(StoryTasks.Entries.Any(t => TaskQuery.Tags(t, TaskState.None, false).Contains("底图未收录")), "Unavailable Icebreaker base map was silently substituted");
        var totalZones = StoryTasks.AllSeries.Sum(s => MapPack.Maps.Select(m => m.Data).Distinct().Sum(m => StoryTasks.Zones(s.Id, m).Count));
        Check(totalZones == 118, "Unexpected loss of supported-map coordinate groups");
        foreach (var mapKey in new[] { "reserve", "reserve-tiles", "factory", "interchange" })
        {
            var map = MapPack.Find(mapKey)!;
            foreach (var zone in StoryTasks.AllSeries.SelectMany(s => StoryTasks.Zones(s.Id, map.Data)))
            {
                var projected = zone.Project(map);
                Check(projected.IsStory && projected.Points.Length >= 3 && projected.Points.All(p => float.IsFinite(p.X) && float.IsFinite(p.Y)),
                    "Story point/area projection failed");
            }
        }
        using (var canvas = (Control)Activator.CreateInstance(T("MapCanvas"), true)!)
        {
            var zone = StoryTasks.Zones("tour", "factory").First();
            Check(!(bool)Call(canvas, "IsZoneVisible", zone, TaskState.Started)!, "Canvas default reveals story");
            canvas.GetType().GetProperty("ShowStory")!.SetValue(canvas, true);
            canvas.GetType().GetProperty("OnlyActive")!.SetValue(canvas, true);
            Check((bool)Call(canvas, "IsZoneVisible", zone, TaskState.None)!, "Explicit story enable is blocked by canvas OnlyActive");
            foreach (var state in Enum.GetValues<TaskState>())
            {
                var color = (Color)T("MapCanvas").GetMethod("TaskColor", Flags)!.Invoke(null, [zone.TaskId, state])!;
                Check(color.R == 255 && color.G == 109 && color.B == 61, "Story color changed with log status");
            }
        }
        PlayerLocator.SetSetting("taskFilter", ""); PlayerLocator.SetSetting("view", "");
        using (var form = (Form)Activator.CreateInstance(T("MapForm"), true)!)
        {
            _ = form.Handle; Call(form, "FillMapCombo"); Call(form, "SelectMap", "factory-tiles", false);
            var canvas = Field<Control>(form, "_canvas");
            var list = Field<TaskListControl>(form, "_taskList");
            ZoneShape[] Zones() => ((IEnumerable<ZoneShape>)canvas.GetType().GetProperty("Zones")!.GetValue(canvas)!).ToArray();
            Check(!Zones().Any(z => z.IsStory), "Restored session revealed story before a manual action");
            Call(form, "PatchTaskFilter", "trader", "story");
            var filter = Field<TaskFilter>(form, "_taskFilter");
            Check(filter.ShowStory && filter.Trader == "story" && filter.StorySeries == "tour", "Story merchant selection did not enable manual story browsing");
            Check(Zones().Any(z => z.IsStory) && Zones().Any(z => !z.IsStory), "Story displaced merchant map zones");
            Check(list.Items.Count > 0 && list.Items.Cast<object>().All(i => (string)i.GetType().GetProperty("Id")!.GetValue(i)! is { } id && StoryTasks.IsTask(id)), "Merchant rows leaked into story series");
            var selected = Zones().First(z => z.IsStory).TaskId;
            Call(form, "SelectTaskFromMobile", selected);
            Check(Field<RichTextBox>(form, "_detail").Text.Contains(StoryTasks.Get(selected)!.Target.Description), "Story detail truncated the full target text");
            var log = Field<TaskLog>(form, "_log");
            Call(log, "ApplyNotification", "{\"message\":{\"type\":12,\"templateId\":\"" + selected + " success\"}}");
            Call(form, "RefreshTaskList");
            Check(list.SelectedItem != null && (TaskState)list.SelectedItem.GetType().GetProperty("State")!.GetValue(list.SelectedItem)! == TaskState.None,
                "A log notification changed a story row's manual state");
            Check(!TaskFilter.Load(true, false).ShowStory && TaskFilter.Load(true, false).Trader != StoryTasks.TraderId,
                "Story activation persisted into the next launch");
            var regularZoneCount = Zones().Count(z => !z.IsStory);
            foreach (var series in StoryTasks.AllSeries)
            {
                Call(form, "PatchTaskFilter", "storySeries", series.Id);
                Check(Zones().Where(z => z.IsStory).All(z => StoryTasks.Get(z.TaskId)!.Series.Id == series.Id), "Switching series left old story markers");
                Check(Zones().Count(z => !z.IsStory) == regularZoneCount, "Switching series altered merchant zones");
            }
            var sidebar = form.Controls.Cast<Control>().Single(c => c.Dock == DockStyle.Right);
            var summary = Field<Label>(form, "_taskSummary");
            var originalTitle = list.Font; var originalTag = summary.Font;
            using (var largeTitle = new Font(originalTitle.FontFamily, originalTitle.Size * 2))
            using (var largeTag = new Font(originalTag.FontFamily, originalTag.Size * 2))
            {
                form.ClientSize = new Size(2240, 1600); sidebar.Width = 672; list.Font = largeTitle; summary.Font = largeTag;
                foreach (var series in StoryTasks.AllSeries)
                {
                    Call(form, "ChangeTaskFilter", on with { Trader = StoryTasks.TraderId, StorySeries = series.Id });
                    using var graphics = list.CreateGraphics(); var fits = true;
                    for (var i=0;i<list.Items.Count;i++)
                    {
                        var measure = new MeasureItemEventArgs(graphics, i); Call(form, "MeasureTaskRow", list, measure);
                        fits &= list.GetItemHeight(i) >= measure.ItemHeight;
                    }
                    Check(fits, "Long story target clipped at 200% text/layout scale");
                }
                list.Font = originalTitle; summary.Font = originalTag;
            }
            sidebar.Width = 336; form.ClientSize = new Size(1120, 800);
            Call(form, "ChangeTaskFilter", new TaskFilter { Region = "current", ShowStory = true, Trader = StoryTasks.TraderId, Status = -1 });
            Call(form, "SelectTaskFromMobile", selected);
            if (output != null)
            {
                Directory.CreateDirectory(output);
                using var host = new Form { ClientSize = new Size(1120,800), Text = "主线任务验证" };
                foreach (var control in form.Controls.Cast<Control>().ToArray()) host.Controls.Add(control);
                host.Show(); Application.DoEvents();
                using (var frame = new Bitmap(host.Width, host.Height))
                { host.DrawToBitmap(frame, new Rectangle(Point.Empty, host.Size)); frame.Save(Path.Combine(output, "story-map-desktop.png")); }
                var mobile = Field<object>(form, "_mobile");
                mobile.GetType().GetField("_listener", Flags)!.SetValue(mobile, new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0));
                Call(form, "PumpMobile");
                var snapshot = mobile.GetType().GetProperty("State")!.GetValue(mobile)!;
                var text = (string)snapshot.GetType().GetProperty("Json")!.GetValue(snapshot)!;
                File.WriteAllText(Path.Combine(output, "story-state.json"), text);
                var json = JsonNode.Parse(text)!;
                Check(json["taskFilters"]?["showStory"]?.GetValue<bool>() == true &&
                    json["zones"]!.AsArray().Any(z => z?["story"]?.GetValue<bool>() == true), "Mobile snapshot lost explicit story activation or marker kind");
                Call(mobile, "Stop");
            }
            Call(form, "PatchTaskFilter", "showStory", "false");
            Check(!Zones().Any(z => z.IsStory) && !Field<TaskFilter>(form, "_taskFilter").ShowStory, "Disabling story left map markers");
            Check(!StoryTasks.IsTask((string?)canvas.GetType().GetProperty("SelectedTaskId")!.GetValue(canvas)), "Disabling story retained selected story highlight");
        }
        using (var reopened = (Form)Activator.CreateInstance(T("MapForm"), true)!)
            Check(!Field<TaskFilter>(reopened, "_taskFilter").ShowStory, "Restart reopened story automatically");
        PlayerLocator.SetSetting("taskFilter", ""); PlayerLocator.SetSetting("view", "");
        Console.WriteLine($"PASS: {checks} story dataset, opt-in, manual-only state, series, floor conversion, projection, color and long-row checks.");
        return checks;
    }
}
