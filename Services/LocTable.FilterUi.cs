namespace logcat.Services;

/// <summary>英文译文表：过滤面板与过滤设置窗口。</summary>
static partial class LocTable
{
    public static Dictionary<string, string> FilterUi() => new Dictionary<string, string>()
    {
        ["过滤"] = "Filter",
        ["过滤设置"] = "Filter Settings",
        ["级别"] = "Level",
        ["应用  (Ctrl+Enter)"] = "Apply  (Ctrl+Enter)",
        ["自动应用"] = "Auto-apply",
        ["（无过滤）"] = "(no filter)",

        // ── 输入框占位提示 ──
        ["多值用空格分隔"] = "Space-separated values",
        // ["如 05 20（空格分隔）"] 由 frmMain 的工具栏 + 过滤面板共用，译文在 MainUi。

        // ── 排除 / 大小写 ──
        ["排除"] = "Exclude",
        ["大小写"] = "Case",

        // ── 条件说明（{0}=字段名）──
        ["包含匹配"] = "includes",
        ["任一词命中（OR）"] = "any term matches (OR)",
        ["全部词命中（AND）"] = "all terms match (AND)",
        ["区分大小写"] = "case-sensitive",
        ["忽略大小写"] = "case-insensitive",
        ["已排除命中项"] = "excluding matches",
        ["{0}：{1}（设置在「过滤设置」里）"] = "{0}: {1} (set in Filter Settings)",

        // ── 收藏菜单 ──
        ["（暂无收藏，点 ★ 收藏当前内容）"] = "(No favorites yet — click ★ to favorite current content)",
        ["（无匹配收藏）"] = "(No matching favorite)",
        ["清空收藏"] = "Clear Favorites",
        ["清空收藏|确定要清空{0}收藏？"] = "Clear Favorites|Clear all {0} favorites?",
        ["内容为空，无法收藏"] = "Content is empty, cannot favorite",
        ["已收藏：{0}"] = "Favorited: {0}",
        ["已移除收藏：{0}"] = "Removed from favorites: {0}",

        // ── 过滤条件摘要（FilterSpec.Describe）──
        ["级别 {0}"] = "Level {0}",
        ["分钟({0})"] = "Minute({0})",

        // ── 标点分隔符（中文全角→英文半角）──
        ["；"] = "; ",
        ["："] = ": ",
    };
}