using logcat.Services;

namespace logcat;

public partial class frmMain
{
    // ── sidecar 缓存命中（design D5/D10，spec「重开同文件命中缓存并跳过解析」）──
    // 本地静态文件首次解析完成后把列式索引落盘；重开同一文件时校验身份并直接装载，
    // 一次到位绕过 RunChunkedLoadAsync 的渐进编排。命中路径的一切失败都静默回退，
    // 不弹提示、不加开关，只写启动日志。

    /// <summary>sidecar 缓存存储。internal 字段：测试注入临时目录（生产为 exe 同级 cache/）。</summary>
    internal SidecarCacheStore _sidecarStore = SidecarCacheStore.Default;

    /// <summary>进入分块编排的次数。internal：测试断言「命中不进入分块编排」。</summary>
    internal int ChunkedLoadInvocations { get; private set; }

    /// <summary>最近一次命中的装载耗时（ms，只含读盘 + 装配，不含其后的全量过滤）。</summary>
    internal double LastSidecarLoadMs { get; private set; }

    /// <summary>
    /// 本地文件装载分流：先试 sidecar 命中（命中即装配并返回），未命中走既有分块渐进编排。
    /// 返回 null = 分块加载被取消或条件收集失败（与 <see cref="RunChunkedLoadAsync"/> 同口径）。
    /// </summary>
    internal async Task<LogDocument?> LoadLocalFileAsync(string path, int gen,
        CancellationToken ct, SidecarIdentity? identityBefore = null)
    {
        LogDocument? hit = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // 大条目读盘 + 装配不能占 UI 线程：挪到线程池（装载段是验收指标，必须如实计时）
            hit = await Task.Run(() => TryLoadSidecarHit(path), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // spec「静默降级到原路径」：命中判定自身出错也必须能正常打开
            StartupLog.Write($"[Sidecar] 命中判定失败，改走全量加载：{ex.Message}");
        }
        sw.Stop();

        if (hit != null)
        {
            if (_workerGen != gen)
            {
                hit.Close();          // 已被新一代抢占：丢弃命中文档，不动当前状态
                return null;
            }
            LastSidecarLoadMs = sw.Elapsed.TotalMilliseconds;
            await AdoptSidecarHitAsync(hit);
            return hit;
        }

        ChunkedLoadInvocations++;
        return await RunChunkedLoadAsync(path, gen, ct);
    }

    /// <summary>
    /// 命中判定 + 装配（worker 线程）。缓存缺失 / 损坏 / 版本不识别 / 目录不可写 / 装配异常
    /// 一律返回 null 由调用方回退；不抛异常给 UI。
    /// </summary>
    internal LogDocument? TryLoadSidecarHit(string path)
    {
        if (!_sidecarStore.TryLoad(path, out var payload) || payload == null) return null;
        LogDocument doc;
        try
        {
            doc = LogDocument.LoadIndexed(path, payload);
        }
        catch (Exception ex)
        {
            // 装载中途异常（mmap 打不开等）：静默降级，绝不复用 StartIndex 的失败弹框分支
            StartupLog.Write($"[Sidecar] 装载失败，改走全量加载：{ex.Message}");
            return null;
        }
        StartupLog.Write($"[Sidecar] 命中缓存：{doc.RowCount:N0} 行");
        return doc;
    }

    /// <summary>
    /// 命中后的接管与按当前条件全量过滤（spec「命中后过滤立即生效」）：
    /// 装载一次到位，随后按当前过滤条件全量过滤并上屏，不经过渐进发布。
    /// </summary>
    internal async Task AdoptSidecarHitAsync(LogDocument doc)
    {
        _doc = doc;
        _listView.SetDocument(doc);
        _indexMs = LastSidecarLoadMs;
        await ApplyFilter(quiet: true);
    }

    /// <summary>
    /// 首次解析完成后调度后台落盘（spec「后台落盘不阻塞退出」）：
    /// 独立后台线程，退出不等它；代理 spool 与 adb 采集显式排除（design D7）。
    /// </summary>
    /// <param name="identityBefore">解析开始前的身份取样（静态文件边界比对用）。</param>
    internal void ScheduleSidecarPersist(string path, SidecarIdentity? identityBefore, LogDocument doc)
    {
        // 代理 spool 的源路径是暂存文件，重开时路径不同、也无意义；adb 采集不走 StartIndex。
        // 两条非静态入口在此显式排除，防未来重构误接（design D7）。
        if (identityBefore is null || doc.IsProxied || doc.LoadedFromSidecar) return;

        var store = _sidecarStore;
        var before = identityBefore.Value;
        var thread = new Thread(() => PersistSidecarCore(store, path, before, doc))
        {
            IsBackground = true,          // 进程退出不等待落盘（spec「落盘中途退出」）
            Name = "sidecar-write",
        };
        thread.Start();
    }

    void PersistSidecarCore(SidecarCacheStore store, string path, SidecarIdentity before, LogDocument doc)
    {
        try
        {
            // 快照期间与 Reload / Append 串行（列数组可能被重分配），与既有 _docBusy 纪律一致
            SidecarPayload payload;
            _docBusy.Wait();
            try { payload = doc.SnapshotIndex(); }
            finally { _docBusy.Release(); }

            var after = SidecarIdentity.Capture(path);
            store.Save(path, before, after, payload);   // 内部已静默：返回 false 即「不写」
        }
        catch (Exception ex)
        {
            StartupLog.Write($"[Sidecar] 落盘失败（静默）：{ex.Message}");
        }
    }
}
