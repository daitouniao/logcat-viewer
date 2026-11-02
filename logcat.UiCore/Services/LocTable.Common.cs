namespace logcat.Services;

/// <summary>
/// 英文译文表。<b>键 = 中文原文</b>，由 <c>LocTable</c> 的各分区汇总而成。
///
/// ── 维护约定 ──
/// 1. 键必须是**代码里出现的中文原文**，一字不差。改代码里的中文而不改表，
///    英文态会静默回退显示中文（<see cref="Loc.T"/> 的兜底行为，不会崩）。
///    <c>LocTableTests</c> 会双向断言「表里的键都被用到」+「代码里的键都在表里」。
/// 2. 占位符必须与中文**个数相同、类型兼容**（用 <c>{0}</c> 风格）。
///    译文多一个或少一个占位符都会被测试拦下。
/// 3. 译文里不要出现中文。
/// </summary>
static partial class LocTable
{
    // ── 分区汇总 ──

    /// <summary>通用：按钮动作、状态栏、弹窗标题、文件对话框过滤器。</summary>
    public static Dictionary<string, string> Common() => new Dictionary<string, string>()
    {
        // ── 通用动作 ──
        ["确定"] = "OK",
        ["取消"] = "Cancel",
        ["关闭"] = "Close",
        ["重置"] = "Reset",
        ["应用"] = "Apply",
        ["全选"] = "All",
        ["停止"] = "Stop",
        ["复制"] = "Copy",
        ["清空"] = "Clear",
        ["删除选中"] = "Delete Selected",
        ["复制输出"] = "Copy Output",
        ["命令窗口"] = "Commands",
        ["移除收藏"] = "Remove Favorite",
        ["设备操作"] = "Device Ops",
        ["设备：{0}{1}"] = "Device: {0}{1}",
        ["— 退出码 {0}"] = "— exit code {0}",
        ["刷新"] = "Refresh",
        ["重命名"] = "Rename",
        ["保存…"] = "Save…",
        ["浏览…"] = "Browse…",
        ["新建文件夹"] = "New Folder",
        ["文件夹名称："] = "Folder name:",

        // ── 通用对话框标题 ──
        ["错误"] = "Error",
        ["提示"] = "Notice",
        ["关于"] = "About",
        ["警告"] = "Warning",

        // ── 文件对话框过滤器 ──
        ["日志文件 (*.log *.txt)|*.log;*.txt|所有文件 (*.*)|*.*"] =
            "Log files (*.log *.txt)|*.log;*.txt|All files (*.*)|*.*",
        ["APK 文件 (*.apk)|*.apk|所有文件 (*.*)|*.*"] =
            "APK files (*.apk)|*.apk|All files (*.*)|*.*",
        ["PNG 图片 (*.png)|*.png|JPEG 图片 (*.jpg)|*.jpg|所有文件 (*.*)|*.*"] =
            "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg|All files (*.*)|*.*",
        ["MP4 视频 (*.mp4)|*.mp4|所有文件 (*.*)|*.*"] =
            "MP4 video (*.mp4)|*.mp4|All files (*.*)|*.*",

        // ── 文件对话框标题 ──
        ["打开日志文件"] = "Open Log File",
        ["导出"] = "Export",
        ["保存日志"] = "Save Log",
        ["保存截图"] = "Save Screenshot",
        ["保存录屏"] = "Save Screen Recording",
        ["选择要安装的 APK"] = "Select APK to Install",
        ["选择要上传到设备的文件"] = "Select Files to Upload to Device",

        // ── 状态栏 / 通用提示 ──
        ["执行中…"] = "Working…",
        ["停止中…"] = "Stopping…",
        ["已取消"] = "Cancelled",
        ["加载中…"] = "Loading…",
        ["加载失败"] = "Load failed",
        ["刷新中…"] = "Refreshing…",
        ["没有输出可复制"] = "No output to copy",
        ["输出已复制到剪贴板"] = "Output copied to clipboard",
        ["传输失败"] = "Transfer failed",
        ["传输结果"] = "Transfer Result",
        ["复制结果"] = "Copy Result",
        ["正在传输，请稍候…"] = "Transferring, please wait…",
        ["没有可传输的文件"] = "No files to transfer",
    };
}