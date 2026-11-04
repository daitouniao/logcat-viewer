namespace logcat.Services;

/// <summary>英文译文表：命令窗口（CommandDialog）主体。</summary>
static partial class LocTable
{
    /// <summary>命令窗口（CommandDialog）主体文案。</summary>
    /// <remarks>
    /// 命令窗口的表刻意与 <see cref="CommandUi"/> 分开：该窗口字符串密度最高
    /// （92 处），单独成文件便于逐条校对，且不干扰其他窗口的阅读。
    /// </remarks>
    public static Dictionary<string, string> CommandUiMain() => new Dictionary<string, string>()
    {
        ["（全部）"] = "(All)",

        // ── 分类行 ──
        ["分类"] = "Category",
        ["新建"] = "New",
        ["删除"] = "Delete",
        ["  筛选"] = "  Filter",
        ["命令或备注"] = "Command or remark",

        // ── 模式 ──
        ["收藏"] = "Favorites",

        // ── 操作按钮 ──
        ["★ 收藏当前…"] = "★ Favorite Current…",
        ["编辑"] = "Edit",
        ["移除"] = "Remove",
        ["清空列表"] = "Clear List",
        ["执行"] = "Run",
        ["清空输出"] = "Clear Output",
        ["另存输出…"] = "Save Output As…",
        ["输出已清空"] = "Output cleared",

        // ── 选项行 ──
        ["  超时"] = "  Timeout",
        ["秒（0 不限）"] = "sec (0 = no limit)",
        ["root（su -c）"] = "root (su -c)",
        ["跟随输出"] = "Follow Output",
        ["Enter 执行；{pkg}、{path} 等占位符执行前提示填值"] =
            "Enter runs it; placeholders like {pkg} and {path} are filled in before running",

        // ── 列表列头 ──
        ["命令"] = "Command",
        ["通道"] = "Channel",
        ["次数"] = "Count",
        ["最近使用"] = "Last Used",
        ["备注"] = "Remark",

        // ── 右键菜单 ──
        ["填入命令框"] = "Fill into Command Box",
        ["直接执行"] = "Run Now",
        ["复制命令"] = "Copy Command",
        ["编辑收藏…"] = "Edit Favorite…",
        ["从收藏移除"] = "Remove from Favorites",
        ["收藏这条…"] = "Favorite This…",
        ["从历史删除"] = "Delete from History",
        ["清空当前列表"] = "Clear Current List",

        // ── 状态栏 ──
        ["{0} 条"] = "{0} items",
        ["已填入命令框，按 Enter 执行"] = "Filled into the command box — press Enter to run",
        ["分类已存在或名称为空"] = "Category already exists or the name is empty",
        ["请先在分类下拉框选中要改名的分类"] = "Select a category to rename from the dropdown first",
        ["改名失败：新名称为空或已存在"] = "Rename failed: the new name is empty or already exists",
        ["请先在分类下拉框选中要删除的分类"] = "Select a category to delete from the dropdown first",
        ["删除分类「{0}」？\n\n该分类下的 {1} 条收藏会一并删除。"] =
            "Delete category \"{0}\"?\n\nIts {1} favorite commands will be deleted too.",
        ["删除分类"] = "Delete Category",
        ["把「{0}」改名为："] = "Rename \"{0}\" to:",
        ["重命名分类"] = "Rename Category",

        // ── 收藏 ──
        ["命令为空，无法收藏"] = "Command is empty, cannot favorite",
        ["该命令已在「{0}」收藏中"] = "This command is already favorited under \"{0}\"",
        ["收藏失败：命令重复或为空"] = "Favorite failed: duplicate or empty command",
        ["已收藏到「{0}」"] = "Favorited under \"{0}\"",
        ["请先在列表中选中一条收藏"] = "Select a favorite in the list first",
        ["保存失败：与另一条收藏的命令重复"] = "Save failed: the command duplicates another favorite",
        ["从收藏移除？\n{0}"] = "Remove from favorites?\n{0}",

        // ── 清空 ──
        ["最近使用历史"] = "the recent-usage history",
        ["「{0}」的收藏"] = "the favorites in \"{0}\"",
        ["全部分类"] = "All Categories",
        ["清空{0}？此操作不可恢复。"] = "Clear {0}? This cannot be undone.",

        // ── 执行 ──
        ["提示：当前设备未检测到 root，su -c 可能失败并在手机上弹授权"] =
            "Note: root was not detected on this device, so su -c may fail and may prompt for authorization on the phone",
        ["设备：未选择（本机 adb 通道仍可用）"] = "Device: none selected (the local adb channel still works)",
        ["请输入命令"] = "Please enter a command",
        ["未选择设备：请先在主界面选设备，或把通道切到「本机 adb」"] =
            "No device selected: pick a device in the main window, or switch the channel to Local adb",
        ["已手动停止"] = "Stopped by user",
        ["超过 {0} 秒未结束，已中断"] = "Did not finish within {0}s, interrupted",
        ["完成 · {0:F1}s · {1} 行{2}"] = "Done · {0:F1}s · {1} lines{2}",
        [" · 退出码 {0}"] = " · exit code {0}",
        ["命令没有返回输出：可能是命令不存在、权限不足，或输出里含报错文本被 adb 判定为失联（可试试勾选 root）"] =
            "The command returned no output: it may not exist, lack permission, or its output contained an error " +
            "that adb treated as a dropped connection (try enabling root)",
        // 键只含前缀本身：调用点是 Loc.T("…（最早的输出已丢弃）…\n") + head，
// 保留的输出内容是变量、不是占位符，故不进键
["…（最早的输出已丢弃）…\n"] = "…(earliest output discarded)…\n",
        ["没有输出可保存"] = "No output to save",

        // ── 保存与参数 ──
        ["保存命令输出"] = "Save Command Output",
        ["文本文件 (*.txt)|*.txt|日志文件 (*.log)|*.log|所有文件 (*.*)|*.*"] =
            "Text file (*.txt)|*.txt|Log file (*.log)|*.log|All files (*.*)|*.*",
        ["已保存 → {0}"] = "Saved → {0}",
        ["保存失败"] = "Save Failed",
        ["填写命令参数"] = "Fill Command Parameter",
        ["为 {{{0}}} 输入值："] = "Enter a value for {{{0}}}:",
        ["参数 {{{0}}} 不能为空，否则命令会拼错。"] = "Parameter {{{0}}} cannot be empty, or the command will be malformed.",

        // ── 执行结果状态行 ──
        ["失败 · {0:F1}s · {1} 行"] = "Failed · {0:F1}s · {1} rows",
        ["完成 · {0:F1}s · {1} 行"] = "Done · {0:F1}s · {1} rows",

        // ── 提示气泡 / 状态栏 ──
        ["无法访问 {path}，权限不足或目录不存在"] = "Cannot access {path}: insufficient permission, or the directory does not exist",
        // 整串版本在 FileUi（"请先在下拉框…\n（也可以直接用左侧…）"），此处不重复收半句。
        ["（也可以直接用左侧的「☆ 取消收藏」按钮移除当前目录。）"] =
            "(You can also use the \"☆ Unfavorite\" button on the left to remove the current directory.)",
    };
}