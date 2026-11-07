namespace TarkovMapLite;

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

    /// <summary>图片要先整体读进内存，GDI+ 会一直按住流。</summary>
    public static Image? ReadImage(string relativePath)
    {
        using var stream = Open(relativePath);
        if (stream == null)
        {
            return null;
        }
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        buffer.Position = 0;
        return Image.FromStream(buffer);
    }
}
