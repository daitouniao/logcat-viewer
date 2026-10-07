namespace logcat.Services;

/// <summary>英文译文表：命令窗口、命令编辑、命令分类与内置命令库。</summary>
static partial class LocTable
{
    public static Dictionary<string, string> CommandUi() => new Dictionary<string, string>()
    {
        // ── 通道显示名（对应 Models/CommandKinds）──
        ["设备 shell"] = "Device shell",
        ["本机 adb"] = "Local adb",

        // ── 编辑对话框 ──
        ["新建收藏命令"] = "New Favorite Command",
        ["编辑收藏命令"] = "Edit Favorite Command",
        ["分类："] = "Category:",
        ["新建分类…"] = "New Category…",
        ["新建分类"] = "New Category",
        ["分类名称："] = "Category name:",
        ["通道："] = "Channel:",
        ["以 root 执行（su -c 包裹）"] = "Run as root (wrapped in su -c)",
        ["命令："] = "Command:",
        ["备注："] = "Remark:",
        ["请输入命令。"] = "Please enter a command.",
        ["请选择或输入分类。"] = "Please select or enter a category.",
        ["命令里的 {名称} 是参数占位符，执行前会提示填值并记住上次输入，如 {pkg}、{path}。本机 adb 通道只写 adb 后面的部分（install -r xx.apk）。"] =
            "{name} placeholders in the command are filled in when you run it, and your last input is remembered " +
            "(e.g. {pkg}, {path}). For the local adb channel, write only the part after adb (install -r xx.apk).",

        // ── 内置分类 ──
        ["shell"] = "shell",
        ["dumpsys"] = "dumpsys",
        ["应用与包"] = "Apps & Packages",
        ["日志与异常"] = "Logs & Crashes",
        ["adb"] = "adb",
    };

