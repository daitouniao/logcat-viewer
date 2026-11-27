namespace Next.App.Services;

/// <summary>最近文件菜单的一行：显示文本（可能中部省略）与完整路径（tooltip / 打开用）。</summary>
public sealed record RecentFileMenuEntry(string DisplayText, string FullPath);

/// <summary>
/// 最近文件子菜单的构建结果：可显示条目、需要移除的本地失效路径与子菜单是否可用。
/// </summary>
public sealed record RecentFilesMenuModel(
    IReadOnlyList<RecentFileMenuEntry> Entries,
    IReadOnlyList<string> MissingLocalPaths,
    bool IsEnabled);

/// <summary>
/// 最近文件菜单的展示规则（design D5/D6/D7）：本地缺失条目上报移除、网络路径不探测、
/// 超过上限中部省略、可见集合为空则禁用。无 UI 依赖，便于单测。
/// </summary>
public static class RecentFilesMenu
{
    /// <summary>条目显示宽度上限（字符数，含省略号）。</summary>
    public const int MaxDisplayLength = 80;

    public static RecentFilesMenuModel Build(IEnumerable<string> paths, Func<string, bool> exists)
    {
        var entries = new List<RecentFileMenuEntry>();
        var missing = new List<string>();
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }
            if (!IsNetworkPath(path))
            {
                if (!SafeExists(path, exists))
                {
                    missing.Add(path);
                    continue;
                }
            }
            entries.Add(new RecentFileMenuEntry(ElideMiddle(path, MaxDisplayLength), path));
        }
        return new RecentFilesMenuModel(entries, missing, entries.Count > 0);
    }

    /// <summary>UNC 网络路径（`\\` 开头）不做存在性探测。</summary>
    public static bool IsNetworkPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>超过上限时保留首尾、中间以单个省略号连接；不超限原样返回。</summary>
    public static string ElideMiddle(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }
        if (maxLength <= 1)
        {
            return "…";
        }

        var keep = maxLength - 1;
        var front = (keep + 1) / 2;
        var back = keep - front;
        return string.Concat(text.AsSpan(0, front), "…", text.AsSpan(text.Length - back));
    }

    static bool SafeExists(string path, Func<string, bool> exists)
    {
        try
        {
            return exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
