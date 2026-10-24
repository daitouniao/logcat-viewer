using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// 传输进度映射内核（<see cref="TransferProgress"/>）的纯函数单测（change file-transfer-progress 任务 1.3）。
/// 契约：总字节不可信 → 「不可用」判定而非 0；钳制 0–100；进度回退取历史较大值；
/// 多文件总字节按字节聚合、任一项未知则整体分母不可用。
/// 全部同步方法、无窗体依赖（design D6）。
/// </summary>
public class TransferProgressTests
{
    // ── PercentOf：基本映射 ──

    [Theory]
    [InlineData(0, 100, 0.0)]
    [InlineData(50, 100, 50.0)]
    [InlineData(1, 3, 33.333333333333336)]
    [InlineData(3, 3, 100.0)]
    public void PercentOf_正常映射(long sent, long total, double expected)
    {
        var pct = TransferProgress.PercentOf(sent, total);
        Assert.NotNull(pct);
        Assert.Equal(expected, pct.Value, precision: 10);
    }

    // ── PercentOf：总字节不可信 ──

    [Fact]
    public void PercentOf_总字节为0返回不可用而非0()
    {
        Assert.Null(TransferProgress.PercentOf(0, 0));
        Assert.Null(TransferProgress.PercentOf(100, 0));
    }

    [Fact]
    public void PercentOf_总字节为负返回不可用()
    {
        Assert.Null(TransferProgress.PercentOf(10, -1));
    }

    // ── PercentOf：越界与负数入参钳制 ──

    [Fact]
    public void PercentOf_越界已传字节钳到100()
    {
        Assert.Equal(100.0, TransferProgress.PercentOf(200, 100));
    }

    [Fact]
    public void PercentOf_负数已传字节钳到0()
    {
        Assert.Equal(0.0, TransferProgress.PercentOf(-5, 100));
    }

    // ── PercentOf：进度回退防倒退 ──

    [Fact]
    public void PercentOf_结果小于历史值时取历史较大值()
    {
        Assert.Equal(50.0, TransferProgress.PercentOf(10, 100, previous: 50));
    }

    [Fact]
    public void PercentOf_结果大于历史值时取新值()
    {
        Assert.Equal(60.0, TransferProgress.PercentOf(60, 100, previous: 10));
    }

    [Fact]
    public void PercentOf_无历史值直接返回当前()
    {
        Assert.Equal(30.0, TransferProgress.PercentOf(30, 100, previous: null));
    }

    // ── TotalBytes：多文件聚合 ──

    [Fact]
    public void TotalBytes_全部已知按字节求和()
    {
        Assert.Equal(1009, TransferProgress.TotalBytes([1000L, (long?)1, (long?)1, (long?)1, (long?)1, (long?)1, (long?)1, (long?)1, (long?)1, (long?)1]));
    }

    [Fact]
    public void TotalBytes_任一项未知则整体分母不可用()
    {
        Assert.Null(TransferProgress.TotalBytes([1000L, null]));
    }

    [Fact]
    public void TotalBytes_空文件计入聚合()
    {
        Assert.Equal(0, TransferProgress.TotalBytes([(long?)0, (long?)0]));
    }

    // ── 组合：多文件按字节折算而非按个数 ──

    /// <summary>
    /// 1 个大文件 + 9 个小文件：大文件传完后整体百分比应接近 100%（按字节），
    /// 而不是按个数折算的 10%。这是 design D2 的核心动机用例。
    /// </summary>
    [Fact]
    public void 多文件按字节折算_大文件主导整体百分比()
    {
        var sizes = new long?[] { 1_000_000, 1, 1, 1, 1, 1, 1, 1, 1, 1 };
        var total = TransferProgress.TotalBytes(sizes);
        Assert.NotNull(total);

        var afterBigFile = TransferProgress.PercentOf(1_000_000, total!.Value);
        Assert.NotNull(afterBigFile);
        Assert.True(afterBigFile > 90.0, $"按字节折算应 >90%，实际 {afterBigFile}");

        var countBased = 100.0 * 1 / 10;
        Assert.True(afterBigFile > countBased * 5, "按字节折算必须显著高于按个数折算");
    }
}
