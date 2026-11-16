using System.Diagnostics;

namespace TarkovMapLite;

internal sealed partial class MapForm
{
    private readonly UiButton _updateButton = new();
    private readonly CancellationTokenSource _updateLifetime = new();
    private CancellationTokenSource? _downloadCancel;
    private UpdateSettings _updateSettings = UpdateSettings.Load();
    private ReleaseOffer? _updateOffer;
    private UpdateDownload? _updateDownload;
    private UpdateTicket? _updateTicket;
    private UpdateForm? _updateForm;
    private bool _updateBusy, _closingForUpdate, _updateInstalling;
    private string _updateMessage = "启动后会在后台检查新版，也可以手动检查。";
    private UpdateProgress? _updateProgress;
    internal event Action? UpdateChanged;
    internal UpdateSettings SoftwareUpdateSettings => _updateSettings;
    internal ReleaseOffer? SoftwareUpdateOffer => _updateOffer;
    internal bool SoftwareUpdateBusy => _updateBusy;
    internal bool SoftwareUpdateReady => _updateDownload != null;
    internal bool SoftwareUpdateDownloading => _downloadCancel != null;
    internal bool SoftwareUpdateInstalling => _updateInstalling;
    internal bool ClosingForSoftwareUpdate => _closingForUpdate;
    internal string SoftwareUpdateMessage => _updateMessage;
    internal UpdateProgress? SoftwareUpdateProgress => _updateProgress;