    /// <summary>
    /// 内置命令库的备注译文。键 = <see cref="Models.CommandEntry.Remark"/> 的中文原文。
    /// 走独立表而非并入 <see cref="CommandUi"/>：这些串只出现在内置命令上，
    /// 且按用户要求「按语言查表」——切语言立即生效，不受 commands.json 里已存的旧文案影响。
    /// </summary>
    public static Dictionary<string, string> CommandSeed() => new Dictionary<string, string>()
    {
        // shell · 基本信息
        ["全部系统属性"] = "All system properties",
        ["安卓版本"] = "Android version",
        ["API 级别"] = "API level",
        ["机型"] = "Device model",
        ["序列号"] = "Serial number",
        ["内核版本"] = "Kernel version",
        ["设备时间"] = "Device time",
        ["运行时长与负载"] = "Uptime and load",
        ["内存使用（MB）"] = "Memory usage (MB)",
        ["各分区剩余空间"] = "Free space per partition",
        ["CPU 占用快照（前 20 个进程）"] = "CPU usage snapshot (top 20 processes)",
        ["全部进程"] = "All processes",
        ["查应用的进程"] = "Find an app's processes",
        ["查应用 PID"] = "Find an app's PID",
        ["结束指定进程"] = "Kill a process",
        ["列目录"] = "List directory",
        ["临时目录内容"] = "Temp directory contents",
        ["按名字找文件"] = "Find files by name",
        ["目录占用"] = "Directory size",
        ["网卡地址"] = "Network interfaces",
        ["测网络连通"] = "Test network connectivity",
        ["飞行模式状态"] = "Airplane mode state",
        ["屏幕亮度"] = "Screen brightness",
        ["动画缩放（0 关闭 / 1 默认）"] = "Animation scale (0 off / 1 default)",
        ["分辨率"] = "Resolution",
        ["屏幕密度"] = "Screen density",
        ["HOME 键"] = "HOME key",
        ["BACK 键"] = "BACK key",
        ["电源键（锁屏/解锁）"] = "Power key (lock/unlock)",
        ["MENU 键"] = "MENU key",
        ["上滑（x1 y1 x2 y2 时长ms）"] = "Swipe up (x1 y1 x2 y2 duration-ms)",
        ["输入文本"] = "Type text",
        ["截图存到设备"] = "Save screenshot to device",
        ["SELinux 状态"] = "SELinux state",
        ["下拉通知栏"] = "Expand the notification shade",
        ["受限目录与 SELinux 上下文"] = "Restricted dirs and SELinux contexts",
        ["内核日志末尾"] = "Tail of kernel log",
        ["分区挂载情况"] = "Partition mounts",

        // shell · dumpsys
        ["列出全部 dumpsys 服务"] = "List all dumpsys services",
        ["窗口状态"] = "Window state",
        ["显示与分辨率细节"] = "Display and resolution details",
        ["Activity 任务栈"] = "Activity task stack",
        ["前台 Activity 与视图层级"] = "Foreground activity and view hierarchy",
        ["最近任务"] = "Recents",
        ["广播队列"] = "Broadcast queue",
        ["应用安装信息（权限、组件、版本）"] = "Package info (permissions, components, version)",
        ["应用内存详情"] = "App memory details",
        ["整机内存概况"] = "Overall memory usage",
        ["整机 CPU 概况"] = "Overall CPU usage",
        ["帧率与卡顿统计"] = "Frame rate and jank stats",
        ["数据库打开情况"] = "Open databases",
        ["电源与 wakelock"] = "Power and wakelocks",
        ["电池状态"] = "Battery state",
        ["WiFi 状态"] = "WiFi state",
        ["网络连接"] = "Network connections",
        ["通知与监听器"] = "Notifications and listeners",
        ["输入法"] = "Input method",
        ["应用使用统计"] = "App usage stats",
        ["JobScheduler 任务"] = "JobScheduler jobs",
        ["系统崩溃 / ANR 记录"] = "System crash / ANR records",

        // shell · 应用与包
        ["第三方应用包名"] = "Third-party packages",
        ["系统应用包名"] = "System packages",
        ["包名与 APK 路径"] = "Packages and APK paths",
        ["APK 路径"] = "APK Path",
        ["危险权限分组"] = "Dangerous permission groups",
        ["授予运行时权限"] = "Grant a runtime permission",
        ["收回运行时权限"] = "Revoke a runtime permission",
        ["清除应用数据"] = "Clear app data",
        ["禁用应用"] = "Disable app",
        ["启用应用"] = "Enable app",
        ["启动指定组件"] = "Start a component",
        ["打开应用详情页"] = "Open app details",
        ["强制停止应用"] = "Force-stop app",
        ["杀后台进程"] = "Kill background process",
        ["发送广播"] = "Send a broadcast",
        ["打开链接"] = "Open a link",
        ["随机压测 1000 事件"] = "Random stress test, 1000 events",

        // shell · 日志与异常
        ["最近 200 行（-d 打印后退出）"] = "Last 200 lines (-d dumps then exits)",
        ["只看 Error"] = "Errors only",
        ["只看某个 tag"] = "One tag only",
        ["crash 缓冲区"] = "crash buffer",
        ["events 缓冲区"] = "events buffer",
        ["radio 缓冲区"] = "radio buffer",
        ["清空日志缓冲区"] = "Clear log buffers",
        ["告警及以上，threadtime 格式"] = "Warning and above, threadtime format",
        ["ANR trace 文件"] = "ANR trace files",
        ["native crash tombstone"] = "native crash tombstone",
        ["进程状态详情"] = "Process status details",

        // adb
        ["adb 版本"] = "adb version",
        ["设备与传输方式"] = "Devices and transport",
        ["设备状态"] = "Device state",
        ["设备序列号"] = "Device serial",
        ["阻塞直到设备上线（注意超时设置）"] = "Block until the device is online (watch the timeout)",
        ["以 root 重启 adbd"] = "Restart adbd as root",
        ["以普通身份重启 adbd"] = "Restart adbd as a normal user",
        ["切回 USB 调试"] = "Switch back to USB debugging",
        ["切到网络调试"] = "Switch to network debugging",
        ["无线连接设备"] = "Connect to a device over WiFi",
        ["断开全部网络调试连接"] = "Disconnect all network debugging",
        ["重启设备"] = "Reboot device",
        ["进入 fastboot"] = "Reboot to fastboot",
        ["进入 recovery"] = "Reboot to recovery",
        ["重新挂载分区为可写（需 root）"] = "Remount partitions read-write (needs root)",
        ["关闭 dm-verity（需 root）"] = "Disable dm-verity (needs root)",
        ["覆盖安装 APK"] = "Reinstall an APK",
        ["卸载应用"] = "Uninstall an app",
        ["推送文件到设备"] = "Push a file to the device",
        ["从设备取文件"] = "Pull a file from the device",
        ["端口转发：本机 → 设备"] = "Port forward: local → device",
        ["端口反向转发：设备 → 本机"] = "Port reverse forward: device → local",
        ["查看端口转发列表"] = "List port forwards",

        // ── 命令编辑对话框（CommandEditDialog）──
        // HINT 常量是「两段用 + 拼起来」的，键必须与拼好后的整串完全一致
        // （含中文分号）。整串版本在下方 CommandUi 表首，这里不重复收半句。
        ["如 dumpsys meminfo {pkg}"] = "e.g. dumpsys meminfo {pkg}",
    };
}