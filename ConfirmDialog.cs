// ============================================================================
// ConfirmDialog.cs — 危险操作确认框（对应 docs/UI-SPEC.md §7.3）
//
// 为什么不直接用 MessageBox：
//  · MessageBox 的按钮只能是「确定 / 取消」，规范要求**动宾明确**（「彻底清空」）
//  · 危险按钮要用 Danger 色，MessageBox 给不了
//  · 弹窗垂直位置规范要求 38.2%（视口黄金分割），MessageBox 只能正中
//
// 用法：
//   if (!ConfirmDialog.Ask(this, "清空本图已完成标记？",
//           "共 12 条手动标记，清空后无法恢复（只影响手动标记，不动任务状态）。",
//           "彻底清空"))
//       return;
//
// 注意：CenterParent 在 WinForms 里对最大化/多屏窗口会算错（踩过），
// 这里自己按 owner 的位置算，并夹到工作区内。
// ============================================================================
namespace TarkovMapLite;

internal sealed class ConfirmDialog : Form
{
    private bool _confirmed;

    private ConfirmDialog(string title, string message, string confirmText)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        BackColor = UiTokens.PanelBack;
        ForeColor = UiTokens.TextMain;
        ClientSize = new Size(460, 190);
        Font = new Font("Microsoft YaHei UI", UiTokens.FontBody);
        Padding = new Padding(24, 20, 24, 18);

        // 标题：问句，中立交代对象（不带"确定要…吗"这种诱导）
        var heading = new Label
        {
            Text = title,
            Dock = DockStyle.Top,
            Height = 30,
            Font = new Font("Microsoft YaHei UI", UiTokens.FontTitle, FontStyle.Bold),
            ForeColor = UiTokens.TextMain,
            BackColor = Color.Transparent,
        };
        // 描述：写清后果与不可逆性
        var body = new Label
        {
            Text = message,
            Dock = DockStyle.Fill,
            ForeColor = UiTokens.TextDim,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 6, 0, 0),
        };

        // 按钮区：危险的放右边，取消放左边（破坏性操作的常规顺序）
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 38,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 6, 0, 0),
        };

        var confirm = new Button
        {
            Text = confirmText,                 // 动宾明确，不是"确定"
            DialogResult = DialogResult.OK,
            Size = new Size(104, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = UiTokens.Danger,        // 危险色，唯一一处用它
            ForeColor = UiTokens.Back,          // 深底上要反色才够对比
            Font = new Font("Microsoft YaHei UI", UiTokens.FontBody, FontStyle.Bold),
            Margin = new Padding(8, 0, 0, 0),
        };
        confirm.FlatAppearance.BorderSize = 0;
        confirm.Click += (_, _) =>
        {
            _confirmed = true;
            DialogResult = DialogResult.OK;
            Close();
        };

        var cancel = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Size = new Size(84, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = UiTokens.ButtonBack,    // 次级，中性
            ForeColor = UiTokens.TextMain,
            Font = new Font("Microsoft YaHei UI", UiTokens.FontBody),
        };
        cancel.FlatAppearance.BorderColor = UiTokens.Line;
        cancel.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };

        buttons.Controls.Add(confirm);
        buttons.Controls.Add(cancel);
        Controls.Add(body);
        Controls.Add(buttons);
        Controls.Add(heading);

        AcceptButton = confirm;
        CancelButton = cancel;
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
            }
        };
    }

    /// <summary>确认则返回 true。点取消、按 Esc、点窗外都返回 false。</summary>
    public static bool Ask(IWin32Window? owner, string title, string message, string confirmText)
    {
        using var dialog = new ConfirmDialog(title, message, confirmText);
        if (owner is Control control)
        {
            var screen = control.FindForm()?.Bounds ?? control.Bounds;
            // 垂直位置：视口 38.2% 处（UI-SPEC §3.4），并夹到工作区里
            var top = screen.Top + (int)(screen.Height * 0.382) - dialog.Height / 2;
            var left = screen.Left + (screen.Width - dialog.Width) / 2;
            var work = Screen.FromControl(control).WorkingArea;
            dialog.Location = new Point(
                Math.Clamp(left, work.Left + 8, Math.Max(work.Left + 8, work.Right - dialog.Width - 8)),
                Math.Clamp(top, work.Top + 8, Math.Max(work.Top + 8, work.Bottom - dialog.Height - 8)));
        }
        dialog.ShowDialog(owner);
        return dialog._confirmed;
    }
}