    private void BuildUpdateButton(Control bottom)
    {
        _updateButton.Text = "软件更新"; _updateButton.Dock = DockStyle.Right; _updateButton.Width = 112;
        _updateButton.Font = new Font("Microsoft YaHei UI", UiTokens.FontCaption);
        _updateButton.Click += (_, _) => ShowSoftwareUpdates(); bottom.Controls.Add(_updateButton);
        var outcome = UpdateInstaller.LastOutcome();
        if (outcome != null) _updateMessage = outcome.Message;
        Shown += async (_, _) =>
        {
            try
            {
                await Task.Delay(4000, _updateLifetime.Token);
                _ = Task.Run(UpdateFiles.CleanOldSessions);
                if (_updateSettings.AutoCheck) await CheckSoftwareUpdateAsync(false);
            }
            catch (OperationCanceledException) { }
        };
        FormClosing += (_, _) =>
        {
            if (!_closingForUpdate) _updateTicket?.Cancel();
            _updateLifetime.Cancel(); _downloadCancel?.Cancel();
        };
        FormClosed += (_, _) =>
        {
            if (!_closingForUpdate && _updateDownload != null) UpdateFiles.CleanSession(_updateDownload.Directory);
        };
    }
    private void NotifySoftwareUpdate()
    {
        if (IsDisposed || Disposing) return;
        _updateButton.Text = _downloadCancel != null && _updateProgress != null ? $"更新 {_updateProgress.Percent}%" :
            _updateDownload != null ? "更新已就绪" : _updateOffer != null && _updateSettings.SkippedVersion != _updateOffer.Tag
                ? "新版 " + AppUpdate.DisplayVersion(_updateOffer.Version) : "软件更新";
        UpdateChanged?.Invoke();
    }
    internal void ShowSoftwareUpdates()
    {
        if (_updateForm is { IsDisposed: false }) { _updateForm.Activate(); return; }
        _updateForm = new UpdateForm(this); _updateForm.Show(this);
    }
    internal async Task CheckSoftwareUpdateAsync(bool manual)
    {
        if (_updateBusy || IsDisposed) return;
        _updateBusy = true; _updateMessage = "正在检查软件新版…"; NotifySoftwareUpdate();
        try
        {
            using var service = new AppUpdate(_updateSettings);
            var result = await service.CheckAsync(AppUpdate.CurrentVersion, _updateLifetime.Token);
            if (IsDisposed) return;
            _updateSettings = _updateSettings with { LastCheck = DateTimeOffset.Now }; _updateSettings.Save();
            if (_updateDownload != null && _updateDownload.Offer.Tag != result.Offer?.Tag)
            { UpdateFiles.CleanSession(_updateDownload.Directory); _updateDownload = null; }
            _updateOffer = result.Offer;
            if (manual && _updateOffer != null && _updateSettings.SkippedVersion == _updateOffer.Tag)
            { _updateSettings = _updateSettings with { SkippedVersion = "" }; _updateSettings.Save(); }
            _updateMessage = _updateOffer == null ? $"当前版本无需更新。最新正式发布：{result.LatestTag}。" :
                _updateSettings.SkippedVersion == _updateOffer.Tag ? $"已跳过 {_updateOffer.Tag}，其他版本仍会提示。" :
                $"发现 {_updateOffer.Tag}，可下载更新。下载不会关闭当前程序。";
        }
        catch (OperationCanceledException) when (_updateLifetime.IsCancellationRequested) { }
        catch (Exception ex) { _updateMessage = "检查未完成：" + FriendlyUpdateError(ex); }
        finally { _updateBusy = false; NotifySoftwareUpdate(); }
    }
    internal async Task DownloadSoftwareUpdateAsync()
    {
        if (_updateBusy || _updateOffer == null || _updateDownload != null) return;
        _updateBusy = true; _updateProgress = new(0, _updateOffer.Size);
        _downloadCancel = CancellationTokenSource.CreateLinkedTokenSource(_updateLifetime.Token);
        _updateMessage = "正在下载新版；可以取消，也可以关闭此面板让下载继续。"; NotifySoftwareUpdate();
        try
        {
            using var service = new AppUpdate(_updateSettings);
            _updateDownload = await service.DownloadAsync(_updateOffer, new Progress<UpdateProgress>(value =>
            { _updateProgress = value; NotifySoftwareUpdate(); }), _downloadCancel.Token);
            _updateMessage = "下载及校验完成。点击“重启并更新”后才会关闭程序并替换。";
        }
        catch (OperationCanceledException) { _updateMessage = "下载已取消，当前程序可继续使用。"; }
        catch (Exception ex) { _updateMessage = "下载未完成：" + FriendlyUpdateError(ex); }
        finally { _downloadCancel?.Dispose(); _downloadCancel = null; _updateBusy = false; NotifySoftwareUpdate(); }
    }
    internal void CancelSoftwareDownload() => _downloadCancel?.Cancel();
    internal void SkipSoftwareVersion()
    {
        if (_updateBusy || _updateOffer == null) return;
        _updateSettings = _updateSettings with { SkippedVersion = _updateOffer.Tag }; _updateSettings.Save();
        if (_updateDownload != null) { UpdateFiles.CleanSession(_updateDownload.Directory); _updateDownload = null; }
        _updateMessage = "已跳过 " + _updateOffer.Tag + "，可以手动检查重新显示。"; NotifySoftwareUpdate();
    }
    internal void SaveSoftwareUpdateSettings(bool automatic, string mode, string proxy)
    {
        if (_updateBusy) throw new InvalidOperationException("当前正在检查或下载，请完成后再修改连接设置。");
        if (mode == "custom") proxy = UpdateSettings.ValidateProxy(proxy).AbsoluteUri;
        _updateSettings = _updateSettings with { AutoCheck = automatic, ProxyMode = mode, ProxyUrl = proxy.Trim() };
        _updateSettings.Save(); _updateMessage = "更新设置已保存；代理仅用于软件版本检查与程序下载。"; NotifySoftwareUpdate();
    }
    internal void SetSoftwareAutoCheck(bool value)
    {
        _updateSettings = _updateSettings with { AutoCheck = value }; _updateSettings.Save();
    }
    internal void SoftwareUpdateBooted()
    {
        _updateMessage = "已启动新版 v" + AppUpdate.DisplayVersion(AppUpdate.CurrentVersion) + "。设置与手动标记已保留。";
        NotifySoftwareUpdate();
    }
    internal async Task InstallSoftwareUpdateAsync()
    {
        if (_updateBusy || _updateDownload == null) return;
        _updateBusy = true; _updateInstalling = true; _updateMessage = "正在准备更新，准备失败时当前程序会保持运行。"; NotifySoftwareUpdate();
        try
        {
            using var parent = Process.GetCurrentProcess();
            var target = Environment.ProcessPath ?? throw new IOException("无法确定当前程序路径，请手动下载更新。");
            _updateTicket = await Task.Run(() => UpdateInstaller.Prepare(_updateDownload, target, parent, target));
            _updateLifetime.Token.ThrowIfCancellationRequested();
            await UpdateInstaller.StartHelperAsync(_updateTicket, _updateLifetime.Token);
            _closingForUpdate = true; Close();
        }
        catch (OperationCanceledException) { _updateTicket?.Cancel(); }
        catch (Exception ex) { _updateTicket?.Cancel(); _updateMessage = "无法开始更新：" + FriendlyUpdateError(ex); }
        finally { _updateBusy = false; _updateInstalling = false; NotifySoftwareUpdate(); }
    }
    private static string FriendlyUpdateError(Exception error) => error is OperationCanceledException
        ? "连接超时，可检查代理设置后重试，或打开下载页面。" : error.Message;
}
