// ============================================================================
// MapForm.cs — 主窗口：顶栏 + 画布 + 右侧任务栏 + 状态栏（+ 手机端服务的宿主）
//
// 界面结构（BuildLayout）：
//   顶栏：地图下拉 / 撤离点 / 图层 / 刷新数据 / 复位 / 瓦片版 / 手机端
//   右侧：筛选（仅进行中、任意地区）+ 任务列表 + 目标 + 所需物品
//   底部：状态栏（本图进行中、区域数、数据来源、玩家坐标）
//
// 事件流（记住这条就不容易改错）：
//   TaskLog（游戏日志）  → Changed/MapLoading → 自动切图 + RefreshTaskList
//   PlayerLocator（截图）→ Changed           → UpdatePlayerMarker → 画布
//   两个 Timer：_timer 3s（读日志/读截图）、_mobileTimer 250ms（推手机端状态）
//
// 改动须知：
//  · 任务选中态的唯一真相是画布的 SelectedTaskId；改选中一般走
//    _taskList.SelectedIndex（会顺带刷新目标、自动切楼层）。手机端也走这条路。
//  · ShowTaskDetail 必须从 _detail.Clear() 开始：AppendDetail 只追加，
//    不清空的话新任务的目标会接在上一个后面（踩过）。
//  · 顶栏按钮是绝对坐标，加按钮要自己挪位置；窄窗口会被裁掉。
//  · 手机端：PumpMobile() 每 250ms 把画布状态塞进 MobileState 并取出手机的动作；
//    服务没启动时它直接返回，不产生任何开销。
// ============================================================================
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TarkovMapLite;

internal sealed class MapForm : Form
{
    private static readonly Color PanelBack = UiTokens.PanelBack;
    private static readonly Color PanelLine = UiTokens.Line;
    private static readonly Color TextMain = UiTokens.TextMain;
    private static readonly Color TextDim = UiTokens.TextDim;

    private readonly MapCanvas _canvas = new() { Dock = DockStyle.Fill };
    /// <summary>只当状态用（隐藏）：索引对应 MapPack.Maps，界面由 _mapButton 呈现。</summary>
    private readonly ComboBox _mapSelect = new();
    private readonly Chip _onlyActive = new();
    private readonly Chip _showExtracts = new();
    /// <summary>「瓦片版 / SVG 版」切换：只在同一张图有两种画法时出现。</summary>
    private readonly Chip _variant = new();
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
    /// <summary>手机端：顶栏那一颗按钮（文字随连接状态变）。</summary>
    private readonly UiButton _mobileButton = new();
    private readonly UiButton _layerButton = new();
    private readonly UiButton _mapButton = new();
    /// <summary>底部状态栏最右边的「截图目录」按钮：手动指定游戏截图放在哪。</summary>
    private readonly UiButton _shotsButton = new();
    /// <summary>顶栏「钥匙」：列出这张图的钥匙，点一把就跳到对应的锁。</summary>
    private readonly UiButton _keyButton = new();
    /// <summary>底部「测试截图」按钮：拿目录里最新的一张截图试投影。</summary>
    private readonly UiButton _testButton = new();
    /// <summary>测试截图给出的坐标（真实定位没出来时优先用它画标记）。</summary>
    private (double X, double Y, double Z, double FX, double FZ)? _testFix;
    /// <summary>局域网 HTTP 服务 + 手机要的状态快照。</summary>
    private readonly LocalServer _mobile = new();
    /// <summary>250ms 一次：把画布状态推给手机、把手机的动作取回来。</summary>
    private readonly System.Windows.Forms.Timer _mobileTimer = new() { Interval = 250 };
    private MobileForm? _mobilePanel;
    private IReadOnlyList<MobileTask> _mobileTasks = Array.Empty<MobileTask>();
    private IReadOnlyList<string> _mobileObjectives = Array.Empty<string>();
    private IReadOnlyList<MobileItem> _mobileItems = Array.Empty<MobileItem>();
    private readonly ContextMenuStrip _layerMenu = new();
    private readonly ContextMenuStrip _mapMenu = new();
    private bool _layerUpdateQueued;
    /// <summary>当前选中任务的 wiki 链接（没有就是空串）。</summary>
    private string _currentWiki = "";
    private string _currentSlug = "";
    private string _currentTaskId = "";
    private readonly TaskLog _log = new();
    private readonly PlayerLocator _player = new();
    /// <summary>最近一次进图（scene preset path）读到的地图 key，玩家定位就绑在这张图上。</summary>
    private string _raidMapKey = "";
    /// <summary>本局开始的时刻：对局结束清理截图时用它界定"本局期间拍的"。</summary>
    private DateTime _raidStartedAt = DateTime.MinValue;
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
        BackColor = UiTokens.Back;
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
        // 点画布上的门锁 = 复制钥匙名（悬浮时画布上会画出「钥匙：xxx」提示）
        _canvas.CopyRequested += key =>
        {
            try
            {
                Clipboard.SetText(key);
                _status.Text = $"已复制钥匙：{key}";
            }
            catch (ExternalException)
            {
                _status.Text = "剪贴板被占用，复制失败";
            }
        };
        // 面板上「停止服务」后按钮文字要跟着回去（那时候 PumpMobile 已经不跑了）
        _mobile.StateChanged += () =>
        {
            if (IsHandleCreated && !IsDisposed)
            {
                UpdateMobileButton();
            }
        };

