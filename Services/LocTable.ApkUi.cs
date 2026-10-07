namespace logcat.Services;

/// <summary>英文译文表：安装 / 卸载 APK 窗口。</summary>
static partial class LocTable
{
    public static Dictionary<string, string> ApkUi() => new Dictionary<string, string>()
    {
        // ── 窗口与公共控件 ──
        ["安装 APK — {0}"] = "Install APK — {0}",
        ["卸载 APK — {0}"] = "Uninstall APK — {0}",
        ["输出"] = "Output",
        ["APK 文件:"] = "APK file:",
        ["★ 收藏"] = "★ Favorite",
        ["☆ 移除"] = "☆ Remove",
        ["安装方式:"] = "Install method:",
        ["参数:"] = "Options:",
        ["临时目录:"] = "Temp dir:",
        ["安装"] = "Install",
        ["提示：pm install 完成后会自动清理临时 APK。输出里出现 Success 即安装成功。"] =
            "Note: the temp APK is cleaned up automatically after pm install finishes. A \"Success\" line in the output means it succeeded.",

        // ── 安装选项 ──
        ["pm install（adb 安装被禁用时，先推送到设备再装）"] =
            "pm install (when adb install is disabled, push to device first)",
        ["-r 覆盖安装（保留数据）"] = "-r reinstall (keep data)",
        ["-d 允许降级"] = "-d allow downgrade",
        ["-g 授予全部权限"] = "-g grant all permissions",
        ["-t 允许测试包"] = "-t allow test packages",
        ["以 root 执行（su -c 包裹，仅 pm install 生效）"] =
            "Run as root (wrapped in su -c, only applies to pm install)",

        // ── 安装状态 ──
        ["APK 路径为空，无法收藏"] = "APK path is empty, cannot favorite",
        ["该路径未在收藏中"] = "That path is not in favorites",
        ["请选择或输入 APK 文件路径"] = "Please select or enter an APK file path",
        ["找不到文件：{0}"] = "File not found: {0}",
        ["adb install 完成，请检查输出中的 Success"] = "adb install finished, check output for Success",
        ["adb install 结束（退出码 {0}）"] = "adb install ended (exit code {0})",
        ["pm install 执行完毕，请检查输出中的 Success/Failure"] =
            "pm install finished, check output for Success/Failure",
        ["安装失败：{0}"] = "Install failed: {0}",

        // ── 卸载 ──
        ["应用包名:"] = "App package:",
        ["↻ 刷新列表"] = "↻ Refresh List",
        ["卸载方式:"] = "Uninstall method:",
        ["pm uninstall（adb 卸载被禁用时）"] = "pm uninstall (when adb uninstall is disabled)",
        ["-k 保留数据和缓存"] = "-k keep data and cache",
        ["以 root 执行（su -c 包裹，仅 pm 生效）"] = "Run as root (wrapped in su -c, only applies to pm)",
        ["卸载输入的包名"] = "Uninstall Entered Package",
        ["提示：双击下方列表项可直接卸载；卸载系统预装应用通常需 root。"] =
            "Note: double-click a list item to uninstall it; removing preinstalled system apps usually requires root.",
        ["应用名"] = "App Name",
        ["包名"] = "Package",
        // ["APK 路径"] 在 CommandUi（内置命令库的「应用与包」分类），此处不重复定义：
        // 同一个键写两遍不会报错，但 BuildEn 里后者静默覆盖前者，译文会随分区顺序漂移。

        // ── 卸载状态 ──
        ["包名为空，无法收藏"] = "Package name is empty, cannot favorite",
        ["该包名未在收藏中"] = "That package is not in favorites",
        ["请输入或选择要卸载的应用包名"] = "Please enter or select an app package to uninstall",
        ["确定要卸载应用 {0} 吗？"] = "Uninstall app {0}?",
        ["确定要卸载「{0}」吗？\n\n包名：{1}\n此操作不可恢复。"] =
            "Uninstall \"{0}\"?\n\nPackage: {1}\nThis cannot be undone.",
        ["确认卸载"] = "Confirm Uninstall",
        ["adb uninstall 完成，请检查输出中的 Success"] = "adb uninstall finished, check output for Success",
        ["adb uninstall 结束（退出码 {0}）"] = "adb uninstall ended (exit code {0})",
        ["pm uninstall 执行完毕，请检查输出中的 Success/Failure"] =
            "pm uninstall finished, check output for Success/Failure",
        ["卸载失败：{0}"] = "Uninstall failed: {0}",
        ["读取应用列表失败：{0}"] = "Reading app list failed: {0}",
        ["已加载 {0} 个三方应用{1}"] = "Loaded {0} third-party apps{1}",
        // ["（未取到应用名，已用包名末段代替）"] 是进度行尾注，译文在 RuntimeMsg。
        ["确定要删除设备上的以下文件/目录吗？\n{0}"] =
            "Delete the following files/directories on the device?\n{0}",
        ["确定要删除本机上的以下文件/目录吗？\n{0}\n\n（此操作不可撤销）"] =
            "Delete the following files/directories on this PC?\n{0}\n\n(This cannot be undone)",
        ["确认删除"] = "Confirm Delete",
        ["…等 {0} 项"] = "… and {0} more",

        // ── 两窗口共用 ──
        ["命令没有返回输出：可能是命令不存在、权限不足，或被设备限制（可试试勾选 root 或改用 pm 通道）"] =
            "The command returned no output: it may not exist, lack permission, or be blocked by the device " +
            "(try enabling root, or switch to the pm channel)",

        // ── 状态行失败前缀 ──
        // 整串键（"安装失败：{0}" 等）在上面；调用点已改用 Loc.F 拼整句，
        // 不再保留「前缀 + 运行时拼接」用的半句——译文无法调整语序，是坑。

        // ── 应用列表 ──
        ["已加载 {0} 个三方应用"] = "Loaded {0} third-party apps",
        ["已安装 {0} 个应用"] = "Installed {0} apps",
    };
}