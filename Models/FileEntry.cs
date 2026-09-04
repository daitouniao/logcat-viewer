namespace logcat.Models;

/// <summary>
/// 设备端文件条目。
/// </summary>
public class FileEntry
{
    public string Name { get; set; } = "";

    /// <summary>完整路径（设备端以 '/' 分隔，本机端以 '\' 分隔）。</summary>
    public string Path { get; set; } = "";

    public bool IsDir { get; set; }
    public long Size { get; set; }
    public string Permissions { get; set; } = "";
    public string Owner { get; set; } = "";
    public string DateStr { get; set; } = "";
}
