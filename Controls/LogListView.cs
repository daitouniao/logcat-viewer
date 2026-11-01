using logcat.Models;
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
    HashSet<int> _marked = new();
    int _fontPt = 10;

    // ── 分段行号结构（design 决策 6）──
    // 原「整表替换数组」在分块加载下逐块整表拷贝，O(n²) 已实测成立（4 MB 块占 9%、1 MB 占 26%）。
    // 改为分段块数组 + 段前缀和：AppendRows 只填尾段或新建段，不再整表拷贝。
    // 不变式：段内升序、段间升序拼接，整体等价于一条升序文档行号序列（DocRow/ModelRowOf/NearestModelRow 语义不变）。
    readonly struct Seg
    {
        public readonly int[] Buf;   // 容量缓冲
        public readonly int Len;     // 有效长度（<= Buf.Length）
        public Seg(int[] buf, int len) { Buf = buf; Len = len; }
    }
    readonly List<Seg> _segs = new();
    int[] _starts = Array.Empty<int>();   // 段起始模型行号前缀和：_starts[i] = 段 i 起点，_starts[段数] = 总行数
    int _rowCount;

    /// <summary>把分段起点前缀和重建到 _starts。仅段列表结构变化（增段）时调用，O(段数)。</summary>
    void RefreshStarts()
    {
        if (_starts.Length < _segs.Count + 1)
            _starts = new int[(_segs.Count + 1) * 2];
        int acc = 0;
        for (int i = 0; i < _segs.Count; i++) { _starts[i] = acc; acc += _segs[i].Len; }
        _starts[_segs.Count] = acc;
        _rowCount = acc;
    }

    // 缓存
    readonly Dictionary<int, ListViewItem> _cache = new();
    const int CACHE_SIZE = 20000;

    public LogDocument? Document { get => _doc; }
    /// <summary>模型行总数（分段结构的整表行数，O(1)）。</summary>
    public int RowCount => _rowCount;
    public HashSet<int> Marked { get => _marked; }
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
        _segs.Clear();
        RefreshStarts();
        ClearCache();
        VirtualListSize = 0;
    }

    public void SetRows(int[] rows)
    {
        _segs.Clear();
        // 语义同旧整表实现：取得数组所有权，不复制
        if (rows.Length > 0)
            _segs.Add(new Seg(rows, rows.Length));
        RefreshStarts();
        ClearCache();
        // 抑制中间重绘，避免实时采集每秒刷新时闪屏
        BeginUpdate();
        try { VirtualListSize = _rowCount; }
        finally { EndUpdate(); }
    }

    public void AppendRows(int[] newRows)
    {
        if (newRows.Length == 0) return;
        int n = newRows.Length;

        if (_segs.Count == 0)
        {
            // 空表：防御性复制（旧实现也复制调用方数组）
            var buf = new int[n];
            Array.Copy(newRows, buf, n);
            _segs.Add(new Seg(buf, n));
            RefreshStarts();
        }
        else
        {
            var tail = _segs[^1];
            int free = tail.Buf.Length - tail.Len;
            if (n <= free)
            {
                // 尾段容量足够：原地填入，段结构不变
                Array.Copy(newRows, 0, tail.Buf, tail.Len, n);
                _segs[^1] = new Seg(tail.Buf, tail.Len + n);
                _rowCount += n;
                _starts[_segs.Count] = _rowCount;   // 仅末端边界变化
            }
            else
            {
                // 尾段放不下：新建段（容量留 50% 余量，吸收后续小追加），原尾段冻结
                var buf = new int[n + (n >> 1)];
                Array.Copy(newRows, buf, n);
                _segs.Add(new Seg(buf, n));
                RefreshStarts();
            }
        }
        VirtualListSize = _rowCount;
    }

    /// <summary>整表快照（导出等一次性消费；分段结构 O(n) 物化，勿在热路径调用）。</summary>
    public int[] GetRowsCopy()
    {
        var all = new int[_rowCount];
        int w = 0;
        for (int i = 0; i < _segs.Count; i++)
        {
            var s = _segs[i];
            Array.Copy(s.Buf, 0, all, w, s.Len);
            w += s.Len;
        }
        return all;
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

    /// <summary>在升序文档行号序列（分段拼接）中找与 docRow 最近的存活行。</summary>
    public int NearestModelRow(int docRow)
    {
        if (_rowCount == 0) return -1;
        int seg = SegLastGe(docRow);
        if (seg < 0) return _rowCount - 1;   // 所有段末元素都 < docRow → 最近的是末行

        var t = _segs[seg];
        int bs = Array.BinarySearch(t.Buf, 0, t.Len, docRow);
        if (bs >= 0) return _starts[seg] + bs;
        int idx = ~bs;                       // 段内插入点
        int after = idx < t.Len ? _starts[seg] + idx : -1;
        int before = idx > 0 ? _starts[seg] + idx - 1
                   : seg > 0 ? _starts[seg] - 1 : -1;
        if (before < 0) return after;
        if (after < 0) return before;
        long dA = (long)DocRow(after) - docRow;
        long dB = (long)docRow - DocRow(before);
        return dA < dB ? after : before;
    }

    /// <summary>第一个「末元素 >= docRow」的段（段间升序，末元素即段内最大文档行号）；无则 -1。</summary>
    int SegLastGe(int docRow)
    {
        int lo = 0, hi = _segs.Count - 1, ans = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var s = _segs[mid];
            if (s.Len > 0 && s.Buf[s.Len - 1] >= docRow) { ans = mid; hi = mid - 1; }
            else lo = mid + 1;
        }
        return ans;
    }

    // ── 行号换算 ──
    public int DocRow(int modelRow)
    {
        if (modelRow < 0 || modelRow >= _rowCount) return -1;
        int lo = 0, hi = _segs.Count - 1, seg = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_starts[mid] <= modelRow) { seg = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return _segs[seg].Buf[modelRow - _starts[seg]];
    }

    public int ModelRowOf(int docRow)
    {
        if (docRow < 0 || _rowCount == 0) return -1;
        int seg = SegLastGe(docRow);
        if (seg < 0) return -1;              // docRow 比所有段都大
        var t = _segs[seg];
        int bs = Array.BinarySearch(t.Buf, 0, t.Len, docRow);
        return bs >= 0 ? _starts[seg] + bs : -1;   // bs < 0 = 落在段间空隙
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

    /// <summary>
    /// 该行若是崩溃头，返回对应崩溃事件；否则返回 null。
    /// <see cref="LogDocument.Crashes"/> 按 Row 升序，二分查找。
    /// </summary>
    public CrashEvent? CrashOf(int docRow)
    {
        var crashes = _doc?.Crashes;
        if (crashes == null || crashes.Count == 0 || docRow < 0) return null;
        int lo = 0, hi = crashes.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            int r = crashes[mid].Row;
            if (r == docRow) return crashes[mid];
            if (r < docRow) lo = mid + 1;
            else hi = mid - 1;
        }
        return null;
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
            if (_doc == null || modelRow < 0 || modelRow >= _rowCount)
                return item;

            int docRow = DocRow(modelRow);
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
        // Time：行首时间戳原文（保留原始小数秒位数，含方括号 long 格式）；
        // 行首无时间戳显示空（绝对毫秒兜底已随分钟列改造移除）
        byte[] hb = _doc!.HeaderBytes(docRow);
        string timeStr = hb.Length > 0 ? _doc.Decode(hb) : "";

        int pid = _doc.Pid[docRow], tid = _doc.Tid[docRow];
        int lvl = _doc.Lvl[docRow];

        // Message：崩溃行显示摘要，普通行走原文渲染
        string msg = _doc.Decode(_doc.MessageBytes(docRow));
        var crash = CrashOf(docRow);
        if (crash != null)
            msg = CrashSummary(crash.Value, msg);
        else
        {
            if (msg.Length > MSG_LIMIT)
                msg = msg[..MSG_LIMIT] + Loc.F(" …(+{0} 字符)", msg.Length - MSG_LIMIT);
        }

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

    /// <summary>
    /// 语言切换后清空行缓存并重绘。
    /// 缓存的行文本里嵌着 <see cref="Loc.F"/> 算出的文案（如「…(+N 字符)」截断后缀），
    /// 不清缓存的话切语言后已渲染过的行会一直显示旧语言的文字。
    /// </summary>
    public void OnLanguageChanged()
    {
        ClearCache();
        Invalidate();
    }

    /// <summary>
    /// 崩溃行的 Message 列摘要文案：<c>☠ 类型 · 包名 · 时间</c>。
    /// 只是显示得更聪明，原文一个字不动 ——
    /// 查看完整记录、复制原始文本仍读原始字节；带格式复制由 frmMain 还原原文。
    /// </summary>
    string CrashSummary(CrashEvent ce, string rawMsg)
    {
        string exc = ce.ExcId >= 0 && ce.ExcId < _doc!.ExcTypes.Count ? _doc.ExcTypes[ce.ExcId] : "";
        string pkg = ce.PkgId >= 0 && ce.PkgId < _doc!.Pkgs.Count ? _doc.Pkgs[ce.PkgId] : "";

        // id 缺失（批次边界把崩溃头与 Process 行切开、ANR 无异常类型、自定义命名空间异常等）：
        // 行内重新提取兜底，仍提不到异常类型再退到类别名（CrashKind.Name）
        if (exc.Length == 0 || pkg.Length == 0)
        {
            var m = CrashExtractor.TryExtract(rawMsg);
            if (m != null)
            {
                if (exc.Length == 0) exc = m.Value.ExcType;
                if (pkg.Length == 0) pkg = m.Value.Pkg;
            }
            if (exc.Length == 0) exc = CrashKind.Name(ce.Kind);
        }

        // 行首原始时间戳的时分秒部分（与 Time 列同源）；行首无时间戳显示 "-"
        string time = CrashTime.SummaryText(_doc!, ce.Row) ?? "-";

        return Loc.F("☠ {0} · {1} · {2}", exc, pkg.Length > 0 ? pkg : "-", time);
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
