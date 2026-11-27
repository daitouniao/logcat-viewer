namespace Next.App.Storage;

/// <summary>最近文件列表的规模口径（spec next/recent-files）：上限与读取侧截断。</summary>
public static class RecentFilesList
{
    public const int MaxCount = 10;

    /// <summary>去掉空项并按上限截断；路径文本原样保留，不做规范化。</summary>
    public static List<string> Normalize(IEnumerable<string>? paths)
    {
        var list = new List<string>();
        if (paths is null)
        {
            return list;
        }
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }
            list.Add(path);
            if (list.Count == MaxCount)
            {
                break;
            }
        }
        return list;
    }
}
