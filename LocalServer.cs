// ============================================================================
// LocalServer.cs — 手机端的局域网 HTTP 服务（TcpListener :8787，手写迷你 HTTP）
//
// 为什么不用 HttpListener：绑 http://*:8787/ 需要管理员权限或 netsh http add urlacl，
// 玩家机器上会弹权限窗口。TcpListener 只受防火墙提示，点一次"允许"就行。
//
// 路由（完整契约；改任何一条都要同步改 Assets/mobile/app.js）：
//   GET  /                     → index.html
//   GET  /app.js  /app.css /icons.json → 静态资源、共享矢量符号
//   GET  /api/state            → MobileState.Json（不可变快照，Cache-Control: no-store）
//   GET  /img/{目录}/{stem}     → 地图图片字节；stem **不带扩展名**（.jpg 优先，其次 .png）
//   POST /api/action           → {type,value}；解析失败返回 400 + len + raw 片段
//   GET  /favicon.ico          → 204
//
// 线程模型（最重要的一条）：
//   HTTP 线程只做三件事——读请求、序列化 State.Json、把动作塞进 Actions 队列。
//   **绝不碰 WinForms 控件**：动作由 MapForm 的 250ms 定时器取空后再执行。
//
// 其它设计取舍：
//  · 短连接（回完就关）：手机 3Hz 轮询在局域网上完全够，代码简单也不容易出粘包问题。
//  · 图片字节缓存 96 MB / 至少保留 2 张：底图 2~8 MB，来回切楼层不用重复读嵌入资源。
//  · 端口固定 8787；被占用时 Start() 返回 false，Error 里是原因，面板上显示。
// ============================================================================
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TarkovMapLite;

/// <summary>
/// 手机端用的迷你 HTTP 服务（局域网）。
///
/// 为什么自己写而不用 <c>HttpListener</c>：绑 <c>http://*:8787/</c> 需要管理员权限或
/// <c>netsh http add urlacl</c>，别人的机器上会弹权限窗口。TcpListener 只需要放行防火墙。
///
/// 只做四件事：给静态页、给地图图片、给状态快照、收反向动作。
/// 连接用的是短连接（回完就关），手机 4Hz 轮询在局域网上完全够。
/// </summary>
internal sealed class LocalServer : IDisposable
{
    public const int Port = 8787;

    private const int MaxHeader = 16 * 1024;
    private const int MaxBody = 64 * 1024;
    /// <summary>地图图片的字节缓存上限。一张底图 2~8 MB，手机来回切楼层不用反复读嵌入资源。</summary>
    private const long ImageBudget = 96L * 1024 * 1024;
    private const int ImageMinimum = 2;

    private readonly object _imageGate = new();
    private readonly Dictionary<string, byte[]> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _imageTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _imageBytes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _imageOrder = new();
    private readonly ConcurrentDictionary<string, DateTime> _clients = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>手机端发来的动作队列，UI 定时器每 250ms 取空。</summary>
    public ConcurrentQueue<MobileAction> Actions { get; } = new();

    public MobileState State { get; } = new();

    public bool Running => _listener != null;

    /// <summary>启动/停止时通知界面刷新按钮文字（停掉以后 PumpMobile 不再跑，得靠这个）。</summary>
    public event Action? StateChanged;

    /// <summary>启动失败的原因（端口被占用等），给面板显示。</summary>
    public string? Error { get; private set; }

    /// <summary>最近 60 秒内有请求的局域网设备数。</summary>
    public int Clients
    {
        get
        {
            var cutoff = DateTime.Now - TimeSpan.FromSeconds(60);
            var count = 0;
            foreach (var (address, seen) in _clients)
            {
                if (seen >= cutoff)
                {
                    count++;
                }
                else
                {
                    _clients.TryRemove(address, out _);
                }
            }
            return count;
        }
    }

    /// <summary>手机要打开的地址：http://&lt;局域网 IP&gt;:8787/</summary>
    public string Address => $"http://{LanAddress()}:{Port}/";

