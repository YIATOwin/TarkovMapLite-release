// ============================================================================
// UiTokens.cs — 界面的颜色 / 圆角 / 字号唯一出处（对应 docs/UI-SPEC.md §2 §3 §6）
//
// 为什么要有这个文件：GDI+ 没有 CSS 变量，颜色以前散落在 MapForm / MapCanvas /
// MobileForm 各自的 private static readonly 里，同一个灰有三份值，改一次配色
// 要翻四个文件，还容易改漏。
//
// 规矩（UI-SPEC §14）：
//  · 业务代码**不要**再写 Color.FromArgb(...) 字面量，用这里的语义名
//  · 加新颜色：先改 docs/UI-SPEC.md §2.3，再加到这里，最后同步手机端 app.css 的 :root
//  · 这里只放"界面外壳"的颜色。**数据驱动的颜色不在这里**——任务状态色、
//    物资分类色、撤离点颜色由各自的数据模块给（ContainerKinds / LootKinds），
//    因为它们跟数据绑定，不是主题色
//
// 配色方向（v0.3.2 重调）：中性石墨灰打底 + **低饱和沙色**做唯一强调色。
// 之前用的亮蓝 #4C9AFF 太像 SaaS 后台，和塔科夫这种灰绿/土黄的调子不搭；
// 现在强调色只在"选中/激活"时出现，常态界面几乎全是灰，靠明度分层。
// 圆角统一 7px（按钮、下拉框、胶囊一致），避免"方不方圆不圆"的观感。
// ============================================================================
namespace TarkovMapLite;

internal static class UiTokens
{
    // ---- 表面 ----------------------------------------------------------------
    /// <summary>画布底色。和手机端 --back 一致，图片没加载时不闪白。</summary>
    public static readonly Color Back = Color.FromArgb(14, 16, 18);
    public static readonly Color IconOutline = Back;
    public static readonly Color PlayerSpawn = Color.FromArgb(150, 200, 240);
    public static readonly Color Transport = Color.FromArgb(120, 210, 218);
    public static readonly Color SwitchMarker = Color.FromArgb(255, 208, 64);

    /// <summary>面板底色（顶栏、任务栏、状态栏、抽屉）。</summary>
    public static readonly Color PanelBack = Color.FromArgb(20, 23, 26);

    /// <summary>面板里再抬一层：表格行、选中项底、输入框底的凹槽。</summary>
    public static readonly Color Sunken = Color.FromArgb(11, 13, 15);

    /// <summary>悬浮/选中行的高亮底色，也是下拉菜单的底色。</summary>
    public static readonly Color Raised = Color.FromArgb(25, 29, 33);

    /// <summary>控件底色（顶栏按钮、下拉框）。比工具栏亮一档，靠明度区分而不是靠描边。</summary>
    public static readonly Color ButtonBack = Color.FromArgb(32, 37, 42);

    /// <summary>按钮悬浮态：再亮一档，不加边框（描边留给菜单项）。</summary>
    public static readonly Color ButtonHover = Color.FromArgb(39, 45, 51);

    // ---- 线 ------------------------------------------------------------------
    /// <summary>标准边框（输入框、按钮描边、面板分割）。</summary>
    public static readonly Color Line = Color.FromArgb(43, 49, 55);

    /// <summary>
    /// 0.5px 观感的微边框：GDI+ 画不出半像素，只能靠低透明度。
    /// 用在列表横线、卡片分隔这种"只要看见边界、不要看见线"的地方（UI-SPEC §3.1）。
    /// </summary>
    public static readonly Color LineSubtle = Color.FromArgb(38, 226, 232, 240);

    // ---- 文字 ----------------------------------------------------------------
    /// <summary>主文字：任务名、物品名、正文。</summary>
    public static readonly Color TextMain = Color.FromArgb(231, 235, 239);

    /// <summary>次要文字：副标题、坐标、时间戳、表头、未激活按钮。</summary>
    public static readonly Color TextDim = Color.FromArgb(152, 162, 171);

    /// <summary>三级文字：占位符、禁用态（对比度仍然 ≥3:1）。</summary>
    public static readonly Color TextFaint = Color.FromArgb(107, 116, 124);

    // ---- 强调与状态 ----------------------------------------------------------
    /// <summary>唯一强调色：低饱和沙色。只在"激活/选中"时出现，不当装饰。</summary>
    public static readonly Color Accent = Color.FromArgb(198, 162, 110);

    /// <summary>强调色 12% 混在面板上的实心底（激活胶囊的底色）。</summary>
    public static readonly Color AccentTint = Color.FromArgb(41, 40, 36);

    /// <summary>选中行底色（比 AccentTint 更实一档，菜单勾选行用它）。</summary>
    public static readonly Color AccentSelection = Color.FromArgb(67, 61, 52);

    /// <summary>强调色的浅底（半透明版，给支持透明的自绘控件用）。</summary>
    public static readonly Color AccentBack = Color.FromArgb(26, 198, 162, 110);

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
    /// <summary>控件圆角：顶栏按钮、下拉框、胶囊统一用它，保证同一套观感。</summary>
    public const int RadiusControl = 7;
    public const int RadiusSm = 4;
    public const int RadiusMd = 6;
    public const int RadiusLg = 8;
    public const int RadiusXl = 12;
    public const int Radius2Xl = 16;

    /// <summary>同心圆角：外半径 = 内半径 + 内边距（UI-SPEC §3.3）。</summary>
    public static int Concentric(int innerRadius, int padding) => innerRadius + padding;

    /// <summary>圆角矩形路径（自绘控件共用）。</summary>
    public static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var d = Math.Max(2, radius * 2);
        if (d > r.Height)
        {
            d = r.Height;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ---- 字号（WinForms 用 pt，对应 UI-SPEC §6 的四档）--------------------
    public const float FontTitle = 12f;     // 模块标题 / 面板标题 ≈ 16px
    public const float FontBody = 8.5f;     // 正文 ≈ 14px（项目现有正文尺寸）
    public const float FontCaption = 7.5f;  // 辅助 ≈ 12px
}
