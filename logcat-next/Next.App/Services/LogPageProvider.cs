using Avalonia;
using Avalonia.Threading;
using Next.Interop;

namespace Next.App.Services;

/// <summary>
/// 视口页供给（design D5/D6）：按视口需要以「起始行 + 行数」取页，后台线程调用
/// core、解码为托管行模型（原生缓冲在解码后立即释放），回 UI 线程发一次变更通知。
/// 页按起始行缓存；取失败的页记入失败集，不再重复请求，避免错误页无限重试。
/// 复制走同一份缓存与失败集（design D4）：按行区间补齐缺页，任一行取不到即整体失败。
/// 取页抽象 IPageFetcher 在 Next.Interop 定义（真实源与测试假实现共用）。
/// </summary>
public sealed class LogPageProvider : IDisposable
{
    readonly IPageFetcher _fetcher;
    readonly ulong _totalRows;
    readonly object _lock = new();
    readonly Dictionary<long, LogcorePageRow[]> _pages = new();
    readonly HashSet<long> _failed = new();
    readonly Dictionary<long, Task<LogcorePageRow[]?>> _inflight = new();

    public LogPageProvider(LogcoreSource source, ulong totalRows)
        : this((IPageFetcher)source, totalRows)
    {
    }

    public LogPageProvider(IPageFetcher fetcher, ulong totalRows)
    {
        _fetcher = fetcher;
        _totalRows = totalRows;
    }

    /// <summary>页数据变化（新页就绪）；回调已在 UI 线程。</summary>
    public event Action? Changed;

    public ulong TotalRows => _totalRows;

    public bool HasFailedPages
    {
        get { lock (_lock) return _failed.Count > 0; }
    }

    /// <summary>取可见行；页未就绪时返回 null 并发起后台请求（同页只发一次）。</summary>
    public LogcorePageRow? TryGetRow(long index)
    {
        if (index < 0 || (ulong)index >= _totalRows)
        {
            return null;
        }
        var pageStart = PageStart(index);
        lock (_lock)
        {
            if (_pages.TryGetValue(pageStart, out var page))
            {
                return page[(int)(index - pageStart)];
            }
            if (_failed.Contains(pageStart) || _inflight.ContainsKey(pageStart))
            {
                return null;
            }
        }
        _ = GetPageAsync(pageStart);
        return null;
    }

    /// <summary>只读缓存中的行，缺页时不发起请求（详情窗口按已渲染行取原文）。</summary>
    public LogcorePageRow? TryGetCachedRow(long index)
    {
        if (index < 0 || (ulong)index >= _totalRows)
        {
            return null;
        }
        var pageStart = PageStart(index);
        lock (_lock)
        {
            return _pages.TryGetValue(pageStart, out var page) ? page[(int)(index - pageStart)] : null;
        }
    }

    /// <summary>预取覆盖 [start, start+count) 的整页（跳转目标提前加热）。</summary>
    public void Prefetch(long start, int count)
    {
        if (count <= 0 || (ulong)start >= _totalRows)
        {
            return;
        }
        var last = Math.Min(start + count - 1, (long)_totalRows - 1);
        for (var page = PageStart(start); page <= last; page += LogcoreErrorCodes.MaxPageRows)
        {
            _ = TryGetRow(page);
        }
    }

    /// <summary>
    /// 按行区间取行（复制用，design D4）：命中缓存直接复用，缺页按页大小补齐；
    /// 任一页取数失败（含失败集命中）返回 null，由调用方整体失败，不产出缺行结果。
    /// </summary>
    public async Task<LogcorePageRow[]?> FetchRangeAsync(long start, long count)
    {
        if (count <= 0)
        {
            return [];
        }
        var begin = Math.Max(0, start);
        var end = Math.Min(start + count, (long)_totalRows);
        if (end <= begin)
        {
            return [];
        }
        var rows = new List<LogcorePageRow>((int)Math.Min(end - begin, 1 << 20));
        for (var pageStart = PageStart(begin); pageStart < end; pageStart += LogcoreErrorCodes.MaxPageRows)
        {
            var page = await GetPageAsync(pageStart).ConfigureAwait(true);
            if (page is null)
            {
                return null;
            }
            var from = (int)(Math.Max(begin, pageStart) - pageStart);
            var to = (int)Math.Min(end - pageStart, page.Length);
            for (var i = from; i < to; i++)
            {
                rows.Add(page[i]);
            }
        }
        return rows.ToArray();
    }

    /// <summary>取整页：命中缓存 / 失败集直接给出结果，其余登记在途请求后后台取。</summary>
    Task<LogcorePageRow[]?> GetPageAsync(long pageStart)
    {
        lock (_lock)
        {
            if (_failed.Contains(pageStart))
            {
                return Task.FromResult<LogcorePageRow[]?>(null);
            }
            if (_pages.TryGetValue(pageStart, out var page))
            {
                return Task.FromResult<LogcorePageRow[]?>(page);
            }
            if (_inflight.TryGetValue(pageStart, out var running))
            {
                return running;
            }
            var created = FetchPageAsync(pageStart);
            _inflight[pageStart] = created;
            return created;
        }
    }

    async Task<LogcorePageRow[]?> FetchPageAsync(long pageStart)
    {
        try
        {
            var count = (int)Math.Min((long)LogcoreErrorCodes.MaxPageRows, (long)_totalRows - pageStart);
            var page = await Task.Run(() => _fetcher.GetPage(pageStart, count)).ConfigureAwait(true);
            lock (_lock)
            {
                _pages[pageStart] = page;
            }
            return page;
        }
        catch (Exception)
        {
            lock (_lock)
            {
                _failed.Add(pageStart);
            }
            return null;
        }
        finally
        {
            lock (_lock)
            {
                _inflight.Remove(pageStart);
            }
            Dispatcher.UIThread.Post(() => Changed?.Invoke());
        }
    }

    static long PageStart(long index) =>
        index / LogcoreErrorCodes.MaxPageRows * LogcoreErrorCodes.MaxPageRows;

    public void Dispose()
    {
        // 句柄生命周期归 MainViewModel 管理；此处只清缓存
        lock (_lock)
        {
            _pages.Clear();
            _failed.Clear();
            _inflight.Clear();
        }
    }
}
