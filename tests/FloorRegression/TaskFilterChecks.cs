using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using TarkovMapLite;

internal static class TaskFilterChecks
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static Type T(string name) => typeof(MapPack).Assembly.GetType("TarkovMapLite." + name)!;
    private static object? Call(object target, string method, params object?[] args) => target.GetType().GetMethod(method, Flags)!.Invoke(target, args);
    private static V Field<V>(object target, string name) => (V)target.GetType().GetField(name, Flags)!.GetValue(target)!;
    private static void Apply(string text) => T("TarkovData").GetMethod("ApplyCache", Flags)!.Invoke(null, [text, true]);

    public static int Run(string? output = null)
    {
        var checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        var seed = (string)T("Assets").GetMethod("ReadText", Flags)!.Invoke(null, ["data-seed.json"])!;
        Apply(seed);
        var catalog = TaskQuery.Build();
        Check(catalog.Count(t => !t.IsStory) == 499, "Offline merchant task catalogue was lost");
        Check(catalog.Count(t => t.Metadata.MinLevel != null && t.Metadata.Kappa != null) >= 470,
            "Bundled task metadata is not usable offline");
        Check(catalog.Any(t => t.Metadata.Regions.Length > 0 && !TarkovData.ZonesByMap.Values.SelectMany(z => z).Any(z => z.TaskId == t.Id)),
            "Tasks with map restrictions but no coordinates need region metadata");
        var entry = new TaskEntry("test", "中英任务", "trader", new TaskMetadata
        { TraderName = "商人 Mechanic", Regions = ["reserve", "customs"], RegionKnown = true,
            MinLevel = 20, Faction = "USEC", Kappa = true, Lightkeeper = false },
            ["击杀", "交付"], "中英任务 商人 Mechanic 目标 交付 医疗包 钥匙 Key");
        var all = new TaskFilter { Region = "all", Status = -1 };
        bool Match(TaskFilter filter, TaskEntry? task = null) => TaskQuery.Matches(task ?? entry, filter, "reserve", TaskState.Started);
        Check(Match(all), "All filter hides tasks");
        Check(Match(all with { Region = "current" }), "Follow-map filter does not match");
        Check(Match(all with { Region = "customs" }), "Secondary task map not matched");
        Check(!Match(all with { Region = "woods" }), "Unrelated map matched");
        Check(Match(all with { Search = "MECHANIC 医疗包 KEY" }), "Case-insensitive multi-word item/key search failed");
        Check(!Match(all with { Search = "医疗包 不存在" }), "Search must match every word");
        Check(Match(all with { Category = "交付" }), "Multi-category membership was reduced to first objective");
        Check(!Match(all with { Category = "撤离" }), "Wrong category matched");
        Check(Match(all with { PlayerLevel = 20 }), "Inclusive level boundary failed");
        Check(!Match(all with { PlayerLevel = 19 }), "Below minimum level matched");
        Check(Match(all with { Trader = "trader", Faction = "USEC", Kappa = true, Lightkeeper = false }), "Combined filters failed");
        Check(!Match(all with { Status = 3 }), "Finished filter matched a started task");
        Check(TaskQuery.StateLabel(TaskState.None) == "日志未记录", "Missing game logs imply unstarted incorrectly");
        var unknown = entry with { Metadata = new() };
        Check(Match(all, unknown), "Unknown metadata hides tasks with no metadata filters");
        foreach (var filter in new[] { all with { PlayerLevel = 100 }, all with { Kappa = false },
                     all with { Lightkeeper = false }, all with { Faction = "Any" } })
            Check(!Match(filter, unknown), "Unknown value became a false flag or a common faction");
        Check(Match(all with { Region = "unknown" }, unknown), "Unknown-region filter failed");
        Check(!Match(all with { Region = "any" }, unknown), "No coordinates became unrestricted region");
        Check(Match(all with { Region = "any" }, entry with { Metadata = new() { RegionKnown = true } }), "Explicit unrestricted region missing");
        Check(TaskQuery.Tags(entry, TaskState.Started, true).Contains("Kappa 必需") &&
              !TaskQuery.Tags(unknown, TaskState.None, false).Contains("通用阵营"), "Metadata tags misrepresent unknown fields");
        var live = JsonNode.Parse("""
            {"map":null,"trader":{"id":"5a7c2eca46aef81a7ca2145d","name":"Mechanic"},"minPlayerLevel":20,
             "factionName":"Any","kappaRequired":false,"lightkeeperRequired":true,
             "objectives":[{"maps":["Woods"]}]}
            """)!.AsObject();
        var parsed = (TaskMetadata)typeof(TaskMetadata).GetMethod("FromApi", Flags)!.Invoke(null, [live])!;
        Check(parsed.Regions.SequenceEqual(["woods"]) && parsed.RegionKnown && parsed.Kappa == false &&
            parsed.Lightkeeper == true && parsed.MinLevel == 20 && parsed.Faction == "Any", "Live API metadata parsing failed");

        var old = JsonNode.Parse(seed)!.AsObject();
        foreach (var task in old["tasks"]!.AsArray()) task!.AsObject().Remove("meta");
        old["schema"] = 12; Apply(old.ToJsonString());
        Check(TaskQuery.Build().Count(t => t.Metadata.MinLevel != null) >= 470, "Old cache did not merge offline metadata");
        Apply("{\"schema\":12,\"tasks\":[{\"id\":\"unknown-task\",\"name\":\"未知\",\"obj\":[{\"d\":\"目标\",\"t\":\"visit\"}]}]}");
        var orphan = TaskQuery.Build().Single(t => !t.IsStory);
        Check(!orphan.Metadata.RegionKnown && orphan.Metadata.Kappa == null, "Unmatched old-cache task invented metadata");
        Apply(seed);

        PlayerLocator.SetSetting("taskFilter", ""); PlayerLocator.SetSetting("view", "");
        using (var form = (Form)Activator.CreateInstance(T("MapForm"), true)!)
        {
            _ = form.Handle; Call(form, "FillMapCombo"); Call(form, "ApplyMap", false);
            Call(form, "ChangeTaskFilter", all);
            var list = Field<TaskListControl>(form, "_taskList");
            var canvas = Field<Control>(form, "_canvas");
            var current = (MapProjection)form.GetType().GetProperty("CurrentMap", Flags)!.GetValue(form)!;
            var cross = TaskQuery.Build().First(t => t.Metadata.Regions.Length == 1 && t.Metadata.Regions[0] != current.Data &&
                TarkovData.ZonesByMap.Values.SelectMany(z => z).Any(z => z.TaskId == t.Id));
            Call(form, "SelectTaskFromMobile", cross.Id);
            Check(((MapProjection)form.GetType().GetProperty("CurrentMap", Flags)!.GetValue(form)!).Key == current.Key,
                "Selecting a cross-map task switched the map");
            Check(canvas.GetType().GetProperty("SelectedTaskId")!.GetValue(canvas) == null,
                "Cross-map selection hides current-map zones");
            var selected = list.SelectedItem;
            Call(form, "RefreshTaskList");
            Check(list.SelectedItem?.ToString() == selected?.ToString(), "Refresh lost a valid selection");
            Call(form, "ChangeTaskFilter", all with { Search = "no-matching-task-329847" });
            Check(list.Items.Count == 0 && list.SelectedIndex == -1 && Field<string>(form, "_currentTaskId") == "",
                "Excluded selection retained stale details");
            Call(form, "ChangeTaskFilter", all);
            Call(form, "SelectTaskFromMobile", cross.Id);
            var destinations = ((System.Collections.IEnumerable)Call(form, "TaskDestinations", cross.Id)!).Cast<object>().ToArray();
            Check(destinations.Length > 0, "Cross-map task has no explicit destination");
            var target = (string)destinations[0].GetType().GetProperty("Key")!.GetValue(destinations[0])!;
            Call(form, "JumpToTaskMap", target, cross.Id);
            Check(((MapProjection)form.GetType().GetProperty("CurrentMap", Flags)!.GetValue(form)!).Data == cross.Metadata.Regions[0],
                "Explicit map jump failed");
            Check(list.SelectedItem?.ToString() == cross.Name, "Explicit jump lost task selection");
            Call(form, "ChangeTaskFilter", all with { Search = "Mechanic", PlayerLevel = 30, Kappa = true });
            var restored = TaskFilter.Load(true, false);
            Check(restored.Search == "" && restored.PlayerLevel == 30 && restored.Kappa == true && restored.Region == "all",
                "Preferences or session-only search persistence failed");
            Call(form, "ChangeTaskFilter", all);
            foreach (var size in new[] { new Size(1120, 700), new Size(760, 480) })
            {
                form.ClientSize = size; form.PerformLayout();
                Check(Field<TextBox>(form, "_taskSearch").Width > 100, "Search box collapsed at narrow width");
                var clip = list.Parent!.ClientRectangle;
                Check(clip.Height > 60, "Filters leave no task-list viewport");
            }
            var sidebar = form.Controls.Cast<Control>().Single(c => c.Dock == DockStyle.Right);
            var summary = Field<Label>(form, "_taskSummary");
            var titleFont = list.Font; var tagFont = summary.Font;
            foreach (var scale in new[] { 1f, 1.5f, 2f })
            {
                using var scaledTitle = new Font(titleFont.FontFamily, titleFont.Size * scale);
                using var scaledTag = new Font(tagFont.FontFamily, tagFont.Size * scale);
                form.ClientSize = new Size((int)(1120 * scale), (int)(800 * scale));
                sidebar.Width = (int)(336 * scale); list.Font = scaledTitle; summary.Font = scaledTag;
                Call(form, "RefreshTaskList");
                using var graphics = list.CreateGraphics();
                var fits = true;
                for (var i = 0; i < list.Items.Count; i++)
                {
                    var measure = new MeasureItemEventArgs(graphics, i);
                    Call(form, "MeasureTaskRow", list, measure);
                    var actualHeight = list.GetItemHeight(i);
                    if (fits && actualHeight < measure.ItemHeight) Console.WriteLine($"Task row size mismatch: {i}, native={actualHeight}, expected={measure.ItemHeight}, width={list.ClientSize.Width}, item={list.Items[i]}");
                    fits &= actualHeight >= measure.ItemHeight;
                }
                Check(fits, $"Native variable task row clipped at {scale * 100}% text/layout scale");
                list.Font = titleFont; summary.Font = tagFont;
            }
            sidebar.Width = 336;
            using (var scrollHost = new Form { ClientSize = new Size(336, 220) })
            {
                var originalParent = list.Parent!;
                scrollHost.Controls.Add(list); list.Dock = DockStyle.Fill;
                scrollHost.Show(); Application.DoEvents(); list.Remeasure(); list.SelectedIndex = 0;
                Call(list, "OnKeyDown", new KeyEventArgs(Keys.Down));
                Check(list.SelectedIndex == 1, "Task-list keyboard selection failed");
                list.TopIndex = 0;
                Call(list, "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, 20, 20, -120));
                Check(list.AutoScrollPosition.Y < 0, "Task-list native wheel scrolling failed");
                var topBefore = list.TopIndex;
                Call(form, "RefreshTaskList");
                Check(list.TopIndex == topBefore, "Task-list refresh lost top visible row");
                list.Dock = DockStyle.None; originalParent.Controls.Add(list);
                list.Bounds = new Rectangle(0, 0, originalParent.ClientSize.Width + 18, originalParent.ClientSize.Height);
            }
            if (output != null)
            {
                Directory.CreateDirectory(output);
                // A plain host displays the actual controls without starting the app's Load/network watcher.
                using var host = new Form { ClientSize = new Size(1120, 800), Text = "任务面板验证" };
                foreach (var control in form.Controls.Cast<Control>().ToArray()) host.Controls.Add(control);
                host.Show(); Application.DoEvents();
                Call(form, "RefreshTaskList");
                using (var image = new Bitmap(host.Width, host.Height))
                { host.DrawToBitmap(image, new Rectangle(Point.Empty, host.Size)); image.Save(Path.Combine(output, "task-panel-desktop.png")); }
                Call(Field<Control>(form, "_taskMore"), "OnClick", EventArgs.Empty); Application.DoEvents();
                using (var image = new Bitmap(host.Width, host.Height))
                { host.DrawToBitmap(image, new Rectangle(Point.Empty, host.Size)); image.Save(Path.Combine(output, "task-panel-desktop-expanded.png")); }
                var mobile = Field<object>(form, "_mobile");
                mobile.GetType().GetField("_listener", Flags)!.SetValue(mobile, new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0));
                Call(form, "PumpMobile");
                var snapshot = mobile.GetType().GetProperty("State")!.GetValue(mobile)!;
                File.WriteAllText(Path.Combine(output, "task-state.json"), (string)snapshot.GetType().GetProperty("Json")!.GetValue(snapshot)!);
                Call(mobile, "Stop");
            }
        }
        var state = Activator.CreateInstance(T("MobileState"), true)!;
        T("MobileState").GetProperty("Map")!.SetValue(state, MapPack.Find("reserve"));
        T("MobileState").GetProperty("TaskFiltersJson")!.SetValue(state, "{\"region\":\"all\"}");
        T("MobileState").GetProperty("TaskStatesJson")!.SetValue(state, "{\"hidden-by-search\":1}");
        Call(state, "Flush");
        var json = JsonNode.Parse((string)T("MobileState").GetProperty("Json")!.GetValue(state)!)!;
        Check(json["taskStates"]?["hidden-by-search"]?.GetValue<int>() == 1 && json["taskFilters"]?["region"]?.GetValue<string>() == "all",
            "Mobile snapshot omitted independent zone states or filters");
        var revision = json["rev"]!.GetValue<int>();
        T("MobileState").GetProperty("TaskFiltersJson")!.SetValue(state, "{\"region\":\"current\"}");
        Call(state, "Flush");
        json = JsonNode.Parse((string)T("MobileState").GetProperty("Json")!.GetValue(state)!)!;
        Check(json["rev"]!.GetValue<int>() > revision, "Same-count filter updates do not invalidate mobile state");
        PlayerLocator.SetSetting("taskFilter", ""); PlayerLocator.SetSetting("view", ""); Apply(seed);
        Console.WriteLine($"PASS: {checks} task metadata, filters, persistence, cross-map selection and mobile protocol checks.");
        return checks;
    }
}
