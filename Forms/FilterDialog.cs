namespace logcat.Forms;

/// <summary>
/// 过滤设置窗口：承载主窗体构建好的过滤面板（级别 / Tag / Message / PID / TID / 分钟 / 仅标记行 等）。
/// 采用非模态、作为主窗体的 Owned 窗口，因此始终悬浮在主窗口之上，可与日志列表并行操作。
/// 面板控件实例仍归主窗体所有，这里关闭只是隐藏，过滤状态不会丢失。
/// </summary>
public class FilterDialog : Form
{
    readonly Action? _applyAll;

    /// <param name="filterPanel">主窗体已构建好的过滤面板控件。</param>
    /// <param name="applyAll">Ctrl+Enter 时触发，交回主窗体应用过滤。</param>
    public FilterDialog(Control filterPanel, Action? applyAll = null)
    {
        _applyAll = applyAll;

        Text = "过滤设置";
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        KeyPreview = true;
        Size = new Size(1260, 205);
        MinimumSize = new Size(760, 190);

        filterPanel.Dock = DockStyle.Fill;
        Controls.Add(filterPanel);

        // 主窗体的 Ctrl+Enter 只在主窗体获得焦点时生效，焦点在本窗口时补一份
        KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.Enter) { _applyAll?.Invoke(); e.Handled = true; }
        };
    }

    /// <summary>贴到主窗口顶部并水平居中，避免遮住日志列表的可视区域。</summary>
    public void PositionAboveOwner()
    {
        if (Owner == null) return;
        var b = Owner.Bounds;
        int x = b.X + (b.Width - Width) / 2;
        int y = b.Y + 40;
        var wa = Screen.FromControl(Owner).WorkingArea;
        x = Math.Clamp(x, wa.Left, Math.Max(wa.Left, wa.Right - Width));
        y = Math.Clamp(y, wa.Top, Math.Max(wa.Top, wa.Bottom - Height));
        Location = new Point(x, y);
    }

    // 用户点关闭按钮只是收起窗口，控件实例仍要留给主窗体继续使用
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }
}
