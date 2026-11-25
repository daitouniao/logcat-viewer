using Avalonia;
using Avalonia.Threading;
using Next.Interop;

namespace Next.App.Services;

/// <summary>
/// 视口页供给（design D5/D6）：按视口需要以「起始行 + 行数」取页，后台线程调用
/// core、解码为托管行模型（原生缓冲在解码后立即释放），回 UI 线程发一次变更通知。
/// 页按起始行缓存；取失败的页记入失败集，不再重复请求，避免错误页无限重试。
/// 取页抽象 IPageFetcher 在 Next.Interop 定义（真实源与测试假实现共用）。
/// </summary>
public sealed class LogPageProvider : IDisposable
{
    readonly IPageFetcher _fetcher;
    readonly ulong _totalRows;
    readonly object _lock = new();
    readonly Dictionary<long, LogcorePageRow[]?> _pages = new();
    readonly HashSet<long> _failed = new();

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
        var pageStart = index / LogcoreErrorCodes.MaxPageRows * LogcoreErrorCodes.MaxPageRows;
        LogcorePageRow[]? page;
        var needsFetch = false;
        lock (_lock)
        {
            if (_pages.TryGetValue(pageStart, out page))
            {
                return page is null ? null : page[index - pageStart];
            }
            if (_failed.Contains(pageStart))
            {
                return null;
            }
            _pages[pageStart] = null;   // 占位，防止重复请求
            needsFetch = true;
        }
        if (needsFetch)
        {
            _ = FetchAsync(pageStart);
        }
        return null;
    }

    /// <summary>预取覆盖 [start, start+count) 的整页（跳转目标提前加热）。</summary>
    public void Prefetch(long start, int count)
    {
        if (count <= 0 || (ulong)start >= _totalRows)
        {
            return;
        }
        var last = Math.Min(start + count - 1, (long)_totalRows - 1);
        for (var page = start / LogcoreErrorCodes.MaxPageRows * LogcoreErrorCodes.MaxPageRows;
             page <= last;
             page += LogcoreErrorCodes.MaxPageRows)
        {
            _ = TryGetRow(page);
        }
    }

    async Task FetchAsync(long pageStart)
    {
        try
        {
            var count = (int)Math.Min((long)LogcoreErrorCodes.MaxPageRows, (long)_totalRows - pageStart);
            var page = await Task.Run(() => _fetcher.GetPage(pageStart, count)).ConfigureAwait(true);
            lock (_lock)
            {
                _pages[pageStart] = page;
            }
        }
        catch (Exception)
        {
            lock (_lock)
            {
                _pages.Remove(pageStart);
                _failed.Add(pageStart);
            }
        }
        Dispatcher.UIThread.Post(() => Changed?.Invoke());
    }

    public void Dispose()
    {
        // 句柄生命周期归 MainViewModel 管理；此处只清缓存
        lock (_lock)
        {
            _pages.Clear();
            _failed.Clear();
        }
    }
}