        _log.Changed += () => { RefreshTaskList(); FillMapCombo(); _canvas.Invalidate(); UpdateStatus(); };
        _log.MapLoading += key => BeginInvoke(() =>
        {
            // 进新图 = 上一局已经结束，先把上一局期间拍的截图清掉（开关默认关）
            CleanupRaidScreenshots(_raidStartedAt, DateTime.Now);
            _raidStartedAt = DateTime.Now;
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
        FormClosed += (_, _) => SaveWindowBounds();
        FormClosed += (_, _) =>
        {
            _timer.Stop();
            _mobileTimer.Stop();
            _mobile.Dispose();
        };
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
        // ---- 地图下拉 --------------------------------------------------------
        // ComboBox 自带一条白边框、也画不出圆角（FlatStyle 去不掉），所以把它
        // 藏起来只当"状态"用，界面上是自绘的 _mapButton + 菜单。
        _mapSelect.Visible = false;
        _mapSelect.Width = 250;
        _mapSelect.Location = new Point(50, 11);
        _mapSelect.Font = new Font("Microsoft YaHei UI", 9f);
        _mapSelect.SelectedIndexChanged += (_, _) =>
        {
            SyncMapButtonText();
            if (!_suppressMapChange)
            {
                ApplyMap();
            }
        };

        _mapButton.Text = "加载中…";
        _mapButton.ShowArrow = true;
        _mapButton.Location = new Point(50, 8);
        _mapButton.Size = new Size(252, 30);
        _mapButton.Font = new Font("Microsoft YaHei UI", 9f);
        _mapMenu.Font = _mapSelect.Font;
        _mapMenu.BackColor = UiTokens.Raised;
        _mapMenu.ForeColor = TextMain;
        _mapMenu.Renderer = new LayerMenuRenderer();
        _mapMenu.ShowImageMargin = false;
        _mapMenu.ShowCheckMargin = false;
        _ = new MenuButtonBinding(_mapButton, _mapMenu, RefreshMapMenu);

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
        _showExtracts.Location = new Point(312, 10);
        _showExtracts.Size = new Size(64, 26);
        // 默认打开：开图第一眼要能看见撤离点
        _showExtracts.Active = true;
        _canvas.ShowExtracts = true;
        _showExtracts.Click += (_, _) =>
        {
            _showExtracts.Active = !_showExtracts.Active;
            _canvas.ShowExtracts = _showExtracts.Active;
            _canvas.Invalidate();
        };

        // 同一张图的另一种画法：默认隐藏，ApplyMap 里按当前地图决定要不要显示
        _variant.Text = "瓦片版";
        _variant.Location = new Point(618, 10);
        _variant.Size = new Size(72, 26);
        _variant.Visible = false;
        _variant.Click += (_, _) =>
        {
            if (_variant.Tag is string key)
            {
                var floor = _canvas.CurrentFloor;
                SelectMap(key, preferDefault: false);
                _canvas.ShowFloor(floor);
            }
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
        _layerButton.Location = new Point(382, 8);
        _layerButton.Size = new Size(66, 30);
        _ = new MenuButtonBinding(_layerButton, _layerMenu, () => { });
        BuildLayerMenu();

        var refresh = new UiButton
        {
            Text = "刷新数据",
            Location = new Point(456, 8),
            Size = new Size(86, 30),
        };
        refresh.Click += async (_, _) =>
        {
            refresh.Enabled = false;
            SetDataStatus(null);
            await TarkovData.RefreshAsync();
            ApplyMap();
            refresh.Enabled = true;
        };

        var fit = new UiButton
        {
            Text = "复位",
            Location = new Point(550, 8),
            Size = new Size(58, 30),
        };
        fit.Click += (_, _) => _canvas.Fit();

        // 手机端：点开面板，里面有地址和二维码
        _mobileButton.Text = "手机端";
        _mobileButton.Location = new Point(698, 8);
        _mobileButton.Size = new Size(66, 30);
        _mobileButton.Click += (_, _) => ShowMobilePanel();

        top.Controls.AddRange(new Control[]
        {
            title, _mapButton, _showExtracts, _keyButton, _layerButton, refresh, fit, _variant, _mobileButton,
        });

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

        // 底部最右：「截图目录」——游戏截图不一定在默认位置，这里手动指定
        _shotsButton.Text = "截图目录";
        _shotsButton.Dock = DockStyle.Right;
        _shotsButton.Width = 86;
        _keyButton.Text = "钥匙";
        _keyButton.Location = new Point(784, 10);
        _keyButton.Size = new Size(60, 26);
        _keyButton.Font = new Font("Microsoft YaHei UI", 8.5f);
        _keyButton.Click += (_, _) => OpenKeyList();

        _shotsButton.Font = new Font("Microsoft YaHei UI", 8.5f);
        _shotsButton.Click += (_, _) => PickScreenshotFolder();
        // 右键：清理开关（默认关）和打开目录
        _shotsButton.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
            {
                ShowScreenshotsMenu();
            }
        };

        // 测试截图：立刻用最新一张截图验证目录和投影，不用等进游戏再来一张
        _testButton.Text = "测试截图";
        _testButton.Dock = DockStyle.Right;
        _testButton.Width = 86;
        _testButton.Font = new Font("Microsoft YaHei UI", 8.5f);
        _testButton.Click += (_, _) => TestNewestScreenshot();
        bottom.Controls.Add(_status);
        bottom.Controls.Add(_dataStatus);
        bottom.Controls.Add(_shotsButton);
        bottom.Controls.Add(_testButton);

        var right = new Panel { Dock = DockStyle.Right, Width = 250, BackColor = UiTokens.Sunken, Padding = new Padding(0) };
        _header.Dock = DockStyle.Top;
        _header.Height = 32;
        _header.ForeColor = TextMain;
        _header.BackColor = UiTokens.PanelBack;
        _header.TextAlign = ContentAlignment.MiddleLeft;
        _header.Padding = new Padding(10, 0, 0, 0);
        _header.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);

        _taskList.Dock = DockStyle.Fill;
        _taskList.BorderStyle = BorderStyle.None;
        _taskList.BackColor = UiTokens.Sunken;
        _taskList.ForeColor = TextMain;
        _taskList.DrawMode = DrawMode.OwnerDrawFixed;
        _taskList.ItemHeight = 34;
        _taskList.Font = new Font("Microsoft YaHei UI", 9f);
        _taskList.DrawItem += DrawTaskRow;
        // 列表为空时画一段说明 + 两个可点出口（规范五态里的"空态要给出口"）
        _taskList.Paint += PaintEmptyTaskHint;
        _taskList.MouseClick += ClickEmptyTaskHint;
        // 单击已选中的任务 = 取消选中（恢复显示所有任务的区域）。
        // 注意 WinForms 在 MouseDown 之前就已经改好了 SelectedIndex，所以不能拿它
        // 和当前选中项比较——那样每次点击都会被当成"点的就是已选中的"，立刻取消。
        // 用"上一次点的是同一行"来判断，才符合直觉。
        _taskList.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
            {
                OnTaskListRightClick(e);
                return;
            }
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
        _detailHeader.BackColor = UiTokens.PanelBack;
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
        _wikiButton.BackColor = UiTokens.ButtonBack;
        _wikiButton.ForeColor = UiTokens.Accent;
        _wikiButton.Font = new Font("Microsoft YaHei UI", 8f);
        _wikiButton.Cursor = Cursors.Hand;
        _wikiButton.Visible = false;
        // 点击就列出所有可用的任务资料站，选一个再跳（不再默认直开 tarkov.dev）
        _wikiButton.Click += (_, _) => ShowTaskPageMenu();
        

        var detailHeaderPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 26,
            BackColor = UiTokens.PanelBack,
        };
        detailHeaderPanel.Controls.Add(_detailHeader);
        detailHeaderPanel.Controls.Add(_wikiButton);

        _detail.Dock = DockStyle.Fill;
        _detail.ReadOnly = true;
        _detail.BorderStyle = BorderStyle.None;
        _detail.BackColor = UiTokens.Sunken;
        _detail.ForeColor = Color.FromArgb(200, 208, 218);
        _detail.Font = new Font("Microsoft YaHei UI", 9f);
        _detail.ScrollBars = RichTextBoxScrollBars.None;
        _detail.TabStop = false;

