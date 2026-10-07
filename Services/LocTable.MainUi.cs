namespace logcat.Services;

/// <summary>英文译文表：主窗体（菜单、工具栏、状态栏、列头、右键菜单）。</summary>
static partial class LocTable
{
    public static Dictionary<string, string> MainUi() => new Dictionary<string, string>()
    {
        // ── 顶层菜单 ──
        ["设置"] = "Settings",
        ["帮助"] = "Help",

        // ── 文件菜单 ──
        ["打开…"] = "Open…",
        ["重载"] = "Reload",
        ["导出结果…"] = "Export Results…",
        ["导出标记行…"] = "Export Marked Rows…",
        ["退出"] = "Exit",
        ["保存日志…"] = "Save Log…",

        // ── 设置菜单 ──
        ["续行合并（堆栈并入上一条记录）"] = "Join continuation lines (merge stack into previous record)",
        ["导出时单行化（换行转 \\n）"] = "Single-line on export (newlines → \\n)",
        ["过滤设置…"] = "Filter Settings…",
        ["字号"] = "Font Size",

        // ── 帮助菜单 ──
        ["关于…"] = "About…",
        ["语言"] = "Language",
        ["简体中文"] = "简体中文",
        ["English"] = "English",

        // ── ADB 工具栏 ──
        ["刷新设备"] = "Refresh Devices",
        ["▶ 开始采集"] = "▶ Start Capture",
        ["■ 停止采集"] = "■ Stop Capture",
        ["设备:"] = "Device:",

        // ── 第二行工具栏：标记与快速过滤 ──
        ["仅标记行"] = "Marked only",
        ["跟随尾部"] = "Follow tail",
        ["标记:"] = "Marks:",
        ["◀ 上一个"] = "◀ Previous",
        ["下一个 ▶"] = "Next ▶",
        ["清除标记"] = "Clear Marks",
        ["分钟"] = "Minute",
        ["Tag"] = "Tag",
        ["Message"] = "Message",

        // ── 收藏按钮提示 ──
        ["从收藏中选择（Tag）"] = "Pick from favorites (Tag)",
        ["收藏/移除当前内容（Tag）"] = "Favorite / remove current content (Tag)",
        ["从收藏中选择（Message）"] = "Pick from favorites (Message)",
        ["收藏/移除当前内容（Message）"] = "Favorite / remove current content (Message)",
        ["从收藏中选择"] = "Pick from favorites",
        ["收藏当前内容（已收藏则移除）"] = "Favorite current content (removes if already favorited)",

        // ── 状态栏 ──
        ["未打开文件 —— 可直接把日志文件拖进窗口"] = "No file open — you can drop a log file onto the window",
        ["无设备"] = "No device",
        ["ADB 不可用"] = "ADB unavailable",
        ["[实时采集] {0}"] = "[Live capture] {0}",

        // ── 状态栏统计（{0}=总数 {1}=命中 {2}=标记 {3}=索引ms {4}=过滤ms）──
        ["总 {0:N0} 条 | 命中 {1:N0} | 标记 {2:N0} | 索引 {3:F0} ms | 过滤 {4:F0} ms"] =
            "{0:N0} total | {1:N0} matched | {2:N0} marked | index {3:F0} ms | filter {4:F0} ms",
        ["视口 {0:N0}/{1:N0} (记录 #{2})"] = "View {0:N0}/{1:N0} (record #{2})",

        // ── 行右键菜单 ──
        ["复制原始文本（{0} 行）"] = "Copy Raw Text ({0} rows)",
        ["复制为表格行"] = "Copy as Tabular Rows",
        ["按此 tag 过滤：{0}"] = "Filter by this tag: {0}",
        ["按此 PID 过滤：{0}"] = "Filter by this PID: {0}",
        ["按此 TID 过滤：{0}"] = "Filter by this TID: {0}",
        ["按此分钟过滤：{0}"] = "Filter by this minute: {0}",
        ["取消标记"] = "Unmark",
        ["标记（M）"] = "Mark (M)",
        ["查看完整记录"] = "View Full Record",

        // ── 标记导航 ──
        ["已清除全部标记"] = "All marks cleared",
        ["当前结果中没有标记行"] = "No marked rows in current results",

        // ── 复制 / 查找 ──
        ["已复制 {0} 行"] = "Copied {0} rows",
        ["复制失败：剪贴板被其他程序占用"] = "Copy failed: clipboard is held by another program",
        ["请先在 Message 框里填写检索词"] = "Enter a search term in the Message box first",
        ["命中第 {0:N0} 行"] = "Matched row {0:N0}",
        ["没有更多匹配"] = "No more matches",

        // ── 索引 / 过滤 / 导出 ──
        ["建立索引…"] = "Building index…",
        ["建立索引失败：{0}"] = "Indexing failed: {0}",
        ["检查更新…"] = "Checking for changes…",
        ["文件无变化"] = "File unchanged",
        ["重载失败：{0}"] = "Reload failed: {0}",
        ["过滤…"] = "Filtering…",
        ["过滤失败：{0}"] = "Filter failed: {0}",
        ["没有可导出的行"] = "No rows to export",
        ["已导出 {0:N0} 行 → {1}"] = "Exported {0:N0} rows → {1}",
        ["导出失败：{0}"] = "Export failed: {0}",
        ["没有可保存的日志"] = "No log to save",
        ["日志已保存 → {0}"] = "Log saved → {0}",
        ["采集中… {0:N0} 行"] = "Capturing… {0:N0} rows",
        ["刷新失败: {0}"] = "Refresh failed: {0}",

        // ── 工具栏输入框提示（ToolTip）──
        ["如 05 20（空格分隔）"] = "e.g. 05 20 (space-separated)",
        ["多个用空格分隔，短语用双引号包裹"] = "Separate multiple with spaces; wrap phrases in double quotes",
        ["多词用空格分隔，短语用双引号包裹"] = "Separate words with spaces; wrap phrases in double quotes",

        // ── 过滤条件摘要尾注 ──
        ["（设置在「过滤设置」里）"] = "(configurable in Filter Settings)",

        // ── 右键菜单：按当前值过滤 ──
        ["按此分钟过滤：{0:D2}"] = "Filter by this minute: {0:D2}",

        // ── 收藏清理 ──
        ["确定清空{0}收藏？"] = "Clear all {0} favorites?",
        ["已清空收藏"] = "Favorites cleared",

        // ── 关于对话框 ──
        // 三个键各自独立：ShowAbout 里是三段 Loc.T(...)，换行符 "\n\n" / "\n" 由调用点自己拼，
        // 所以进键的只是单段文案，不含换行
        ["Windows 桌面端 Android 日志（logcat）查看器"] = "A Windows desktop viewer for Android logs (logcat)",
        ["许可：Apache License 2.0（详见 LICENSE）"] = "License: Apache License 2.0 (see LICENSE)",
        ["第三方声明：见程序目录下 THIRD-PARTY-NOTICES.md"] = "Third-party notices: see THIRD-PARTY-NOTICES.md in the program directory",
    };
}