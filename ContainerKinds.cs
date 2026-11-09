using System.Drawing;

namespace TarkovMapLite;

/// <summary>
/// 容器类型的显示信息：一个颜色 + 一个极简象形记号。
///
/// 站点的容器图标是它自己的 SVG 组件，不在资源接口里；而且那些图标缩到 10 来像素
/// 就糊成一团。这里的做法是「圆角底 + 内部一两个笔画」——抽屉画两条横线、衣架画个
/// 三角钩、保险箱画个转盘、医疗画十字，一眼能分出大类，具体哪一类靠颜色。
/// </summary>
internal static class ContainerKinds
{
    /// <summary>象形记号的种类；用不到新图形就别加，太多反而认不出。</summary>
    internal enum Icon
    {
        Crate,    // 箱子：方框 + 十字封条
        Bag,      // 包：圆角袋 + 提手
        Drawer,   // 抽屉：两条横线
        Hanger,   // 衣架：三角 + 钩
        Dial,     // 保险箱 / 收银机：圆盘 + 指针
        Monitor,  // 电子：屏幕 + 支架
        Cross,    // 医疗：十字
        Body,     // 尸体：人头 + 横躺的身体
    }

    internal sealed record Kind(string Name, Color Color, Icon Glyph);

    private static readonly Dictionary<string, Kind> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        // —— 行李 / 包 ——
        ["duffle-bag"] = new("行李袋", Color.FromArgb(96, 160, 226), Icon.Bag),
        ["plastic-suitcase"] = new("塑料手提箱", Color.FromArgb(126, 178, 232), Icon.Bag),
        // —— 衣物 / 家具 ——
        ["jacket"] = new("夹克", Color.FromArgb(198, 154, 118), Icon.Hanger),
        ["drawer"] = new("抽屉", Color.FromArgb(172, 133, 100), Icon.Drawer),
        // —— 箱子类 ——
        ["wooden-crate"] = new("木箱", Color.FromArgb(206, 174, 108), Icon.Crate),
        ["weapon-box"] = new("武器箱", Color.FromArgb(140, 176, 118), Icon.Crate),
        ["toolbox"] = new("工具箱", Color.FromArgb(120, 168, 132), Icon.Crate),
        ["grenade-box"] = new("手榴弹箱", Color.FromArgb(104, 156, 112), Icon.Crate),
        ["wooden-ammo-box"] = new("木制弹药箱", Color.FromArgb(186, 162, 96), Icon.Crate),
        ["technical-supply-crate"] = new("技术物资箱", Color.FromArgb(108, 160, 150), Icon.Crate),
        ["ration-supply-crate"] = new("配给物资箱", Color.FromArgb(196, 168, 124), Icon.Crate),
        ["buried-barrel-cache"] = new("埋藏桶", Color.FromArgb(150, 140, 118), Icon.Crate),
        ["ground-cache"] = new("地面藏匿点", Color.FromArgb(166, 158, 132), Icon.Crate),
        ["shturmans-stash"] = new("施图尔曼藏匿点", Color.FromArgb(226, 200, 120), Icon.Crate),
        // —— 贵重 / 电子 ——
        ["safe"] = new("保险箱", Color.FromArgb(236, 196, 92), Icon.Dial),
        ["bank-safe"] = new("银行保险箱", Color.FromArgb(246, 212, 108), Icon.Dial),
        ["cash-register"] = new("收银机", Color.FromArgb(220, 186, 118), Icon.Dial),
        ["bank-cash-register"] = new("银行收银机", Color.FromArgb(232, 198, 132), Icon.Dial),
        ["pc-block"] = new("电脑机箱", Color.FromArgb(120, 200, 226), Icon.Monitor),
        // —— 医疗 ——
        ["medbag"] = new("SMU06 医疗包", Color.FromArgb(236, 120, 120), Icon.Cross),
        ["medcase"] = new("医药箱", Color.FromArgb(226, 138, 138), Icon.Cross),
        ["medical-supply-crate"] = new("医疗物资箱", Color.FromArgb(210, 150, 146), Icon.Cross),
        // —— 尸体 ——
        ["dead-scav"] = new("死亡的 Scav", Color.FromArgb(160, 160, 168), Icon.Body),
        ["scav-body"] = new("Scav 尸体", Color.FromArgb(172, 168, 176), Icon.Body),
        ["pmc-body"] = new("PMC 尸体", Color.FromArgb(150, 158, 186), Icon.Body),
        ["civilian-body"] = new("死亡的平民", Color.FromArgb(186, 180, 170), Icon.Body),
        ["lab-technician-body"] = new("实验室技术员尸体", Color.FromArgb(176, 172, 190), Icon.Body),
    };

    public static Kind Resolve(string slug) =>
        Table.TryGetValue(slug, out var kind) ? kind : new Kind(slug, Color.FromArgb(150, 150, 158), Icon.Crate);

    public static bool Known(string slug) => Table.ContainsKey(slug);

    /// <summary>按数量排序、只保留确实有数据出现的类型。</summary>
    public static IEnumerable<string> OrderByCount(IEnumerable<string> present) =>
        present.GroupBy(k => k, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key);

    public static string NameOf(string slug) => Resolve(slug).Name;
}
