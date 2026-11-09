// ============================================================================
// MobileForm.cs — 「手机端」面板：局域网地址 + 二维码 + 已连接台数
//
// 服务本体挂在 MapForm 上（关掉这个面板**不会**断开手机），这里只是展示 + 开关。
// 二维码用 NuGet QRCoder 的 PngByteQRCode：纯托管、不依赖 GDI+ 编码器；
// 生成失败也只是没有图，地址照旧可手打（见 ShowQr 的 catch）。
//
// 坑：StartPosition=CenterParent 在非模态窗口上会算到屏幕外（实测 x=-158），
//     所以定位由 MapForm.ShowMobilePanel 用 Manual + WorkingArea 手动算。别改回去。
//     另外面板不显示在任务栏、关掉只销毁自己，服务继续跑。
// ============================================================================
using System.Runtime.InteropServices;
using QRCoder;

namespace TarkovMapLite;

/// <summary>
/// 「手机端」面板：显示局域网地址和二维码，手机扫一下就能进页面。
/// 服务本身挂在主窗口上，关掉这个面板不会断开手机。
/// </summary>
internal sealed class MobileForm : Form
{
    private static readonly Color PanelBack = Color.FromArgb(22, 25, 29);
    private static readonly Color TextMain = Color.FromArgb(232, 238, 245);
    private static readonly Color TextDim = Color.FromArgb(150, 160, 172);

    private readonly LocalServer _server;
    private readonly Label _status = new();
    private readonly TextBox _address = new();
    private readonly PictureBox _qr = new();
    private readonly Label _hint = new();
    private readonly Label _clients = new();
    private readonly Button _toggle = new();
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 500 };
    private string _shownAddress = "";

    public MobileForm(LocalServer server)
    {
        _server = server;
        Text = "手机端";
        BackColor = PanelBack;
        ForeColor = TextMain;
        ClientSize = new Size(332, 470);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9f);

        var title = new Label
        {
            Text = "手机端",
            Location = new Point(16, 14),
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold),
            ForeColor = TextMain,
        };
        _status.Location = new Point(120, 17);
        _status.AutoSize = true;
        _status.ForeColor = TextDim;

        var lead = new Label
        {
            Text = "手机浏览器打开下面这个地址：",
            Location = new Point(16, 44),
            AutoSize = true,
            ForeColor = TextDim,
        };

        _address.Location = new Point(16, 66);
        _address.Size = new Size(226, 26);
        _address.ReadOnly = true;
        _address.BorderStyle = BorderStyle.FixedSingle;
        _address.BackColor = Color.FromArgb(30, 34, 41);
        _address.ForeColor = Color.FromArgb(140, 190, 255);
        _address.Font = new Font("Consolas", 10f);
        _address.Text = "-";

        var copy = new Button
        {
            Text = "复制",
            Location = new Point(250, 66),
            Size = new Size(62, 26),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(34, 39, 46),
            ForeColor = TextMain,
            Font = new Font("Microsoft YaHei UI", 8.5f),
        };
        copy.FlatAppearance.BorderColor = Color.FromArgb(44, 49, 56);
        copy.Click += (_, _) => CopyAddress();

        _qr.Location = new Point(56, 104);
        _qr.Size = new Size(220, 220);
        _qr.SizeMode = PictureBoxSizeMode.Zoom;
        _qr.BackColor = Color.FromArgb(13, 15, 17);

        _hint.Location = new Point(16, 334);
        _hint.Size = new Size(300, 62);
        _hint.ForeColor = TextDim;
        _hint.Font = new Font("Microsoft YaHei UI", 8.5f);
        _hint.Text = "手机和电脑要在同一个 WiFi 下。\r\n"
                     + "第一次运行可能会弹防火墙提示，要允许「专用网络」，"
                     + "否则手机连不上。";

        _clients.Location = new Point(16, 400);
        _clients.Size = new Size(190, 24);
        _clients.ForeColor = TextDim;

        _toggle.Location = new Point(214, 398);
        _toggle.Size = new Size(102, 28);
        _toggle.FlatStyle = FlatStyle.Flat;
        _toggle.BackColor = Color.FromArgb(34, 39, 46);
        _toggle.ForeColor = TextMain;
        _toggle.FlatAppearance.BorderColor = Color.FromArgb(44, 49, 56);
        _toggle.Click += (_, _) => ToggleServer();

        Controls.AddRange(new Control[] { title, _status, lead, _address, copy, _qr, _hint, _clients, _toggle });

        _tick.Tick += (_, _) => RefreshState();
        _tick.Start();
        Load += (_, _) => RefreshState();
        FormClosed += (_, _) => _tick.Stop();
    }

    private void ToggleServer()
    {
        if (_server.Running)
        {
            _server.Stop();
            _qr.Image?.Dispose();
            _qr.Image = null;
            _shownAddress = "";
        }
        else
        {
            _server.Start();
        }
        RefreshState();
    }

    private void RefreshState()
    {
        if (_server.Running)
        {
            _status.Text = "运行中";
            _status.ForeColor = Color.FromArgb(120, 200, 140);
            _toggle.Text = "停止服务";
            _clients.Text = _server.Clients > 0 ? $"已连接 {_server.Clients} 台" : "等待手机连接…";
            var address = _server.Address;
            if (address != _shownAddress)
            {
                _shownAddress = address;
                _address.Text = address;
                ShowQr(address);
            }
            if (_address.Text != address)
            {
                _address.Text = address;
            }
        }
        else
        {
            _status.Text = _server.Error is { Length: > 0 } error ? "启动失败" : "已停止";
            _status.ForeColor = Color.FromArgb(220, 130, 130);
            _toggle.Text = "启动服务";
            _clients.Text = _server.Error is { Length: > 0 } message
                ? message.Length > 34 ? message[..34] + "…" : message
                : "端口 " + LocalServer.Port;
            _address.Text = "-";
            _shownAddress = "";
        }
    }

    private void ShowQr(string address)
    {
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(address, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data).GetGraphic(8);
            using var stream = new MemoryStream(png);
            _qr.Image?.Dispose();
            _qr.Image = Image.FromStream(stream);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                       or ExternalException or IOException)
        {
            // 二维码生成失败不影响用：地址就在上面，手打也行
            _qr.Image?.Dispose();
            _qr.Image = null;
            _hint.Text = "二维码生成失败（" + ex.GetType().Name + "），请手动输入上面的地址。";
        }
    }

    private void CopyAddress()
    {
        var text = _address.Text;
        if (string.IsNullOrWhiteSpace(text) || text == "-")
        {
            return;
        }
        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException)
        {
            // 剪贴板偶尔被别的程序占着，忽略
        }
    }
}
