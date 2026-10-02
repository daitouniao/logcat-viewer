using logcat.Services;

namespace logcat.Controls;

/// <summary>
/// 虚拟模式 ListView：只渲染可见行，字段按需从 LogDocument 解码并缓存。
/// </summary>
public class LogListView : ListView
{
    // 列定义
    public static readonly string[] COLUMNS = { "#", "Time", "PID", "TID", "Lv", "Tag", "Message" };
    public const int COL_ROW = 0, COL_TIME = 1, COL_PID = 2, COL_TID = 3, COL_LVL = 4, COL_TAG = 5, COL_MSG = 6;
    const int MSG_LIMIT = 4000;

    // 级别颜色
    static readonly Color[] LEVEL_COLORS =
    {
        Color.Empty,                    // 0 unknown
        Color.FromArgb(140, 140, 140),  // V
        Color.FromArgb(26, 115, 232),   // D
        Color.FromArgb(24, 128, 56),    // I
        Color.FromArgb(176, 96, 0),     // W
        Color.FromArgb(198, 40, 40),    // E
        Color.FromArgb(142, 36, 170),   // F
        Color.FromArgb(106, 27, 154),   // A
    };
    static readonly Color MARK_BG = Color.FromArgb(255, 233, 168);

    // 数据源
    LogDocument? _doc;
    int[] _rows = Array.Empty<int>();
    HashSet<int> _marked = new();
    string _newlineVis = "↵";
    int _fontPt = 10;

    // 缓存
    readonly Dictionary<int, ListViewItem> _cache = new();
    const int CACHE_SIZE = 20000;

    public LogDocument? Document { get => _doc; }
    public int[] Rows { get => _rows; }
    public HashSet<int> Marked { get => _marked; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string NewlineVis { get => _newlineVis; set { _newlineVis = value; ClearCache(); Invalidate(); } }
    public int FontPt { get => _fontPt; }

    public event EventHandler<int>? RowMarkToggled;
    public event EventHandler? Scrolled;

    public LogListView()
    {
        View = View.Details;
        FullRowSelect = true;
        VirtualMode = true;
        GridLines = false;
        HideSelection = false;
        DoubleBuffered = true;
        Font = new Font("Consolas", _fontPt);

        foreach (var col in COLUMNS)
            Columns.Add(col);

        // 默认列宽
        int[] colWidths = { 90, 175, 68, 68, 34, 180, 900 };
        for (int i = 0; i < Columns.Count && i < colWidths.Length; i++)
            Columns[i].Width = colWidths[i];

        RetrieveVirtualItem += OnRetrieveVirtualItem;
        CacheVirtualItems += OnCacheVirtualItems;
    }

    static int[] DefaultColumnWidths { get; } = { 90, 175, 68, 68, 34, 180, 900 };

    // ── 数据切换 ──
    public void SetDocument(LogDocument? doc)
    {
        _doc = doc;
        _rows = Array.Empty<int>();
        ClearCache();
        VirtualListSize = 0;
    }

    public void SetRows(int[] rows)
    {
        _rows = rows;
        ClearCache();
        // 抑制中间重绘，避免实时采集每秒刷新时闪屏
        BeginUpdate();
        try { VirtualListSize = rows.Length; }
        finally { EndUpdate(); }
    }

    public void AppendRows(int[] newRows)
    {
        if (newRows.Length == 0) return;
        int oldLen = _rows.Length;
        var combined = new int[oldLen + newRows.Length];
        Array.Copy(_rows, combined, oldLen);
        Array.Copy(newRows, 0, combined, oldLen, newRows.Length);
        _rows = combined;
        VirtualListSize = _rows.Length;
    }

    public void SetMarked(HashSet<int> marked)
    {
        _marked = marked;
        ClearCache();
    }

    public void ClearMarks()
    {
        _marked.Clear();
        ClearCache();
    }

    public void ToggleMark(int docRow)
    {
        if (docRow < 0) return;
        if (!_marked.Remove(docRow))
            _marked.Add(docRow);
        ClearCache();
        RowMarkToggled?.Invoke(this, docRow);
    }

    // ── 视图状态快照（刷新时保持选中与滚动位置）──
    public readonly record struct ViewSnapshot(int TopDocRow, int[] SelectedDocRows);

    const int LVM_FIRST = 0x1000;
    const int LVM_GETTOPINDEX = LVM_FIRST + 39;
    const int LVM_SCROLL = LVM_FIRST + 20;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lp);

    /// <summary>记录当前视口顶行与选中行（按文档行号，刷新后可重新映射）。</summary>
    public ViewSnapshot CaptureView()
    {
        int top = -1;
        try { if (TopItem != null) top = DocRow(TopItem.Index); } catch { }
        var sel = new List<int>();
        foreach (int i in SelectedIndices)
        {
            int dr = DocRow(i);
            if (dr >= 0) sel.Add(dr);
        }
        return new ViewSnapshot(top, sel.ToArray());
    }

