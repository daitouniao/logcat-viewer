using System.Text;
using Next.App.Selection;
using Next.App.Services;
using Next.Interop;
using Xunit;

namespace Next.Tests;

/// <summary>复制内容拼装契约（design D4）：按行序、\n 连接、末尾无换行、空选择为空结果。</summary>
public class CopyTextBuilderTests
{
    static LogcorePageRow Row(string text) =>
        new() { Raw = Encoding.UTF8.GetBytes(text) };

    [Fact]
    public void JoinsInAscendingOrder()
    {
        var text = CopyTextBuilder.Build([Row("first"), Row("second"), Row("third")]);
        Assert.Equal("first\nsecond\nthird", text);
    }

    [Fact]
    public void NoTrailingNewline()
    {
        var text = CopyTextBuilder.Build([Row("a"), Row("b")]);
        Assert.False(text.EndsWith('\n'));
        Assert.Equal("a\nb", text);
    }

    [Fact]
    public void EmptySelectionYieldsEmpty()
    {
        Assert.Equal("", CopyTextBuilder.Build([]));
    }
}

/// <summary>复制编排契约（design D4）：成功、剪贴板失败、无选中、失败页四条路径。</summary>
public class ClipboardCopyTests
{
    sealed class StringFetcher(ulong totalRows) : IPageFetcher
    {
        public HashSet<long> FailPages { get; } = [];

        public LogcorePageRow[] GetPage(long start, int count)
        {
            if (FailPages.Contains(start))
            {
                throw new InvalidOperationException("取页失败");
            }
            var n = Math.Min(count, (int)Math.Max(0L, (long)totalRows - start));
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

    sealed class FakeClipboard : IClipboardSink
    {
        public List<string> Writes { get; } = [];

        public bool FailOnWrite { get; set; }

        public Task SetTextAsync(string text)
        {
            if (FailOnWrite)
            {
                throw new InvalidOperationException("剪贴板被占用");
            }
            Writes.Add(text);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task SuccessWritesRowsAndReportsCount()
    {
        using var provider = new LogPageProvider(new StringFetcher(100), 100);
        var selection = new RowSelection();
        selection.AddRange(0, 2);
        var clipboard = new FakeClipboard();

        var result = await ClipboardCopy.CopyAsync(selection, provider, clipboard);

        Assert.Equal(CopyOutcome.Success, result.Outcome);
        Assert.Equal(3, result.RowCount);
        Assert.Equal("row-0\nrow-1\nrow-2", Assert.Single(clipboard.Writes));
    }

    [Fact]
    public async Task ClipboardFailureKeepsSelection()
    {
        using var provider = new LogPageProvider(new StringFetcher(100), 100);
        var selection = new RowSelection();
        selection.AddRange(0, 2);
        var clipboard = new FakeClipboard { FailOnWrite = true };

        var result = await ClipboardCopy.CopyAsync(selection, provider, clipboard);

        Assert.Equal(CopyOutcome.ClipboardFailed, result.Outcome);
        Assert.Empty(clipboard.Writes);
        Assert.Equal(3, selection.Count);
    }

    [Fact]
    public async Task NoSelectionDoesNothing()
    {
        using var provider = new LogPageProvider(new StringFetcher(100), 100);
        var clipboard = new FakeClipboard();

        var result = await ClipboardCopy.CopyAsync(new RowSelection(), provider, clipboard);

        Assert.Equal(CopyOutcome.Nothing, result.Outcome);
        Assert.Empty(clipboard.Writes);
    }

    [Fact]
    public async Task FailedPageAbortsWithoutClipboardWrite()
    {
        var fetcher = new StringFetcher(20000);
        fetcher.FailPages.Add(LogcoreErrorCodes.MaxPageRows);
        using var provider = new LogPageProvider(fetcher, 20000);
        var selection = new RowSelection();
        selection.AddRange(0, 5000);
        var clipboard = new FakeClipboard();

        var result = await ClipboardCopy.CopyAsync(selection, provider, clipboard);

        Assert.Equal(CopyOutcome.FetchFailed, result.Outcome);
        Assert.Empty(clipboard.Writes);
        Assert.Equal(5001, selection.Count);
    }
}
