using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TarkovMapLite;

internal sealed record UpdateSettings
{
    public bool AutoCheck { get; init; } = true;
    public string ProxyMode { get; init; } = "system";
    public string ProxyUrl { get; init; } = "";
    public string SkippedVersion { get; init; } = "";
    public DateTimeOffset? LastCheck { get; init; }
    public static UpdateSettings Load()
    {
        var saved = PlayerLocator.GetSetting("updates");
        if (string.IsNullOrWhiteSpace(saved)) return new();
        try { return JsonSerializer.Deserialize<UpdateSettings>(saved) ?? new(); }
        catch (JsonException) { return new(); }
    }
    public void Save() => PlayerLocator.SetSetting("updates", JsonSerializer.Serialize(this));
    public static Uri ValidateProxy(string text)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) || uri.Port is < 1 or > 65535 ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new InvalidDataException("代理地址请使用 http://127.0.0.1:7890 这样的格式，不包含账号密码或路径。");
        return uri;
    }
}

internal sealed record ReleaseOffer(string Tag, Version Version, string Notes, Uri Page, Uri Download,
    long Size, string? Sha256, Uri? Checksum);
internal sealed record UpdateCheck(string LatestTag, ReleaseOffer? Offer);
internal sealed record UpdateProgress(long Received, long Total)
{
    public int Percent => Total <= 0 ? 0 : (int)Math.Min(100, Received * 100 / Total);
}
internal sealed record UpdateDownload(string Directory, string Payload, ReleaseOffer Offer, string Sha256);