    /// <summary>刷新后恢复快照。restoreTop=false 时只恢复选中，不动滚动位置（跟随尾部场景）。</summary>
    public void RestoreView(ViewSnapshot snap, bool restoreTop = true)
    {
        try
        {
            if (VirtualListSize == 0 || IsDisposed) return;
            int focus = -1;
            foreach (var dr in snap.SelectedDocRows)
            {
                int mr = ModelRowOf(dr);
                if (mr < 0) continue;
                SelectedIndices.Add(mr);
                if (focus < 0) focus = mr;
            }
            if (focus >= 0 && !Items[focus].Focused)
                Items[focus].Focused = true;
            if (restoreTop && snap.TopDocRow >= 0)
            {
                int mr = ModelRowOf(snap.TopDocRow);
                if (mr >= 0) SetTopModelRow(mr);
            }
        }
        catch { }
    }

    /// <summary>把指定模型行滚动到视口顶部（LVM_SCROLL 按像素滚动，虚拟模式下比 TopItem setter 可靠）。</summary>
    public void SetTopModelRow(int modelRow)
    {
        if (!IsHandleCreated || modelRow < 0 || VirtualListSize == 0) return;
        modelRow = Math.Min(modelRow, VirtualListSize - 1);
        try
        {
            int cur = (int)SendMessage(Handle, LVM_GETTOPINDEX, IntPtr.Zero, IntPtr.Zero);
            if (cur < 0) cur = 0;
            int delta = modelRow - cur;
            if (delta == 0) return;
            int rowH = Font.Height + 6;
            try
            {
                int a = Math.Min(cur, VirtualListSize - 1);
                int b = Math.Min(a + 1, VirtualListSize - 1);
                if (b > a)
                {
                    int h = Items[b].Position.Y - Items[a].Position.Y;
                    if (h > 0) rowH = h;
                }
            }
            catch { }
            SendMessage(Handle, LVM_SCROLL, IntPtr.Zero, (IntPtr)(delta * rowH));
        }
        catch { }
    }

    // ── 过滤锚点：过滤/取消过滤后把选中行（或最近存活行）固定回原屏幕位置 ──

    /// <summary>捕获锚点：选中行的文档行号 + 它相对视口顶部的行偏移。无选中时以视口顶行为锚（偏移 0）。</summary>
    public (int docRow, int offsetFromTop) CaptureAnchor()
    {
        int sel = -1;
        foreach (int i in SelectedIndices) { sel = i; break; }
        int top = -1;
        try { top = (int)SendMessage(Handle, LVM_GETTOPINDEX, IntPtr.Zero, IntPtr.Zero); } catch { }
        if (sel < 0) sel = top;
        if (sel < 0 || top < 0) return (-1, 0);
        int dr = DocRow(sel);
        if (dr < 0) return (-1, 0);
        return (dr, Math.Max(0, sel - top));
    }

    /// <summary>
    /// 过滤完成后固定锚点：若原行仍在结果中，恢复到原屏幕偏移位置；
    /// 若被过滤掉，取文档行号最近的存活行（前后取更近者）固定在该位置，并选中它。
    /// 返回实际固定的文档行号（失败返回 -1）。
    /// </summary>
    public int PinAnchor(int anchorDocRow, int offsetFromTop)
    {
        if (anchorDocRow < 0 || VirtualListSize == 0 || IsDisposed) return -1;
        int mr = ModelRowOf(anchorDocRow);
        if (mr < 0) mr = NearestModelRow(anchorDocRow);
        if (mr < 0) return -1;

        SelectedIndices.Clear();
        SelectedIndices.Add(mr);
        try { Items[mr].Focused = true; } catch { }

        // 把目标行滚到原偏移处（超界时会被 clamp 到列表末尾）
        int target = Math.Max(0, mr - Math.Max(0, offsetFromTop));
        SetTopModelRow(Math.Min(target, VirtualListSize - 1));
        return DocRow(mr);
    }

    /// <summary>在 _rows（升序文档行号）中找与 docRow 最近的存活行。</summary>
    public int NearestModelRow(int docRow)
    {
        if (_rows.Length == 0) return -1;
        int pos = Array.BinarySearch(_rows, docRow);
        if (pos >= 0) return pos;
        int after = ~pos;          // 第一个大于 docRow 的位置
        int before = after - 1;
        if (before < 0 && after >= _rows.Length) return -1;
        if (before < 0) return after;
        if (after >= _rows.Length) return before;
        long dB = (long)docRow - _rows[before];
        long dA = (long)_rows[after] - docRow;
        return dA < dB ? after : before;
    }

    // ── 行号换算 ──
    public int DocRow(int modelRow) =>
        modelRow >= 0 && modelRow < _rows.Length ? _rows[modelRow] : -1;

    public int ModelRowOf(int docRow)
    {
        int pos = Array.BinarySearch(_rows, docRow);
        return pos >= 0 ? pos : -1;
    }

