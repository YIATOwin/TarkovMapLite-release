// ==== 钥匙图鉴：Assets/keys.json（构建期由 tools/build-keys.py 生成）===
// 职责：把门锁数据里的「钥匙名」映射到物品 ID 和一张 96×96 的 PNG 图标，
//       并缓存已解码的 Bitmap（一张钥匙列表会用到几十张图，别每次重读）。
// 谁在用：KeyListForm（列表里的图标）、MapForm（打开钥匙列表时取数据）。
// 改动须知：keys.json 和 keyicons/ 都是构建产物，改它们要改 tools/build-keys.py，
//           别手改 —— 重新跑脚本会覆盖。
// 踩过的坑：
//  · 图标要从 webp 转 PNG，GDI+ 不认 webp，所以转换在构建期用 Pillow 做了；
//  · 资源是嵌入的（EmbeddedResource），新增 Assets 子目录必须同步改 csproj，
//    否则运行时 Assets.Open 会抛“找不到资源”。
// ====

using System.Drawing;
using System.Text.Json;

namespace TarkovMapLite;

internal sealed record KeyEntry(string Label, string Id, string IconPath);

internal static class KeyCatalog
{
    private static Dictionary<string, KeyEntry>? _entries;
    private static readonly Dictionary<string, Bitmap?> Icons = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>钥匙名 → 图鉴条目；没有 keys.json（老版本/裁剪版）时返回空表而不是崩。</summary>
    public static IReadOnlyDictionary<string, KeyEntry> Entries
    {
        get
        {
            if (_entries != null)
            {
                return _entries;
            }
            var table = new Dictionary<string, KeyEntry>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var document = JsonDocument.Parse(Assets.ReadText("keys.json"));
                if (document.RootElement.TryGetProperty("keys", out var keys))
                {
                    foreach (var entry in keys.EnumerateObject())
                    {
                        var id = entry.Value.TryGetProperty("id", out var idNode) ? idNode.GetString() ?? "" : "";
                        var icon = entry.Value.TryGetProperty("icon", out var iconNode) ? iconNode.GetString() ?? "" : "";
                        table[entry.Name] = new KeyEntry(entry.Name, id, icon);
                    }
                }
            }
            catch
            {
                // 没有这个资源就当作没有图鉴，列表退化成纯文字
            }
            _entries = table;
            return table;
        }
    }

    public static KeyEntry? Find(string label) => Entries.TryGetValue(label, out var entry) ? entry : null;

    /// <summary>取一张图标；取不到返回 null（列表里画一个占位方块）。</summary>
    public static Bitmap? Icon(string label)
    {
        var entry = Find(label);
        if (entry == null || entry.IconPath.Length == 0)
        {
            return null;
        }
        lock (Gate)
        {
            if (Icons.TryGetValue(entry.IconPath, out var cached))
            {
                return cached;
            }
            Bitmap? bitmap = null;
            try
            {
                using var stream = Assets.Open(entry.IconPath);
                bitmap = new Bitmap(stream);
            }
            catch
            {
                bitmap = null;
            }
            Icons[entry.IconPath] = bitmap;
            return bitmap;
        }
    }
}