/// <summary>Public GitHub releases only. Stream the executable; no token, script, or full-file allocation.</summary>
internal sealed class AppUpdate : IDisposable
{
    public const string Repository = "YIATOwin/TarkovMapLite-release";
    public const string AssetName = "TarkovMapLite.exe";
    public const string ReleasePage = "https://github.com/" + Repository + "/releases/latest";
    public const string LatestApi = "https://api.github.com/repos/" + Repository + "/releases/latest";
    public const long MaximumSize = 512L * 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http;
    public static Version CurrentVersion => Normalize(typeof(AppUpdate).Assembly.GetName().Version ?? new Version(0, 0, 0));
    public static Version Normalize(Version value) => new(value.Major, value.Minor, Math.Max(0, value.Build), Math.Max(0, value.Revision));
    public static string DisplayVersion(Version value) => value.Revision > 0 ? value.ToString(4) : value.ToString(3);
    public static Version ParseVersion(string text)
    {
        if (!Regex.IsMatch(text, @"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(\.(0|[1-9]\d*))?$", RegexOptions.CultureInvariant) ||
            !Version.TryParse(text.TrimStart('v'), out var value)) throw new InvalidDataException("发布版本号格式不受支持。");
        return Normalize(value);
    }
    public AppUpdate(UpdateSettings settings, HttpMessageHandler? handler = null)
    {
        if (handler == null)
        {
            var network = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None };
            if (settings.ProxyMode == "direct") network.UseProxy = false;
            else if (settings.ProxyMode == "custom") network.Proxy = new WebProxy(UpdateSettings.ValidateProxy(settings.ProxyUrl));
            _http = new(network);
        }
        else _http = new(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TarkovMapLite/" + DisplayVersion(CurrentVersion));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
    }
    public async Task<UpdateCheck> CheckAsync(Version current, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var text = await ReadTextAsync(new Uri(LatestApi), 512 * 1024, timeout.Token);
            return ParseRelease(text, current);
        }
        catch (Exception ex) when ((ex is HttpRequestException or OperationCanceledException) && !cancel.IsCancellationRequested)
        {
            // Shared proxy addresses can exhaust GitHub's anonymous API quota.
            // /releases/latest redirects to the formal release; SHA and HEAD verify its assets.
            using var fallback = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            fallback.CancelAfter(TimeSpan.FromSeconds(25));
            using var pageResponse = await GetAsync(new Uri(ReleasePage), fallback.Token);
            var uri = pageResponse.RequestMessage?.RequestUri ?? throw new InvalidDataException("无法识别正式发布页面。");
            var prefix = "/" + Repository + "/releases/tag/";
            if (uri.Host != "github.com" || !uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("发布页面没有返回正式版本号。");
            var tag = Uri.UnescapeDataString(uri.AbsolutePath[prefix.Length..]); var version = ParseVersion(tag);
            if (version <= Normalize(current)) return new(tag, null);
            var asset = new Uri("https://github.com/" + Repository + "/releases/download/" + Uri.EscapeDataString(tag) + "/" + AssetName);
            var checksum = new Uri(asset.AbsoluteUri + ".sha256");
            var hash = ParseChecksum(await ReadTextAsync(checksum, 4096, fallback.Token));
            using var head = await GetAsync(asset, fallback.Token, HttpMethod.Head);
            var size = head.Content.Headers.ContentLength;
            if (size is not > 0 or > MaximumSize) throw new InvalidDataException("无法确认新版文件大小，请打开下载页面手动更新。");
            return new(tag, new(tag, version, "当前网络无法读取完整更新说明。请点击“打开下载页面”查看该版本详情。", uri, asset, size.Value, hash, checksum));
        }
    }
    public static UpdateCheck ParseRelease(string text, Version current)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("当前发布不是正式版本。");
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = ParseVersion(tag);
        if (version <= Normalize(current)) return new(tag, null);
        var page = new Uri("https://github.com/" + Repository + "/releases/tag/" + Uri.EscapeDataString(tag));
        var assets = root.GetProperty("assets").EnumerateArray().ToArray();
        var executable = assets.Where(a => a.GetProperty("name").GetString() == AssetName &&
            a.TryGetProperty("state", out var state) && state.GetString() == "uploaded").ToArray();
        if (executable.Length != 1) throw new InvalidDataException("新版程序尚未完整上传，请稍后重试或打开下载页面。");
        var asset = executable[0];
        var size = asset.GetProperty("size").GetInt64();
        if (size <= 0 || size > MaximumSize) throw new InvalidDataException("新版文件大小不符合更新要求。");
        Uri AssetUrl(JsonElement item, string name)
        {
            var uri = new Uri(item.GetProperty("browser_download_url").GetString() ?? "");
            var expected = "/" + Repository + "/releases/download/" + Uri.EscapeDataString(tag) + "/" + name;
            if (uri.Scheme != "https" || uri.Host != "github.com" || uri.Port != 443 || uri.UserInfo.Length > 0 || uri.Query.Length > 0 ||
                uri.Fragment.Length > 0 || !uri.AbsolutePath.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新文件不属于当前项目的正式发布。");
            return uri;
        }
        var download = AssetUrl(asset, AssetName);
        var digest = asset.TryGetProperty("digest", out var digestNode) ? digestNode.GetString() : null;
        string? hash = digest?.StartsWith("sha256:", StringComparison.Ordinal) == true ? digest[7..] : null;
        if (hash != null && !Regex.IsMatch(hash, "^[0-9a-fA-F]{64}$")) throw new InvalidDataException("发布文件校验值格式错误。");
        Uri? checksum = null;
        var checksums = assets.Where(a => a.GetProperty("name").GetString() == AssetName + ".sha256" &&
            a.TryGetProperty("state", out var state) && state.GetString() == "uploaded").ToArray();
        if (checksums.Length == 1) checksum = AssetUrl(checksums[0], AssetName + ".sha256");
        if (hash == null && checksum == null) throw new InvalidDataException("该版本没有提供文件校验信息，请通过下载页面手动更新。");
        var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        if (notes.Length > 20000) notes = notes[..20000] + "\n…更多内容请查看发布页面。";
        return new(tag, new(tag, version, notes, page, download, size, hash?.ToLowerInvariant(), checksum));
    }
    private static void ValidateNetworkUri(Uri uri)
    {
        if (uri.Scheme != "https" || uri.UserInfo.Length > 0 || uri.Port != 443 || uri.Fragment.Length > 0 ||
            !new[] { "api.github.com", "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com", "github-releases.githubusercontent.com" }.Contains(uri.Host))
            throw new InvalidDataException("更新下载跳转到了不受支持的地址。");
    }
    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancel, HttpMethod? method = null)
    {
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            ValidateNetworkUri(uri);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var request = new HttpRequestMessage(method ?? HttpMethod.Get, uri);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.RequestMessage ??= request;
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var next = response.Headers.Location; response.Dispose();
                if (next == null) throw new InvalidDataException("下载地址跳转缺少目标。");
                uri = next.IsAbsoluteUri ? next : new Uri(uri, next); continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode; response.Dispose();
                throw new HttpRequestException($"更新服务返回 HTTP {code}，可检查代理设置或打开下载页面。");
            }
            return response;
        }
        throw new InvalidDataException("更新下载跳转次数过多。");
    }
    private async Task<string> ReadTextAsync(Uri uri, int max, CancellationToken cancel)
    {
        using var response = await GetAsync(uri, cancel);
        await using var stream = await response.Content.ReadAsStreamAsync(cancel);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancel)) > 0)
        {
            if (memory.Length + count > max) throw new InvalidDataException("更新信息过大。");
            memory.Write(buffer, 0, count);
        }
        return System.Text.Encoding.UTF8.GetString(memory.ToArray());
    }
    public async Task<UpdateDownload> DownloadAsync(ReleaseOffer offer, IProgress<UpdateProgress>? progress, CancellationToken cancel)
    {
        var directory = UpdateFiles.CreateSession();
        var part = Path.Combine(directory, "payload.part");
        try
        {
            var hash = offer.Sha256;
            if (hash == null)
            {
                using var textTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                textTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                var checksum = await ReadTextAsync(offer.Checksum!, 4096, textTimeout.Token);
                hash = ParseChecksum(checksum);
            }
            using var response = await GetAsync(offer.Download, cancel);
            if (response.Content.Headers.ContentLength is { } length && length != offer.Size)
                throw new InvalidDataException("服务器返回的文件大小与发布信息不一致。");
            await using var source = await response.Content.ReadAsStreamAsync(cancel);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var destination = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[65536]; long received = 0;
                var watch = Stopwatch.StartNew(); long previous = -250;
                while (true)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    idle.CancelAfter(TimeSpan.FromSeconds(30));
                    var count = await source.ReadAsync(buffer, idle.Token);
                    if (count == 0) break;
                    received += count;
                    if (received > offer.Size) throw new InvalidDataException("下载数据超过发布文件大小。");
                    digest.AppendData(buffer, 0, count);
                    await destination.WriteAsync(buffer.AsMemory(0, count), cancel);
                    if (watch.ElapsedMilliseconds - previous >= 200) { previous = watch.ElapsedMilliseconds; progress?.Report(new(received, offer.Size)); }
                }
                if (received != offer.Size) throw new InvalidDataException("文件未下载完整，旧版程序仍可使用。");
                await destination.FlushAsync(cancel); destination.Flush(true);
            }
            var actual = Convert.ToHexStringLower(digest.GetHashAndReset());
            if (!actual.Equals(hash, StringComparison.Ordinal)) throw new InvalidDataException("文件 SHA-256 校验失败，未执行更新。");
            UpdateFiles.VerifyExecutable(part, offer.Version);
            var payload = Path.Combine(directory, "payload.exe"); File.Move(part, payload);
            progress?.Report(new(offer.Size, offer.Size));
            return new(directory, payload, offer, hash);
        }
        catch { UpdateFiles.CleanSession(directory); throw; }
    }
    private static string ParseChecksum(string text)
    {
        var match = Regex.Match(text.Trim(), @"^([0-9a-fA-F]{64})(?:[ \t]+\*?TarkovMapLite\.exe)?$");
        if (!match.Success) throw new InvalidDataException("下载文件的校验信息无效。");
        return match.Groups[1].Value.ToLowerInvariant();
    }
    public void Dispose() => _http.Dispose();
}
