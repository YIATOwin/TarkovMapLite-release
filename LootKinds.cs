// ============================================================================
// LootKinds.cs — 散落物资的分类
//
// 分类依据是物品的 types（tools/build-markers.ps1 构建时算好位掩码，写进
// markers.json 的第三列）。一个点位可能"可能出钥匙也可能出杂物"，所以是位掩码。
//
// 只有 7 个桶，是数据决定的，不是随便划的：
//   keys 157 种、barter 80、poster 36、injectors/meds 20、container 12…
//   · poster 其实是**海报**（alex-green-poster 这类）
//   · 空 types 的那几种是**文件/资料**（medical-documents 之类）
//   · 弹药箱（ammunition-case）是 container，归「收纳」，不是「弹药」——
//     接口里 lootLoose 根本没有 ammo 类型（350 种里 0 个），弹药只在容器里
//     （wooden-ammo-box / weapon-box）。别再想给弹药单开一桶。
//
// 顺序有意义：钥匙最"值得看"，所以它的颜色优先级最高。
// ============================================================================
using System.Drawing;

namespace TarkovMapLite;

/// <summary>一个散落物资点：图片像素坐标 + 分类位掩码。</summary>
internal readonly record struct LootPoint(PointF Point, int Mask);

internal sealed record LootKind(int Bit, string Name, Color Color);

internal static class LootKinds
{
    public const int Keys = 1;
    public const int Meds = 2;
    public const int Food = 4;
    public const int Poster = 8;
    public const int Storage = 16;
    public const int Junk = 32;
    public const int Docs = 64;

    /// <summary>按"一眼最想找"的顺序排列；<see cref="Dominant"/> 取第一个命中的。</summary>
    public static readonly LootKind[] All =
    {
        new(Keys, "钥匙", Color.FromArgb(255, 212, 84)),
        new(Meds, "药品", Color.FromArgb(120, 220, 150)),
        new(Food, "食物", Color.FromArgb(240, 170, 120)),
        new(Poster, "海报", Color.FromArgb(200, 160, 250)),
        new(Storage, "收纳", Color.FromArgb(140, 200, 235)),
        new(Junk, "杂物", Color.FromArgb(255, 186, 88)),
        new(Docs, "文件", Color.FromArgb(190, 198, 210)),
    };

    /// <summary>这个点用哪个颜色画：取优先级最高的那一类。</summary>
    public static LootKind Dominant(int mask)
    {
        foreach (var kind in All)
        {
            if ((mask & kind.Bit) != 0)
            {
                return kind;
            }
        }
        return All[^1];
    }

    /// <summary>
    /// 是否要画：只要有一类没被隐藏就画。
    /// 一个"可出钥匙也可出杂物"的点，在只关了杂物时仍然显示（因为还能出钥匙）。
    /// </summary>
    public static bool Visible(int mask, ISet<int> hidden)
    {
        if (hidden.Count == 0)
        {
            return true;
        }
        var kept = mask & ~Mask(hidden);
        return kept != 0;
    }

    private static int Mask(ISet<int> hidden)
    {
        var all = 0;
        foreach (var bit in hidden)
        {
            all |= bit;
        }
        return all;
    }

    /// <summary>某个桶里有多少个点（给菜单显示数量）。</summary>
    public static int Count(IEnumerable<LootPoint> points, int bit)
    {
        var total = 0;
        foreach (var point in points)
        {
            if ((point.Mask & bit) != 0)
            {
                total++;
            }
        }
        return total;
    }
}
