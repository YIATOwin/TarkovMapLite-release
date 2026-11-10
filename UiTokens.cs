// ============================================================================
// UiTokens.cs — 界面的颜色 / 圆角 / 字号唯一出处（对应 docs/UI-SPEC.md §2 §3 §6）
//
// 为什么要有这个文件：GDI+ 没有 CSS 变量，颜色以前散落在 MapForm / MapCanvas /
// MobileForm / MobileForm 各自的 private static readonly 里，同一个灰有三份值，
// 改一次配色要翻四个文件，还容易改漏。
//
// 规矩（UI-SPEC §14）：
//  · 业务代码**不要**再写 Color.FromArgb(...) 字面量，用这里的语义名
//  · 加新颜色：先改 docs/UI-SPEC.md §2.3，再加到这里，最后同步手机端 app.css 的 :root
//  · 这里只放"界面外壳"的颜色。**数据驱动的颜色不在这里**——任务状态色、
//    物资分类色、撤离点颜色由各自的数据模块给（ContainerKinds / LootKinds），
//    因为它们跟数据绑定，不是主题色
//
// 数值和改版前完全一致，这一步是纯搬运，不改观感。
// ============================================================================
namespace TarkovMapLite;

internal static class UiTokens
{
    // ---- 表面 ----------------------------------------------------------------
    /// <summary>画布底色。和手机端 --back 一致，图片没加载时不闪白。</summary>
    public static readonly Color Back = Color.FromArgb(13, 15, 17);

    /// <summary>面板底色（顶栏、任务栏、状态栏、抽屉）。</summary>
    public static readonly Color PanelBack = Color.FromArgb(22, 25, 29);

    /// <summary>面板里再抬一层：表格行、选中项底、输入框底的凹槽。</summary>
    public static readonly Color Sunken = Color.FromArgb(17, 19, 23);

    /// <summary>悬浮/选中行的高亮底色。</summary>
    public static readonly Color Raised = Color.FromArgb(28, 32, 38);

    /// <summary>次级按钮底色（顶栏那排都是它）。</summary>
    public static readonly Color ButtonBack = Color.FromArgb(34, 39, 46);

    // ---- 线 ------------------------------------------------------------------
    /// <summary>标准边框（输入框、按钮描边、面板分割）。</summary>
    public static readonly Color Line = Color.FromArgb(44, 49, 56);

    /// <summary>
    /// 0.5px 观感的微边框：GDI+ 画不出半像素，只能靠低透明度。
    /// 用在列表横线、卡片分隔这种"只要看见边界、不要看见线"的地方（UI-SPEC §3.1）。
    /// </summary>
    public static readonly Color LineSubtle = Color.FromArgb(38, 226, 232, 240);

    // ---- 文字 ----------------------------------------------------------------
    /// <summary>主文字：任务名、物品名、正文。</summary>
    public static readonly Color TextMain = Color.FromArgb(232, 238, 245);

    /// <summary>次要文字：副标题、坐标、时间戳、表头。</summary>
    public static readonly Color TextDim = Color.FromArgb(150, 160, 172);

    /// <summary>三级文字：占位符、禁用态（对比度仍然 ≥3:1）。</summary>
    public static readonly Color TextFaint = Color.FromArgb(107, 116, 128);

    // ---- 强调与状态 ----------------------------------------------------------
    /// <summary>全屏唯一强调色。"主操作"和高亮只能用它。</summary>
    public static readonly Color Accent = Color.FromArgb(140, 190, 255);

    /// <summary>强调色的浅底（主按钮底、选中行底）：暗色下用低透明度叠上去。</summary>
    public static readonly Color AccentBack = Color.FromArgb(26, 140, 190, 255);

    /// <summary>
    /// 强调色 14% 混到底色上的**实心**版本。
    /// WinForms 的 Button 不吃半透明 BackColor（会画成脏色），要主按钮浅底就用这个。
    /// </summary>
    public static readonly Color AccentTint = Color.FromArgb(39, 48, 61);

    /// <summary>成功 / 已完成 / 可撤离。</summary>
    public static readonly Color Ok = Color.FromArgb(120, 200, 140);

    /// <summary>预警 / 需要钥匙 / 提示。</summary>
    public static readonly Color Warn = Color.FromArgb(226, 168, 74);

    /// <summary>失败 / 危险区 / 危险操作。</summary>
    public static readonly Color Danger = Color.FromArgb(220, 110, 110);

    /// <summary>中性信息（转移点这类不属于成败的标记）。</summary>
    public static readonly Color Info = Color.FromArgb(190, 150, 245);

    /// <summary>遮罩层（对话框后面的压暗）。</summary>
    public static readonly Color Scrim = Color.FromArgb(205, 0, 0, 0);

    // ---- 圆角（GDI+ 自绘用，见 UI-SPEC §3.2）--------------------------------
    public const int RadiusSm = 4;
    public const int RadiusMd = 6;
    public const int RadiusLg = 8;
    public const int RadiusXl = 12;
    public const int Radius2Xl = 16;

    /// <summary>同心圆角：外半径 = 内半径 + 内边距（UI-SPEC §3.3）。</summary>
    public static int Concentric(int innerRadius, int padding) => innerRadius + padding;

    // ---- 字号（WinForms 用 pt，对应 UI-SPEC §6 的四档）--------------------
    public const float FontTitle = 12f;     // 模块标题 / 面板标题 ≈ 16px
    public const float FontBody = 8.5f;     // 正文 ≈ 14px（项目现有正文尺寸）
    public const float FontCaption = 7.5f;  // 辅助 ≈ 12px
}