    // ── 字号 ──
    public void SetFont(int pt)
    {
        _fontPt = pt;
        Font = new Font("Consolas", pt);
    }

    // ── 完整文本 ──
    public string FullText(int docRow)
    {
        if (_doc == null || docRow < 0) return "";
        return _doc.Decode(_doc.LineBytes(docRow));
    }

    // ── 虚拟列表事件 ──
    void OnRetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        int idx = e.ItemIndex;
        if (_cache.TryGetValue(idx, out var cached))
        {
            e.Item = cached;
            return;
        }
        e.Item = MakeItem(idx);
    }

    void OnCacheVirtualItems(object? sender, CacheVirtualItemsEventArgs e)
    {
        // 预缓存：不需要额外操作，RetrieveVirtualItem 会按需创建
    }

    ListViewItem MakeItem(int modelRow)
    {
        // 虚拟模式要求每个 item 的 SubItems 数量必须匹配列数
        var item = new ListViewItem();
        for (int c = 1; c < COLUMNS.Length; c++)
            item.SubItems.Add("");

        try
        {
            if (_doc == null || modelRow < 0 || modelRow >= _rows.Length)
                return item;

            int docRow = _rows[modelRow];
            if (docRow < 0 || docRow >= _doc.RowCount)
                return item;

            var fields = MakeFields(docRow);

            // 行号列
            string rowText = _marked.Contains(docRow) ? $"● {docRow + 1}" : (docRow + 1).ToString();
            item.Text = rowText;
            // fields[0..] 对应 SubItems[1..]（SubItems[0] 是行号列本身）
            for (int c = 0; c < fields.Length; c++)
                item.SubItems[c + 1] = new ListViewItem.ListViewSubItem(item, fields[c]);

            // 级别颜色
            int lvl = docRow < _doc.Lvl.Length ? _doc.Lvl[docRow] : 0;
            if (lvl > 0 && lvl < LEVEL_COLORS.Length && LEVEL_COLORS[lvl] != Color.Empty)
            {
                item.ForeColor = LEVEL_COLORS[lvl];
                for (int c = 0; c < item.SubItems.Count; c++)
                    item.SubItems[c].ForeColor = LEVEL_COLORS[lvl];
            }

            // 标记背景
            if (_marked.Contains(docRow))
            {
                item.BackColor = MARK_BG;
                for (int c = 0; c < item.SubItems.Count; c++)
                    item.SubItems[c].BackColor = MARK_BG;
            }

            // 缓存
            _cache[modelRow] = item;
            if (_cache.Count > CACHE_SIZE)
            {
                var keysToRemove = _cache.Keys.Take(_cache.Count - CACHE_SIZE / 2).ToArray();
                foreach (var k in keysToRemove) _cache.Remove(k);
            }
        }
        catch
        {
            // 后台 Reload 与 UI 渲染竞态：返回空行，下次刷新会修正
        }
        return item;
    }

    string[] MakeFields(int docRow)
    {
        // Time
        string timeStr;
        byte[] hb = _doc!.HeaderBytes(docRow);
        if (hb.Length > 0)
            timeStr = _doc.Decode(hb);
        else
            timeStr = LogDocument.TsToText(_doc.Ts[docRow], _doc.BaseYear);

        int pid = _doc.Pid[docRow], tid = _doc.Tid[docRow];
        int lvl = _doc.Lvl[docRow];

        // Message
        string msg = _doc.Decode(_doc.MessageBytes(docRow));
        if (_newlineVis != null && (msg.Contains('\n') || msg.Contains('\r')))
            msg = msg.Replace("\r\n", _newlineVis).Replace("\n", _newlineVis).Replace("\r", _newlineVis);
        if (msg.Length > MSG_LIMIT)
            msg = msg[..MSG_LIMIT] + $" …(+{msg.Length - MSG_LIMIT} 字符)";

        string lvlName = lvl > 0 && lvl < LogParser.LEVEL_NAME.Length ? LogParser.LEVEL_NAME[lvl] : "";

        return new[]
        {
            timeStr,
            pid >= 0 ? pid.ToString() : "",
            tid >= 0 ? tid.ToString() : "",
            lvlName,
            _doc.TagOf(docRow),
            msg
        };
    }

    void ClearCache() => _cache.Clear();

    // ── 滚动检测 ──
    const int WM_VSCROLL = 0x115;
    const int WM_HSCROLL = 0x114;
    const int WM_MOUSEWHEEL = 0x020A;
    const int WM_KEYDOWN = 0x0100;

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg is WM_VSCROLL or WM_HSCROLL or WM_MOUSEWHEEL)
            Scrolled?.Invoke(this, EventArgs.Empty);
        else if (m.Msg == WM_KEYDOWN)
        {
            int key = (int)m.WParam;
            if (key is >= 0x25 and <= 0x28) // arrow keys
                Scrolled?.Invoke(this, EventArgs.Empty);
        }
    }
}
