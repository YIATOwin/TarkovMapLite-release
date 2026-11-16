using System.Text.Json;
using System.Text.Json.Nodes;

namespace TarkovMapLite;

internal sealed partial class MapForm
{
    private TaskFilter _taskFilter = new();
    private TaskEntry[] _taskCatalog = [];
    private object? _taskDataSource;
    private readonly TaskSearchBox _taskSearch = new();
    private readonly ComboBox _taskRegion = new(), _taskStatus = new(), _taskTrader = new(),
        _taskCategory = new(), _taskFaction = new(), _taskKappa = new(), _taskLightkeeper = new();
    private readonly TaskSearchBox _taskLevel = new();
    private readonly UiButton _taskStoryToggle = new() { Text = "主线：关闭" };
    private readonly ComboBox _taskStorySeries = new();
    private string _storyZoneSignature = "";
    private object? _storyBaseZones;
    private List<ZoneShape>? _combinedTaskZones;
    private readonly Label _taskSummary = new();
    private readonly TableLayoutPanel _taskAdvanced = new();
    private readonly UiButton _taskMore = new() { Text = "筛选" };
    private readonly FlowLayoutPanel _taskJump = new();
    private readonly System.Windows.Forms.Timer _taskSearchTimer = new() { Interval = 200 };
    private readonly Font _taskTitleFont = new("Microsoft YaHei UI", 10.5f);
    private readonly Font _taskTagFont = new("Microsoft YaHei UI", 9f);
    private bool _syncingTaskControls, _refreshingTasks;
    private string _taskOptionsJson = "{}", _taskStatesJson = "{}";
    private int _taskContentVersion;
    private static readonly JsonSerializerOptions TaskJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private sealed record FilterOption(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    private Control BuildTaskFilters()
    {
        _taskFilter = TaskFilter.Load(_viewSettings.OnlyActive, _viewSettings.AnyRegion);
        _noZone.Active = _taskFilter.Region == "any";
        var host = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1,
            Padding = new Padding(8), BackColor = UiTokens.PanelBack, Margin = Padding.Empty };
        TableLayoutPanel Row(params Control[] controls)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Top, Height = 36, ColumnCount = controls.Length,
                Margin = Padding.Empty, Padding = Padding.Empty };
            for (var i = 0; i < controls.Length; i++)
            {
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / controls.Length));
                controls[i].Dock = DockStyle.Fill;
                controls[i].Margin = new Padding(0, 4, i < controls.Length - 1 ? 8 : 0, 4);
                row.Controls.Add(controls[i], i, 0);
            }
            return row;
        }
        _taskSearch.PlaceholderText = "搜索任务、目标、物品或钥匙";
        _taskSearch.MaxLength = 256;
        _taskSearch.AccessibleName = "搜索任务";
        _taskLevel.PlaceholderText = "角色等级（不限）";
        _taskLevel.MaxLength = 3;
        _taskLevel.AccessibleName = "角色等级，只筛选最低等级要求";
        foreach (var box in new[] { _taskSearch, _taskLevel })
        {
            box.BackColor = UiTokens.Sunken; box.ForeColor = TextMain; box.BorderStyle = BorderStyle.FixedSingle;
            box.Font = _taskTagFont;
            box.Leave += (_, _) => { if (!_taskSearchTimer.Enabled) SyncTaskFilterControls(); };
        }
        var reset = new UiButton { Text = "重置", AutoSize = true };
        reset.Click += (_, _) => ChangeTaskFilter(new());
        var searchLabel = new Label { Text = "搜索", ForeColor = TextDim, Font = _taskTagFont, TextAlign = ContentAlignment.MiddleLeft };
        var searchRow = Row(searchLabel, _taskSearch, reset);
        searchRow.ColumnStyles[0] = new ColumnStyle(SizeType.Absolute, 40);
        searchRow.ColumnStyles[1] = new ColumnStyle(SizeType.Percent, 100);
        searchRow.ColumnStyles[2] = new ColumnStyle(SizeType.Absolute, 64);
        host.Controls.Add(searchRow);
        host.Controls.Add(Row(_taskRegion, _taskStatus, _taskMore));
        host.Controls.Add(Row(_taskStoryToggle, _taskStorySeries));
        _taskStoryToggle.Click += (_, _) => PatchTaskFilter("showStory", (!_taskFilter.ShowStory).ToString());
        _taskMore.Click += (_, _) => { _taskAdvanced.Visible = !_taskAdvanced.Visible; _taskMore.Text = _taskAdvanced.Visible ? "收起筛选" : "筛选"; };
        _taskAdvanced.ColumnCount = 1; _taskAdvanced.AutoSize = true; _taskAdvanced.Dock = DockStyle.Top;
        _taskAdvanced.Margin = Padding.Empty;
        _taskAdvanced.Controls.Add(Row(_taskTrader, _taskCategory));
        var levelPanel = Row(new Label { Text = "角色等级", ForeColor = TextDim, Font = _taskTagFont, TextAlign = ContentAlignment.MiddleLeft }, _taskLevel);
        levelPanel.ColumnStyles[0] = new ColumnStyle(SizeType.Absolute, 72);
        levelPanel.ColumnStyles[1] = new ColumnStyle(SizeType.Percent, 100);
        _taskAdvanced.Controls.Add(Row(_taskFaction, levelPanel));
        _taskAdvanced.Controls.Add(Row(_taskKappa, _taskLightkeeper));
        host.Controls.Add(_taskAdvanced);
        _taskAdvanced.Visible = false;
        _taskSummary.AutoSize = true; _taskSummary.ForeColor = TextDim; _taskSummary.Font = _taskTagFont;
        _taskSummary.Margin = new Padding(0, 4, 0, 4);
        host.Controls.Add(_taskSummary);
        host.SizeChanged += (_, _) => _taskSummary.MaximumSize = new Size(Math.Max(80, host.ClientSize.Width - 16), 0);

        foreach (var (combo, field, name) in new[] {
            (_taskRegion, "region", "地区"), (_taskStatus, "status", "状态"), (_taskTrader, "trader", "商人"),
            (_taskCategory, "category", "任务类别"), (_taskFaction, "faction", "阵营"),
            (_taskKappa, "kappa", "Kappa 必需性"), (_taskLightkeeper, "lightkeeper", "灯塔商人必需性"),
            (_taskStorySeries, "storySeries", "主线系列") })
        {
            combo.DropDownStyle = ComboBoxStyle.DropDownList; combo.FlatStyle = FlatStyle.Flat;
            combo.BackColor = UiTokens.ButtonBack; combo.ForeColor = TextMain; combo.Font = _taskTagFont;
            combo.AccessibleName = name; combo.DropDownWidth = 240;
            combo.SelectedIndexChanged += (_, _) =>
            {
                if (!_syncingTaskControls && combo.SelectedItem is FilterOption option) PatchTaskFilter(field, option.Value);
            };
        }
        void SearchChanged(object? sender, EventArgs e)
        {
            if (_syncingTaskControls) return;
            _taskSearchTimer.Stop(); _taskSearchTimer.Start();
        }
        _taskSearch.TextChanged += SearchChanged;
        _taskLevel.TextChanged += SearchChanged;
        _taskSearch.CompositionEnded += SearchChanged;
        _taskLevel.CompositionEnded += SearchChanged;
        _taskSearchTimer.Tick += (_, _) =>
        {
            if (_taskSearch.Composing || _taskLevel.Composing) return;
            _taskSearchTimer.Stop();
            if (_taskLevel.Text.Length > 0 && (!int.TryParse(_taskLevel.Text, out var level) || level is < 1 or > 100))
            { _taskSummary.Text = "角色等级请输入 1～100；留空表示不限"; return; }
            ChangeTaskFilter(_taskFilter with { Search = _taskSearch.Text,
                PlayerLevel = int.TryParse(_taskLevel.Text, out var number) ? number : null });
        };
        FillTaskFilterOptions();
        SyncTaskFilterControls();
        return host;
    }

    private void FillTaskFilterOptions()
    {
        _syncingTaskControls = true;
        try
        {
            void Fill(ComboBox control, IEnumerable<FilterOption> options)
            { control.Items.Clear(); control.Items.AddRange(options.Cast<object>().ToArray()); }
            var regions = new List<FilterOption> { new("current", "地区：当前地图"), new("all", "全部地区"),
                new("any", "任意地区"), new("unknown", "地区未知") };
            regions.AddRange(MapPack.Maps.GroupBy(m => m.Data).Select(g => g.First())
                .Select(m => new FilterOption(m.Data, TaskQuery.MapLabel(m.Data))));
            regions.AddRange(_taskCatalog.SelectMany(t => t.Metadata.Regions).Distinct()
                .Where(k => !regions.Any(r => r.Value == k)).Select(k => new FilterOption(k, TaskQuery.MapLabel(k))));
            var traders = new[] { new FilterOption("", "全部商人") }.Concat(_taskCatalog.GroupBy(t => t.TraderId)
                .Where(g => g.Key.Length > 0).Select(g => new FilterOption(g.Key, g.First().Metadata.TraderName))).ToArray();
            var states = new[] { new FilterOption("-1", "全部状态"), new("1", "进行中"), new("3", "已完成"),
                new("2", "已失败"), new("0", "日志未记录") };
            var categories = new[] { new FilterOption("", "全部类别") }.Concat(TaskQuery.Categories.Select(c => new FilterOption(c, c))).ToArray();
            var factions = new[] { new FilterOption("", "全部阵营"), new("Any", "通用阵营"), new("USEC", "USEC"), new("BEAR", "BEAR") };
            var kappa = new[] { new FilterOption("", "Kappa：不限"), new("true", "Kappa：必需"), new("false", "Kappa：非必需") };
            var lightkeeper = new[] { new FilterOption("", "灯塔商人：不限"), new("true", "灯塔商人：必需"), new("false", "灯塔商人：非必需") };
            Fill(_taskRegion, regions); Fill(_taskStatus, states); Fill(_taskTrader, traders); Fill(_taskCategory, categories);
            Fill(_taskFaction, factions); Fill(_taskKappa, kappa); Fill(_taskLightkeeper, lightkeeper);
            var storySeries = StoryTasks.AllSeries.OrderBy(c => c.Order)
                .Select(c => new FilterOption(c.Id, $"{c.Order}. {c.Name}")).ToArray();
            Fill(_taskStorySeries, storySeries);
            _taskOptionsJson = JsonSerializer.Serialize(new { regions, traders, states, categories, factions, kappa, lightkeeper, storySeries }, TaskJson);
        }
        finally { _syncingTaskControls = false; }
    }

    private void SyncTaskFilterControls()
    {
        _syncingTaskControls = true;
        try
        {
            void Select(ComboBox box, string value)
            { box.SelectedItem = box.Items.Cast<FilterOption>().FirstOrDefault(o => o.Value == value); }
            Select(_taskRegion, _taskFilter.Region); Select(_taskStatus, _taskFilter.Status.ToString());
            Select(_taskTrader, _taskFilter.Trader); Select(_taskCategory, _taskFilter.Category);
            Select(_taskFaction, _taskFilter.Faction);
            Select(_taskKappa, _taskFilter.Kappa?.ToString().ToLowerInvariant() ?? "");
            Select(_taskLightkeeper, _taskFilter.Lightkeeper?.ToString().ToLowerInvariant() ?? "");
            Select(_taskStorySeries, _taskFilter.StorySeries);
            _taskStorySeries.Enabled = _taskFilter.ShowStory;
            _taskStoryToggle.Text = _taskFilter.ShowStory ? "主线：已开启" : "主线：关闭";
            var storyOnly = _taskFilter.Trader == StoryTasks.TraderId;
            foreach (var control in new Control[] { _taskStatus, _taskLevel, _taskFaction, _taskKappa, _taskLightkeeper })
                control.Enabled = !storyOnly;
            // Avoid replacing native edit contents (and its IME/caret) on every log or map refresh.
            if (_taskSearch.Text != _taskFilter.Search && !_taskSearch.Composing && !_taskSearch.Focused) _taskSearch.Text = _taskFilter.Search;
            var level = _taskFilter.PlayerLevel?.ToString() ?? "";
            if (_taskLevel.Text != level && !_taskLevel.Focused) _taskLevel.Text = level;
        }
        finally { _syncingTaskControls = false; }
    }

    private void ChangeTaskFilter(TaskFilter filter)
    {
        _taskSearchTimer.Stop();
        var next = filter.Normalize();
        var changedPreference = (_taskFilter with { Search = "" }) != (next with { Search = "" });
        var changedLegacyRegion = _noZone.Active != (next.Region == "any");
        _taskFilter = next;
        _noZone.Active = _taskFilter.Region == "any";
        SyncTaskFilterControls(); RefreshTaskList();
        if (changedPreference) _taskFilter.Save();
        if (changedLegacyRegion) SaveViewSettings();
    }

    private void PatchTaskFilter(string field, string value)
    {
        var pendingText = _taskSearchTimer.Enabled && field is not ("search" or "playerLevel" or "reset" or "showStory" or "storySeries")
            && !(field == "trader" && value == StoryTasks.TraderId);
        var draftSearch = _taskSearch.Text; var draftLevel = _taskLevel.Text;
        bool? Flag() => bool.TryParse(value, out var flag) ? flag : null;
        var next = field switch
        {
            "showStory" when bool.TryParse(value, out var show) => StoryFilter(show),
            "storySeries" when StoryTasks.AllSeries.Any(c => c.Id == value) => StoryFilter(true) with { StorySeries = value },
            "trader" when value == StoryTasks.TraderId => StoryFilter(true),
            "search" => _taskFilter with { Search = value },
            "region" when _taskRegion.Items.Cast<FilterOption>().Any(o => o.Value == value) => _taskFilter with { Region = value },
            "status" when int.TryParse(value, out var state) => _taskFilter with { Status = state },
            "trader" when value.Length == 0 || _taskCatalog.Any(t => t.TraderId == value) => _taskFilter with { Trader = value },
            "category" => _taskFilter with { Category = value },
            "faction" => _taskFilter with { Faction = value },
            "playerLevel" when value.Length == 0 => _taskFilter with { PlayerLevel = null },
            "playerLevel" when int.TryParse(value, out var n) && n is >= 1 and <= 100 => _taskFilter with { PlayerLevel = n },
            "kappa" => _taskFilter with { Kappa = Flag() },
            "lightkeeper" => _taskFilter with { Lightkeeper = Flag() },
            "reset" => new(), _ => _taskFilter,
        };
        ChangeTaskFilter(next);
        if (pendingText)
        {
            _syncingTaskControls = true;
            try { _taskSearch.Text = draftSearch; _taskLevel.Text = draftLevel; }
            finally { _syncingTaskControls = false; }
            _taskSearchTimer.Start();
        }
    }

    private TaskFilter StoryFilter(bool show) => show
        ? _taskFilter with { ShowStory = true, Trader = StoryTasks.TraderId, Status = -1, Category = "", Search = "",
            PlayerLevel = null, Faction = "", Kappa = null, Lightkeeper = null }
        : _taskFilter with { ShowStory = false, Trader = _taskFilter.Trader == StoryTasks.TraderId ? "" : _taskFilter.Trader,
            Status = _taskFilter.Trader == StoryTasks.TraderId ? 1 : _taskFilter.Status };

    private void UpdateStoryMapZones()
    {
        _canvas.ShowStory = _taskFilter.ShowStory;
        var basis = TarkovData.ZonesByMap.GetValueOrDefault(CurrentMap?.Data ?? "");
        var signature = (CurrentMap?.Key ?? "") + "|" + _taskFilter.ShowStory + "|" + _taskFilter.StorySeries;
        if (signature == _storyZoneSignature && ReferenceEquals(basis, _storyBaseZones) &&
            ReferenceEquals(_canvas.Zones, _combinedTaskZones)) return;
        _combinedTaskZones = (basis ?? []).Concat(_taskFilter.ShowStory
            ? StoryTasks.Zones(_taskFilter.StorySeries, CurrentMap?.Data ?? "") : []).ToList();
        _storyZoneSignature = signature; _storyBaseZones = basis;
        _canvas.Zones = _combinedTaskZones;
        _canvas.Invalidate();
    }

    private void RefreshFilteredTasks()
    {
        UpdateStoryMapZones();
        var selected = (_taskList.SelectedItem as TaskRow)?.Id;
        var top = _taskList.Items.Count > 0 ? (_taskList.Items[_taskList.TopIndex] as TaskRow)?.Id : null;
        var catalogChanged = !ReferenceEquals(_taskDataSource, TarkovData.TaskMetadata);
        if (catalogChanged)
        {
            _taskDataSource = TarkovData.TaskMetadata; _taskCatalog = TaskQuery.Build();
            FillTaskFilterOptions(); SyncTaskFilterControls();
        }
        var states = _taskCatalog.ToDictionary(t => t.Id, t => t.IsStory ? TaskState.None : _log.StateOf(t.Id));
        var rows = _taskCatalog.Where(t => TaskQuery.Matches(t, _taskFilter, CurrentMap?.Data ?? "", states[t.Id]))
            .Select(t => new TaskRow(t.Id, t.Name, "", states[t.Id], TaskQuery.Tags(t, states[t.Id],
                DoneZones.IsDone(t.Id, CurrentMap?.Data ?? "")))).ToArray();
        _refreshingTasks = true; _lastClickedRow = -1;
        _taskList.BeginUpdate();
        try
        {
            _taskList.Items.Clear(); _taskList.Items.AddRange(rows);
            _taskList.SelectedIndex = Array.FindIndex(rows, r => r.Id == selected);
            var topIndex = Array.FindIndex(rows, r => r.Id == top);
            if (topIndex >= 0) _taskList.TopIndex = topIndex;
        }
        finally { _taskList.EndUpdate(); _refreshingTasks = false; }
        var row = _taskList.SelectedItem as TaskRow;
        if (catalogChanged || _currentTaskId != (row?.Id ?? "")) ShowTaskDetail(row);
        else UpdateTaskJumpLinks(row?.Id);
        RevealSelectedTask(row); UpdateSpawnPoints(row);
        SyncMobileTasks();
        var total = _taskCatalog.Count(t => !t.IsStory || (_taskFilter.ShowStory && t.StorySeries == _taskFilter.StorySeries));
        if (_taskFilter.Trader == StoryTasks.TraderId) total = _taskCatalog.Count(t => t.StorySeries == _taskFilter.StorySeries);
        _header.Text = $"{(_taskFilter.Trader == StoryTasks.TraderId ? "主线" : "任务")} · {rows.Length} / {total}";
        var missing = _taskCatalog.Count(t => !t.IsStory && (t.Metadata.MinLevel == null || t.Metadata.Faction == null ||
            t.Metadata.Kappa == null || t.Metadata.Lightkeeper == null || !t.Metadata.RegionKnown));
        var summary = new List<string>();
        foreach (var combo in new[] { _taskTrader, _taskCategory, _taskFaction, _taskKappa, _taskLightkeeper })
            if (combo.SelectedItem is FilterOption option && option.Value.Length > 0) summary.Add(option.Label);
        if (_taskFilter.PlayerLevel is { } level) summary.Add($"角色 {level} 级（仅等级条件）");
        if (_taskFilter.Search.Length > 0) summary.Add($"搜索：{_taskFilter.Search}");
        var dates = _taskCatalog.Where(t => !t.IsStory).Select(t => t.Metadata.AsOf).Where(d => d.Length > 0).Distinct().Order().ToArray();
        var lines = new List<string>();
        if (summary.Count > 0) lines.Add(string.Join(" · ", summary));
        if (_taskFilter.ShowStory)
        {
            var series = StoryTasks.AllSeries.Single(c => c.Id == _taskFilter.StorySeries);
            lines.Add($"{series.Name}：{series.Tasks.Length} 个子任务，{series.Tasks.Count(t => t.Zones.Length > 0)} 个有坐标");
            lines.Add("仅手动显示，不读取日志进度；每次启动默认关闭");
        }
        if (_taskFilter.Trader != StoryTasks.TraderId)
        {
            if (dates.Length > 0) lines.Add("元数据快照：" + (dates.Length == 1 ? dates[0] : dates[0] + "～" + dates[^1]));
            if (missing > 0) lines.Add($"{missing} 项部分元数据未知；相关条件不匹配未知值");
        }
        _taskSummary.Text = string.Join('\n', lines);
        _taskStatesJson = JsonSerializer.Serialize(_canvas.ProjectedZones.Select(z => z.TaskId).Distinct()
            .ToDictionary(id => id, id => StoryTasks.IsTask(id) ? 0 : (int)_log.StateOf(id)));
        _taskContentVersion++;
        _taskList.Invalidate();
    }

    private IEnumerable<(string Text, Rectangle Rect)> TaskTagLayout(TaskRow row, int width, int startY)
    {
        var padding = TaskPx(12); var gap = TaskPx(4);
        var x = padding; var y = startY;
        var tagHeight = TextRenderer.MeasureText("标签", _taskSummary.Font).Height + gap;
        foreach (var tag in row.Tags ?? [])
        {
            var size = TextRenderer.MeasureText(tag, _taskSummary.Font, new Size(Math.Max(40, width - padding * 3), 0),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            var tagWidth = Math.Min(width - padding * 2, size.Width + padding);
            if (x > padding && x + tagWidth > width - padding) { x = padding; y += tagHeight + gap; }
            var height = Math.Max(tagHeight, size.Height + gap);
            yield return (tag, new Rectangle(x, y, tagWidth, height));
            x += tagWidth + gap;
            if (height > tagHeight) { x = padding; y += height + gap; }
        }
    }
    private int TaskPx(int value) => (int)Math.Round(value * DeviceDpi / 96f);
    // ClipScrollbar keeps the native scroll bar outside the visible parent.
    private int TaskViewportWidth => Math.Max(120, _taskList.Parent?.ClientSize.Width ?? _taskList.ClientSize.Width);
    private int TaskTitleHeight(TaskRow row, int width) => TextRenderer.MeasureText(row.Name, _taskList.Font,
        new Size(Math.Max(40, width - TaskPx(24)), 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
    private void MeasureTaskRow(object? sender, MeasureItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _taskList.Items.Count) return;
        var row = (TaskRow)_taskList.Items[e.Index]!;
        var width = TaskViewportWidth;
        var start = TaskPx(12) + TaskTitleHeight(row, width);
        e.ItemHeight = TaskTagLayout(row, width, start).Select(t => t.Rect.Bottom).DefaultIfEmpty(start).Max() + TaskPx(12);
    }
    private void DrawFilteredTaskRow(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _taskList.Items.Count) return;
        var row = (TaskRow)_taskList.Items[e.Index]!;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using var back = new SolidBrush(selected ? UiTokens.AccentTint : UiTokens.Sunken);
        e.Graphics.FillRectangle(back, e.Bounds);
        var width = TaskViewportWidth;
        var titleHeight = TaskTitleHeight(row, width);
        TextRenderer.DrawText(e.Graphics, row.Name, _taskList.Font,
            new Rectangle(e.Bounds.X + TaskPx(12), e.Bounds.Y + TaskPx(8), width - TaskPx(24), titleHeight), TextMain,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        foreach (var (text, rectangle) in TaskTagLayout(row, width, titleHeight + TaskPx(12)))
        {
            var r = rectangle; r.Offset(e.Bounds.Location);
            using var tagBack = new SolidBrush(UiTokens.Raised); e.Graphics.FillRectangle(tagBack, r);
            var color = text == TaskQuery.StateLabel(row.State) ? row.State switch
            { TaskState.Started => UiTokens.Accent, TaskState.Finished => UiTokens.Ok, TaskState.Failed => UiTokens.Danger, _ => TextDim } : TextDim;
            r.Inflate(-TaskPx(6), -TaskPx(2));
            TextRenderer.DrawText(e.Graphics, text, _taskSummary.Font, r, color, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }
        using var line = new Pen(UiTokens.LineSubtle);
        e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
    }

    private void UpdateTaskJumpLinks(string? id)
    {
        foreach (Control child in _taskJump.Controls.Cast<Control>().ToArray()) child.Dispose();
        _taskJump.Controls.Clear();
        foreach (var destination in TaskDestinations(id))
        {
            var button = new UiButton { Text = "前往" + destination.Name, AutoSize = true, Height = 28, Margin = new Padding(4) };
            button.Click += (_, _) => JumpToTaskMap(destination.Key, id ?? "");
            _taskJump.Controls.Add(button);
        }
        _taskJump.Visible = _taskJump.Controls.Count > 0;
    }
    private void ShowStoryTaskDetail(TaskRow row, StoryTasks.TargetInfo info)
    {
        _detail.Clear(); _currentTaskId = row.Id; _currentWiki = info.Series.Wiki; _currentSlug = "";
        _wikiButton.Visible = _currentWiki.Length > 0;
        _detailHeader.Text = $"主线 · {info.Series.Name} · {info.Target.Step:00}";
        var lines = new List<string> { "主线任务仅手动显示，不自动读取或更新进度。" };
        if (info.Target.Section.Length > 0) lines.Add(info.Target.Section);
        lines.Add(info.Target.Description);
        lines.Add(info.Target.Optional ? "可选目标" : "主线目标");
        lines.Add(info.Target.Zones.Length == 0 ? "这个目标暂时没有可靠坐标，保留说明供查阅。"
            : _canvas.ProjectedZones.Any(z => z.TaskId == row.Id) ? "当前地图已有该子任务的点位。"
            : "当前地图没有该目标的坐标；可查看其他任务目的地。未收录底图的地点暂不能定位。");
        foreach (var line in lines) AppendDetail(line, TextMain);
        _detail.SelectionStart = 0; _detail.ScrollToCaret();
        _mobileObjectives = lines; _mobileItems = [];
        _itemList.Items.Clear(); _itemList.Items.Add(new ItemRow("", "（主线所需物品请以目标说明为准）"));
        UpdateSpawnPoints(null);
    }
    private MobileTaskMap[] TaskDestinations(string? id) => _taskCatalog.FirstOrDefault(t => t.Id == id)?.Metadata.Regions
        .Where(key => key != CurrentMap?.Data).Select(key => MapPack.Maps.FirstOrDefault(m => m.Data == key))
        .Where(m => m != null).Select(m => new MobileTaskMap(m!.Key, m.DisplayName)).ToArray() ?? [];
    private void JumpToTaskMap(string key, string taskId)
    {
        if (!TaskDestinations(taskId).Any(m => m.Key == key)) return;
        SelectMap(key); SelectTaskFromMobile(taskId);
        if (_taskList.SelectedItem is TaskRow row) FocusTaskFloor(row.Id);
    }
}

/// <summary>Debouncing must not apply a half-composed Chinese IME string.</summary>
internal sealed class TaskSearchBox : TextBox
{
    public bool Composing { get; private set; }
    public event EventHandler? CompositionEnded;
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x010D) Composing = true;
        base.WndProc(ref m);
        if (m.Msg == 0x010E) { Composing = false; CompositionEnded?.Invoke(this, EventArgs.Empty); }
    }
}
