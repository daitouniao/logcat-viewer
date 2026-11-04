namespace logcat.Models;

/// <summary>
/// 按设备记忆的文件浏览起始目录。
/// </summary>
/// <remarks>
/// 设备标识用连接标识（序列号）；身份模式（root / run-as / 普通）<b>不</b>参与区分——
/// 同一台设备在不同身份下共享同一条记录，身份差异导致的目录不可见由静默降级吸收。
/// </remarks>
public class DeviceLastDir
{
    /// <summary>设备连接标识（序列号）。</summary>
    public string Serial { get; set; } = "";

    /// <summary>该设备用户最后一次<b>主动导航成功</b>的目录。</summary>
    public string Path { get; set; } = "";
}
