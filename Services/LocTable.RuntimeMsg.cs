namespace logcat.Services;

/// <summary>
/// 英文译文表：运行时用户可见消息（索引 / 过滤进度、ADB 相关）。
///
/// ── 不进本表的内容（有意保留中文）──
/// - <c>StartupLog</c> / <c>DpiDiag</c> / <c>_logger.Log*</c> 的诊断日志
/// - <c>ArgumentException</c> / <c>InvalidOperationException</c> 的异常消息
/// 这些面向开发者排查问题，不属于本地化范围，翻译只会增加噪音与维护成本。
/// </summary>
static partial class LocTable
{
    public static Dictionary<string, string> RuntimeMsg() => new Dictionary<string, string>()
    {
        // ── 索引 / 过滤进度（LogDocument / FilterEngine 上报给状态栏）──
        ["扫描行…"] = "Scanning lines…",
        ["合并索引…"] = "Merging index…",
        ["追加新内容…"] = "Appending new content…",
        ["匹配 message {0}/{1}"] = "Matching message {0}/{1}",
        ["候选 {0:N0} 行"] = "{0:N0} candidate rows",
        ["导出 {0:N0}/{1:N0}"] = "Exporting {0:N0}/{1:N0}",
        ["完成"] = "Done",

        // ── ADB ──
        ["设备 {0} 未连接"] = "Device {0} is not connected",
        ["采集异常：{0}"] = "Capture error: {0}",
        ["采集过程中发生异常"] = "An error occurred during capture",

        // ── 列表截断提示 ──
        // 键含前导空格：调用点是 `msg + Loc.F(" …(+{0} 字符)", n)`，
        // 空格属于拼接分隔符，必须一起进键，否则查不到译文
        [" …(+{0} 字符)"] = " …(+{0} chars)",

        // ── 捕获期间输出的命令回显 ──
        ["> 推送 {0} → {1}:{2}"] = "> Pushing {0} → {1}:{2}",

        // ── 进度行尾注 ──
        ["（未取到应用名，已用包名末段代替）"] = " (app names unavailable; using the last segment of each package name)",
    };
}