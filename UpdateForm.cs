using System.Diagnostics;

namespace TarkovMapLite;

/// <summary>Modeless so checking/downloading never steals the map or interrupts a raid.</summary>
internal sealed class UpdateForm : Form
{
    private readonly MapForm _owner;
    private readonly Label _version = new(), _state = new();
    private readonly RichTextBox _notes = new();
    private readonly ProgressBar _progress = new();
    private readonly Button _check = new(), _download = new(), _skip = new(), _cancel = new(), _later = new();
    private readonly CheckBox _automatic = new();
    private readonly ComboBox _proxyMode = new();
    private readonly TextBox _proxy = new();
    private readonly Button _save = new();
    private bool _loading;
    public UpdateForm(MapForm owner)
    {
        _owner = owner; Text = "软件更新"; ClientSize = new Size(600, 550); MinimumSize = new Size(500, 460);
        StartPosition = FormStartPosition.Manual; ShowInTaskbar = false;
        BackColor = UiTokens.PanelBack; ForeColor = UiTokens.TextMain; Font = new Font("Microsoft YaHei UI", UiTokens.FontBody);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Padding = new Padding(16) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var size in new[] { 28f, 64f, -1f, 24f, 44f, 32f, 72f, 32f })
            layout.RowStyles.Add(size < 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, size));
        _version.Dock = DockStyle.Fill; _version.ForeColor = UiTokens.TextMain;
        _state.Dock = DockStyle.Fill; _state.ForeColor = UiTokens.TextDim;
        _notes.Dock = DockStyle.Fill; _notes.ReadOnly = true; _notes.DetectUrls = false;
        _notes.BorderStyle = BorderStyle.None; _notes.BackColor = UiTokens.Sunken; _notes.ForeColor = UiTokens.TextMain;
        _notes.Font = Font; _notes.ScrollBars = RichTextBoxScrollBars.Vertical;
        _progress.Dock = DockStyle.Fill; _progress.Margin = new Padding(0, 4, 0, 4);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        void Style(Button button, string text)
        {
            button.Text = text; button.AutoSize = true; button.MinimumSize = new Size(72, 32);
            button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderColor = UiTokens.Line;
            button.BackColor = UiTokens.ButtonBack; button.ForeColor = UiTokens.TextMain;
            button.Margin = new Padding(0, 4, 8, 0);
        }
        Style(_check, "检查更新"); Style(_download, "下载更新"); Style(_skip, "跳过此版本"); Style(_cancel, "取消下载");
        Style(_later, "稍后");
        actions.Controls.AddRange([_check, _download, _skip, _cancel, _later]);
        _check.Click += async (_, _) => await owner.CheckSoftwareUpdateAsync(true);
        _download.Click += async (_, _) =>
        {
            if (owner.SoftwareUpdateReady) await owner.InstallSoftwareUpdateAsync();
            else await owner.DownloadSoftwareUpdateAsync();
        };
        _skip.Click += (_, _) => owner.SkipSoftwareVersion(); _cancel.Click += (_, _) => owner.CancelSoftwareDownload();
        _later.Click += (_, _) => Close();
        _automatic.Text = "启动时自动检查软件新版"; _automatic.Dock = DockStyle.Fill;
        var connection = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Margin = Padding.Empty };
        connection.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 124)); connection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); connection.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        connection.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); connection.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        _proxyMode.Items.AddRange(["系统代理", "直接连接", "自定义代理"]); _proxyMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _proxyMode.Dock = DockStyle.Fill; _proxyMode.AccessibleName = "更新连接方式";
        _proxy.Dock = DockStyle.Fill; _proxy.PlaceholderText = "http://127.0.0.1:7890"; _proxy.AccessibleName = "更新代理地址";
        foreach (var control in new Control[] { _proxy, _proxyMode }) { control.BackColor = UiTokens.Sunken; control.ForeColor = UiTokens.TextMain; control.Margin = new Padding(0, 4, 8, 4); }
        Style(_save, "保存设置"); _save.Dock = DockStyle.Fill;
        connection.Controls.Add(_proxyMode, 0, 0); connection.Controls.Add(_proxy, 1, 0); connection.Controls.Add(_save, 2, 0);
        var connectionHint = new Label { Text = "代理仅用于软件更新。下载失败时可直接打开发布页面。", Dock = DockStyle.Fill, ForeColor = UiTokens.TextDim };
        connection.Controls.Add(connectionHint, 0, 1); connection.SetColumnSpan(connectionHint, 3);
        _proxyMode.SelectedIndexChanged += (_, _) => _proxy.Enabled = _proxyMode.SelectedIndex == 2 && !owner.SoftwareUpdateBusy;
        _save.Click += (_, _) => SaveSettings();
        _automatic.CheckedChanged += (_, _) => { if (!_loading) owner.SetSoftwareAutoCheck(_automatic.Checked); };
        var manual = new LinkLabel { Text = "打开下载页面", Dock = DockStyle.Fill, LinkColor = UiTokens.Accent, ActiveLinkColor = UiTokens.Accent, VisitedLinkColor = UiTokens.Accent };
        manual.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(owner.SoftwareUpdateOffer?.Page.AbsoluteUri ?? AppUpdate.ReleasePage) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception) { _state.Text = "浏览器无法打开，请从项目 GitHub Releases 下载。"; }
        };
        layout.Controls.Add(_version, 0, 0); layout.Controls.Add(_state, 0, 1); layout.Controls.Add(_notes, 0, 2);
        layout.Controls.Add(_progress, 0, 3); layout.Controls.Add(actions, 0, 4); layout.Controls.Add(_automatic, 0, 5);
        layout.Controls.Add(connection, 0, 6); layout.Controls.Add(manual, 0, 7); Controls.Add(layout);
        _loading = true;
        var settings = owner.SoftwareUpdateSettings; _automatic.Checked = settings.AutoCheck;
        _proxyMode.SelectedIndex = settings.ProxyMode switch { "direct" => 1, "custom" => 2, _ => 0 };
        _proxy.Text = settings.ProxyUrl; _loading = false;
        owner.UpdateChanged += RefreshState; FormClosed += (_, _) => owner.UpdateChanged -= RefreshState;
        FormClosing += (_, e) =>
        { if (owner.SoftwareUpdateInstalling && !owner.ClosingForSoftwareUpdate && e.CloseReason != CloseReason.FormOwnerClosing) e.Cancel = true; };
        Shown += (_, _) =>
        {
            var area = Screen.FromControl(owner).WorkingArea;
            Left = Math.Clamp(owner.Left + (owner.Width - Width) / 2, area.Left, Math.Max(area.Left, area.Right - Width));
            Top = Math.Clamp(owner.Top + (int)((owner.Height - Height) * .382), area.Top, Math.Max(area.Top, area.Bottom - Height));
        };
        RefreshState();
    }
    private void SaveSettings()
    {
        try { _owner.SaveSoftwareUpdateSettings(_automatic.Checked, _proxyMode.SelectedIndex switch { 1 => "direct", 2 => "custom", _ => "system" }, _proxy.Text); }
        catch (Exception ex) { _state.Text = ex.Message; }
    }
    private void RefreshState()
    {
        if (IsDisposed) return;
        var offer = _owner.SoftwareUpdateOffer; var busy = _owner.SoftwareUpdateBusy;
        _version.Text = "当前 v" + AppUpdate.DisplayVersion(AppUpdate.CurrentVersion) + (offer == null ? "" : "    新版 " + offer.Tag);
        _state.Text = _owner.SoftwareUpdateMessage;
        var text = offer?.Notes ?? "检查新版后，这里显示更新说明。手动下载入口始终可用。";
        if (_notes.Text != text) _notes.Text = text;
        _check.Enabled = !busy; _download.Enabled = !busy && offer != null;
        _download.Text = _owner.SoftwareUpdateReady ? "重启并更新" : "下载更新";
        _skip.Enabled = !busy && offer != null; _cancel.Visible = _owner.SoftwareUpdateDownloading;
        _later.Enabled = !_owner.SoftwareUpdateInstalling;
        _progress.Value = _owner.SoftwareUpdateProgress?.Percent ?? 0;
        _automatic.Enabled = !busy; _proxyMode.Enabled = !busy; _save.Enabled = !busy;
        _proxy.Enabled = !busy && _proxyMode.SelectedIndex == 2;
    }
}
