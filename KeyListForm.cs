// ==== 钥匙列表：当前地图上所有「需要钥匙的门」按钥匙归组 ====
// 职责：列出这张图用得上的钥匙（带图标和"几处"），点一行就把地图移到对应的锁。
// 谁在用：MapForm.OpenKeyList()（顶栏「钥匙」按钮）。
// 改动须知：行高和图标尺寸是配套的（52 / 40），改一个要改另一个；
//           数据来自 MapDoors 按 DoorLabel 归组，图标来自 KeyCatalog。
// 踩过的坑：
//  · OwnerDraw 的 ListBox 里不要用 e.Graphics.DrawString 画中文，会发虚，
//    用 TextRenderer.DrawText 才和系统一致；
//  · 单击就确认（不是双击）——玩家点一下就想跳过去，多一步很烦。
// ====

using System.Drawing;
using System.Windows.Forms;

namespace TarkovMapLite;

internal sealed class KeyListForm : Form
{
    /// <summary>一行钥匙：名字、这张图上有几处锁、第一处锁的世界坐标。</summary>
    internal sealed record Row(string Label, int Locks, PointF World);

    private readonly List<Row> _rows;
    private readonly ListBox _list = new();

    /// <summary>用户选中的那把钥匙对应的锁（世界坐标）；取消时是 null。</summary>
    public PointF? Chosen { get; private set; }

    public string ChosenLabel { get; private set; } = "";

    public KeyListForm(string mapName, IReadOnlyList<Row> rows)
    {
        _rows = rows.ToList();
        Text = $"钥匙 · {mapName}（{_rows.Count} 把）";
        BackColor = UiTokens.PanelBack;
        ForeColor = UiTokens.TextMain;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(430, 580);
        MinimumSize = new Size(320, 320);
        Font = new Font("Microsoft YaHei UI", 9f);
        ShowIcon = false;
        KeyPreview = true;

        var hint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 32,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 0, 0),
            ForeColor = UiTokens.TextFaint,
            BackColor = UiTokens.PanelBack,
            Text = "点一把钥匙 → 地图移到对应的锁并闪一下",
        };

        _list.Dock = DockStyle.Fill;
        _list.BackColor = UiTokens.Sunken;
        _list.ForeColor = UiTokens.TextMain;
        _list.BorderStyle = BorderStyle.None;
        _list.DrawMode = DrawMode.OwnerDrawFixed;
        _list.ItemHeight = 52;
        _list.IntegralHeight = false;
        _list.DrawItem += DrawRow;
        _list.MouseUp += (_, e) =>
        {
            var index = _list.IndexFromPoint(e.Location);
            if (index >= 0)
            {
                Choose(index);
            }
        };
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && _list.SelectedIndex >= 0)
            {
                Choose(_list.SelectedIndex);
            }
        };

        foreach (var row in _rows)
        {
            _list.Items.Add(row.Label);
        }

        Controls.Add(_list);
        Controls.Add(hint);
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
            }
        };
    }

    private void Choose(int index)
    {
        if (index < 0 || index >= _rows.Count)
        {
            return;
        }
        Chosen = _rows[index].World;
        ChosenLabel = _rows[index].Label;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void DrawRow(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _rows.Count)
        {
            return;
        }
        var row = _rows[e.Index];
        var selected = (e.State & DrawItemState.Selected) != 0;
        using (var back = new SolidBrush(selected ? UiTokens.AccentSelection : UiTokens.Sunken))
        {
            e.Graphics.FillRectangle(back, e.Bounds);
        }

        // 图标：40×40，缺图就画个圆角占位块
        var iconRect = new Rectangle(e.Bounds.X + 10, e.Bounds.Y + 6, 40, 40);
        if (KeyCatalog.Icon(row.Label) is { } icon)
        {
            e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(icon, iconRect);
        }
        else
        {
            using var placeholder = new SolidBrush(UiTokens.Raised);
            e.Graphics.FillRectangle(placeholder, iconRect);
        }

        var textRect = new Rectangle(e.Bounds.X + 60, e.Bounds.Y + 8, e.Bounds.Width - 130, 20);
        TextRenderer.DrawText(
            e.Graphics, row.Label, Font, textRect,
            selected ? UiTokens.Accent : UiTokens.TextMain,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        var countRect = new Rectangle(e.Bounds.Right - 76, e.Bounds.Y + 8, 66, 20);
        TextRenderer.DrawText(
            e.Graphics, $"{row.Locks} 处", Font, countRect,
            UiTokens.TextFaint,
            TextFormatFlags.Right | TextFormatFlags.NoPrefix);

        // 分隔线（半透明，别抢图标）
        using var line = new Pen(UiTokens.LineSubtle, 1f);
        e.Graphics.DrawLine(line, e.Bounds.Left + 10, e.Bounds.Bottom - 1,
            e.Bounds.Right - 10, e.Bounds.Bottom - 1);
    }
}
