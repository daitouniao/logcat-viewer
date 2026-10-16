using logcat.Models;
using logcat.Services;

namespace logcat.Forms;

/// <summary>
/// 崩溃汇总窗口：独立非模态弹出窗口，按「类别 × 异常类型 × 包名」聚合全量崩溃事件，
/// 次数降序列出（不设上限）；点击行 → 主窗口定位到对应崩溃位置，窗口保持打开可连续逐条核对。
/// 沿用 FilterDialog 的非模态范式：Owned 窗口悬浮于主窗口之上，UserClosing 只隐藏不销毁。
/// </summary>
public class CrashSummaryForm : Form, ILocalizedUi
{
    readonly ListView _list;
    readonly Label _empty;
    List<CrashGroup> _groups = new();
    int _baseYear = DateTime.Now.Year;

    /// <summary>点击汇总行 → 主窗口定位到该组首次发生的文档行号。</summary>
    public event Action<int>? RowActivate;

    public CrashSummaryForm()
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        Text = Loc.T("崩溃汇总");
        Loc.Bind(this, "崩溃汇总");   // 登记资源键，切语言时 ApplyTo 才能重设标题
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        KeyPreview = true;
        Size = new Size(880, 420);
        MinimumSize = new Size(560, 240);

        _list = new BufferedListView
        {
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            VirtualMode = true,
            HideSelection = false,
            Dock = DockStyle.Fill,
        };
        // 列头必须 Loc.BindCol 登记资源键（只赋 Text 的话切语言不刷新）
        var cols = new (string zh, int w)[]
        {
            ("类别", 150), ("异常类型", 230), ("包名", 230),
            ("次数", 70), ("首次", 130), ("末次", 130),
        };
        foreach (var (zh, w) in cols)
        {
            var col = new ColumnHeader { Width = w };
            _list.Columns.Add(col);
            Loc.BindCol(col, zh);
        }
        _list.RetrieveVirtualItem += OnRetrieveItem;
        _list.ItemSelectionChanged += OnItemSelected;
        Controls.Add(_list);

        // 空态：「无崩溃」（绑定资源键，切语言自动刷新）
        _empty = new Label
        {
            Text = Loc.T("无崩溃"),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.Transparent,
        };
        Loc.Bind(_empty, "无崩溃");
        _empty.BringToFront();
        Controls.Add(_empty);
    }

    /// <summary>用文档的崩溃清单重建汇总。文档为 null 或无崩溃时显示空态。</summary>
    public void RefreshFrom(LogDocument? doc)
    {
        _baseYear = doc?.BaseYear ?? DateTime.Now.Year;
        _groups = doc == null
            ? new List<CrashGroup>()
            : CrashAggregator.Aggregate(doc.Crashes, doc.ExcTypes, doc.Pkgs);
        _list.VirtualListSize = _groups.Count;
        _empty.Visible = _groups.Count == 0;
        _list.Invalidate();
    }

    void OnRetrieveItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        int i = e.ItemIndex;
        var item = new ListViewItem("");
        for (int c = 1; c < _list.Columns.Count; c++) item.SubItems.Add("");
        if (i >= 0 && i < _groups.Count)
        {
            var g = _groups[i];
            string first = TimeText(g.FirstTs), last = TimeText(g.LastTs);
            item.Text = CrashKind.Name(g.Kind);                                    // 类别
            item.SubItems[1].Text = g.ExcType.Length > 0 ? g.ExcType : "-";        // 异常类型
            item.SubItems[2].Text = g.Pkg.Length > 0 ? g.Pkg : "-";                // 包名
            item.SubItems[3].Text = g.Count.ToString("N0");                        // 次数
            item.SubItems[4].Text = first;                                         // 首次
            item.SubItems[5].Text = last;                                          // 末次
        }
        e.Item = item;
    }

    /// <summary>时间戳 → "MM-dd HH:mm:ss"（跨天日志必须带日期）；未知显示 "-"。</summary>
    string TimeText(long ts)
    {
        var s = LogDocument.TsToText(ts, _baseYear, withMs: false);
        return s.Length > 0 ? s : "-";
    }

    void OnItemSelected(object? sender, ListViewItemSelectionChangedEventArgs e)
    {
        if (!e.IsSelected) return;
        int i = e.ItemIndex;
        if (i >= 0 && i < _groups.Count) RowActivate?.Invoke(_groups[i].FirstRow);
    }

    /// <summary>贴到主窗口中部（首次显示定位）。</summary>
    public void PositionOverOwner()
    {
        if (Owner == null) return;
        var b = Owner.Bounds;
        int x = b.X + (b.Width - Width) / 2 + 60;
        int y = b.Y + (b.Height - Height) / 2;
        var wa = Screen.FromControl(Owner).WorkingArea;
        Location = new Point(
            Math.Clamp(x, wa.Left, Math.Max(wa.Left, wa.Right - Width)),
            Math.Clamp(y, wa.Top, Math.Max(wa.Top, wa.Bottom - Height)));
    }

    /// <summary>语言切换：行文案（类别名等）由 RetrieveVirtualItem 现算，重绘即可。</summary>
    public void OnLanguageChanged() => _list.Invalidate();

    // 用户点关闭按钮只是收起窗口（与 FilterDialog 一致），下次打开直接复用
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

    /// <summary>DoubleBuffered 是 protected 属性，包一层子类开启（消除汇总列表重绘闪烁）。</summary>
    sealed class BufferedListView : ListView
    {
        public BufferedListView() => DoubleBuffered = true;
    }
}
