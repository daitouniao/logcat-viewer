using logcat.Controls;
using logcat.Models;
using logcat.Services;

namespace logcat;

public partial class frmMain
{
    // ── 分块渐进加载编排（design 决策 1/6/7/8）──
    // 加载期所有文档访问都串在加载 worker 上：块边界检查取消；发布回调里做增量过滤
    // （含加载期过滤条件变更时的已发布前缀全量重过滤）并写入发布缓冲；
    // UI 只经节流定时器消费缓冲（行数组快照），不直接读增长中的文档。
    // 线程约定：_spec / _lblSpec 仅 UI 线程写；_pendingLoadSpec UI 写、worker 取走；
    // 发布缓冲线程安全；会话与累计行数由 worker 独占，加载结束（await 返回）后经闭包清理。
    Controls.LoadPublishBuffer? _loadPub;    // UI 定时器消费的缓冲（每次加载换新实例）
    FilterSpec? _pendingLoadSpec;            // 加载期过滤条件变更：worker 在下一块边界取走应用
    volatile bool _loading;                  // 渐进加载进行中（ApplyFilter / Reload 据此分流）
    volatile LogDocument? _loadPendingDoc;   // 首次发布后待 UI 接管的文档
    volatile int _loadPendingDocGen;         // 待接管文档所属的 worker 代
    LogDocument? _loadOwnedDoc;              // 本次加载创建的文档（清理时只关自己创建的）

    /// <summary>每次加载独立的 worker 侧状态（会话 / 累计行数 / 条件快照），随加载生命周期建立与销毁。</summary>
    sealed class ChunkedLoadState
    {
        public FilterSpec Spec;
        public readonly LoadPublishBuffer Pub = new();
        public FilterEngine.MsgScanSession? Session;   // worker 独占
        public int FilteredRows;                        // worker 独占：视图已覆盖的文档行数
        public ChunkedLoadState(FilterSpec spec) { Spec = spec; }
    }

    /// <summary>
    /// 分块渐进加载（本地文件路径）：首块 4 MB 出首屏、稳态 32 MB；加载期过滤器与
    /// 时间过滤增量生效（spec「加载期过滤行为」）；发布上屏 100 ms 节流；取消整份清空。
    /// 返回 null = 条件收集失败（已提示）。完成时视图已按条件增量构建，调用方不再全量重过滤。
    /// </summary>
    async Task<LogDocument?> RunChunkedLoadAsync(string path, int gen, CancellationToken ct)
    {
        FilterSpec spec;
        try { spec = CollectSpec(); }
        catch (Exception ex) { ShowError(ex.Message); return null; }
        _spec = spec;
        _lblSpec.Text = spec.Describe();

        var state = new ChunkedLoadState(spec);
        _loadPub = state.Pub;
        _pendingLoadSpec = null;
        _loadPendingDoc = null;
        _loadOwnedDoc = null;
        _loading = true;
        SetLoadUiState(true);
        var flushTimer = StartLoadFlushTimer();

        bool completed = false;
        try
        {
            var doc = await LogDocument.BuildChunkedAsync(path,
                publish: (d, from, to) => OnChunkPublished(state, d, to, gen),
                progress: new Progress<(double pct, string msg)>(p =>
                {
                    if (_workerGen == gen) { _pbar.Value = (int)(p.pct * 100); ShowStatusRaw(p.msg); }
                }),
                ct: ct);
            completed = true;
            return doc;
        }
        finally
        {
            flushTimer.Stop();
            // 会话由 worker 创建；await 返回时 worker 已退出，闭包引用安全清理（跨代抢占下也只关自己的）
            state.Session?.Dispose();
            if (_workerGen == gen)
            {
                if (completed)
                {
                    // 加载完成：最终整批发布（spec「加载完成无内容滞留」）——定时器已停，
                    // 无条件取走缓冲内全部发布操作（含未到节流窗口的）走既有应用路径；
                    // 缓冲为空时若文档已接管，仍补一次 UpdateStat()（spec「加载完成后
                    // 状态栏统计反映最终值」），消除状态栏停在最后一次节流快照的时序窗口。
                    var buf = _loadPub;
                    if (buf != null)
                    {
                        var ops = buf.TakeAll();
                        if (ops.Count > 0) ApplyLoadOps(ops);
                        else if (_doc != null) UpdateStat();
                    }
                }
                _loading = false;
                SetLoadUiState(false);
                if (!completed)
                {
                    // 取消 / 失败：整份清空（spec「取消加载整份清空」）
                    CleanupAbortedLoad(_loadOwnedDoc, gen);
                    _loadPendingDoc = null;
                    _loadPub = null;
                    ShowStatusRaw("");
                }
            }
        }
    }

