namespace logcat.Models;

/// <summary>
/// 收藏的目录。设备端与本机端各维护一份列表。
/// </summary>
public class FavoriteDir
{
    /// <summary>目录完整路径。</summary>
    public string Path { get; set; } = "";

    /// <summary>备注名（可空，为空时直接显示路径）。</summary>
    public string? Alias { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.Now;

    public string Display =>
        string.IsNullOrWhiteSpace(Alias) ? Path : $"{Alias}   [{Path}]";

    public override string ToString() => Display;
}