    public bool Start()
    {
        if (_listener != null)
        {
            return true;
        }
        Error = null;
        try
        {
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Start(64);
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException)
        {
            _listener = null;
            Error = ex.Message;
            return false;
        }
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        StateChanged?.Invoke();
        return true;
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经停过了
        }
        try
        {
            _listener?.Stop();
        }
        catch (SocketException)
        {
            // 关监听时偶尔会抛，不影响
        }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
        _loop = null;
        _clients.Clear();
        lock (_imageGate)
        {
            _images.Clear();
            _imageTypes.Clear();
            _imageBytes.Clear();
            _imageOrder.Clear();
        }
        StateChanged?.Invoke();
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }
                // 端口被系统暂时收回之类，别把整个循环打死
                await Task.Delay(120, token).ConfigureAwait(false);
                continue;
            }
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            client.NoDelay = true;
            using var _ = client;
            using var stream = client.GetStream();
            stream.ReadTimeout = 5000;
            stream.WriteTimeout = 15000;

            var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
            _clients[remote] = DateTime.Now;

            var head = await ReadHeadAsync(stream).ConfigureAwait(false);
            if (head == null)
            {
                return;
            }
            var header = head.Value.Head;
            var requestLine = header.Split("\r\n", 2)[0].Split(' ');
            if (requestLine.Length < 2)
            {
                await WriteAsync(stream, 400, "Bad Request", "text/plain", Encoding.ASCII.GetBytes("bad request"))
                    .ConfigureAwait(false);
                return;
            }
            var method = requestLine[0];
            var path = requestLine[1];
            var query = path.IndexOf('?');
            if (query >= 0)
            {
                path = path[..query];
            }

            if (method == "GET")
            {
                switch (path)
                {
                    case "/":
                    case "/index.html":
                        await ServeTextAsync(stream, "index.html", "text/html; charset=utf-8").ConfigureAwait(false);
                        return;
                    case "/icons.json":
                        await ServeTextAsync(stream, "icons.json", "application/json; charset=utf-8").ConfigureAwait(false);
                        break;
                    case "/app.js":
                        await ServeTextAsync(stream, "app.js", "text/javascript; charset=utf-8").ConfigureAwait(false);
                        return;
                    case "/app.css":
                        await ServeTextAsync(stream, "app.css", "text/css; charset=utf-8").ConfigureAwait(false);
                        return;
                    case "/api/state":
                        await WriteAsync(stream, 200, "OK", "application/json; charset=utf-8",
                            Encoding.UTF8.GetBytes(State.Json), "Cache-Control: no-store\r\n").ConfigureAwait(false);
                        return;
                    case "/favicon.ico":
                        await WriteAsync(stream, 204, "No Content", "text/plain", Array.Empty<byte>())
                            .ConfigureAwait(false);
                        return;
                }
                if (path.StartsWith("/img/", StringComparison.Ordinal))
                {
                    await ServeImageAsync(stream, path[5..]).ConfigureAwait(false);
                    return;
                }
            }
            else if (method == "POST" && path == "/api/action")
            {
                var body = await ReadBodyAsync(stream, header, head.Value.Leftover).ConfigureAwait(false);
                var action = ParseAction(body);
                if (action == null)
                {
                    // 返回 400 而不是假装成功：手机端上能看到原因，也好排查
                    var snippet = body.Length > 80 ? body[..80] : body;
                    snippet = snippet.Replace("\\", "").Replace("\"", "'")
                        .Replace("\r", "").Replace("\n", "");
                    await WriteAsync(stream, 400, "Bad Request", "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes(
                            $"{{\"ok\":false,\"reason\":\"bad action\",\"len\":{body.Length},\"raw\":\"{snippet}\"}}"),
                        "Cache-Control: no-store\r\n").ConfigureAwait(false);
                    return;
                }
                Actions.Enqueue(action);
                await WriteAsync(stream, 200, "OK", "application/json; charset=utf-8",
                    Encoding.UTF8.GetBytes($"{{\"ok\":true,\"type\":\"{action.Type}\",\"len\":{body.Length}}}"),
                    "Cache-Control: no-store\r\n").ConfigureAwait(false);
                return;
            }

            await WriteAsync(stream, 404, "Not Found", "text/plain; charset=utf-8",
                Encoding.UTF8.GetBytes("not found")).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException
                                       or OperationCanceledException or TimeoutException)
        {
            // 手机切后台、轮询被打断都会走到这里，正常现象
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    /// <summary>
    /// 读到空行为止（含 Content-Length 之类的头）。返回头文本 + 同一次读取里多出来的字节，
    /// 后者通常是 POST 的 body——不交出来就会被丢掉，动作接口永远收不到东西。
    /// </summary>
    private static async Task<(string Head, byte[] Leftover)?> ReadHeadAsync(NetworkStream stream)
    {
        var buffer = new byte[2048];
        var accumulated = new List<byte>(1024);
        while (accumulated.Count < MaxHeader)
        {
            var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (read <= 0)
            {
                return null;
            }
            var end = -1;
            for (var i = 0; i < read; i++)
            {
                accumulated.Add(buffer[i]);
                var count = accumulated.Count;
                if (count >= 4
                    && accumulated[count - 1] == (byte)'\n'
                    && accumulated[count - 2] == (byte)'\r'
                    && accumulated[count - 3] == (byte)'\n'
                    && accumulated[count - 4] == (byte)'\r')
                {
                    end = i + 1;
                    break;
                }
            }
            if (end >= 0)
            {
                var head = Encoding.ASCII.GetString(accumulated.ToArray());
                var leftover = new byte[read - end];
                Array.Copy(buffer, end, leftover, 0, leftover.Length);
                return (head, leftover);
            }
        }
        return null;
    }

    private static async Task<string> ReadBodyAsync(NetworkStream stream, string header, byte[] leftover)
    {
        var length = 0;
        foreach (var line in header.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line[15..].Trim(), out var parsed))
            {
                length = Math.Clamp(parsed, 0, MaxBody);
            }
        }
        if (length == 0)
        {
            return "";
        }
        var body = new byte[length];
        var offset = 0;
        if (leftover.Length > 0)
        {
            offset = Math.Min(leftover.Length, length);
            Array.Copy(leftover, body, offset);
        }
        while (offset < length)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset, length - offset)).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }
            offset += read;
        }
        return Encoding.UTF8.GetString(body, 0, offset);
    }

    private static MobileAction? ParseAction(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeNode) ? typeNode.GetString() ?? "" : "";
            var value = "";
            foreach (var key in new[] { "value", "svgLayer", "taskId", "key" })
            {
                if (root.TryGetProperty(key, out var node) && node.ValueKind == JsonValueKind.String)
                {
                    value = node.GetString() ?? "";
                    break;
                }
            }
            if (type.Length == 0)
            {
                return null;
            }
            return new MobileAction { Type = type, Value = value };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task ServeTextAsync(NetworkStream stream, string name, string contentType)
    {
        var text = MobileAssets.Text(name);
        if (text == null)
        {
            await WriteAsync(stream, 404, "Not Found", "text/plain; charset=utf-8",
                Encoding.UTF8.GetBytes($"missing {name}")).ConfigureAwait(false);
            return;
        }
        await WriteAsync(stream, 200, "OK", contentType, Encoding.UTF8.GetBytes(text),
            "Cache-Control: no-cache\r\n").ConfigureAwait(false);
    }

    /// <summary>路径是 /img/{地图目录}/{图片名}，图片名不带扩展名（.jpg 优先，其次 .png）。</summary>
    private async Task ServeImageAsync(NetworkStream stream, string relative)
    {
        var parts = relative.Split('/');
        if (parts.Length != 2 || !IsSafeSegment(parts[0]) || !IsSafeSegment(parts[1]))
        {
            await WriteAsync(stream, 400, "Bad Request", "text/plain", Array.Empty<byte>()).ConfigureAwait(false);
            return;
        }
        var key = parts[0] + "/" + parts[1];
        byte[]? bytes;
        string? contentType;
        lock (_imageGate)
        {
            if (_images.TryGetValue(key, out var cached) && _imageTypes.TryGetValue(key, out var cachedType))
            {
                TouchImage(key);
                bytes = cached;
                contentType = cachedType;
            }
            else
            {
                bytes = null;
                contentType = null;
            }
        }
        if (bytes == null || contentType == null)
        {
            // 瓦片底图是 .jpg（裁边压缩过），楼层图和 SVG 渲染图是 .png
            foreach (var extension in new[] { ".jpg", ".png" })
            {
                var data = ReadAsset("maps/" + key + extension);
                if (data == null)
                {
                    continue;
                }
                bytes = data;
                contentType = extension == ".jpg" ? "image/jpeg" : "image/png";
                CacheImage(key, data, contentType);
                break;
            }
        }
        if (bytes == null || contentType == null)
        {
            await WriteAsync(stream, 404, "Not Found", "text/plain; charset=utf-8",
                Encoding.UTF8.GetBytes("no image " + key)).ConfigureAwait(false);
            return;
        }
        await WriteAsync(stream, 200, "OK", contentType, bytes, "Cache-Control: max-age=86400\r\n")
            .ConfigureAwait(false);
    }

    private static byte[]? ReadAsset(string relative)
    {
        using var stream = Assets.Open(relative);
        if (stream == null)
        {
            return null;
        }
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>只允许字母数字和 . _ -，顺便挡掉 ../ 之类的穿越。</summary>
    private static bool IsSafeSegment(string segment)
    {
        if (segment.Length is 0 or > 64)
        {
            return false;
        }
        foreach (var c in segment)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '.' && c != '_' && c != '-')
            {
                return false;
            }
        }
        return segment != "." && segment != "..";
    }

    private void CacheImage(string key, byte[] bytes, string contentType)
    {
        lock (_imageGate)
        {
            // 停止以后才完成的请求不再把已清空的缓存重新填满。
            if (!Running) return;
            _images[key] = bytes;
            _imageTypes[key] = contentType;
            _imageBytes[key] = bytes.Length;
            TouchImage(key);
            while (_imageOrder.Count > ImageMinimum)
            {
                long total = 0;
                foreach (var path in _imageOrder)
                {
                    total += _imageBytes.TryGetValue(path, out var size) ? size : 0;
                }
                if (total <= ImageBudget)
                {
                    break;
                }
                var victim = _imageOrder[0];
                _imageOrder.RemoveAt(0);
                _imageBytes.Remove(victim);
                _imageTypes.Remove(victim);
                _images.Remove(victim);
            }
        }
    }

    private void TouchImage(string key)
    {
        if (_imageOrder.Count > 0 && _imageOrder[^1] == key)
        {
            return;
        }
        _imageOrder.Remove(key);
        _imageOrder.Add(key);
    }

    private static async Task WriteAsync(NetworkStream stream, int code, string reason,
        string contentType, byte[] body, string extraHeaders = "")
    {
        var header = new StringBuilder(160)
            .Append("HTTP/1.1 ").Append(code).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Type: ").Append(contentType).Append("\r\n")
            .Append("Content-Length: ").Append(body.Length).Append("\r\n")
            .Append("Connection: close\r\n")
            .Append(extraHeaders)
            .Append("\r\n");
        var head = Encoding.ASCII.GetBytes(header.ToString());
        await stream.WriteAsync(head).ConfigureAwait(false);
        if (body.Length > 0)
        {
            await stream.WriteAsync(body).ConfigureAwait(false);
        }
        await stream.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 本机局域网地址：优先 192.168 / 10 / 172.16-31 的网卡，跳过虚拟网卡和回环。
    /// 找不到就退回 127.0.0.1（手机连不上，面板上会提示）。
    /// </summary>
    public static string LanAddress()
    {
        var candidates = new List<(int Score, string Address)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }
                var name = nic.Name;
                var virtualNic = name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase)
                                 || name.Contains("VMware", StringComparison.OrdinalIgnoreCase)
                                 || name.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
                                 || name.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
                                 || name.Contains("Tailscale", StringComparison.OrdinalIgnoreCase)
                                 || name.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase)
                                 || name.Contains("Radmin", StringComparison.OrdinalIgnoreCase);
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork
                        || IPAddress.IsLoopback(unicast.Address))
                    {
                        continue;
                    }
                    var text = unicast.Address.ToString();
                    var score = text.StartsWith("192.168.", StringComparison.Ordinal) ? 40
                        : text.StartsWith("10.", StringComparison.Ordinal) ? 30
                        : IsPrivate172(text) ? 20
                        : 0;
                    if (score == 0)
                    {
                        continue;
                    }
                    if (virtualNic)
                    {
                        score -= 15;
                    }
                    candidates.Add((score, text));
                }
            }
        }
        catch (NetworkInformationException)
        {
            // 取网卡信息失败不致命
        }
        if (candidates.Count == 0)
        {
            return "127.0.0.1";
        }
        return candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Address, StringComparer.Ordinal)
            .First().Address;
    }

    private static bool IsPrivate172(string address)
    {
        if (!address.StartsWith("172.", StringComparison.Ordinal))
        {
            return false;
        }
        var dot = address.IndexOf('.', 4);
        return dot > 4 && int.TryParse(address[4..dot], out var second) && second is >= 16 and <= 31;
    }

    public void Dispose()
    {
        Stop();
    }
}
