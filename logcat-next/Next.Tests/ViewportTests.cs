using Xunit;
using Next.App.Services;
using Next.Interop;

namespace Next.Tests;

/// <summary>视口取页契约（tasks 5.2）：只请求可见范围、跳转直达尾部页。</summary>
public class ViewportTests
{
    /// <summary>假取页器：记录请求并返回定宽行数组。</summary>
    sealed class FakeFetcher(ulong totalRows) : IPageFetcher
    {
        public List<(long Start, int Count)> Requests { get; } = [];

        public LogcorePageRow[] GetPage(long start, int count)
        {
            Requests.Add((start, count));
            var n = Math.Min(count, (int)Math.Max(0L, (long)totalRows - start));
            var rows = new LogcorePageRow[n];
            for (var i = 0; i < n; i++)
            {
                rows[i] = new LogcorePageRow { Ord = (uint)(start + i) };
            }
            return rows;
        }
    }

    static async Task WaitAsync(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("假取页器未在时限内被调用");
            }
            await Task.Delay(20);
        }
    }

    /// <summary>视口首屏只请求覆盖可见行的页（页大小 4096：首屏 50 行 → 仅第 0 页）。</summary>
    [Fact]
    public async Task RequestsOnlyVisibleRange()
    {
        var fetcher = new FakeFetcher(totalRows: 100000);
        using var provider = new LogPageProvider(fetcher, 100000);
        provider.Prefetch(0, 50);
        await WaitAsync(() => fetcher.Requests.Count > 0);
        var requests = fetcher.Requests.Select(r => (r.Start, r.Count)).ToArray();
        Assert.Single(requests);
        Assert.Equal((0, LogcoreErrorCodes.MaxPageRows), requests[0]);
    }

    /// <summary>跳转文件尾部：只请求尾部所在页，不预热头部分页。</summary>
    [Fact]
    public async Task JumpToTailRendersTailRows()
    {
        const int total = 9000;   // 页大小 4096 → 3 页：0 / 4096 / 8192（尾页 808 行）
        var fetcher = new FakeFetcher(total);
        using var provider = new LogPageProvider(fetcher, total);

        // 模拟跳转：滚动到最大偏移后的视口（最后 50 行）
        provider.Prefetch(total - 50, 50);
        await WaitAsync(() => fetcher.Requests.Count > 0);
        var requests = fetcher.Requests.Select(r => (r.Start, r.Count)).ToArray();
        Assert.Single(requests);
        Assert.Equal((8192, 808), requests[0]);

        // 尾页行可取，头部页未被请求
        var tailRow = provider.TryGetRow(total - 1);
        Assert.NotNull(tailRow);
        Assert.Equal((uint)(total - 1), tailRow!.Ord);
        Assert.Null(provider.TryGetRow(0));
    }

    /// <summary>同一页并发请求只发一次（占位防重）。</summary>
    [Fact]
    public async Task SamePageRequestedOnce()
    {
        var fetcher = new FakeFetcher(totalRows: 100000);
        using var provider = new LogPageProvider(fetcher, 100000);
        provider.Prefetch(0, 10);
        provider.Prefetch(100, 10);   // 同页
        await WaitAsync(() => fetcher.Requests.Count > 0);
        Assert.Single(fetcher.Requests);
    }
}
