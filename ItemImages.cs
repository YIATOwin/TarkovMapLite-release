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
    private static readonly LinkedList<string> Recent = new();
    private static readonly Dictionary<string,LinkedListNode<string>> Nodes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string,DateTime> RetryAt = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim Downloads = new(4);
    private const int MaximumImages = 128;
    private const int ThumbnailSize = 128; // 24px 列表图标在 400% DPI 下仍有足够分辨率。

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

    /// <summary>在锁内使用缓存图，避免后台淘汰与 UI 绘制并发导致使用已释放的位图。</summary>
    public static bool Draw(Graphics graphics, string itemId, Rectangle bounds)
    {
        lock (Gate)
        {
            if (!Loaded.TryGetValue(itemId,out var image) || image == null) return false;
            Touch(itemId);
            graphics.DrawImage(image,bounds);
            return true;
        }
    }

    private static void Touch(string id)
    {
        if (Nodes.TryGetValue(id,out var node)) Recent.Remove(node);
        else Nodes[id] = node = new LinkedListNode<string>(id);
        Recent.AddLast(node);
    }

    private static void Store(string id, Image? image)
    {
        lock (Gate)
        {
            if (Loaded.Remove(id,out var old) && !ReferenceEquals(old,image)) old?.Dispose();
            Loaded[id] = image;
            if (image == null) RetryAt[id] = DateTime.UtcNow.AddMinutes(1);
            else RetryAt.Remove(id);
            Touch(id);
            while (Loaded.Count > MaximumImages)
            {
                var victim = Recent.First!.Value;
                Recent.RemoveFirst(); Nodes.Remove(victim); RetryAt.Remove(victim);
                if (Loaded.Remove(victim,out var removed)) removed?.Dispose();
            }
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
            if (Loaded.TryGetValue(itemId,out var image))
            {
                if (image != null || !RetryAt.TryGetValue(itemId,out var retry) || DateTime.UtcNow < retry)
                {
                    Touch(itemId);
                    return;
                }
                Loaded.Remove(itemId); RetryAt.Remove(itemId);
                if (Nodes.Remove(itemId,out var node)) Recent.Remove(node);
            }
            if (!Pending.Add(itemId)) return;
        }

        Task.Run(async () =>
        {
            await Downloads.WaitAsync().ConfigureAwait(false);
            try
            {
                var image = LoadFromDisk(itemId) ?? await DownloadAsync(itemId);
                Store(itemId,image);
            }
            catch
            {
                // 网络/解码失败就当没有图，图标位置留空
                Store(itemId,null);
            }
            finally
            {
                Downloads.Release();
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
        var ratio = Math.Min(1,ThumbnailSize/(double)Math.Max(source.Width,source.Height));
        var width = Math.Max(1,(int)Math.Round(source.Width*ratio));
        var height = Math.Max(1,(int)Math.Round(source.Height*ratio));
        var copy = new Bitmap(width,height,System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            using var g = Graphics.FromImage(copy);
            g.Clear(Color.Transparent);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(source,new Rectangle(0,0,width,height),0,0,source.Width,source.Height,GraphicsUnit.Pixel);
            return copy;
        }
        catch { copy.Dispose(); throw; }
    }
}
