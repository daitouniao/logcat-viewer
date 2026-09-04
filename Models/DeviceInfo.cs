namespace logcat.Models;

/// <summary>
/// 一台已连接设备的摘要信息。
/// </summary>
public class DeviceInfo
{
    public string Serial { get; set; } = "";
    public string Model { get; set; } = "";
    public string State { get; set; } = "device";
    public bool IsRoot { get; set; }

    public override string ToString() =>
        string.IsNullOrEmpty(Model) || Model == Serial ? Serial : $"{Model} ({Serial})";
}
