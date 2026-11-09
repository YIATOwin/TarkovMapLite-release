// ============================================================================
// ItemImages.cs — 物品缩略图（下载 + 磁盘缓存）
//
// 只给右侧"所需物品"列表用。图源是 assets.tarkov.dev 的 <id>-base-image.png
// （接口另给了 webp，但 GDI+ 不能直接解 webp，PNG 版刚好能用）。
// 下载后缓存到 %LOCALAPPDATA%\TarkovMapLite\img，之后离线也能显示。
//
// 用法永远是"Request(id, 好了以后回调重画)"，请求是后台排队异步的：
// 拿不到就画占位方块，列表不会卡住。别把它改成同步下载。
// 缓存目录建不出来时只在内存里缓存（不影响功能）。
// ============================================================================
namespace TarkovMapLite;

/// <summary>
/// 物品缩略图。塔科夫的物品图有三个来源（都是 webp）：
/// 图标、物品图、手持检查图；但 <c>&lt;id&gt;-base-image.png</c> 有真正的 PNG 版本，
/// GDI+ 直接能解码，不需要额外的 webp 解码器。
/// 下载后缓存在 %LOCALAPPDATA%\TarkovMapLite\img，之后离线也能显示。
/// </summary>
internal static class ItemImages
{
    private const string BaseUrl = "https://assets.tarkov.dev/";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly Dictionary<string, Image?> Loaded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Pending = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    private static readonly string CacheDir = CreateCacheDir();

    private static string CreateCacheDir()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TarkovMapLite", "img");
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch
        {
            // 目录建不出来就只在内存里缓存
        }
        return dir;
    }

    /// <summary>已经拿到的图；没有就返回 null（同时不会触发下载）。</summary>
    public static Image? Get(string itemId)
    {
        lock (Gate)
        {
            return Loaded.TryGetValue(itemId, out var image) ? image : null;
        }
    }

    /// <summary>
    /// 请求一张图：先看内存，再看磁盘缓存，最后才联网。
    /// 完成时在后台线程回调，调用方自己切回 UI 线程。
    /// </summary>
    public static void Request(string itemId, Action? loaded)
    {
        if (string.IsNullOrEmpty(itemId))
        {
            return;
        }
        lock (Gate)
        {
            if (Loaded.ContainsKey(itemId) || !Pending.Add(itemId))
            {
                return;
            }
        }

        Task.Run(async () =>
        {
            try
            {
                var image = LoadFromDisk(itemId) ?? await DownloadAsync(itemId);
                lock (Gate)
                {
                    Loaded[itemId] = image;
                }
            }
            catch
            {
                // 网络/解码失败就当没有图，图标位置留空
            }
            finally
            {
                lock (Gate)
                {
                    Pending.Remove(itemId);
                }
                loaded?.Invoke();
            }
        });
    }

    private static Image? LoadFromDisk(string itemId)
    {
        var path = Path.Combine(CacheDir, itemId + ".png");
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            var bytes = File.ReadAllBytes(path);
            return Decode(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<Image?> DownloadAsync(string itemId)
    {
        var url = $"{BaseUrl}{itemId}-base-image.png";
        var bytes = await Http.GetByteArrayAsync(url);
        var image = Decode(bytes);
        if (image != null)
        {
            try
            {
                File.WriteAllBytes(Path.Combine(CacheDir, itemId + ".png"), bytes);
            }
            catch
            {
                // 写不进去不影响显示
            }
        }
        return image;
    }

    /// <summary>GDI+ 会一直按住来源流，所以解码后复制一份位图再释放流。</summary>
    private static Image? Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var source = Image.FromStream(stream);
        var copy = new Bitmap(source.Width, source.Height);
        using (var g = Graphics.FromImage(copy))
        {
            g.Clear(Color.Transparent);
            g.DrawImage(source, 0, 0, source.Width, source.Height);
        }
        return copy;
    }
}
