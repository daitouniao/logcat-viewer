using Next.App.Selection;
using Next.Interop;

namespace Next.App.Services;

/// <summary>剪贴板写入探针：真实实现走 TopLevel.Clipboard，测试注入假实现。</summary>
internal interface IClipboardSink
{
    Task SetTextAsync(string text);
}

internal enum CopyOutcome
{
    /// <summary>无选中：不复制、不改状态栏、不消费按键。</summary>
    Nothing,
    Success,
    /// <summary>选区内有页取数失败：整体失败，不写剪贴板。</summary>
    FetchFailed,
    /// <summary>剪贴板写入失败：不崩溃，提示失败。</summary>
    ClipboardFailed,
}

internal readonly record struct CopyResult(CopyOutcome Outcome, long RowCount);

/// <summary>
/// 复制编排（design D4）：按触发时刻的选择快照逐区间取行，后台拼装后写剪贴板。
/// 任一行取不到即整体失败（宁缺勿假），失败不自动重试；复制完成后不清空选择。
/// </summary>
internal static class ClipboardCopy
{
    internal static async Task<CopyResult> CopyAsync(
        RowSelection selection,
        LogPageProvider provider,
        IClipboardSink clipboard)
    {
        if (selection.IsEmpty)
        {
            return new CopyResult(CopyOutcome.Nothing, 0);
        }

        // 以触发时刻的选择快照为准：复制期间用户改动选择不影响本次结果
        var ranges = selection.OrderedRanges.ToArray();
        var selectedCount = selection.Count;

        var rows = new List<LogcorePageRow>((int)Math.Min(selectedCount, 1 << 20));
        foreach (var range in ranges)
        {
            var page = await provider.FetchRangeAsync(range.Start, range.Length).ConfigureAwait(true);
            if (page is null)
            {
                return new CopyResult(CopyOutcome.FetchFailed, 0);
            }
            rows.AddRange(page);
        }

        var text = CopyTextBuilder.Build(rows);
        try
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
        catch (Exception)
        {
            return new CopyResult(CopyOutcome.ClipboardFailed, 0);
        }
        return new CopyResult(CopyOutcome.Success, selectedCount);
    }
}
