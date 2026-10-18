namespace logcat.Services;

/// <summary>
/// 英文译文表：文件代理（fileproxy）——设置对话框、菜单入口、协议与编排错误消息。
/// 渠道状态三键与「已在运行」提示在 <see cref="RuntimeMsg"/>（状态栏一次性文案口径）。
/// </summary>
static partial class LocTable
{
    public static Dictionary<string, string> FileProxyUi() => new Dictionary<string, string>()
    {
        // ── 菜单入口 ──
        ["文件代理…"] = "File Proxy…",
        ["手动输入完整路径…"] = "Enter Full Path Manually…",
        ["手动输入完整路径"] = "Enter Full Path Manually",
        ["请输入日志文件的完整路径："] = "Enter the full path of the log file:",

        // ── 设置对话框 ──
        ["文件代理设置"] = "File Proxy Settings",
        ["模式"] = "Mode",
        ["停用"] = "Off",
        ["启用"] = "On",
        ["自动"] = "Auto",
        ["管道名"] = "Pipe Name",
        ["启动命令"] = "Launch Command",
        ["可执行文件（解释器或程序的完整路径）"] = "Executable (full path to the interpreter or program)",
        ["启动等待超时（毫秒）"] = "Launch timeout (ms)",
        ["测试连接"] = "Test Connection",
        ["保存"] = "Save",
        ["测试连接成功：{0}（协议版本 {1}）"] = "Connection OK: {0} (protocol version {1})",
        ["测试连接失败：{0}"] = "Connection failed: {0}",
        ["管道名不能为空"] = "Pipe name cannot be empty",
        ["管道名不能包含路径分隔符"] = "Pipe name cannot contain path separators",
        ["管道名过长（上限 {0} 个字符）"] = "Pipe name is too long (max {0} characters)",

        // ── 设置对话框：浏览按钮（design D16）──（「浏览…」已入 Common 表，勿重复定义）
        ["选择可执行文件"] = "Select Executable",
        ["选择脚本文件"] = "Select Script File",
        ["程序 (*.exe)|*.exe|所有文件 (*.*)|*.*"] = "Programs (*.exe)|*.exe|All Files (*.*)|*.*",
        ["脚本 (*.js;*.py)|*.js;*.py|所有文件 (*.*)|*.*"] = "Scripts (*.js;*.py)|*.js;*.py|All Files (*.*)|*.*",

        // ── 设置对话框：--pipe 自动追加与命令行预览（design D17）──
        ["参数（其余参数；--pipe 管道名由应用自动追加）"] = "Arguments (extra args only; --pipe <name> is appended automatically)",
        ["命令行预览：{0}"] = "Command line preview: {0}",

        // ── 协议客户端错误（FileProxyClient / FrameReader）──
        ["连接文件代理超时（{0} ms）：管道 {1} 无响应"] = "Connecting to the file proxy timed out ({0} ms): pipe {1} is not responding",
        ["连接文件代理失败：管道 {0}（{1}）"] = "Failed to connect to the file proxy: pipe {0} ({1})",
        ["文件代理协议版本不匹配：代理自描述 {0}，协议版本 {1}，应用仅支持 {2}"] =
            "File proxy protocol version mismatch: proxy \"{0}\", protocol {1}, app supports {2}",
        ["文件代理返回错误：{0}"] = "The file proxy returned an error: {0}",
        ["文件代理返回了空响应：{0}"] = "The file proxy returned an empty response: {0}",
        ["文件代理响应不是合法 JSON：{0}"] = "The file proxy response is not valid JSON: {0}",
        ["文件代理响应 ID 不匹配：期望 {0}，实际 {1}"] = "File proxy response ID mismatch: expected {0}, got {1}",
        ["文件代理返回的负载长度越界：{0}"] = "The file proxy returned an out-of-range payload length: {0}",
        ["文件代理连接中断：读取控制消息时流已结束（已收 {0} 字节）"] =
            "File proxy connection lost: stream ended while reading a control message ({0} bytes received)",
        ["文件代理控制消息超过单行上限 {0} 字节"] = "File proxy control message exceeds the {0}-byte line limit",
        ["文件代理连接中断：收取裸块时流已结束（应收 {0} 字节，实收 {1}）"] =
            "File proxy connection lost: stream ended while reading a data block (expected {0} bytes, got {1})",
        ["文件代理控制消息超长（{0} 字节，上限 {1}）：路径可能过长"] =
            "File proxy control message is too long ({0} bytes, limit {1}): the path may be too long",

        // ── 编排 / spool 失败分类（FileProxyService）──
        ["未连接到文件代理。请确认代理正在运行，或在「设置 - 文件代理」中配置启动命令。"] =
            "Not connected to the file proxy. Make sure the proxy is running, or configure a launch command in Settings - File Proxy.",
        ["文件代理启动超时（{0} ms）。命令行：{1}"] = "File proxy launch timed out ({0} ms). Command line: {1}",
        ["文件代理启动失败：{0}（命令行：{1}）"] = "File proxy launch failed: {0} (command line: {1})",
        ["文件代理管道名无效：{0}"] = "Invalid file proxy pipe name: {0}",
        ["经文件代理读取不完整：实收 {0} 字节，期望 {1} 字节。可重新打开文件重试。"] =
            "Incomplete read via file proxy: received {0} of {1} bytes. Reopen the file to retry.",
        ["文件代理读取失败：{0}"] = "File proxy read failed: {0}",

        // ── 超大文件预警（阈值 1GB 写死）──
        ["目标文件大小 {0}，经文件代理读取前将整体暂存到本机磁盘（应用目录 temp 下），磁盘不足可能失败。是否继续？"] =
            "The file is {0}. Reading it via the file proxy first spools the whole file to local disk (under the app's temp folder) and may fail if the disk is full. Continue?",
        ["文件代理"] = "File Proxy",

        // ── 装载入口分流（frmMain）──
        ["（转代理前直接打开失败：{0}）"] = "(Direct open before the proxy fallback failed: {0})",
    };
}
