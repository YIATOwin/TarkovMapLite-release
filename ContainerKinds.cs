using System.Drawing;

namespace TarkovMapLite;

/// <summary>
/// 容器类型的显示信息。tarkov.dev 给每种容器画了专属小图标，那些图标是站点自己的
/// SVG 组件、不在资源接口里，所以这里退一步：每种容器一个固定颜色 + 一个简单形状，
/// 再配上中文名，在「图层」菜单里当图例用。
/// </summary>
internal static class ContainerKinds
{
    /// <summary>形状：0 方块 1 圆 2 菱形 3 十字 4 三角 5 空心圈</summary>
    internal sealed record Kind(string Name, Color Color, int Shape);

    private static readonly Dictionary<string, Kind> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        // —— 行李 / 包 ——
        ["duffle-bag"] = new("行李袋", Color.FromArgb(96, 160, 226), 1),
        ["plastic-suitcase"] = new("塑料手提箱", Color.FromArgb(126, 178, 232), 1),
        // —— 衣物 / 家具 ——
        ["jacket"] = new("夹克", Color.FromArgb(198, 154, 118), 4),
        ["drawer"] = new("抽屉", Color.FromArgb(172, 133, 100), 4),
        // —— 箱子类 ——
        ["wooden-crate"] = new("木箱", Color.FromArgb(206, 174, 108), 0),
        ["weapon-box"] = new("武器箱", Color.FromArgb(140, 176, 118), 0),
        ["toolbox"] = new("工具箱", Color.FromArgb(120, 168, 132), 0),
        ["grenade-box"] = new("手榴弹箱", Color.FromArgb(104, 156, 112), 0),
        ["wooden-ammo-box"] = new("木制弹药箱", Color.FromArgb(186, 162, 96), 0),
        ["technical-supply-crate"] = new("技术物资箱", Color.FromArgb(108, 160, 150), 0),
        ["ration-supply-crate"] = new("配给物资箱", Color.FromArgb(196, 168, 124), 0),
        ["buried-barrel-cache"] = new("埋藏桶", Color.FromArgb(150, 140, 118), 0),
        ["ground-cache"] = new("地面藏匿点", Color.FromArgb(166, 158, 132), 0),
        ["shturmans-stash"] = new("施图尔曼藏匿点", Color.FromArgb(226, 200, 120), 0),
        // —— 贵重 / 电子 ——
        ["safe"] = new("保险箱", Color.FromArgb(236, 196, 92), 2),
        ["bank-safe"] = new("银行保险箱", Color.FromArgb(246, 212, 108), 2),
        ["cash-register"] = new("收银机", Color.FromArgb(220, 186, 118), 2),
        ["bank-cash-register"] = new("银行收银机", Color.FromArgb(232, 198, 132), 2),
        ["pc-block"] = new("电脑机箱", Color.FromArgb(120, 200, 226), 5),
        // —— 医疗 ——
        ["medbag"] = new("SMU06 医疗包", Color.FromArgb(236, 120, 120), 3),
        ["medcase"] = new("医药箱", Color.FromArgb(226, 138, 138), 3),
        ["medical-supply-crate"] = new("医疗物资箱", Color.FromArgb(210, 150, 146), 3),
        // —— 尸体 ——
        ["dead-scav"] = new("死亡的 Scav", Color.FromArgb(160, 160, 168), 1),
        ["scav-body"] = new("Scav 尸体", Color.FromArgb(172, 168, 176), 1),
        ["pmc-body"] = new("PMC 尸体", Color.FromArgb(150, 158, 186), 1),
        ["civilian-body"] = new("死亡的平民", Color.FromArgb(186, 180, 170), 1),
        ["lab-technician-body"] = new("实验室技术员尸体", Color.FromArgb(176, 172, 190), 1),
    };

    public static Kind Resolve(string slug) =>
        Table.TryGetValue(slug, out var kind) ? kind : new Kind(slug, Color.FromArgb(150, 150, 158), 0);

    public static bool Known(string slug) => Table.ContainsKey(slug);

    /// <summary>按数量排序、只保留确实有数据出现的类型。</summary>
    public static IEnumerable<string> OrderByCount(IEnumerable<string> present) =>
        present.GroupBy(k => k, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key);

    public static string NameOf(string slug) => Resolve(slug).Name;
}
