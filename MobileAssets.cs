// ============================================================================
// MobileAssets.cs — 读内嵌的手机页面（Assets/mobile/*）
//
// 文本只读一次就缓存（每个页面生命周期只请求一次，缓存省的是重复 UTF-8 解码）。
//
// 调试技巧：想改页面又不想重编译，把 Assets\mobile\app.js 放到 exe 同目录，
// Assets.Open 会优先读散文件；但**本类的缓存要重启进程才失效**，改完要重启。
// ============================================================================
namespace TarkovMapLite;

/// <summary>手机页面的静态资源（全部编进 exe，不落盘、不依赖网络）。</summary>
internal static class MobileAssets
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>读 Assets/mobile/&lt;name&gt; 的文本；找不到返回 null（页面上会显示 404）。</summary>
    public static string? Text(string name)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(name, out var hit))
            {
                return hit;
            }
        }
        var text = Assets.ReadText("mobile/" + name);
        if (text == null)
        {
            return null;
        }
        lock (Gate)
        {
            Cache[name] = text;
        }
        return text;
    }
}
