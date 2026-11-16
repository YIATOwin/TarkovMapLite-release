using System.Collections.ObjectModel;

namespace TarkovMapLite;

/// <summary>
/// A measured task list backed by WinForms native scrolling. Win32 ListBox truncates
/// owner-drawn row heights to one byte, so multi-tag rows at 200% DPI cannot use it.
/// No controls or images are allocated per task; only visible rows are painted.
/// </summary>
public sealed class TaskListControl : ScrollableControl
{
    public sealed class ItemCollection(TaskListControl owner) : Collection<object>
    {
        public void AddRange(IEnumerable<object> items) { foreach (var item in items) Add(item); }
        protected override void InsertItem(int index, object item) { base.InsertItem(index, item); owner.ItemsChanged(); }
        protected override void SetItem(int index, object item) { base.SetItem(index, item); owner.ItemsChanged(); }
        protected override void RemoveItem(int index) { base.RemoveItem(index); owner.ItemsChanged(); }
        protected override void ClearItems() { base.ClearItems(); owner.SelectedIndex = -1; owner.ItemsChanged(); }
    }
    public ItemCollection Items { get; }
    public event MeasureItemEventHandler? MeasureItem;
    public event DrawItemEventHandler? DrawItem;
    public event EventHandler? SelectedIndexChanged;
    private int[] _tops = [0];
    private int _selectedIndex = -1, _updates;
    private bool _measuring;
    public TaskListControl()
    {
        Items = new(this); AutoScroll = true; TabStop = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        AutoScrollMargin = Size.Empty;
    }
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            var next = value >= 0 && value < Items.Count ? value : -1;
            if (_selectedIndex == next) return;
            _selectedIndex = next;
            if (_updates == 0 && next >= 0 && _tops.Length == Items.Count + 1)
            {
                var y = -AutoScrollPosition.Y;
                if (_tops[next] < y) AutoScrollPosition = new Point(0, _tops[next]);
                else if (_tops[next + 1] > y + ClientSize.Height)
                    AutoScrollPosition = new Point(0, Math.Max(0, _tops[next + 1] - ClientSize.Height));
            }
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty); Invalidate();
        }
    }
    public object? SelectedItem => SelectedIndex >= 0 && SelectedIndex < Items.Count ? Items[SelectedIndex] : null;
    public int TopIndex
    {
        get => Items.Count == 0 ? 0 : Math.Max(0, IndexAt(-AutoScrollPosition.Y));
        set { if (value >= 0 && value < Items.Count && _tops.Length == Items.Count + 1) AutoScrollPosition = new Point(0, _tops[value]); }
    }
    public void ClearSelected() => SelectedIndex = -1;
    public void BeginUpdate() => _updates++;
    public void EndUpdate() { if (_updates > 0) _updates--; if (_updates == 0) Remeasure(); }
    private void ItemsChanged() { if (_updates == 0) Remeasure(); }
    public int GetItemHeight(int index) => _tops[index + 1] - _tops[index];
    public int IndexFromPoint(Point point) => point.Y < 0 || point.Y >= ClientSize.Height ? -1 : IndexAt(point.Y - AutoScrollPosition.Y);
    private int IndexAt(int y)
    {
        if (Items.Count == 0 || _tops.Length != Items.Count + 1 || y < 0 || y >= _tops[^1]) return -1;
        var index = Array.BinarySearch(_tops, y);
        return index >= 0 ? Math.Min(index, Items.Count - 1) : ~index - 1;
    }
    public void Remeasure()
    {
        if (_updates > 0 || _measuring || Items == null || !IsHandleCreated) return;
        _measuring = true;
        try
        {
            var top = TopIndex;
            var offset = top < _tops.Length ? -AutoScrollPosition.Y - _tops[top] : 0;
            var tops = new int[Items.Count + 1];
            using var graphics = CreateGraphics();
            for (var i = 0; i < Items.Count; i++)
            {
                var measure = new MeasureItemEventArgs(graphics, i, Font.Height + 16);
                MeasureItem?.Invoke(this, measure);
                tops[i + 1] = tops[i] + Math.Max(1, measure.ItemHeight);
            }
            _tops = tops;
            AutoScrollMinSize = new Size(0, tops[^1]);
            VerticalScroll.SmallChange = Math.Max(24, Font.Height * 2);
            if (Items.Count > 0) AutoScrollPosition = new Point(0, Math.Max(0, _tops[Math.Min(top, Items.Count - 1)] + offset));
            Invalidate();
        }
        finally { _measuring = false; }
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Remeasure(); }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Remeasure(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Remeasure(); }
    protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); Invalidate(); }
    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        base.OnMouseDown(e); // observers see the previous selection for click-to-deselect
        if (e.Button == MouseButtons.Left) SelectedIndex = IndexFromPoint(e.Location);
    }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is
        Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var next = e.KeyCode switch
        {
            Keys.Up => Math.Max(0, SelectedIndex - 1), Keys.Down => Math.Min(Items.Count - 1, SelectedIndex + 1),
            Keys.Home => 0, Keys.End => Items.Count - 1,
            Keys.PageUp => Math.Max(0, IndexAt(Math.Max(0, (SelectedIndex >= 0 ? _tops[SelectedIndex] : 0) - ClientSize.Height))),
            Keys.PageDown => IndexAt(Math.Min(_tops[^1] - 1, (SelectedIndex >= 0 ? _tops[SelectedIndex] : 0) + ClientSize.Height)),
            _ => -2,
        };
        if (next != -2) { SelectedIndex = next; e.Handled = true; }
        base.OnKeyDown(e);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        if (_tops.Length == Items.Count + 1 && Items.Count > 0)
        {
            for (var i = TopIndex; i < Items.Count; i++)
            {
                var bounds = new Rectangle(0, _tops[i] + AutoScrollPosition.Y, ClientSize.Width, GetItemHeight(i));
                if (bounds.Top >= e.ClipRectangle.Bottom) break;
                if (!bounds.IntersectsWith(e.ClipRectangle)) continue;
                var state = i == SelectedIndex ? DrawItemState.Selected : DrawItemState.None;
                if (i == SelectedIndex && Focused) state |= DrawItemState.Focus;
                DrawItem?.Invoke(this, new DrawItemEventArgs(e.Graphics, Font, bounds, i, state, ForeColor, BackColor));
            }
        }
        base.OnPaint(e); // empty-state paint handler
    }
}
