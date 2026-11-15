using System.Runtime.InteropServices;

namespace TarkovMapLite;

/// <summary>支持悬停滚轮的只读任务描述，不抢任务列表或搜索框的键盘焦点。</summary>
internal sealed class TaskDescriptionBox : RichTextBox, IMessageFilter
{
    private const int WmMouseWheel = 0x020A;
    private int _wheelRemainder;

    public TaskDescriptionBox()
    {
        ReadOnly = true;
        ScrollBars = RichTextBoxScrollBars.Vertical;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Application.AddMessageFilter(this);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Application.RemoveMessageFilter(this);
        _wheelRemainder = 0;
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmMouseWheel)
        {
            // RichEdit 在未聚焦时可能忽略原生滚轮；直接按行滚动，不通过抢焦点来解决。
            // 保留不足一格的增量，兼容高精度滚轮和触控板。
            _wheelRemainder += (short)((m.WParam.ToInt64() >> 16) & 0xffff);
            var notches = _wheelRemainder / 120;
            _wheelRemainder %= 120;
            var lines = SystemInformation.MouseWheelScrollLines;
            if (lines < 0) lines = Math.Max(1, ClientSize.Height / Font.Height - 1);
            if (notches != 0 && lines > 0)
                SendMessage(Handle, 0x00B6 /* EM_LINESCROLL */, IntPtr.Zero, (IntPtr)(-notches * lines));
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != WmMouseWheel || !IsHandleCreated || !Visible || !Enabled || m.HWnd == Handle)
            return false;
        var packed = m.LParam.ToInt64();
        var screenPoint = new Point((short)(packed & 0xffff), (short)((packed >> 16) & 0xffff));
        // 用真实命中窗口判断，避免描述区域上方的菜单或其它窗口被抢走滚轮。
        if (WindowFromPoint(screenPoint) != Handle) return false;
        SendMessage(Handle, WmMouseWheel, m.WParam, m.LParam);
        return true;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
