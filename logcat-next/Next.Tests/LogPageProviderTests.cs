using System.Text;
using Next.App.Services;
using Next.Interop;
using Xunit;

namespace Next.Tests;

/// <summary>按行区间取页契约（design D4）：复用缓存、失败页不重试。</summary>
public class LogPageProviderTests
{
    sealed class CountingFetcher(int totalRows) : IPageFetcher
    {
        readonly object _lock = new();
        readonly List<long> _requests = [];

        public HashSet<long> FailPages { get; } = [];

        public int Count
        {
            get { lock (_lock) return _requests.Count; }
        }

        public LogcorePageRow[] GetPage(long start, int count)
        {
            lock (_lock)
            {
                _requests.Add(start);
            }
            if (FailPages.Contains(start))
            {
                throw new InvalidOperationException("取页失败");
            }
            var n = Math.Min(count, (int)Math.Max(0L, totalRows - start));
            var rows = new LogcorePageRow[n];
            for (var i = 0; i < n; i++)
            {
                var ord = start + i;
                rows[i] = new LogcorePageRow
                {
                    Ord = (uint)ord,
                    Raw = Encoding.UTF8.GetBytes($"row-{ord}"),
                };
            }
            return rows;
        }
    }

    static async Task WaitForPageAsync(LogPageProvider provider, long row)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (provider.TryGetCachedRow(row) is null)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("页未在时限内就绪");
            }
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task RangeFetchReusesCachedPages()
    {
        var fetcher = new CountingFetcher(totalRows: 100000);
        using var provider = new LogPageProvider(fetcher, 100000);

        provider.Prefetch(0, 10);
        await WaitForPageAsync(provider, 0);
        var requestsBefore = fetcher.Count;

        var rows = await provider.FetchRangeAsync(10, 20);

        Assert.NotNull(rows);
        Assert.Equal(20, rows!.Length);
        Assert.Equal(10u, rows[0].Ord);
        Assert.Equal(29u, rows[^1].Ord);
        Assert.Equal(requestsBefore, fetcher.Count);   // 同页不再请求
    }

    [Fact]
    public async Task RangeFetchSkipsFailedPages()
    {
        var fetcher = new CountingFetcher(totalRows: 20000);
        fetcher.FailPages.Add(LogcoreErrorCodes.MaxPageRows);
        using var provider = new LogPageProvider(fetcher, 20000);

        // 首页可取，跨到失败页 → 整体失败
        Assert.Null(await provider.FetchRangeAsync(0, 5000));
        var requestsAfterFailure = fetcher.Count;
        Assert.True(provider.HasFailedPages);

        // 失败页不重试、缓存页不重取
        Assert.Null(await provider.FetchRangeAsync(0, 5000));
        Assert.Equal(requestsAfterFailure, fetcher.Count);
    }
}
