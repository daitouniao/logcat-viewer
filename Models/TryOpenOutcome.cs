namespace logcat.Models;

/// <summary>
/// 「尝试打开目录」的结果（起始目录解析用）。
/// </summary>
/// <remarks>
/// 单独区分「被取消」与「候选失败」：用户导航或关窗会取消进行中的尝试，
/// 此时必须中止整条解析，而不是当成候选失败去试下一个候选（design D4）。
/// </remarks>
public enum TryOpenOutcome
{
    /// <summary>打开成功，目录已提交。</summary>
    Succeeded,

    /// <summary>打不开（目录不存在或无权访问）。</summary>
    Failed,

    /// <summary>被取消（用户已导航或窗体已关闭）。</summary>
    Cancelled,
}