    /// <summary>
    /// 取消 / 失败的整份清空（spec「取消加载整份清空」）：文档丢弃、列表与过滤结果回空状态、
    /// 标记清空。代际守卫：被新一代抢占时不动当前状态（新加载已接管）。
    /// internal：单元测试直接验证清空语义（见 InternalsVisibleTo）。
    /// </summary>
    internal void CleanupAbortedLoad(LogDocument? ownedDoc, int gen)
    {
        if (_workerGen != gen) return;
        if (_doc == ownedDoc)
        {
            _doc = null;
            _marked.Clear();
            _listView.SetDocument(null);
            _filterDocRows = 0;
            UpdateStat();
        }
        ownedDoc?.Close();
        if (_loadOwnedDoc == ownedDoc)
            _loadOwnedDoc = null;
    }

    /// <summary>块发布回调（worker 线程）：增量过滤新发布行 → 发布缓冲；块边界顺带应用待生效的过滤条件变更。</summary>
    void OnChunkPublished(ChunkedLoadState state, LogDocument doc, int to, int gen)
    {
        if (_workerGen != gen) return;

        // 首次发布：登记文档供 UI 接管；建增量块扫描会话（关键词不可块扫时 FilterTail 自动回退逐条）
        if (_loadPendingDoc == null)
        {
            _loadPendingDoc = doc;
            _loadPendingDocGen = gen;
        }
        state.Session ??= FilterEngine.MsgScanSession.TryCreate(doc, state.Spec);

        // 待处理过滤条件变更：对已发布前缀走既有全量路径，之后恢复增量（spec「加载期过滤行为」）
        var newSpec = Interlocked.Exchange(ref _pendingLoadSpec, null);
        if (newSpec != null)
        {
            state.Spec = newSpec;
            state.Session?.Dispose();
            state.Session = null;
            state.FilteredRows = 0;
            var allRows = FilterEngine.ApplyFilter(doc, newSpec, _marked, null, CancellationToken.None);
            state.FilteredRows = doc.RowCount;
            state.Pub.ReplaceAll(allRows);
            if (doc.RowCount > 0)
            {
                long dataEnd = doc.Offs[doc.RowCount - 1] + doc.Lens[doc.RowCount - 1];
                state.Session = FilterEngine.MsgScanSession.TryCreate(doc, newSpec, scannedTo: dataEnd);
            }
            return;
        }

        int start = state.FilteredRows;
        if (to <= start) return;
        var rows = FilterEngine.FilterTail(doc, state.Spec, start, _marked, state.Session);
        state.FilteredRows = to;
        if (rows.Length > 0)
            state.Pub.Add(rows);
    }

    /// <summary>100 ms 节流上屏定时器（UI 线程消费发布缓冲）。</summary>
    System.Windows.Forms.Timer StartLoadFlushTimer()
    {
        var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += (_, _) => FlushLoadOps();
        timer.Start();
        return timer;
    }

    /// <summary>消费一批发布操作（节流窗口到期时）：到期才取，未到期不动。</summary>
    void FlushLoadOps()
    {
        var buf = _loadPub;
        if (buf == null) return;
        var ops = buf.TakeIfDue();
        if (ops == null) return;
        ApplyLoadOps(ops);
    }

    /// <summary>
    /// 应用一批发布操作：接管首次发布的文档、按序应用替换/追加、跟随与状态栏。
    /// 节流定时器与加载收尾的最终整批发布共用此路径（含接管判断，不可旁路——
    /// ≤ 首块的小文件可能从未有过成功 flush，收尾强制消费是其唯一上屏机会）。
    /// </summary>
    void ApplyLoadOps(List<LoadPublishOp> ops)
    {
        // 首次发布接管文档（worker 建好并已产出第一批行之后；跨代抢占下不接管旧代文档）
        if (_doc == null && _loadPendingDoc != null && _workerGen == _loadPendingDocGen)
        {
            _doc = _loadPendingDoc;
            _loadOwnedDoc = _loadPendingDoc;
            _listView.SetDocument(_doc);
        }

        foreach (var op in ops)
        {
            if (op.ReplaceAll) _listView.SetRows(op.Rows);
            else _listView.AppendRows(op.Rows);
        }
        UpdateStat();
        if (_chkToolbarFollow.Checked)
            ScrollBottom();
    }

    /// <summary>加载期 UI 状态（spec「加载期 UI 状态」）：导出与崩溃汇总置灰，加载完成后恢复。</summary>
    internal void SetLoadUiState(bool loading)
    {
        _actExport.Enabled = !loading;
        _actExportMarked.Enabled = !loading;
        _actCrashSummary.Enabled = !loading;
    }
}
