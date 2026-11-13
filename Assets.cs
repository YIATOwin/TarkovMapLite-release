// ============================================================================
// Assets.cs — 嵌入资源读取（地图图片、maps.json、labels.json、markers.json、手机页面）
//
// 资源全部以 EmbeddedResource 编进 exe，单文件分发，玩家不用带 Assets 目录。
//
// 两个必须知道的坑：
//  1. MSBuild 会把资源名里的 '-' 换成 '_'（ground-zero → ground_zero），
//     所以查表前必须先 Normalize()，不能拿路径直接当资源名。
//  2. Open() 优先读 exe 旁边的 Assets/ 散文件 —— 这是"改资源不重新打包"的后门：
//     把 Assets\maps\customs\base.png 放到 exe 同目录就能覆盖内嵌的那份。
//     调试手机页面也靠它（见 MobileAssets.cs），但内存里的缓存要重启才失效。
// ============================================================================
namespace TarkovMapLite;

/// <summary>GDI+ 图片与来源流拥有相同寿命；淘汰时一起释放，不复制整张像素或压缩文件。</summary>
internal sealed class AssetImage : IDisposable
{
    private readonly Stream _source;
    public Image Image { get; }

    public AssetImage(Image image, Stream source) { Image=image; _source=source; }
    public void Dispose() { try { Image.Dispose(); } finally { _source.Dispose(); } }
}

/// <summary>
/// 资源读取：地图图片和 maps.json 编进 exe（单文件分发），开发时也支持旁边的
/// Assets 目录覆盖，方便改图后不重新打包就能看到效果。
/// </summary>
internal static class Assets
{
    private static readonly System.Reflection.Assembly Assembly = typeof(Assets).Assembly;
    private static readonly object Gate = new();
    private static Dictionary<string, string>? _resourceMap;

    /// <summary>
    /// MSBuild 会把资源名里的 '-' 换成 '_'（ground-zero → ground_zero），
    /// 所以不能按路径拼名字，得先把清单里的名字规范化再查。
    /// </summary>
    private static Dictionary<string, string> ResourceMap
    {
        get
        {
            if (_resourceMap != null)
            {
                return _resourceMap;
            }
            lock (Gate)
            {
                if (_resourceMap != null)
                {
                    return _resourceMap;
                }
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in Assembly.GetManifestResourceNames())
                {
                    map[Normalize(name)] = name;
                }
                _resourceMap = map;
                return map;
            }
        }
    }

    private static string Normalize(string name) =>
        name.Replace('-', '_').Replace('\\', '.').Replace('/', '.');

    private static Stream? OpenResource(string relativePath)
    {
        var wanted = Normalize("TarkovMapLite.Assets." + relativePath);
        if (ResourceMap.TryGetValue(wanted, out var actual))
        {
            return Assembly.GetManifestResourceStream(actual);
        }
        return null;
    }

    /// <summary>原始字节流；找不到返回 null。</summary>
    public static Stream? Open(string relativePath)
    {
        var loose = Path.Combine(AppContext.BaseDirectory, "Assets",
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(loose))
        {
            return File.OpenRead(loose);
        }
        return OpenResource(relativePath);
    }

    public static string? ReadText(string relativePath)
    {
        using var stream = Open(relativePath);
        if (stream == null)
        {
            return null;
        }
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>直接从资源流加载，由拥有者保持流至图片释放；避免大图的整图复制峰值。</summary>
    public static AssetImage? ReadImage(string relativePath)
    {
        var stream = Open(relativePath);
        if (stream == null)
        {
            return null;
        }
        try
        {
            return new AssetImage(Image.FromStream(stream),stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }
}
