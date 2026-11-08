using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TarkovMapLite;

internal sealed class MapForm : Form
{
    private static readonly Color PanelBack = Color.FromArgb(22, 25, 29);
    private static readonly Color PanelLine = Color.FromArgb(44, 49, 56);
    private static readonly Color TextMain = Color.FromArgb(232, 238, 245);
    private static readonly Color TextDim = Color.FromArgb(150, 160, 172);

    private readonly MapCanvas _canvas = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _mapSelect = new();
    private readonly Chip _onlyActive = new();
    private readonly Chip _showExtracts = new();
    private readonly Chip _noZone = new();
    private readonly Label _status = new();
    private readonly Label _dataStatus = new();
    private readonly Label _header = new();
    private readonly ListBox _taskList = new();
    private readonly RichTextBox _detail = new();
    private readonly ListBox _itemList = new();
    private readonly Label _itemHeader = new();
    private readonly System.Windows.Forms.Timer _toastTimer = new() { Interval = 1600 };
    private readonly Label _detailHeader = new();
    private readonly Button _wikiButton = new();
    private readonly Button _layerButton = new();
    private readonly ContextMenuStrip _layerMenu = new();
    /// <summary>当前选中任务的 wiki 链接（没有就是空串）。</summary>
    private string _currentWiki = "";
    private string _currentSlug = "";
    private string _currentTaskId = "";
    private readonly TaskLog _log = new();
    private readonly PlayerLocator _player = new();
    /// <summary>最近一次进图（scene preset path）读到的地图 key，玩家定位就绑在这张图上。</summary>
    private string _raidMapKey = "";
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 3000 };

    private bool _suppressMapChange;
    private bool _replaying;
    /// <summary>点已选中的任务行：先记下来，MouseUp 里取消选中。</summary>
    private bool _pendingDeselect;
    /// <summary>上一次在任务列表里点到的行（用来识别"再点一次"）。</summary>
    private int _lastClickedRow = -1;
    /// <summary>鼠标停在"所需物品"的哪一行（-1 表示没有）。</summary>
    private int _hoverItem = -1;

    public MapForm()
    {
        Text = "Tarkov 地图";
        BackColor = Color.FromArgb(13, 15, 17);
        MinimumSize = new Size(760, 480);
        ClientSize = new Size(1120, 700);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        DoubleBuffered = true;

        BuildLayout();

        _canvas.Log = _log;
        _canvas.OnlyActive = _onlyActive.Active;
        _canvas.FloorRequested += layer => _canvas.SetLayer(layer);
        _canvas.ViewChanged += () => UpdateStatus();

        _log.Changed += () => { RefreshTaskList(); FillMapCombo(); _canvas.Invalidate(); UpdateStatus(); };
        _log.MapLoading += key => BeginInvoke(() =>
        {
            // 新一局开始：旧的位置点作废
            _raidMapKey = key;
            _player.NotBefore = DateTime.Now;
            _canvas.PlayerPosition = null;
            _canvas.PlayerForward = null;
            _canvas.PlayerMapKey = "";
            // 回放历史日志时不要跟着老会话乱跳，只跟随当前（最新）会话
            if (!_replaying)
            {
                SelectMap(key);
            }
        });

        Load += async (_, _) => await StartAsync();
        FormClosed += (_, _) => _timer.Stop();
    }

    private void BuildLayout()
    {
        var top = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = PanelBack, Padding = new Padding(10, 8, 10, 6) };
        var title = new Label
        {
            Text = "地图",
            AutoSize = true,
            ForeColor = TextDim,
            Location = new Point(10, 14),
            Font = new Font("Microsoft YaHei UI", 9f),
        };
        _mapSelect.DropDownStyle = ComboBoxStyle.DropDownList;
        _mapSelect.FlatStyle = FlatStyle.Flat;
        _mapSelect.BackColor = Color.FromArgb(34, 39, 46);
        _mapSelect.ForeColor = TextMain;
        _mapSelect.Width = 250;
        _mapSelect.Location = new Point(50, 11);
        _mapSelect.Font = new Font("Microsoft YaHei UI", 9f);
        _mapSelect.DrawMode = DrawMode.OwnerDrawFixed;
        _mapSelect.ItemHeight = 22;
        _mapSelect.DrawItem += (_, e) =>
        {
            var selected = (e.State & DrawItemState.Selected) != 0 && e.Index >= 0;
            using var back = new SolidBrush(selected ? Color.FromArgb(46, 96, 170) : Color.FromArgb(34, 39, 46));
            e.Graphics.FillRectangle(back, e.Bounds);
            if (e.Index >= 0 && e.Index < _mapSelect.Items.Count)
            {
                TextRenderer.DrawText(e.Graphics, _mapSelect.Items[e.Index]?.ToString() ?? "",
                    _mapSelect.Font,
                    new Rectangle(e.Bounds.X + 4, e.Bounds.Y + 3, e.Bounds.Width - 6, e.Bounds.Height - 4),
                    selected ? Color.White : TextMain,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        };
        _mapSelect.SelectedIndexChanged += (_, _) =>
        {
            if (!_suppressMapChange)
            {
                ApplyMap();
            }
        };

        _onlyActive.Active = true;
        _onlyActive.Text = "仅进行中";
        _onlyActive.Click += (_, _) =>
        {
            _onlyActive.Active = !_onlyActive.Active;
            _canvas.OnlyActive = _onlyActive.Active;
            RefreshTaskList();
            _canvas.Invalidate();
        };

        _showExtracts.Text = "撤离点";
        _showExtracts.Location = new Point(310, 9);
        _showExtracts.Size = new Size(62, 26);
        _showExtracts.Click += (_, _) =>
        {
            _showExtracts.Active = !_showExtracts.Active;
            _canvas.ShowExtracts = _showExtracts.Active;
            _canvas.Invalidate();
        };

        _noZone.Text = "任意地区";
        _noZone.Click += (_, _) =>
        {
            _noZone.Active = !_noZone.Active;
            // 切换分类时清掉选中：刷新列表会触发 SelectedIndexChanged，
            // 顺序很重要，否则旧任务的过滤会残留（地图上什么都不显示）
            RefreshTaskList();
            _taskList.ClearSelected();
            _canvas.SelectedTaskId = null;
            ShowTaskDetail(null);
            _canvas.Invalidate();
        };

        // 图层下拉：多选，勾哪个画哪个
        _layerButton.Text = "图层";
        _layerButton.FlatStyle = FlatStyle.Flat;
        _layerButton.BackColor = Color.FromArgb(34, 39, 46);
        _layerButton.ForeColor = TextMain;
        _layerButton.Location = new Point(380, 9);
        _layerButton.Size = new Size(64, 26);
        _layerButton.Font = new Font("Microsoft YaHei UI", 8.5f);
        _layerButton.FlatAppearance.BorderColor = PanelLine;
        _layerButton.Click += (_, _) =>
            _layerMenu.Show(_layerButton, new Point(0, _layerButton.Height));
        BuildLayerMenu();

        var refresh = new Button
        {
            Text = "刷新数据",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(34, 39, 46),
            ForeColor = TextMain,
            Location = new Point(452, 9),
            Size = new Size(84, 26),
            Font = new Font("Microsoft YaHei UI", 8.5f),
        };
        refresh.FlatAppearance.BorderColor = PanelLine;
        refresh.Click += async (_, _) =>
        {
            refresh.Enabled = false;
            SetDataStatus(null);
            await TarkovData.RefreshAsync();
            ApplyMap();
            refresh.Enabled = true;
        };

        var fit = new Button
        {
            Text = "复位",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(34, 39, 46),
            ForeColor = TextMain,
            Location = new Point(544, 9),
            Size = new Size(56, 26),
            Font = new Font("Microsoft YaHei UI", 8.5f),
        };
        fit.FlatAppearance.BorderColor = PanelLine;
        fit.Click += (_, _) => _canvas.Fit();

        top.Controls.AddRange(new Control[] { title, _mapSelect, _showExtracts, _layerButton, refresh, fit });

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 26, BackColor = PanelBack };
        _status.AutoSize = false;
        _status.Dock = DockStyle.Fill;
        _status.ForeColor = TextDim;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(10, 0, 0, 0);
        _status.Font = new Font("Microsoft YaHei UI", 8.5f);
        _dataStatus.AutoSize = false;
        _dataStatus.Dock = DockStyle.Right;
        _dataStatus.Width = 150;
        _dataStatus.TextAlign = ContentAlignment.MiddleRight;
        _dataStatus.Padding = new Padding(0, 0, 10, 0);
        _dataStatus.Font = new Font("Microsoft YaHei UI", 8.5f);
        bottom.Controls.Add(_status);
        bottom.Controls.Add(_dataStatus);

        var right = new Panel { Dock = DockStyle.Right, Width = 250, BackColor = Color.FromArgb(17, 19, 23), Padding = new Padding(0) };
        _header.Dock = DockStyle.Top;
        _header.Height = 32;
        _header.ForeColor = TextMain;
        _header.BackColor = Color.FromArgb(22, 25, 29);
        _header.TextAlign = ContentAlignment.MiddleLeft;
        _header.Padding = new Padding(10, 0, 0, 0);
        _header.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);

        _taskList.Dock = DockStyle.Fill;
        _taskList.BorderStyle = BorderStyle.None;
        _taskList.BackColor = Color.FromArgb(17, 19, 23);
        _taskList.ForeColor = TextMain;
        _taskList.DrawMode = DrawMode.OwnerDrawFixed;
        _taskList.ItemHeight = 34;
        _taskList.Font = new Font("Microsoft YaHei UI", 9f);
        _taskList.DrawItem += DrawTaskRow;
        // 单击已选中的任务 = 取消选中（恢复显示所有任务的区域）。
        // 注意 WinForms 在 MouseDown 之前就已经改好了 SelectedIndex，所以不能拿它
        // 和当前选中项比较——那样每次点击都会被当成"点的就是已选中的"，立刻取消。
        // 用"上一次点的是同一行"来判断，才符合直觉。
        _taskList.MouseDown += (_, e) =>
        {
            var index = _taskList.IndexFromPoint(e.Location);
            if (index >= 0 && index == _lastClickedRow && index == _taskList.SelectedIndex)
            {
                _pendingDeselect = true;
            }
            _lastClickedRow = index;
        };
        _taskList.MouseUp += (_, _) =>
        {
            if (!_pendingDeselect)
            {
                return;
            }
            _pendingDeselect = false;
            _lastClickedRow = -1;
            _taskList.ClearSelected();
            _canvas.SelectedTaskId = null;
            ShowTaskDetail(null);
            _canvas.Invalidate();
        };
        _taskList.SelectedIndexChanged += (_, _) =>
        {
            var row = _taskList.SelectedItem as TaskRow;
            ShowTaskDetail(row);
            if (row != null)
            {
                FocusTaskFloor(row.Id);
            }
            if (!_pendingDeselect)
            {
                RevealSelectedTask(row);
            }
        };

        _detailHeader.Dock = DockStyle.Fill;
        _detailHeader.Text = "目标";
        _detailHeader.ForeColor = TextMain;
        _detailHeader.BackColor = Color.FromArgb(22, 25, 29);
        _detailHeader.TextAlign = ContentAlignment.MiddleLeft;
        _detailHeader.Padding = new Padding(10, 0, 0, 0);
        _detailHeader.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);

        // 任务页：tarkov.dev 在国内能直连（fandom 基本打不开），所以主链接用它，
        // 右键还能选 fandom wiki（挂了代理的人用）。
        _wikiButton.Text = "任务页";
        _wikiButton.Dock = DockStyle.Right;
        _wikiButton.Width = 56;
        _wikiButton.FlatStyle = FlatStyle.Flat;
        _wikiButton.FlatAppearance.BorderSize = 0;
        _wikiButton.BackColor = Color.FromArgb(34, 39, 46);
        _wikiButton.ForeColor = Color.FromArgb(140, 190, 255);
        _wikiButton.Font = new Font("Microsoft YaHei UI", 8f);
        _wikiButton.Cursor = Cursors.Hand;
        _wikiButton.Visible = false;
        _wikiButton.Click += (_, _) => OpenTaskPage();
        _wikiButton.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
            {
                BuildTaskPageMenu().Show(_wikiButton, e.Location);
            }
        };

        var detailHeaderPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 26,
            BackColor = Color.FromArgb(22, 25, 29),
        };
        detailHeaderPanel.Controls.Add(_detailHeader);
        detailHeaderPanel.Controls.Add(_wikiButton);

        _detail.Dock = DockStyle.Fill;
        _detail.ReadOnly = true;
        _detail.BorderStyle = BorderStyle.None;
        _detail.BackColor = Color.FromArgb(17, 19, 23);
        _detail.ForeColor = Color.FromArgb(200, 208, 218);
        _detail.Font = new Font("Microsoft YaHei UI", 9f);
        _detail.ScrollBars = RichTextBoxScrollBars.None;
        _detail.TabStop = false;

        var detailPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(17, 19, 23) };
        var detailHost = ClipScrollbar(_detail);
        detailHost.Dock = DockStyle.Fill;
        detailPanel.Controls.Add(detailHost);
        detailPanel.Controls.Add(detailHeaderPanel);
        // 目标与所需物品之间：6px 空隙 + 2px 亮线。
        // Dock 是"后加的先贴边"，所以要按 空隙 → 亮线 → 物品面板 的顺序加，
        // 才会得到 目标 / 空隙 / 亮线 / 物品面板 这样从上到下的层次。
        var gap = new Panel { Dock = DockStyle.Bottom, Height = 6, BackColor = Color.FromArgb(17, 19, 23) };
        var separator = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 2,
            BackColor = Color.FromArgb(72, 82, 96),
        };
        detailPanel.Controls.Add(gap);
        detailPanel.Controls.Add(separator);
        detailPanel.Controls.Add(BuildItemList());

        var split = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.FromArgb(17, 19, 23),
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        split.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
        split.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
        var taskHost = ClipScrollbar(_taskList);
        taskHost.Dock = DockStyle.Fill;
        split.Controls.Add(taskHost, 0, 0);
        split.Controls.Add(detailPanel, 0, 1);

        // 任务筛选按钮放在任务列表正上方
        var filters = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Color.FromArgb(22, 25, 29) };
        _onlyActive.Location = new Point(10, 7);
        _onlyActive.Size = new Size(78, 26);
        _noZone.Location = new Point(96, 7);
        _noZone.Size = new Size(78, 26);
        filters.Controls.Add(_onlyActive);
        filters.Controls.Add(_noZone);

        right.Controls.Add(split);
        right.Controls.Add(_header);
        right.Controls.Add(filters);

        Controls.Add(_canvas);
        Controls.Add(right);
        Controls.Add(bottom);
        Controls.Add(top);
    }

    /// <summary>"图层"下拉：多选，勾哪个画哪个。</summary>
    private void BuildLayerMenu()
    {
        _layerMenu.Font = new Font("Microsoft YaHei UI", 9f);
        // 深色菜单：默认是白底，和整体风格不搭
        _layerMenu.BackColor = Color.FromArgb(28, 32, 38);
        _layerMenu.ForeColor = TextMain;
        _layerMenu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());

        void Add(string text, bool initial, Func<bool> get, Action<bool> set, Color color)
        {
            var item = new ToolStripMenuItem(text)
            {
                // 不用 CheckOnClick：它会连带把菜单关掉，这里自己处理
                CheckState = initial ? CheckState.Checked : CheckState.Unchecked,
                // 勾选标记跟着文字颜色走，深色背景下才看得见
                ForeColor = color,
            };
            item.Click += (_, _) =>
            {
                var on = item.CheckState != CheckState.Checked;
                item.CheckState = on ? CheckState.Checked : CheckState.Unchecked;
                set(on);
                UpdateLayerButtonText();
                _canvas.Invalidate();
            };
            _layerMenu.Items.Add(item);
        }

        // 点一项就关菜单没法连续开关：把「点了项」这种关闭原因取消掉
        _layerMenu.Closing += (_, e) =>
        {
            if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked)
            {
                e.Cancel = true;
            }
        };

        Add("散落物资", _canvas.ShowLoot, () => _canvas.ShowLoot,
            value => _canvas.ShowLoot = value, Color.FromArgb(255, 186, 88));
        Add("出生点", _canvas.ShowSpawns, () => _canvas.ShowSpawns,
            value => _canvas.ShowSpawns = value, Color.FromArgb(150, 200, 240));
        Add("危险区", _canvas.ShowHazards, () => _canvas.ShowHazards,
            value => _canvas.ShowHazards = value, Color.FromArgb(235, 120, 110));
        Add("固定武器", _canvas.ShowStatics, () => _canvas.ShowStatics,
            value => _canvas.ShowStatics = value, Color.FromArgb(180, 190, 205));
        _layerMenu.Items.Add(new ToolStripSeparator());

        // 容器带子菜单：点父项整体开/关，点子项父项跟着变半选
        var containerItem = new ToolStripMenuItem("容器")
        {
            ForeColor = Color.FromArgb(214, 176, 96),
            CheckState = _canvas.ShowContainers ? CheckState.Checked : CheckState.Unchecked,
        };
        _layerMenu.Items.Add(containerItem);

        var kinds = new List<(string Slug, ToolStripMenuItem Item)>();
        // 父项带子菜单时点父项只会展开，所以整体开关放两条独立命令
        var showAll = new ToolStripMenuItem("全部显示") { ForeColor = TextMain };
        var hideAll = new ToolStripMenuItem("全部隐藏") { ForeColor = TextMain };
        containerItem.DropDownItems.Add(showAll);
        containerItem.DropDownItems.Add(hideAll);
        containerItem.DropDownItems.Add(new ToolStripSeparator());

        void SetAll(bool on)
        {
            foreach (var (slug, sub) in kinds)
            {
                sub.CheckState = on ? CheckState.Checked : CheckState.Unchecked;
                ApplyKindState(slug, on);
            }
            RefreshContainerState(containerItem, kinds);
            // 程序改 CheckState 不会自动重画，要悬停才刷新——必须手动让下拉失效
            containerItem.DropDown.Invalidate(true);
            _canvas.Invalidate();
        }

        showAll.Click += (_, _) => SetAll(true);
        hideAll.Click += (_, _) => SetAll(false);

        foreach (var (kind, count) in MapMarkers.KindsByCount())
        {
            var info = ContainerKinds.Resolve(kind);
            var slug = kind;
            var sub = new ToolStripMenuItem($"{info.Name}（{count}）")
            {
                ForeColor = info.Color,
                CheckState = CheckState.Checked,
            };
            sub.Click += (_, _) =>
            {
                var on = sub.CheckState != CheckState.Checked;
                sub.CheckState = on ? CheckState.Checked : CheckState.Unchecked;
                ApplyKindState(slug, on);
                RefreshContainerState(containerItem, kinds);
                containerItem.DropDown.Invalidate(true);
                _canvas.Invalidate();
            };
            containerItem.DropDownItems.Add(sub);
            kinds.Add((slug, sub));
        }

        containerItem.DropDown.Closing += (_, e) =>
        {
            if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked)
            {
                e.Cancel = true;
            }
        };

        UpdateLayerButtonText();
    }

    private void ApplyKindState(string slug, bool visible)
    {
        if (visible)
        {
            _canvas.HiddenContainerKinds.Remove(slug);
        }
        else
        {
            _canvas.HiddenContainerKinds.Add(slug);
        }
    }

    /// <summary>父项按子项汇总：全开=勾选、全关=空、混合=半选。</summary>
    private void RefreshContainerState(
        ToolStripMenuItem parent, List<(string Slug, ToolStripMenuItem Item)> children)
    {
        if (children.Count == 0)
        {
            return;
        }
        var on = children.Count(c => c.Item.CheckState == CheckState.Checked);
        parent.CheckState = on == 0
            ? CheckState.Unchecked
            : on == children.Count
                ? CheckState.Checked
                : CheckState.Indeterminate;
        _canvas.ShowContainers = on > 0;
        UpdateLayerButtonText();
    }

    /// <summary>深色 ToolStrip 配色：菜单项背景和边框。</summary>
    private sealed class DarkMenuColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Color.FromArgb(46, 52, 62);
        public override Color MenuItemBorder => Color.FromArgb(70, 80, 95);
        public override Color ToolStripDropDownBackground => Color.FromArgb(28, 32, 38);
        public override Color ImageMarginGradientBegin => Color.FromArgb(28, 32, 38);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(28, 32, 38);
        public override Color ImageMarginGradientEnd => Color.FromArgb(28, 32, 38);
        public override Color MenuBorder => Color.FromArgb(58, 66, 78);
    }

    private void UpdateLayerButtonText()
    {
        var count = 0;
        foreach (ToolStripItem item in _layerMenu.Items)
        {
            if (item is ToolStripMenuItem item2 && item2.CheckState == CheckState.Checked)
            {
                count++;
            }
        }
        _layerButton.Text = count == 0 ? "图层" : $"图层 {count}";
    }

    private void DrawTaskRow(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index < 0 || e.Index >= _taskList.Items.Count)
        {
            return;
        }
        var item = (TaskRow)_taskList.Items[e.Index]!;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using var back = new SolidBrush(selected ? Color.FromArgb(40, 48, 60) : Color.FromArgb(17, 19, 23));
        e.Graphics.FillRectangle(back, e.Bounds);

        var dotColor = item.State switch
        {
            TaskState.Started => Color.FromArgb(76, 154, 255),
            TaskState.Finished => Color.FromArgb(120, 200, 140),
            TaskState.Failed => Color.FromArgb(220, 110, 110),
            _ => Color.FromArgb(110, 116, 126),
        };
        using var dot = new SolidBrush(dotColor);
        e.Graphics.FillEllipse(dot, e.Bounds.X + 10, e.Bounds.Y + 13, 8, 8);

        var title = TextRenderer.MeasureText(_taskList.Items[e.Index].ToString(), _taskList.Font);
        using var titleFont = new Font(_taskList.Font, FontStyle.Regular);
        TextRenderer.DrawText(e.Graphics, item.Name, titleFont,
            new Rectangle(e.Bounds.X + 26, e.Bounds.Y + 3, e.Bounds.Width - 32, 16), TextMain,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        using var small = new Font("Microsoft YaHei UI", 7.5f);
        TextRenderer.DrawText(e.Graphics, item.Subtitle, small,
            new Rectangle(e.Bounds.X + 26, e.Bounds.Y + 18, e.Bounds.Width - 32, 14), TextDim,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        GC.KeepAlive(title);
    }

    private sealed record TaskRow(string Id, string Name, string Subtitle, TaskState State)
    {
        public override string ToString() => Name;
    }

    private async Task StartAsync()
    {
        _canvas.SetMap(null, "");
        TarkovData.LoadCached();
        FillMapCombo();

        _replaying = true;
        try
        {
            _log.ReplayAll();
        }
        finally
        {
            _replaying = false;
        }
        // 启动时如果已经在局内，用日志里最后一次进图的地图来绑玩家位置
        _raidMapKey = _log.LastSceneMapKey;
        ApplyMap(keepView: false);

        var stale = TarkovData.FetchedAt == DateTime.MinValue
                    || TarkovData.Schema < TarkovData.CurrentSchema
                    || DateTime.Now - TarkovData.FetchedAt > TimeSpan.FromHours(24);
        if (stale)
        {
            Text = "Tarkov 地图（正在更新任务数据…）";
            SetDataStatus("获取中…");
            try
            {
                await TarkovData.RefreshAsync();
            }
            finally
            {
                // 不管成功失败都要把标题恢复，否则网络不通时会一直卡在"正在更新"
                Text = "Tarkov 地图";
                SetDataStatus(null);
                FillMapCombo();
                ApplyMap();
            }
        }

        _timer.Tick += (_, _) =>
        {
            _log.Poll();
            _player.Poll();
            UpdatePlayerMarker();
            UpdateStatus();
        };
        _player.NotBefore = DateTime.Now;
        _player.Changed += () => BeginInvoke(() =>
        {
            UpdatePlayerMarker();
            UpdateStatus();
        });
        _timer.Start();
        UpdateStatus();
    }

    /// <summary>下拉框：每张地图后面带上这张图上"进行中"的任务数。</summary>
    private void FillMapCombo()
    {
        var active = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, zones) in TarkovData.ZonesByMap)
        {
            var started = zones.Select(z => z.TaskId).Distinct().Count(id => _log.StateOf(id) == TaskState.Started);
            if (started > 0)
            {
                active[key] = started;
            }
        }
        var previous = _mapSelect.SelectedIndex;
        if (previous < 0)
        {
            previous = Math.Max(0, MapPack.Maps.ToList().FindIndex(m => m.Key == "customs"));
        }
        _suppressMapChange = true;
        _mapSelect.BeginUpdate();
        _mapSelect.Items.Clear();
        foreach (var map in MapPack.Maps)
        {
            var name = map.DisplayName.Length > 0 ? map.DisplayName : map.Key;
            // 统计按数据归属算：海关（瓦片）和海关共用同一份区域数据
            _mapSelect.Items.Add(active.TryGetValue(map.Data, out var count) ? $"{name} · {count} 个进行中任务" : name);
        }
        _mapSelect.EndUpdate();
        if (_mapSelect.Items.Count > 0)
        {
            _mapSelect.SelectedIndex = Math.Clamp(previous, 0, _mapSelect.Items.Count - 1);
        }
        _suppressMapChange = false;
    }

    private MapProjection? CurrentMap =>
        _mapSelect.SelectedIndex >= 0 && _mapSelect.SelectedIndex < MapPack.Maps.Count
            ? MapPack.Maps[_mapSelect.SelectedIndex]
            : null;

    private void SelectMap(string key)
    {
        var maps = MapPack.Maps.ToList();
        var index = maps.FindIndex(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index == _mapSelect.SelectedIndex)
        {
            return;
        }
        _suppressMapChange = true;
        _mapSelect.SelectedIndex = index;
        _suppressMapChange = false;
        ApplyMap(keepView: false);
    }

    private void ApplyMap(bool keepView = true)
    {
        var map = CurrentMap;
        if (map == null)
        {
            return;
        }
        _canvas.SetMap(map, map.DisplayName);
        _canvas.Zones = TarkovData.ZonesByMap.TryGetValue(map.Data, out var zones)
            ? zones
            : new List<ZoneShape>();
        _canvas.Extracts = TarkovData.Extracts.TryGetValue(map.Data, out var extracts)
            ? extracts.Select(m => m.Project(map)).ToList()
            : new List<MapMarker>();
        _canvas.Transits = TarkovData.Transits.TryGetValue(map.Data, out var transits)
            ? transits.Select(m => m.Project(map)).ToList()
            : new List<MapMarker>();
        _canvas.SelectedTaskId = null;
        _canvas.Invalidate();
        RefreshTaskList();
        UpdateSpawnPoints(null);
        UpdateStatus();
        GC.KeepAlive(keepView);
    }

    /// <summary>所需物品列表：点一下就把名字复制到剪贴板，方便去跳蚤市场搜索。</summary>
    private Control BuildItemList()
    {
        _itemHeader.Text = "所需物品 · 点击复制";
        _itemHeader.Dock = DockStyle.Bottom;
        _itemHeader.Height = 24;
        _itemHeader.ForeColor = TextDim;
        _itemHeader.BackColor = Color.FromArgb(22, 25, 29);
        _itemHeader.TextAlign = ContentAlignment.MiddleLeft;
        _itemHeader.Padding = new Padding(10, 0, 0, 0);
        _itemHeader.Font = new Font("Microsoft YaHei UI", 8.5f);

        _itemList.Dock = DockStyle.Bottom;
        _itemList.Height = 104;
        _itemList.BorderStyle = BorderStyle.None;
        _itemList.BackColor = Color.FromArgb(19, 22, 26);
        _itemList.ForeColor = Color.FromArgb(210, 218, 228);
        _itemList.DrawMode = DrawMode.OwnerDrawFixed;
        _itemList.ItemHeight = 30;
        _itemList.Font = new Font("Microsoft YaHei UI", 9f);
        _itemList.DrawItem += (sender, e) =>
        {
            e.DrawBackground();
            if (e.Index < 0 || e.Index >= _itemList.Items.Count)
            {
                return;
            }
            var selected = (e.State & DrawItemState.Selected) != 0;
            var hovered = e.Index == _hoverItem;
            using var back = new SolidBrush(
                selected ? Color.FromArgb(46, 96, 170)
                : hovered ? Color.FromArgb(34, 40, 48)
                : Color.FromArgb(19, 22, 26));
            e.Graphics.FillRectangle(back, e.Bounds);
            if (hovered && !selected)
            {
                // 悬停时左侧加一条竖线，明确"可以点"
                using var mark = new SolidBrush(Color.FromArgb(110, 170, 250));
                e.Graphics.FillRectangle(mark, e.Bounds.X, e.Bounds.Y + 3, 2, e.Bounds.Height - 6);
            }
            using var small = new Font("Microsoft YaHei UI", 8.5f);
            var iconSize = e.Bounds.Height - 6;
            var iconRect = new Rectangle(e.Bounds.X + 5, e.Bounds.Y + 3, iconSize, iconSize);
            var id = (_itemList.Items[e.Index] as ItemRow)?.Id ?? "";
            var icon = string.IsNullOrEmpty(id) ? null : ItemImages.Get(id);
            if (icon != null)
            {
                e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                e.Graphics.DrawImage(icon, iconRect);
            }
            else
            {
                // 还没下载好：画个占位方块，避免文字跳来跳去
                using var slot = new SolidBrush(Color.FromArgb(30, 34, 41));
                e.Graphics.FillRectangle(slot, iconRect);
            }
            TextRenderer.DrawText(e.Graphics, _itemList.Items[e.Index]?.ToString() ?? "", small,
                new Rectangle(iconRect.Right + 6, e.Bounds.Y + 6, e.Bounds.Width - iconSize - 16, 18),
                selected || hovered ? Color.White : Color.FromArgb(210, 218, 228),
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
        _itemList.MouseMove += (_, e) =>
        {
            var index = _itemList.IndexFromPoint(e.Location);
            if (index == _hoverItem)
            {
                return;
            }
            _hoverItem = index;
            _itemList.Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
            _itemList.Invalidate();
        };
        _itemList.MouseLeave += (_, _) =>
        {
            if (_hoverItem < 0)
            {
                return;
            }
            _hoverItem = -1;
            _itemList.Cursor = Cursors.Default;
            _itemList.Invalidate();
        };
        _itemList.Click += (_, _) => CopySelectedItem();
        _itemList.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter || (e.Control && e.KeyCode == Keys.C))
            {
                CopySelectedItem();
                e.Handled = true;
            }
        };

        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            UpdateStatus();
        };

        var panel = new Panel { Dock = DockStyle.Bottom, Height = 128, BackColor = Color.FromArgb(19, 22, 26) };
        var itemHost = ClipScrollbar(_itemList);
        itemHost.Dock = DockStyle.Bottom;
        itemHost.Height = 104;
        panel.Controls.Add(itemHost);
        panel.Controls.Add(_itemHeader);
        return panel;
    }

    /// <summary>把选中物品名复制到剪贴板；剪贴板偶尔被别的程序占用，重试两次。</summary>
    private void CopySelectedItem()
    {
        var text = _itemList.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(text) || text.StartsWith("（", StringComparison.Ordinal))
        {
            return;
        }
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                _status.Text = $"已复制：{text}";
                _toastTimer.Stop();
                _toastTimer.Start();
                return;
            }
            catch (ExternalException)
            {
                Thread.Sleep(40);
            }
        }
        _status.Text = "复制失败：剪贴板被占用";
    }

    /// <summary>目标面板按行上色：描述白、物品琥珀、钥匙蓝。</summary>
    private void AppendDetail(string text, Color color)
    {
        _detail.SelectionStart = _detail.TextLength;
        _detail.SelectionLength = 0;
        _detail.SelectionColor = color;
        _detail.AppendText(text + Environment.NewLine);
        _detail.SelectionColor = _detail.ForeColor;
    }

    /// <summary>
    /// 用默认浏览器打开任务页。默认用 tarkov.dev（国内能直连），
    /// 右键菜单里还有 fandom wiki（要代理）和自定义模板。
    /// </summary>
    private void OpenTaskPage()
    {
        var url = string.IsNullOrEmpty(_currentSlug)
            ? _currentWiki
            : $"https://tarkov.dev/task/{_currentSlug}";
        OpenUrl(url);
    }

    private void OpenUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _status.Text = "打开网页失败：" + ex.Message;
        }
    }

    /// <summary>右键菜单：让用户自己挑用哪个站点（fandom 需要代理）。</summary>
    private ContextMenuStrip BuildTaskPageMenu()
    {
        var menu = new ContextMenuStrip { Font = new Font("Microsoft YaHei UI", 9f) };
        if (!string.IsNullOrEmpty(_currentSlug))
        {
            menu.Items.Add("tarkov.dev 任务页", null,
                (_, _) => OpenUrl($"https://tarkov.dev/task/{_currentSlug}"));
        }
        // 环境变量可以覆盖：TARKOVMAPLITE_TASK_URL=https://example.com/task/{slug}
        var custom = Environment.GetEnvironmentVariable("TARKOVMAPLITE_TASK_URL");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            var url = custom.Replace("{slug}", _currentSlug).Replace("{id}", _currentTaskId);
            menu.Items.Add("自定义站点", null, (_, _) => OpenUrl(url));
        }
        if (!string.IsNullOrEmpty(_currentWiki))
        {
            menu.Items.Add("Fandom Wiki（需代理）", null, (_, _) => OpenUrl(_currentWiki));
        }
        return menu;
    }

    private void ShowTaskDetail(TaskRow? row)
    {
        if (row == null)
        {
            _detail.Clear();
            _detailHeader.Text = "目标";
            _itemList.Items.Clear();
            _currentWiki = "";
            _currentSlug = "";
            _currentTaskId = "";
            _wikiButton.Visible = false;
            return;
        }
        _detailHeader.Text = "目标 · " + row.Name;
        _currentTaskId = row.Id;
        _currentWiki = TarkovData.TaskWiki.TryGetValue(row.Id, out var taskWiki) ? taskWiki : "";
        _currentSlug = TarkovData.TaskSlug.TryGetValue(row.Id, out var taskSlug) ? taskSlug : "";
        _wikiButton.Visible = _currentSlug.Length > 0 || _currentWiki.Length > 0;
        var needed = new List<(string Id, string Name)>();
        if (TarkovData.TaskObjectives.TryGetValue(row.Id, out var objectives))
        {
            foreach (var objective in objectives)
            {
                var count = objective.Count is > 0 ? $" ×{objective.Count}" : "";
                AppendDetail("• " + objective.Description + count, Color.FromArgb(226, 232, 240));
                if (objective.Items.Count > 0)
                {
                    var names = objective.Items.Select(ItemName).ToList();
                    foreach (var id in objective.Items)
                    {
                        needed.Add((id, ItemName(id)));
                    }
                    AppendDetail("     物品  " + string.Join("、", names), Color.FromArgb(240, 190, 110));
                }
                if (objective.Keys.Count > 0)
                {
                    var groups = objective.Keys
                        .Select(group => string.Join(" + ", group.Select(ItemName)))
                        .Distinct()
                        .ToList();
                    foreach (var group in objective.Keys)
                    {
                        foreach (var id in group)
                        {
                            needed.Add((id, ItemName(id)));
                        }
                    }
                    AppendDetail("     钥匙  " + string.Join(" 或 ", groups), Color.FromArgb(140, 190, 255));
                }
            }
        }
        else
        {
            AppendDetail("（这个任务没有可显示的目标文本）", TextDim);
        }
        _detail.SelectionStart = 0;
        _detail.ScrollToCaret();

        _itemList.BeginUpdate();
        _itemList.Items.Clear();
        var distinct = needed
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ItemRow(g.Key, g.First().Name))
            .OrderBy(item => item.Name, StringComparer.CurrentCulture)
            .ToList();
        if (distinct.Count == 0)
        {
            _itemList.Items.Add(new ItemRow("", "（这个任务不需要物品）"));
        }
        else
        {
            foreach (var item in distinct)
            {
                _itemList.Items.Add(item);
            }
            // 顺手把缩略图拉下来，好了以后重画这一列
            foreach (var item in distinct)
            {
                var id = item.Id;
                ItemImages.Request(id, () =>
                {
                    if (IsDisposed || !IsHandleCreated)
                    {
                        return;
                    }
                    try
                    {
                        BeginInvoke(() => _itemList.Invalidate());
                    }
                    catch (InvalidOperationException)
                    {
                        // 窗口正在关闭
                    }
                });
            }
        }
        _itemList.EndUpdate();

        UpdateSpawnPoints(row);
    }

    /// <summary>"所需物品"的一行：物品 id + 显示名，列表里带缩略图。</summary>
    private sealed record ItemRow(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    /// <summary>
    /// 把最新截图的位置换算到地图上。位置绑在"截图所属的那一局地图"上：
    /// 如果当前展示的是别的地图（玩家手动切过去了），画布会自己跳过这个点。
    /// </summary>
    private void UpdatePlayerMarker()
    {
        var fix = _player.Current;
        var map = string.IsNullOrEmpty(_raidMapKey) ? null : MapPack.Find(_raidMapKey);
        if (fix == null || map == null)
        {
            _canvas.PlayerPosition = null;
            _canvas.PlayerForward = null;
            _canvas.PlayerMapKey = "";
            return;
        }
        var (x, y) = MapPack.ToImage(map, fix.X, fix.Z);
        var (fx, fy) = MapPack.ToImage(map, fix.X + fix.ForwardX * 3, fix.Z + fix.ForwardZ * 3);
        _canvas.PlayerPosition = new PointF((float)x, (float)y);
        _canvas.PlayerForward = new PointF((float)(fx - x), (float)(fy - y));
        _canvas.PlayerMapKey = map.Key;
        _canvas.Invalidate();
    }

    /// <summary>把选中任务所需物品在本图的候选刷新点交给画布。</summary>
    private void UpdateSpawnPoints(TaskRow? row)
    {
        var map = CurrentMap;
        if (row == null || map == null
            || !TarkovData.TaskObjectives.TryGetValue(row.Id, out var objectives))
        {
            _canvas.SpawnPoints = new List<PointF>();
            _itemHeader.Text = "所需物品 · 点击复制";
            return;
        }
        var ids = objectives
            .SelectMany(o => o.Items.Concat(o.Keys.SelectMany(group => group)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var points = new List<PointF>();
        // 生成点存的是世界坐标，这里按当前地图投影（瓦片图和 SVG 图投影不同）
        if (TarkovData.SpawnPoints.TryGetValue(map.Data, out var perItem))
        {
            foreach (var id in ids)
            {
                if (!perItem.TryGetValue(id, out var list))
                {
                    continue;
                }
                foreach (var world in list)
                {
                    var (u, v) = MapPack.ToImage(map, world.X, world.Y);
                    points.Add(new PointF((float)u, (float)v));
                }
            }
        }
        _canvas.SpawnPoints = points;
        // 去重：同一坐标被多个物品引用时只画一个点
        var unique = points
            .Select(p => (p.X, p.Y))
            .Distinct()
            .Count();
        _itemHeader.Text = unique > 0
            ? $"所需物品 · 点击复制 · 本图候选刷新点 {unique}"
            : "所需物品 · 点击复制";
    }

    /// <summary>选中任务时自动切到它所在的楼层（只在该层存在楼层图时）。</summary>
    private void FocusTaskFloor(string taskId)
    {
        var map = CurrentMap;
        if (map == null || !TarkovData.ZonesByMap.TryGetValue(map.Data, out var zones))
        {
            return;
        }
        var zone = zones.FirstOrDefault(z => string.Equals(z.TaskId, taskId, StringComparison.OrdinalIgnoreCase));
        if (zone == null)
        {
            return;
        }
        if (_canvas.ShowFloor(zone.Floor))
        {
            _canvas.Invalidate();
        }
    }

    /// <summary>物品 id → 中文名；没有就显示 id 前 8 位，至少不会是一片空白。</summary>
    private static string ItemName(string id) =>
        TarkovData.ItemNames.TryGetValue(id, out var name)
            ? name
            : id.Length > 8 ? id[..8] : id;

    /// <summary>选中任务后只显示它的区域；取消选中恢复显示全部。</summary>
    private void RevealSelectedTask(TaskRow? row)
    {
        _canvas.SelectedTaskId = row?.Id;
        _canvas.Invalidate();
    }

    private void RefreshTaskList()
    {
        var map = CurrentMap;
        _lastClickedRow = -1;
        _taskList.BeginUpdate();
        _taskList.Items.Clear();
        if (_noZone.Active)
        {
            // 任意地区：有目标、但任何地图都没有区域的任务
            var withZones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var list in TarkovData.ZonesByMap.Values)
            {
                foreach (var zone in list)
                {
                    withZones.Add(zone.TaskId);
                }
            }
            foreach (var (taskId, objectives) in TarkovData.TaskObjectives)
            {
                if (withZones.Contains(taskId))
                {
                    continue;
                }
                var state = _log.StateOf(taskId);
                if (_onlyActive.Active && state != TaskState.Started)
                {
                    continue;
                }
                var name = TarkovData.TaskNames.TryGetValue(taskId, out var known) ? known : taskId;
                var subtitle = state switch
                {
                    TaskState.Started => $"进行中 · 任意地区 · {objectives.Count} 个目标",
                    TaskState.Finished => "已完成",
                    TaskState.Failed => "已失败",
                    _ => $"未开始 · 任意地区 · {objectives.Count} 个目标",
                };
                _taskList.Items.Add(new TaskRow(taskId, name, subtitle, state));
            }
            _taskList.EndUpdate();
            _header.Text = $"任意地区任务 · {_taskList.Items.Count}";
            return;
        }
        if (map != null && TarkovData.ZonesByMap.TryGetValue(map.Data, out var zones))
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var zone in zones)
            {
                if (!seen.Add(zone.TaskId))
                {
                    continue;
                }
                var state = _log.StateOf(zone.TaskId);
                if (_onlyActive.Active && state != TaskState.Started)
                {
                    continue;
                }
                var subtitle = state switch
                {
                    TaskState.Started => "进行中 · " + zone.Type,
                    TaskState.Finished => "已完成",
                    TaskState.Failed => "已失败",
                    _ => "未开始 · " + zone.Type,
                };
                _taskList.Items.Add(new TaskRow(zone.TaskId, zone.TaskName, subtitle, state));
            }
        }
        _taskList.EndUpdate();
        _header.Text = $"任务 · {_taskList.Items.Count}";
    }

    private void UpdateStatus()
    {
        var map = CurrentMap;
        var zones = map != null && TarkovData.ZonesByMap.TryGetValue(map.Data, out var list) ? list : null;
        var activeOnMap = zones == null
            ? 0
            : zones.Select(z => z.TaskId).Distinct()
                   .Count(id => _log.StateOf(id) == TaskState.Started);
        var playerNote = _player.Current == null
            ? _player.Status
            : string.Equals(_canvas.PlayerMapKey, map?.Key, StringComparison.OrdinalIgnoreCase)
                ? "玩家位置：已标注"
                : $"玩家位置属于 {MapPack.Find(_canvas.PlayerMapKey)?.DisplayName ?? "?"}，切回该图可见";
        var parts = new List<string>
        {
            $"本图进行中 {activeOnMap}",
            $"{map?.DisplayName ?? "-"}：{zones?.Count ?? 0} 个区域",
            TarkovData.Status,
            string.IsNullOrEmpty(_log.LogsFolder) ? "日志：未找到" : "日志：" + _log.LogsFolder,
            playerNote,
        };
        _status.Text = string.Join("　│　", parts);
        SetDataStatus(null);
    }

    /// <summary>底部右侧的"数据来源"指示：在线 / 内置 / 离线。</summary>
    private void SetDataStatus(string? overrideText)
    {
        if (overrideText != null)
        {
            _dataStatus.Text = overrideText;
            _dataStatus.ForeColor = Color.FromArgb(230, 190, 120);
            return;
        }
        var (text, color) = TarkovData.FromSeed
            ? ($"内置数据 · {TarkovData.FetchedAt:MM-dd}", Color.FromArgb(230, 190, 120))
            : TarkovData.Online
                ? ($"在线 · {TarkovData.FetchedAt:HH:mm}", Color.FromArgb(120, 200, 140))
                : TarkovData.FetchedAt != DateTime.MinValue
                    ? ($"缓存 · {TarkovData.FetchedAt:MM-dd HH:mm}", Color.FromArgb(150, 200, 175))
                    : ("无数据", Color.FromArgb(220, 130, 130));
        _dataStatus.Text = text;
        _dataStatus.ForeColor = color;
    }

    /// <summary>
    /// 把控件右边 18px（原生滚动条的位置）裁到容器外面。
    /// ListBox/RichTextBox 会自己把 WS_VSCROLL 加回来，光改窗口样式压不住。
    /// </summary>
    private static Panel ClipScrollbar(Control inner, int offset = 18)
    {
        var host = new Panel { BackColor = inner.BackColor, Padding = Padding.Empty };
        inner.Dock = DockStyle.None;
        inner.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom;
        host.Controls.Add(inner);
        void Relayout()
        {
            inner.Bounds = new Rectangle(0, 0, host.ClientSize.Width + offset, host.ClientSize.Height);
        }
        host.Resize += (_, _) => Relayout();
        host.HandleCreated += (_, _) => Relayout();
        Relayout();
        return host;
    }

    /// <summary>扁平胶囊按钮：选中时高亮，整块都能点。</summary>
    private sealed class Chip : Button
    {
        private bool _active;

        public Chip()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = PanelBack;
            ForeColor = TextMain;
            Cursor = Cursors.Hand;
            Font = new Font("Microsoft YaHei UI", 8.5f);
            TabStop = false;
        }

        public bool Active
        {
            get => _active;
            set
            {
                if (_active == value)
                {
                    return;
                }
                _active = value;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = Rounded(rect, 7);
            using (var fill = new SolidBrush(Active ? Color.FromArgb(52, 112, 200) : Color.FromArgb(30, 34, 41)))
            {
                g.FillPath(fill, path);
            }
            using (var border = new Pen(Active ? Color.FromArgb(96, 160, 255) : Color.FromArgb(50, 56, 65)))
            {
                g.DrawPath(border, path);
            }
            TextRenderer.DrawText(g, Text, Font, rect,
                Active ? Color.White : Color.FromArgb(190, 198, 208),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        private static System.Drawing.Drawing2D.GraphicsPath Rounded(Rectangle r, int radius)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            var d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }


    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.R)
        {
            _canvas.Fit();
        }
        else if (e.KeyCode is Keys.Oemplus or Keys.Add)
        {
            _canvas.ZoomBy(1.25);
        }
        else if (e.KeyCode is Keys.OemMinus or Keys.Subtract)
        {
            _canvas.ZoomBy(1 / 1.25);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
        }
        base.Dispose(disposing);
    }
}
