namespace logcat.Services;

/// <summary>英文译文表：设备操作（截图 / 录屏 / run-as / 设备操作总窗口）。</summary>
static partial class LocTable
{
    public static Dictionary<string, string> DeviceUi() => new Dictionary<string, string>()
    {
        ["截图"] = "Screenshot",
        ["录屏"] = "Screen Record",
        ["文件浏览"] = "File Browser",
        ["安装 APK"] = "Install APK",
        ["卸载 APK"] = "Uninstall APK",
        ["请先在主窗口选择设备"] = "Select a device in the main window first",

        // ── 截图 ──
        ["截图 — {0}"] = "Screenshot — {0}",
        ["截图数据为空"] = "Screenshot data is empty",
        ["截图失败：{0}"] = "Screenshot failed: {0}",

        // ── 录屏 ──
        ["录屏 — {0}"] = "Screen Recording — {0}",
        ["点击「开始录屏」录制设备屏幕"] = "Click \"Start Recording\" to capture the device screen",
        ["开始录屏"] = "Start Recording",
        ["停止录屏"] = "Stop Recording",
        ["拉取到本地…"] = "Pull to Local…",
        ["录屏中…"] = "Recording…",
        ["录屏完成（{0:F1} 秒），可拉取到本地"] = "Recording finished ({0:F1}s), ready to pull locally",
        ["已拉取 → {0}"] = "Pulled → {0}",
        ["拉取失败：{0}"] = "Pull failed: {0}",
        ["● 录屏中  {0:D2}:{1:D2}"] = "● Recording  {0:D2}:{1:D2}",

        // ── run-as 模式 ──
        ["run-as 模式"] = "run-as Mode",
        ["应用包名（可输入，或从访问过的包名中选择）："] = "App package (type it, or pick from visited packages):",
        ["中转目录（设备端路径，默认放 Download）："] = "Relay directory (device path, Download by default):",
        ["受限目录（/data/data 等）的文件先复制到中转目录，再经 sync 通道收发；中转目录需为 shell 可读写的路径（sdcard 下均可）。"] =
            "Files in restricted directories (such as /data/data) are first copied to the relay directory, then transferred over the sync channel. " +
            "The relay directory must be a shell-readable and writable path (anywhere under sdcard works).",
        ["请输入应用包名。"] = "Please enter an app package name.",
        ["中转目录必须是设备端绝对路径（以 / 开头）。"] = "The relay directory must be an absolute device path (starting with /).",
        ["回到 root 模式"] = "Back to root Mode",
        ["退出 run-as 模式"] = "Exit run-as Mode",
        ["切换包名…"] = "Switch Package…",

        // ── 记录详情 ──
        ["记录 #{0}"] = "Record #{0}",
// 键里的 \n 是 verbatim 字符串里的字面两字符（反斜杠 + n），
        // 与调用点 Loc.T(@"把字面的 \n 也拆成真实换行") 完全一致
        ["把字面的 \\n 也拆成真实换行"] = "Also turn literal \\n into real line breaks",

        // ── run-as 中转目录说明（RunAsDialog 的 HINT 常量）──
        // 整段作为一个键：调用点用 const 拼好后交给 Loc.Bind，换行/拼接方式变了键也要跟着变
        ["受限目录（/data/data 等）的文件先复制到中转目录，再经 sync 通道收发，中转目录需为 shell 可读写的路径（sdcard 下均可）。"] =
            "Files in restricted directories (/data/data etc.) are copied to a staging directory first, then transferred over the sync channel; the staging directory must be readable and writable by shell (anything under sdcard works).",
    };
}