        var detailPanel = new Panel { Dock = DockStyle.Fill, BackColor = UiTokens.Sunken };
        var detailHost = ClipScrollbar(_detail);
        detailHost.Dock = DockStyle.Fill;
        detailPanel.Controls.Add(detailHost);
        detailPanel.Controls.Add(detailHeaderPanel);
        // 目标与所需物品之间：6px 空隙 + 2px 亮线。
        // Dock 是"后加的先贴边"，所以要按 空隙 → 亮线 → 物品面板 的顺序加，
        // 才会得到 目标 / 空隙 / 亮线 / 物品面板 这样从上到下的层次。
        var gap = new Panel { Dock = DockStyle.Bottom, Height = 6, BackColor = UiTokens.Sunken };
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
            BackColor = UiTokens.Sunken,
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
        var filters = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = UiTokens.PanelBack };
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

    private void BuildLayerMenu()
    {
        _layerMenu.Font = new Font("Microsoft YaHei UI", 9f);
        _layerMenu.BackColor = UiTokens.Raised;
        _layerMenu.ForeColor = TextMain;

        // 自定义渲染（见 LayerMenuRenderer 顶部三条规则）：
        //   悬浮只描边不铺底色、文字颜色固定、勾选另用一套底色 + 强调色文字
        var renderer = new LayerMenuRenderer();
        _layerMenu.Renderer = renderer;

        // 统一造项：分类颜色只画在 16×16 图标上，文字颜色交给渲染器决定
        ToolStripMenuItem Create(string text, LayerIcon kind, Color color, CheckState check)
        {
            var item = new ToolStripMenuItem(text) { CheckState = check };
            item.Image = LayerMenuArt.Create(kind, color);
            item.ImageScaling = ToolStripItemImageScaling.None;
            item.ImageAlign = ContentAlignment.MiddleLeft;
            // 左内边距留 0：图标由系统的图片边距列负责，加了反而挤掉图标
            item.Padding = new Padding(0, 4, 10, 4);
            item.Margin = Padding.Empty;
            item.CheckStateChanged += (_, _) =>
            {
                // 渲染器直接读取 CheckState；只刷新变化的行，批量操作合并为一次画布更新。
                item.Invalidate();
                QueueLayerUpdate();
            };
            return item;
        }

        void AddToggle(string text, LayerIcon kind, Color color, bool initial, Action<bool> set)
        {
            var item = Create(text, kind, color, initial ? CheckState.Checked : CheckState.Unchecked);
            item.Click += (_, _) =>
            {
                var on = item.CheckState != CheckState.Checked;
                item.CheckState = on ? CheckState.Checked : CheckState.Unchecked;
                set(on);
                QueueLayerUpdate();
            };
            _layerMenu.Items.Add(item);
        }

        void AddCommand(string text, LayerIcon kind, Color color, Action run)
        {
            var item = Create(text, kind, color, CheckState.Unchecked);
            item.Click += (_, _) => run();
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

        AddToggle("地名", LayerIcon.Label, Color.FromArgb(236, 228, 198), _canvas.ShowLabels,
            value => _canvas.ShowLabels = value);
        AddToggle("BTR 站点", LayerIcon.Btr, Color.FromArgb(120, 210, 218), _canvas.ShowBtr,
            value => _canvas.ShowBtr = value);

        // 散落物资带子菜单：7 个分类分别开关（分类位值见 LootKinds）。
        // 一个点位可能同时"能出钥匙也能出杂物"，只要有一类没被关掉就还会显示。
        var lootItem = Create("散落物资", LayerIcon.Loot, Color.FromArgb(255, 186, 88),
            _canvas.ShowLoot ? CheckState.Checked : CheckState.Unchecked);
        _layerMenu.Items.Add(lootItem);
        lootItem.DropDown.Renderer = renderer;

        var lootAll = Create("全部显示", LayerIcon.Dot, UiTokens.Ok, CheckState.Unchecked);
        var lootNone = Create("全部隐藏", LayerIcon.Dot, UiTokens.TextFaint, CheckState.Unchecked);
        lootItem.DropDownItems.Add(lootAll);
        lootItem.DropDownItems.Add(lootNone);
        lootItem.DropDownItems.Add(new ToolStripSeparator());
        var lootRows = new List<(int Bit, ToolStripMenuItem Item)>();
        void SyncLoot()
        {
            _canvas.ShowLoot = lootRows.Any(row => row.Item.CheckState == CheckState.Checked);
            var hidden = lootRows.Count(row => row.Item.CheckState != CheckState.Checked);
            lootItem.CheckState = hidden == 0 ? CheckState.Checked
                : hidden == lootRows.Count ? CheckState.Unchecked
                : CheckState.Indeterminate;
            QueueLayerUpdate();
        }
        foreach (var lootKind in LootKinds.All)
        {
            var sub = Create(lootKind.Name, LayerIcon.Dot, lootKind.Color, CheckState.Checked);
            var bit = lootKind.Bit;
            sub.Click += (_, _) =>
            {
                var on = sub.CheckState != CheckState.Checked;
                sub.CheckState = on ? CheckState.Checked : CheckState.Unchecked;
                if (on)
                {
                    _canvas.HiddenLootKinds.Remove(bit);
                }
                else
                {
                    _canvas.HiddenLootKinds.Add(bit);
                }
                SyncLoot();
            };
            lootItem.DropDownItems.Add(sub);
            lootRows.Add((bit, sub));
        }
        void SetAllLoot(bool on)
        {
            foreach (var (bit, sub) in lootRows)
            {
                sub.CheckState = on ? CheckState.Checked : CheckState.Unchecked;
                if (on)
                {
                    _canvas.HiddenLootKinds.Remove(bit);
                }
                else
                {
                    _canvas.HiddenLootKinds.Add(bit);
                }
            }
            SyncLoot();
        }
        lootAll.Click += (_, _) => SetAllLoot(true);
        lootNone.Click += (_, _) => SetAllLoot(false);
        lootItem.DropDown.Closing += (_, e) =>
        {
            if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked)
            {
                e.Cancel = true;
            }
        };

        // 手动标记：清空（换赛季/重做任务时用）
        AddCommand("清空已完成标记", LayerIcon.ClearMarks, Color.FromArgb(150, 210, 175), ClearDoneMarks);
        AddToggle("出生点", LayerIcon.Spawn, Color.FromArgb(150, 200, 240), _canvas.ShowSpawns,
            value => _canvas.ShowSpawns = value);
        AddToggle("危险区", LayerIcon.Hazard, Color.FromArgb(235, 120, 110), _canvas.ShowHazards,
            value => _canvas.ShowHazards = value);
        AddToggle("固定武器", LayerIcon.Weapon, Color.FromArgb(180, 190, 205), _canvas.ShowStatics,
            value => _canvas.ShowStatics = value);
        // 门锁和闸点：数据来自 tools/build-doors.ps1 的快照（Assets/doors.json）
        AddToggle("门锁（要钥匙）", LayerIcon.Door, UiTokens.Warn, _canvas.ShowDoors,
            value => _canvas.ShowDoors = value);
        AddToggle("闸点（拉闸）", LayerIcon.Switch, Color.FromArgb(255, 208, 64), _canvas.ShowSwitches,
            value => _canvas.ShowSwitches = value);
        _layerMenu.Items.Add(new ToolStripSeparator());

        // 容器带子菜单：点父项整体开/关，点子项父项跟着变半选
        var containerItem = Create("容器", LayerIcon.Container, Color.FromArgb(214, 176, 96),
            _canvas.ShowContainers ? CheckState.Checked : CheckState.Unchecked);
        _layerMenu.Items.Add(containerItem);
        containerItem.DropDown.Renderer = renderer;

        var kinds = new List<(string Slug, ToolStripMenuItem Item)>();
        // 父项带子菜单时点父项只会展开，所以整体开关放两条独立命令
        var showAll = Create("全部显示", LayerIcon.Dot, UiTokens.Ok, CheckState.Unchecked);
        var hideAll = Create("全部隐藏", LayerIcon.Dot, UiTokens.TextFaint, CheckState.Unchecked);
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
        }

        showAll.Click += (_, _) => SetAll(true);
        hideAll.Click += (_, _) => SetAll(false);

        foreach (var (kind, count) in MapMarkers.KindsByCount())
        {
            var info = ContainerKinds.Resolve(kind);
            var slug = kind;
            var sub = Create($"{info.Name}（{count}）", LayerIcon.Dot, info.Color, CheckState.Checked);
            sub.Click += (_, _) =>
            {
                var on = sub.CheckState != CheckState.Checked;
                sub.CheckState = on ? CheckState.Checked : CheckState.Unchecked;
                ApplyKindState(slug, on);
                RefreshContainerState(containerItem, kinds);
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

        // 子项建好以后再同步父项与画布，避免初始化发生在空列表上。
        SetAll(true);
        SyncLoot();
        UpdateLayerButtonText();
    }
    private void ClearDoneMarks()
    {
        var count = DoneZones.Count;
        if (count == 0)
        {
            _status.Text = "没有手动标记";
            return;
        }
        // 危险操作：确认框交代对象和不可逆后果，按钮用动宾文案（UI-SPEC §7.3）
        if (!ConfirmDialog.Ask(this,
                "清空「本图已完成」标记？",
                $"共 {count} 条手动标记，清空后无法恢复。\n只影响手动标记，不动任务的真实状态。",
                "彻底清空"))
        {
            return;
        }
        DoneZones.ClearAll();
        _canvas.Invalidate();
        RefreshTaskList();
        UpdateStatus();
        _status.Text = $"已清空 {count} 个手动标记";
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
        QueueLayerUpdate();
    }

    /// <summary>深色 ToolStrip 配色：菜单项背景和边框。</summary>
    private sealed class DarkMenuColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Color.FromArgb(46, 52, 62);
        public override Color MenuItemBorder => Color.FromArgb(70, 80, 95);
        public override Color ToolStripDropDownBackground => UiTokens.Raised;
        public override Color ImageMarginGradientBegin => UiTokens.Raised;
        public override Color ImageMarginGradientMiddle => UiTokens.Raised;
        public override Color ImageMarginGradientEnd => UiTokens.Raised;
        public override Color MenuBorder => Color.FromArgb(58, 66, 78);
    }

    private void UpdateLayerButtonText()
    {
        var count = 0;
        foreach (ToolStripItem item in _layerMenu.Items)
        {
            if (item is ToolStripMenuItem item2 && item2.CheckState != CheckState.Unchecked)
            {
                count++;
            }
        }
        var text = count == 0 ? "图层" : $"图层 {count}";
        if (_layerButton.Text != text) _layerButton.Text = text;
    }

    private void QueueLayerUpdate()
    {
        if (_layerUpdateQueued || !IsHandleCreated || IsDisposed) return;
        _layerUpdateQueued = true;
        BeginInvoke(() =>
        {
            _layerUpdateQueued = false;
            if (IsDisposed) return;
            UpdateLayerButtonText();
            _canvas.Invalidate();
        });
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
        using var back = new SolidBrush(selected ? Color.FromArgb(40, 48, 60) : UiTokens.Sunken);
        e.Graphics.FillRectangle(back, e.Bounds);

        var dotColor = item.State switch
        {
            TaskState.Started => Color.FromArgb(76, 154, 255),
            TaskState.Finished => UiTokens.Ok,
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
        // 手机端：状态推送和动作回收都走这个 250ms 的计时器（打开面板前它什么也不做）
        _mobileTimer.Tick += (_, _) => PumpMobile();
        _mobileTimer.Start();
        UpdateStatus();
    }

    /// <summary>下拉框：每张地图后面带上这张图上"进行中"的任务数。</summary>
    /// <summary>把隐藏 ComboBox 的当前项同步到自绘按钮上。</summary>
    private void SyncMapButtonText()
    {
        var index = _mapSelect.SelectedIndex;
        var text = index >= 0 && index < _mapSelect.Items.Count
            ? _mapSelect.Items[index]?.ToString() ?? "" : "选择地图";
        if (_mapButton.Text != text) _mapButton.Text = text;
        if (_mapMenu.Visible) RefreshMapMenu();
    }

    /// <summary>复用菜单项；任务数/选中地图变化时只更新文字和选中状态。</summary>
    private void RefreshMapMenu()
    {
        if (_mapMenu.Items.Count != _mapSelect.Items.Count)
        {
            while (_mapMenu.Items.Count > 0) _mapMenu.Items[0].Dispose();
            for (var i = 0; i < _mapSelect.Items.Count; i++)
            {
                var index = i;
                var item = new ToolStripMenuItem { Padding = new Padding(12, 4, 16, 4) };
                item.Click += (_, _) =>
                {
                    _mapMenu.Close();
                    if (index != _mapSelect.SelectedIndex) _mapSelect.SelectedIndex = index;
                };
                _mapMenu.Items.Add(item);
            }
        }
        for (var i = 0; i < _mapMenu.Items.Count; i++)
        {
            var item = (ToolStripMenuItem)_mapMenu.Items[i];
            var text = _mapSelect.Items[i]?.ToString() ?? "";
            if (item.Text != text) item.Text = text;
            item.Checked = i == _mapSelect.SelectedIndex;
        }
    }
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

    /// <summary>
    /// 同一张图的另一种画法：瓦片版指向它基于的 SVG 版，SVG 版则去找指向自己的瓦片版。
    /// 只有真的存在另一种画法时才显示那个按钮，其余地图完全不受影响。
    /// </summary>
    private void UpdateVariantChip(MapProjection map)
    {
        var target = !string.IsNullOrEmpty(map.VariantOf)
            ? map.VariantOf
            : MapPack.Maps
                .FirstOrDefault(other => string.Equals(other.VariantOf, map.Key, StringComparison.OrdinalIgnoreCase))
                ?.Key;
        if (string.IsNullOrEmpty(target) || MapPack.Find(target) == null)
        {
            _variant.Tag = null;
            _variant.Visible = false;
            return;
        }
        _variant.Tag = target;
        _variant.Text = string.IsNullOrEmpty(map.VariantOf) ? "瓦片版" : "SVG 版";
        _variant.Visible = true;
    }

    private void SelectMap(string key, bool preferDefault = true)
    {
        var maps = MapPack.Maps.ToList();
        var target = MapPack.Resolve(key, preferDefault);
        var index = maps.FindIndex(m => m.Key == target?.Key);
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
        // 本图已做完的任务：画布上直接不画它们
        _canvas.HiddenTasks = DoneZones.TasksForMap(map.Data);
        _canvas.Zones = TarkovData.ZonesByMap.TryGetValue(map.Data, out var zones)
            ? zones
            : new List<ZoneShape>();
        _canvas.Extracts = TarkovData.Extracts.TryGetValue(map.Data, out var extracts)
            ? extracts.Select(m => m.Project(map)).ToList()
            : new List<MapMarker>();
        _canvas.Transits = TarkovData.Transits.TryGetValue(map.Data, out var transits)
            ? transits.Select(m => m.Project(map)).ToList()
            : new List<MapMarker>();
        _canvas.BtrStops = TarkovData.BtrStops.TryGetValue(map.Data, out var btrStops)
            ? btrStops.Select(m => m.Project(map)).ToList()
            : new List<MapMarker>();
        // 门锁/闸点：离线快照，按当前地图投影（缓存 key 是 map.Key）
        var (doors, switches) = MapDoors.For(map);
        _canvas.Doors = doors;
        _canvas.Switches = switches;
        _canvas.SelectedTaskId = null;
        UpdateVariantChip(map);
        _canvas.Invalidate();
        RefreshTaskList();
        UpdateSpawnPoints(null);
        UpdateStatus();
        GC.KeepAlive(keepView);
    }

    // ------------------------------------------------------------------ 手机端

    /// <summary>
    /// Ctrl + 方向键：微调当前地图的标定（SVG 版和瓦片版标定对不齐时用）。
    /// Ctrl+0 清零。改完把各图层的投影缓存清掉并重画。
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if ((keyData & Keys.Control) == Keys.Control && _canvas.Map is { } map)
        {
            var key = keyData & Keys.KeyCode;
            const double step = 2.0;
            double dx = 0;
            double dy = 0;
            if (key == Keys.Left)
            {
                dx = -step;
            }
            else if (key == Keys.Right)
            {
                dx = step;
            }
            else if (key == Keys.Up)
            {
                dy = -step;
            }
            else if (key == Keys.Down)
            {
                dy = step;
            }
            else if (key is Keys.D0 or Keys.NumPad0)
            {
                MapCalibration.Clear(map.Key);
                RefreshCalibration(map, "已清零");
                return true;
            }
            else
            {
                return base.ProcessCmdKey(ref msg, keyData);
            }
            var (x, y) = MapCalibration.Nudge(map.Key, dx, dy);
            RefreshCalibration(map, $"标定偏移 {x:+0.#;-0.#;0}, {y:+0.#;-0.#;0}");
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>标定改完：清投影缓存 → 重新投影 → 重画。</summary>
    private void RefreshCalibration(MapProjection map, string note)
    {
        MapDoors.ClearCache();
        MapMarkers.ClearCache();
        MapLabels.ClearCache();
        ApplyMap();
        _canvas.Invalidate();
        _status.Text = $"{map.DisplayName} {note}（Ctrl+方向键继续微调，Ctrl+0 清零）";
    }
    /// <summary>
    /// 任务列表右键：切换"这个任务在本图已完成"。
    /// 标了以后这张图上它的区域/点位全部不画，列表里显示「本图已完成」。
    /// </summary>
    private void ToggleTaskDoneOnMap(TaskRow? row)
    {
        var map = CurrentMap;
        if (row == null || map == null)
        {
            return;
        }
        var now = DoneZones.Toggle(row.Id, map.Data);
        _canvas.HiddenTasks = DoneZones.TasksForMap(map.Data);
        _canvas.Invalidate();
        RefreshTaskList();
        UpdateStatus();
        _status.Text = now
            ? $"{row.Name}：本图已完成（{map.DisplayName} 上不再显示，右键可取消）"
            : $"{row.Name}：已取消「本图已完成」";
    }

    /// <summary>任务列表的右键：把点到的行标成"本图已完成"。</summary>
    private void OnTaskListRightClick(MouseEventArgs e)
    {
        var index = _taskList.IndexFromPoint(e.Location);
        if (index < 0)
        {
            return;
        }
        _lastClickedRow = index;
        _taskList.SelectedIndex = index;
        ToggleTaskDoneOnMap(_taskList.Items[index] as TaskRow);
    }

    /// <summary>点「手机端」：第一次点顺便把服务起起来，然后弹面板。</summary>
    private void ShowMobilePanel()
    {
        if (_mobilePanel is { IsDisposed: false })
        {
            _mobilePanel.Activate();
            return;
        }
        if (!_mobile.Running)
        {
            _mobile.Start();
            UpdateMobileButton();
        }
        _mobilePanel = new MobileForm(_mobile);
        _mobilePanel.FormClosed += (_, _) => _mobilePanel = null;
        // 不用 CenterParent：非模态窗口有时会被算到屏幕外（试过跑到负坐标），自己算最稳
        _mobilePanel.StartPosition = FormStartPosition.Manual;
        var area = Screen.FromControl(this).WorkingArea;
        _mobilePanel.Location = new Point(
            Math.Clamp(Left + (Width - _mobilePanel.Width) / 2, area.Left + 8, area.Right - _mobilePanel.Width - 8),
            Math.Clamp(Top + 80, area.Top + 8, area.Bottom - _mobilePanel.Height - 8));
        _mobilePanel.Show(this);
    }

    private void UpdateMobileButton()
    {
        if (!_mobile.Running)
        {
            _mobileButton.Text = "手机端";
            _mobileButton.ForeColor = TextMain;
            return;
        }
        var clients = _mobile.Clients;
        _mobileButton.Text = clients > 0 ? $"手机端 {clients}" : "手机端 ●";
        _mobileButton.ForeColor = clients > 0 ? UiTokens.Ok : TextMain;
    }

    /// <summary>
    /// 每 250ms：先把手机发来的动作做掉，再把当前状态快照推过去。
    /// 服务没开就什么都不用做。
    /// </summary>
    private void PumpMobile()
    {
        if (!_mobile.Running)
        {
            return;
        }
        while (_mobile.Actions.TryDequeue(out var action))
        {
            ApplyMobileAction(action);
        }
        var state = _mobile.State;
        state.Map = _canvas.Map;
        state.Layer = _canvas.CurrentLayer;
        state.Zones = _canvas.ProjectedZones;
        state.Extracts = _canvas.Extracts;
        state.Transits = _canvas.Transits;
        state.Player = _canvas.PlayerPosition;
        state.PlayerForward = _canvas.PlayerForward;
        // 世界坐标给手机报点用（和底栏显示的是同一份数据）
        state.PlayerWorld = _player.Current is { } fix
            ? new PointF((float)fix.X, (float)fix.Z)
            : null;
        state.PlayerMapKey = _canvas.PlayerMapKey;
        state.SelectedTaskId = _canvas.SelectedTaskId;
        state.OnlyActive = _onlyActive.Active;
        state.Tasks = _mobileTasks;
        state.Objectives = _mobileObjectives;
        state.Items = _mobileItems;
        state.Flush();
        UpdateMobileButton();
    }

    private void ApplyMobileAction(MobileAction action)
    {
        switch (action.Type)
        {
            case MobileAction.SetFloor:
                _canvas.SetLayer(action.Value);
                _canvas.Invalidate();
                break;
            case MobileAction.SelectTask:
                SelectTaskFromMobile(action.Value);
                break;
            case MobileAction.SetMap:
                SelectMap(action.Value, preferDefault: false);
                break;
        }
    }

    /// <summary>手机点了任务：在电脑端的列表里选中同一行（会顺便切楼层、刷新目标）。</summary>
    private void SelectTaskFromMobile(string taskId)
    {
        if (string.IsNullOrEmpty(taskId))
        {
            _lastClickedRow = -1;
            _taskList.ClearSelected();
            _canvas.SelectedTaskId = null;
            ShowTaskDetail(null);
            _canvas.Invalidate();
            return;
        }
        for (var i = 0; i < _taskList.Items.Count; i++)
        {
            if (_taskList.Items[i] is TaskRow row
                && string.Equals(row.Id, taskId, StringComparison.OrdinalIgnoreCase))
            {
                if (i != _taskList.SelectedIndex)
                {
                    _taskList.SelectedIndex = i;
                }
                return;
            }
        }
    }

    /// <summary>任务列表变了就同步一份给手机（手机不自己拼标题和状态文案）。</summary>
    private void SyncMobileTasks()
    {
        var tasks = new List<MobileTask>(_taskList.Items.Count);
        foreach (var item in _taskList.Items)
        {
            if (item is TaskRow row)
            {
                tasks.Add(new MobileTask(row.Id, row.Name, row.Subtitle, row.State));
            }
        }
        _mobileTasks = tasks;
    }

    /// <summary>所需物品列表：点一下就把名字复制到剪贴板，方便去跳蚤市场搜索。</summary>
    private Control BuildItemList()
    {
        _itemHeader.Text = "所需物品 · 点击复制";
        _itemHeader.Dock = DockStyle.Bottom;
        _itemHeader.Height = 24;
        _itemHeader.ForeColor = TextDim;
        _itemHeader.BackColor = UiTokens.PanelBack;
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

    /// <summary>
    /// 「任务页」点击后弹出的站点列表。中文 Wiki（eftarkov.com）和 tarkov.dev 用同一个任务 ID，
    /// 所以这里不需要映射表，直接拼 URL 就行；实测 /task/&lt;id&gt; 返回 200。
    /// </summary>
    private void ShowTaskPageMenu()
    {
        if (string.IsNullOrEmpty(_currentTaskId) && string.IsNullOrEmpty(_currentSlug))
        {
            _status.Text = "这个任务没有可用的资料站链接";
            return;
        }

        var menu = BuildTaskPageMenu();
        menu.Renderer = new LayerMenuRenderer();
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = false;
        var point = _wikiButton.PointToScreen(new Point(0, -menu.GetPreferredSize(Size.Empty).Height));
        menu.Show(point);
    }

    /// <summary>任务资料站列表：中文 Wiki 优先（国内可直连），其次 tarkov.dev，最后 fandom。</summary>
    private ContextMenuStrip BuildTaskPageMenu()
    {
        var menu = new ContextMenuStrip { Font = new Font("Microsoft YaHei UI", 9f) };

        if (!string.IsNullOrEmpty(_currentTaskId))
        {
            menu.Items.Add("中文 Wiki（eftarkov.com）", null,
                (_, _) => OpenUrl($"https://www.eftarkov.com/task/{_currentTaskId}"));
        }
        if (!string.IsNullOrEmpty(_currentSlug))
        {
            menu.Items.Add("tarkov.dev 任务页", null,
                (_, _) => OpenUrl($"https://tarkov.dev/task/{_currentSlug}"));
        }
        if (!string.IsNullOrEmpty(_currentWiki))
        {
            menu.Items.Add("Fandom Wiki（需代理）", null, (_, _) => OpenUrl(_currentWiki));
        }
        // 环境变量可以覆盖：TARKOVMAPLITE_TASK_URL=https://example.com/task/{slug}
        var custom = Environment.GetEnvironmentVariable("TARKOVMAPLITE_TASK_URL");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            var url = custom.Replace("{slug}", _currentSlug).Replace("{id}", _currentTaskId);
            menu.Items.Add("自定义站点", null, (_, _) => OpenUrl(url));
        }
        return menu;
    }

    /// <summary>当前任务可用的资料站（桌面和手机端共用同一份，保证两边看到的一样）。</summary>
    private List<(string Label, string Url)> CurrentTaskLinks()
    {
        var links = new List<(string Label, string Url)>();
        if (!string.IsNullOrEmpty(_currentTaskId))
        {
            links.Add(("中文 Wiki", $"https://www.eftarkov.com/task/{_currentTaskId}"));
        }
        if (!string.IsNullOrEmpty(_currentSlug))
        {
            links.Add(("tarkov.dev", $"https://tarkov.dev/task/{_currentSlug}"));
        }
        if (!string.IsNullOrEmpty(_currentWiki))
        {
            links.Add(("Fandom", _currentWiki));
        }
        return links;
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
            _mobileObjectives = Array.Empty<string>();
            _mobileItems = Array.Empty<MobileItem>();
            return;
        }
        // 换任务必须先清空：AppendDetail 只追加，不清空的话新任务的目标会接在上一个
        // 任务后面，而下面又把光标滚回顶部——看起来就像"描述没有切换"。
        _detail.Clear();
        _detail.SelectionColor = _detail.ForeColor;
        _detailHeader.Text = "目标 · " + row.Name;
        _currentTaskId = row.Id;
        _currentWiki = TarkovData.TaskWiki.TryGetValue(row.Id, out var taskWiki) ? taskWiki : "";
        _currentSlug = TarkovData.TaskSlug.TryGetValue(row.Id, out var taskSlug) ? taskSlug : "";
        _wikiButton.Visible = _currentSlug.Length > 0 || _currentWiki.Length > 0;
        var needed = new List<(string Id, string Name)>();
        // 手机抽屉里的目标文字：和左栏同一份，只是不带颜色
        var mobileObjectives = new List<string>();
        if (TarkovData.TaskObjectives.TryGetValue(row.Id, out var objectives))
        {
            foreach (var objective in objectives)
            {
                var count = objective.Count is > 0 ? $" ×{objective.Count}" : "";
                AppendDetail("• " + objective.Description + count, Color.FromArgb(226, 232, 240));
                mobileObjectives.Add(objective.Description + count);
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
                    AppendDetail("     钥匙  " + string.Join(" 或 ", groups), UiTokens.Accent);
                }
            }
        }
        else
        {
            AppendDetail("（这个任务没有可显示的目标文本）", TextDim);
        }
        _mobileObjectives = mobileObjectives;
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

        _mobileItems = distinct.Select(item => new MobileItem(item.Id, item.Name)).ToList();
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
        // 1) 真实定位：只认"本次启动之后"的截图（避免把几天前的老图当成当前位置）
        var real = _player.Current;
        var realMap = string.IsNullOrEmpty(_raidMapKey) ? null : MapPack.Find(_raidMapKey);
        if (real != null && realMap != null)
        {
            ApplyFix(realMap, real.X, real.Y, real.Z, real.ForwardX, real.ForwardZ);
            return;
        }
        // 2) 测试截图：按"当前正在看的地图"投影，方便确认截图目录和投影是否正常
        if (_testFix is { } test && CurrentMap is { } viewing)
        {
            ApplyFix(viewing, test.X, test.Y, test.Z, test.FX, test.FZ);
            return;
        }
        _canvas.PlayerPosition = null;
        _canvas.PlayerForward = null;
        _canvas.PlayerMapKey = "";
        _canvas.PlayerFloor = null;
    }

    /// <summary>把世界坐标投影到指定地图并交给画布（真实定位和测试截图共用）。</summary>
    private void ApplyFix(MapProjection map, double worldX, double worldY, double worldZ,
        double forwardX, double forwardZ)
    {
        var (x, y) = MapPack.ToImage(map, worldX, worldZ);
        var (fx, fy) = MapPack.ToImage(map, worldX + forwardX * 3, worldZ + forwardZ * 3);
        _canvas.PlayerPosition = new PointF((float)x, (float)y);
        _canvas.PlayerForward = new PointF((float)(fx - x), (float)(fy - y));
        _canvas.PlayerMapKey = map.Key;
        _canvas.PlayerFloor = TarkovData.FloorOfHeight(map.Data, worldX, worldZ, worldY);
        _canvas.Invalidate();
    }

    /// <summary>
    /// 「测试截图」：拿截图目录里**最新的一张**（不管是不是本次启动之后拍的）试一下投影。
    /// 用来确认目录选对了、文件名格式能认出来、以及标记在图上是什么样。
    /// </summary>
    private void TestNewestScreenshot()
    {
        var folder = _player.Folder.Length > 0 ? _player.Folder : PlayerLocator.ResolveFolder();
        var fix = _player.NewestInFolder(folder);
        if (fix == null)
        {
            _testFix = null;
            UpdatePlayerMarker();
            _status.Text = Directory.Exists(folder)
                ? $"没有找到可用截图：{folder}（里面没有 PNG，或文件名里没有坐标）"
                : $"截图目录不存在：{folder}";
            return;
        }
        _testFix = (fix.X, fix.Y, fix.Z, fix.ForwardX, fix.ForwardZ);
        UpdatePlayerMarker();
        // 截图可能来自别的图，投影到当前图上就落到画布外了；说清楚，免得以为按钮没反应
        var outside = _canvas.PlayerPosition is { } point
                      && (point.X < 0 || point.Y < 0 || point.X > _canvas.Width || point.Y > _canvas.Height);
        _status.Text = $"测试截图：{fix.File}　X {fix.X:0.0} Y {fix.Y:0.0} Z {fix.Z:0.0}"
                       + $"（{fix.CapturedAt:MM-dd HH:mm}，按当前地图投影）"
                       + (outside ? "　⚠ 坐标不在当前地图范围内，这张截图可能来自别的地图" : "");
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

    // ------------------------------------------------------------------------
    // 任务列表的空状态（UI-SPEC §9：五态里"空态必须解释为什么空 + 给出口"）
    //
    // 为什么用 Paint 而不是往列表里塞一条假行：
    //   列表是 OwnerDrawFixed + 单击取消选中，塞假行会污染选中逻辑、手机端任务同步，
    //   还要在四五处加特判。直接画在空白处、自己处理点击，副作用最小。
    //   注意：有内容时这里一律不画，正常绘制完全不受影响。
    // ------------------------------------------------------------------------

    private enum EmptyAction
    {
        None,
        DropActiveFilter,   // 关掉「仅进行中」
        ShowAnyRegion,      // 切到「任意地区」
        BackToMap,          // 从「任意地区」切回本图
    }

    /// <summary>空列表时第 1/2 个出口的文字和动作。两个方法必须同源，改文案例子时一起改。</summary>
    private static (string Text, EmptyAction Action) EmptyOption(int index, bool anyRegion, bool onlyActive)
    {
        if (index == 0)
        {
            if (anyRegion)
            {
                return ("· 回到本图任务", EmptyAction.BackToMap);
            }
            return onlyActive
                ? ("· 关掉「仅进行中」，看本图全部任务", EmptyAction.DropActiveFilter)
                : ("· 看「任意地区」任务", EmptyAction.ShowAnyRegion);
        }
        return !anyRegion && onlyActive
            ? ("· 看「任意地区」任务（跨图任务）", EmptyAction.ShowAnyRegion)
            : ("", EmptyAction.None);
    }

    /// <summary>三块可点区域的矩形（标题 / 出口一 / 出口二）。绘制和点击都用它。</summary>
    private Rectangle[] EmptyHintRects()
    {
        var width = Math.Max(40, _taskList.ClientSize.Width - 32);
        return new[]
        {
            new Rectangle(16, 18, width, 24),
            new Rectangle(16, 54, width, 22),
            new Rectangle(16, 78, width, 22),
        };
    }

    private void PaintEmptyTaskHint(object? sender, PaintEventArgs e)
    {
        if (_taskList.Items.Count > 0)
        {
            return;
        }
        var rects = EmptyHintRects();
        var title = _noZone.Active ? "没有符合条件的任意地区任务" : "本图没有进行中的任务";
        TextRenderer.DrawText(e.Graphics, title, _taskList.Font, rects[0], UiTokens.TextMain);
        for (var i = 0; i < 2; i++)
        {
            var (text, _) = EmptyOption(i, _noZone.Active, _onlyActive.Active);
            if (text.Length == 0)
            {
                continue;
            }
            TextRenderer.DrawText(e.Graphics, text, _taskList.Font, rects[i + 1], UiTokens.Accent);
        }
    }

    private void ClickEmptyTaskHint(object? sender, MouseEventArgs e)
    {
        if (_taskList.Items.Count > 0)
        {
            return;
        }
        var rects = EmptyHintRects();
        for (var i = 0; i < 2; i++)
        {
            if (!rects[i + 1].Contains(e.Location))
            {
                continue;
            }
            var (_, action) = EmptyOption(i, _noZone.Active, _onlyActive.Active);
            if (action == EmptyAction.None)
            {
                return;
            }
            switch (action)
            {
                case EmptyAction.DropActiveFilter:
                    _onlyActive.Active = false;
                    _canvas.OnlyActive = false;
                    break;
                case EmptyAction.ShowAnyRegion:
                    _noZone.Active = true;
                    break;
                case EmptyAction.BackToMap:
                    _noZone.Active = false;
                    break;
            }
            RefreshTaskList();
            _taskList.ClearSelected();
            _canvas.SelectedTaskId = null;
            ShowTaskDetail(null);
            _canvas.Invalidate();
            return;
        }
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
            SyncMobileTasks();
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
                // 手动标记：这个任务在本图已经做完了（右键任务列表切换）
                if (DoneZones.IsDone(zone.TaskId, CurrentMap?.Data ?? ""))
                {
                    subtitle += " · 本图已完成 ✓";
                }
                _taskList.Items.Add(new TaskRow(zone.TaskId, zone.TaskName, subtitle, state));
            }
        }
        _taskList.EndUpdate();
        SyncMobileTasks();
        _header.Text = $"任务 · {_taskList.Items.Count}";
    }

    /// <summary>
    /// 手动指定游戏截图目录：有些人的截图不在默认的「文档\Escape From Tarkov\Screenshots」，
    /// 也可能接了多块盘。选完立刻生效并写进 settings.json，重启不用再选。
    /// </summary>
    private void PickScreenshotFolder()
    {
        // 用 FolderBrowserDialog：OpenFolderDialog 在这个目标框架下不可用（CS0246 实测）
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择塔科夫截图文件夹（游戏内按 F12 的截图会存在这里）",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
            SelectedPath = Directory.Exists(_player.Folder) ? _player.Folder : PlayerLocator.ResolveFolder(),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        var folder = dialog.SelectedPath;
        PlayerLocator.SaveFolder(folder);
        _player.UseFolder(folder);

        // 顺手告诉用户这个目录里有多少张、最新一张是什么时候，方便确认没选错
        var count = 0;
        var newest = DateTime.MinValue;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.png"))
            {
                count++;
                var time = File.GetLastWriteTime(file);
                if (time > newest)
                {
                    newest = time;
                }
            }
        }
        catch
        {
            // 读不动就只报目录本身
        }
        UpdateStatus();
        _status.Text = count == 0
            ? $"截图目录：{folder}　（没有 PNG，确认一下是不是这个文件夹）"
            : $"截图目录：{folder}　共 {count} 张，最新 {newest:MM-dd HH:mm}　进游戏按截图键就会定位";
    }
    /// <summary>状态栏用的楼层提示：能判定就写「（2 层）」，判定不了留空。</summary>
    private static string FloorHint(string mapData, double x, double z, double y)
    {
        if (TarkovData.FloorOfHeight(mapData, x, z, y) is not { } floor)
        {
            return "";
        }
        var name = floor switch
        {
            -1 => "地下",
            0 => "地面",
            _ => $"{floor + 1} 层",
        };
        return $"（{name}）";
    }
    /// <summary>
    /// 对局结束时清掉这一局拍的截图。只在开关打开时执行，且只删截图目录里
    /// 符合游戏截图命名、时间落在本局区间内的 PNG——不递归、不碰别的文件。
    /// </summary>
    private void CleanupRaidScreenshots(DateTime from, DateTime to)
    {
        if (!PlayerLocator.CleanupShots || from == DateTime.MinValue || to <= from)
        {
            return;
        }
        var folder = _player.Folder.Length > 0 ? _player.Folder : PlayerLocator.ResolveFolder();
        if (!Directory.Exists(folder))
        {
            return;
        }
        // 给截图留 30 秒余量：退出前最后一张可能比进图日志晚一点点写入
        var start = from.AddSeconds(-30);
        var removed = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*.png"))
            {
                if (!PlayerLocator.LooksLikeScreenshot(Path.GetFileName(path)))
                {
                    continue;
                }
                var time = File.GetLastWriteTime(path);
                if (time < start || time > to)
                {
                    continue;
                }
                File.Delete(path);
                removed++;
            }
        }
        catch
        {
            // 删不掉就算了，不要影响切图
        }
        if (removed > 0)
        {
            _status.Text = $"已清理上一局的截图 {removed} 张（{from:HH:mm}–{to:HH:mm}）";
        }
    }

    /// <summary>截图目录按钮的右键菜单：自动清理开关（默认关）+ 打开目录。</summary>
    private void ShowScreenshotsMenu()
    {
        var menu = new ContextMenuStrip { Font = new Font("Microsoft YaHei UI", 9f) };
        var cleanup = new ToolStripMenuItem("对局结束自动删截图")
        {
            Checked = PlayerLocator.CleanupShots,
        };
        cleanup.Click += (_, _) =>
        {
            var turnOn = !PlayerLocator.CleanupShots;
            if (turnOn && !ConfirmDialog.Ask(
                    this,
                    "自动清理截图",
                    "打开后，每局结束时会删掉这一局期间拍的截图。\n\n"
                    + "只会删截图目录里符合游戏截图命名的 PNG，时间落在本局范围内的；"
                    + "不递归子目录，不碰别的文件。已经过去的旧对局不会被清理。",
                    "打开"))
            {
                return;
            }
            PlayerLocator.CleanupShots = turnOn;
            _status.Text = turnOn ? "已开启：每局结束清理本局截图" : "已关闭：不再自动清理截图";
        };
        menu.Items.Add(cleanup);

        menu.Items.Add("打开截图目录", null, (_, _) =>
        {
            var folder = _player.Folder.Length > 0 ? _player.Folder : PlayerLocator.ResolveFolder();
            if (Directory.Exists(folder))
            {
                OpenUrl(folder);
            }
            else
            {
                _status.Text = $"截图目录不存在：{folder}";
            }
        });

        menu.Renderer = new LayerMenuRenderer();
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = false;
        menu.Show(_shotsButton, new Point(0, -menu.GetPreferredSize(Size.Empty).Height));
    }
    /// <summary>上次关闭时的窗口位置和大小（含最大化状态），存在 settings.json 里。</summary>
    private void RestoreWindowBounds()
    {
        var saved = PlayerLocator.GetSetting("window");
        if (string.IsNullOrEmpty(saved))
        {
            return;
        }
        var parts = saved.Split(',');
        if (parts.Length != 5
            || !int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y)
            || !int.TryParse(parts[2], out var w) || !int.TryParse(parts[3], out var h))
        {
            return;
        }
        // 只接受还在屏幕内的位置：换了显示器/拔了副屏时不要开在看不见的地方
        var screen = Screen.FromPoint(new Point(x + 40, y + 40)).WorkingArea;
        if (!screen.IntersectsWith(new Rectangle(x, y, Math.Max(w, 200), Math.Max(h, 200))))
        {
            return;
        }
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(x, y, Math.Max(w, MinimumSize.Width), Math.Max(h, MinimumSize.Height));
        if (parts[4] == "1")
        {
            WindowState = FormWindowState.Maximized;
        }
    }

    /// <summary>关窗时记住位置和大小（最大化时记还原后的尺寸，不然下次会一直粘在最大化）。</summary>
    private void SaveWindowBounds()
    {
        var rect = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        if (rect.Width < 200 || rect.Height < 200)
        {
            return;
        }
        var max = WindowState == FormWindowState.Maximized ? "1" : "0";
        PlayerLocator.SetSetting("window", $"{rect.X},{rect.Y},{rect.Width},{rect.Height},{max}");
    }
    /// <summary>把这张图的门锁按钥匙归组，弹「钥匙」列表；选中就飞到那把锁并闪一下。</summary>
    private void OpenKeyList()
    {
        var map = CurrentMap;
        if (map == null)
        {
            _status.Text = "先选一张地图";
            return;
        }
        var (doors, _) = MapDoors.For(map);
        var rows = doors
            .Where(door => !string.IsNullOrEmpty(door.KeyName))
            .GroupBy(door => MapDoors.DoorLabel(door.KeyName), StringComparer.OrdinalIgnoreCase)
            .Select(group => new KeyListForm.Row(group.Key, group.Count(), group.First().World))
            .OrderBy(row => row.Label, StringComparer.CurrentCulture)
            .ToList();
        if (rows.Count == 0)
        {
            _status.Text = "这张图没有需要钥匙的门";
            return;
        }
        using var dialog = new KeyListForm(_mapButton.Text.Split('·')[0].Trim(), rows);
        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Chosen is { } world)
        {
            _canvas.FocusWorld(world);
            _status.Text = $"已定位：{dialog.ChosenLabel}";
        }
    }
    private void UpdateStatus()
    {
        var map = CurrentMap;
        var zones = map != null && TarkovData.ZonesByMap.TryGetValue(map.Data, out var list) ? list : null;
        var activeOnMap = zones == null
            ? 0
            : zones.Select(z => z.TaskId).Distinct()
                   .Count(id => _log.StateOf(id) == TaskState.Started);
        // 多人在同一张图上要靠坐标报点，所以标到玩家之后直接把世界坐标念出来
        var fix = _player.Current;
        var playerNote = fix == null
            ? _player.Status
            : string.Equals(_canvas.PlayerMapKey, map?.Key, StringComparison.OrdinalIgnoreCase)
                ? $"玩家：X {fix.X:0.0}　Z {fix.Z:0.0}{FloorHint(map?.Data ?? "", fix.X, fix.Z, fix.Y)}"
                : $"{MapPack.Find(_canvas.PlayerMapKey)?.DisplayName ?? "?"} 的玩家：X {fix.X:0.0} Z {fix.Z:0.0}（切回该图可见）";
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
                ? ($"在线 · {TarkovData.FetchedAt:HH:mm}", UiTokens.Ok)
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
            using var path = UiTokens.RoundedRect(rect, UiTokens.RadiusControl);
            using (var fill = new SolidBrush(Active ? UiTokens.AccentTint : UiTokens.ButtonBack))
            {
                g.FillPath(fill, path);
            }
            using (var border = new Pen(Active ? UiTokens.Accent : UiTokens.Line))
            {
                g.DrawPath(border, path);
            }
            TextRenderer.DrawText(g, Text, Font, rect,
                Active ? UiTokens.Accent : UiTokens.TextDim,
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
