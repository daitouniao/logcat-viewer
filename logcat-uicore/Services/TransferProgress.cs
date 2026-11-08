namespace logcat.Services;

/// <summary>
/// 传输进度映射的纯函数内核：字节数 → 百分比、多文件总字节聚合。
/// 只做算术与边界判定，不碰控件与通道，便于单测。
/// </summary>
public static class TransferProgress
{
    /// <summary>
    /// 已传字节 → 0–100 百分比。
    /// 总字节不可信（≤ 0）时返回 null（「不可用」判定），调用方应转不确定态而非冒充 0%；
    /// 结果钳制在 0–100（含越界与负数入参）；进度回退（本次结果小于 previous）时取历史较大值防倒退。
    /// </summary>
    public static double? PercentOf(long sentBytes, long totalBytes, double? previous = null)
    {
        if (totalBytes <= 0) return null;
        var pct = 100.0 * Math.Clamp(sentBytes, 0L, totalBytes) / totalBytes;
        return previous is { } prev && pct < prev ? prev : pct;
    }

    /// <summary>
    /// 多文件总字节聚合：任一项未知（null）时整体分母判为不可用（返回 null）。
    /// 已知为 0 的文件（空文件）照常计入；聚合结果为 0 时由 <see cref="PercentOf"/> 判不可用。
    /// </summary>
    public static long? TotalBytes(IEnumerable<long?> fileSizes)
    {
        long total = 0;
        foreach (var s in fileSizes)
        {
            if (s is null) return null;
            total += s.Value;
        }
        return total;
    }
}
