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

    public override string ToString()
    {
        var name = string.IsNullOrEmpty(Model) || Model == Serial ? Serial : $"{Model} ({Serial})";
        // root 标记：与文件浏览器标题的 [root] 一致，便于识别设备是否支持 root
        return IsRoot ? $"{name} [root]" : name;
    }
}
