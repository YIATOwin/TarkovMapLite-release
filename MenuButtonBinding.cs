namespace TarkovMapLite;

/// <summary>自绘按钮的菜单开关。先截获重复点击，避免自动关闭后 Click 又把菜单打开。</summary>
internal sealed class MenuButtonBinding : IMessageFilter, IDisposable
{
    private readonly UiButton _button;
    private readonly ContextMenuStrip _menu;
    private readonly Action _prepare;
    private bool _disposed;
    private Form? _form;

    public MenuButtonBinding(UiButton button, ContextMenuStrip menu, Action prepare)
    {
        _button = button;
        _menu = menu;
        _prepare = prepare;
        // 由同一个消息过滤器处理按钮和外部点击，避免系统先关闭、按钮又重新打开。
        menu.AutoClose = false;
        button.HandleCreated += AttachOwnerForm;
        AttachOwnerForm(button, EventArgs.Empty);
        button.Click += Toggle;
        button.Disposed += OwnerDisposed;
        menu.Opened += Opened;
        menu.Closed += Closed;
        Application.AddMessageFilter(this);
    }

    private void Toggle(object? sender, EventArgs e)
    {
        if (_menu.Visible)
        {
            _menu.Close();
            return;
        }
        _prepare();
        _menu.Show(_button, new Point(0, _button.Height));
    }

    public bool PreFilterMessage(ref Message m)
    {
        if (!_menu.Visible) return false;
        if (m.Msg == 0x0100 && (Keys)m.WParam == Keys.Escape)
        {
            _menu.Close();
            return true;
        }
        if (m.Msg is 0x0201 or 0x0203 or 0x0204 or 0x00A1)
        {
            var point = Cursor.Position;
            if (_button.RectangleToScreen(_button.ClientRectangle).Contains(point))
            {
                _menu.Close();
                return true;
            }
            if (!Contains(_menu, point)) _menu.Close();
        }
        return false;
    }

    private static bool Contains(ToolStripDropDown menu, Point point) =>
        menu.Visible && (menu.Bounds.Contains(point) || menu.Items.OfType<ToolStripMenuItem>()
            .Any(item => item.HasDropDownItems && Contains(item.DropDown, point)));

    private void Deactivated(object? sender, EventArgs e) => _menu.Close();

    private void AttachOwnerForm(object? sender, EventArgs e)
    {
        var form = _button.FindForm();
        if (form == _form) return;
        if (_form != null) _form.Deactivate -= Deactivated;
        _form = form;
        if (_form != null) _form.Deactivate += Deactivated;
    }

    private void Opened(object? sender, EventArgs e) => _button.Active = true;
    private void Closed(object? sender, ToolStripDropDownClosedEventArgs e) => _button.Active = false;
    private void OwnerDisposed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Application.RemoveMessageFilter(this);
        if (_form != null) _form.Deactivate -= Deactivated;
        _button.Click -= Toggle;
        _button.Disposed -= OwnerDisposed;
        _button.HandleCreated -= AttachOwnerForm;
        _menu.Opened -= Opened;
        _menu.Closed -= Closed;
        _menu.Dispose();
    }
}
