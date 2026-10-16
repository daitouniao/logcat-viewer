namespace logcat.Models;

/// <summary>
/// 设备的 root 形态。两种 root 互不蕴含——Su 型（Magisk 等）靠 <c>su -c</c> 单命令提权；
/// Adbd 型（<c>adb root</c> 生效的模拟器/工程机）adbd 本身以 uid=0 运行，shell 命令天生 root，
/// 此时再包 <c>su -c</c> 反而可能失败（无 su 二进制），必须跳过包裹。
/// </summary>
public enum RootMode
{
    /// <summary>无可用 root：su 不可用，且 shell 身份不是 uid=0。</summary>
    None = 0,
    /// <summary>su 提权可用（Magisk 等）：shell 命令需包 su -c 才有 root 权限。</summary>
    Su = 1,
    /// <summary>adbd 以 uid=0 运行（adb root 生效）：shell 命令天然 root，禁止再加 su 包裹。</summary>
    Adbd = 2,
}

/// <summary>root 检测结果缓存条目：root 形态 + 「su 阶段未确认」子状态。</summary>
/// <param name="Mode">检测到的 root 形态。</param>
/// <param name="SuFailed">su 探测阶段异常或超时（未得到明确结果，如授权框挂起后被取消）时为 true；
/// 与「su 有输出但非 uid=0」的明确无 su 区分，供重试提示使用。</param>
public sealed record RootDetectEntry(RootMode Mode, bool SuFailed);

/// <summary>
/// 一台已连接设备的摘要信息。
/// </summary>
public class DeviceInfo
{
    public string Serial { get; set; } = "";
    public string Model { get; set; } = "";
    public string State { get; set; } = "device";

    /// <summary>设备的 root 形态。su 可用与 adbd root 都算「有 root」，但提权包裹策略不同。</summary>
    public RootMode RootMode { get; set; } = RootMode.None;

    /// <summary>是否具备 root 能力（su 可用或 adbd 已 root）。</summary>
    public bool IsRoot => RootMode != RootMode.None;

    public override string ToString()
    {
        var name = string.IsNullOrEmpty(Model) || Model == Serial ? Serial : $"{Model} ({Serial})";
        // root 标记：与文件浏览器标题的 [root] 一致，便于识别设备是否支持 root
        return IsRoot ? $"{name} [root]" : name;
    }
}
