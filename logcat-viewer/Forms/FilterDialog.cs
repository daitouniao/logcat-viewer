using logcat.Services;

namespace logcat.Forms;

/// <summary>
/// 过滤设置窗口：承载主窗体构建好的过滤面板（级别 / Tag / Message / PID / TID / 分钟 / 仅标记行 等）。
/// 采用非模态、作为主窗体的 Owned 窗口，因此始终悬浮在主窗口之上，可与日志列表并行操作。
/// 面板控件实例仍归主窗体所有，这里关闭只是隐藏，过滤状态不会丢失。
/// </summary>
public class FilterDialog : Form
{
    readonly Action? _applyAll;
    readonly Control _filterPanel;

    /// <param name="filterPanel">主窗体已构建好的过滤面板控件。</param>
    /// <param name="applyAll">Ctrl+Enter 时触发，交回主窗体应用过滤。</param>
    public FilterDialog(Control filterPanel, Action? applyAll = null)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        _applyAll = applyAll;
        _filterPanel = filterPanel;

        Text = Loc.T("过滤设置");
        Loc.Bind(this, "过滤设置");   // 登记资源键，切语言时 ApplyTo 才能重设标题
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        KeyPreview = true;
        Size = new Size(1260, 205);
        MinimumSize = new Size(760, 190);

        filterPanel.Dock = DockStyle.Fill;
        Controls.Add(filterPanel);

        // 高缩放屏（150%）下行高/字体变大，固定高度会裁掉底部行；
        // 显示与 DPI 切换后都按面板实测的期望尺寸重设窗口
        Shown += (_, _) =>
        {
            // 兜底刷一次当前语言：本窗口在首次 Show 之前不在 Application.OpenForms 里，
            // Loc.ApplyToAllForms 扫不到（宿主 frmMain 也会在 OnLanguageChanged 里补刷一次，
            // 这里防的是「构造时语言 ≠ 首次显示时语言」的其他路径）。
            // Shown 早于宿主调用的 PositionAboveOwner，刷完再量尺寸才不会量到旧文案的高度。
            Loc.ApplyTo(this);
            DpiDiag.Log("FilterDialog.Shown", this);
            LogPanelFit("Shown");
        };
        DpiChanged += (_, e) =>
        {
            DpiDiag.Write($"FilterDialog.DpiChanged dpi→{DeviceDpi} suggested={e.SuggestedRectangle} bounds={Bounds}");
            // 推迟到系统缩放与布局完成后再量，否则量到的是缩放前的旧值
            BeginInvoke(() => PositionAboveOwner());
        };

        // 主窗体的 Ctrl+Enter 只在主窗体获得焦点时生效，焦点在本窗口时补一份
        KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.Enter) { _applyAll?.Invoke(); e.Handled = true; }
        };
    }

    /// <summary>按过滤面板实测的期望尺寸撑开窗口（只放大不缩小），再贴到主窗口顶部居中。</summary>
    public void PositionAboveOwner()
    {
        FitToContent();
        if (Owner == null) return;
        var b = Owner.Bounds;
        int x = b.X + (b.Width - Width) / 2;
        int y = b.Y + 40;
        var wa = Screen.FromControl(Owner).WorkingArea;
        x = Math.Clamp(x, wa.Left, Math.Max(wa.Left, wa.Right - Width));
        y = Math.Clamp(y, wa.Top, Math.Max(wa.Top, wa.Bottom - Height));
        Location = new Point(x, y);
    }

    /// <summary>窗口高度改为「面板内容实际需要的高度」，宽度不够时也放大；已够则不动。</summary>
    void FitToContent()
    {
        try
        {
            // GroupBox 非 AutoSize 时 GetPreferredSize 只返回占位值（实测 6x22），
            // 不能用它；改为按内部 TableLayoutPanel 里各行的实际底边推算需要的高度，
            // 行本身是 AutoSize，其高度始终跟随字体/DPI，是可靠的量测来源
            if (_filterPanel.Controls.Count == 0) return;
            var tlp = _filterPanel.Controls[0];

            int contentBottom = 0, contentRight = 0;
            foreach (Control row in tlp.Controls)
            {
                contentBottom = Math.Max(contentBottom, row.Bottom + row.Margin.Bottom);
                int rowRight = 0;
                foreach (Control c in row.Controls) rowRight += c.Width + c.Margin.Horizontal;
                contentRight = Math.Max(contentRight, rowRight + tlp.Left);
            }
            // 加上 GroupBox 的标题/内边距（用实际差值推算，不依赖常量）
            int needPanelH = tlp.Top + contentBottom + (_filterPanel.Height - tlp.Bottom);
            int needPanelW = contentRight + (_filterPanel.Width - tlp.Right);
            // 面板 Dock=Fill 占满客户区，客户区需要的尺寸 = 面板需要的尺寸
            var nonClient = Size - ClientSize;
            int w = Math.Max(ClientSize.Width, needPanelW + (ClientSize.Width - _filterPanel.Width));
            int h = Math.Max(ClientSize.Height, needPanelH + (ClientSize.Height - _filterPanel.Height));
            if (w != ClientSize.Width || h != ClientSize.Height)
                Size = new Size(w + nonClient.Width, h + nonClient.Height);
            DpiDiag.Write($"FilterDialog.Fit: client={ClientSize} needPanel={needPanelW}x{needPanelH}");
        }
        catch (Exception ex)
        {
            DpiDiag.Write($"FilterDialog.FitToContent 异常: {ex.Message}");
        }
    }

    void LogPanelFit(string when)
    {
        try
        {
            DpiDiag.Write($"FilterDialog.{when}: client={ClientSize} panelActual={_filterPanel.Size}");
        }
        catch { /* 诊断不影响主流程 */ }
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
