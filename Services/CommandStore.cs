using System.Text.Json;
using System.Text.Json.Serialization;
using logcat.Models;

namespace logcat.Services;

/// <summary>
/// 命令窗口的存储：分类、收藏命令、最近使用历史，外加一份内置的常用命令库。
/// 数据以 JSON 形式持久化到 %LOCALAPPDATA%\logcat\commands.json。
/// </summary>
public sealed class CommandStore
{
    const int MaxFavorites = 300;
    const int MaxHistory = 120;

    /// <summary>未指定分类时的归属。</summary>
    public const string FallbackCategory = "shell";

    sealed class StoreData
    {
        public List<string> Categories { get; set; } = new();
        public List<CommandEntry> Favorites { get; set; } = new();
        public List<CommandRun> History { get; set; } = new();

        /// <summary>命令占位符上次填的值（占位符名 → 值），省去重复输入包名之类。</summary>
        public Dictionary<string, string> Placeholders { get; set; } = new();
    }

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "logcat", "commands.json");

    static CommandStore? _default;

    /// <summary>进程内共享实例（首次访问时从磁盘加载）。</summary>
    public static CommandStore Default => _default ??= Load();

    readonly StoreData _data;

    /// <summary>收藏或历史发生变化后触发，供界面刷新。</summary>
    public event EventHandler? Changed;

    CommandStore(StoreData data) => _data = data;

    // ── 加载 / 保存 ──

    static CommandStore Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var data = JsonSerializer.Deserialize<StoreData>(File.ReadAllText(FilePath), JsonOpts);
                if (data != null)
                {
                    data.Categories ??= new List<string>();
                    data.Favorites ??= new List<CommandEntry>();
                    data.History ??= new List<CommandRun>();
                    data.Placeholders ??= new Dictionary<string, string>();
                    if (data.Categories.Count == 0) Seed(data);
                    return new CommandStore(data);
                }
            }
        }
        catch
        {
            // 文件损坏时重建
        }

        var fresh = new StoreData();
        Seed(fresh);
        var store = new CommandStore(fresh);
        store.Save();
        return store;
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_data, JsonOpts));
        }
        catch
        {
            // 磁盘不可写时忽略，收藏仅在当前会话生效
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ── 内置命令库 ──

    record SeedCmd(CommandKind Kind, string Category, string Command, string Remark, bool Root = false);

    static readonly SeedCmd[] Seeds =
    [
        // ── 设备 shell（adb shell 支持的通用命令）──
        new(CommandKind.Shell, "shell", "getprop", "全部系统属性"),
        new(CommandKind.Shell, "shell", "getprop ro.build.version.release", "安卓版本"),
        new(CommandKind.Shell, "shell", "getprop ro.build.version.sdk", "API 级别"),
        new(CommandKind.Shell, "shell", "getprop ro.product.model", "机型"),
        new(CommandKind.Shell, "shell", "getprop ro.serialno", "序列号"),
        new(CommandKind.Shell, "shell", "uname -a", "内核版本"),
        new(CommandKind.Shell, "shell", "date", "设备时间"),
        new(CommandKind.Shell, "shell", "uptime", "运行时长与负载"),
        new(CommandKind.Shell, "shell", "free -m", "内存使用（MB）"),
        new(CommandKind.Shell, "shell", "df -h", "各分区剩余空间"),
        new(CommandKind.Shell, "shell", "top -b -n 1 -m 20", "CPU 占用快照（前 20 个进程）"),
        new(CommandKind.Shell, "shell", "ps -A", "全部进程"),
        new(CommandKind.Shell, "shell", "ps -A | grep {pkg}", "查应用的进程"),
        new(CommandKind.Shell, "shell", "pidof {pkg}", "查应用 PID"),
        new(CommandKind.Shell, "shell", "kill {pid}", "结束指定进程"),
        new(CommandKind.Shell, "shell", "ls -l /sdcard", "列目录"),
        new(CommandKind.Shell, "shell", "ls -l /data/local/tmp", "临时目录内容"),
        new(CommandKind.Shell, "shell", "find /sdcard -name \"*.log\" -type f", "按名字找文件"),
        new(CommandKind.Shell, "shell", "du -sh /sdcard/Download", "目录占用"),
        new(CommandKind.Shell, "shell", "ip addr", "网卡地址"),
        new(CommandKind.Shell, "shell", "ping -c 4 8.8.8.8", "测网络连通"),
        new(CommandKind.Shell, "shell", "settings get global airplane_mode_on", "飞行模式状态"),
        new(CommandKind.Shell, "shell", "settings get system screen_brightness", "屏幕亮度"),
        new(CommandKind.Shell, "shell", "settings put global window_animation_scale 1.0", "动画缩放（0 关闭 / 1 默认）"),
        new(CommandKind.Shell, "shell", "wm size", "分辨率"),
        new(CommandKind.Shell, "shell", "wm density", "屏幕密度"),
        new(CommandKind.Shell, "shell", "input keyevent 3", "HOME 键"),
        new(CommandKind.Shell, "shell", "input keyevent 4", "BACK 键"),
        new(CommandKind.Shell, "shell", "input keyevent 26", "电源键（锁屏/解锁）"),
        new(CommandKind.Shell, "shell", "input keyevent 82", "MENU 键"),
        new(CommandKind.Shell, "shell", "input swipe 300 1500 300 500 300", "上滑（x1 y1 x2 y2 时长ms）"),
        new(CommandKind.Shell, "shell", "input text hello", "输入文本"),
        new(CommandKind.Shell, "shell", "screencap -p /sdcard/screen.png", "截图存到设备"),
        new(CommandKind.Shell, "shell", "getenforce", "SELinux 状态"),
        new(CommandKind.Shell, "shell", "cmd statusbar expand-notifications", "下拉通知栏"),
        new(CommandKind.Shell, "shell", "ls -lZ /data/data", "受限目录与 SELinux 上下文", true),
        new(CommandKind.Shell, "shell", "dmesg | tail -80", "内核日志末尾", true),
        new(CommandKind.Shell, "shell", "mount | grep -E \"/system|/data\"", "分区挂载情况", true),

        // ── dumpsys ──
        new(CommandKind.Shell, "dumpsys", "dumpsys -l", "列出全部 dumpsys 服务"),
        new(CommandKind.Shell, "dumpsys", "dumpsys window", "窗口状态"),
        new(CommandKind.Shell, "dumpsys", "dumpsys window displays", "显示与分辨率细节"),
        new(CommandKind.Shell, "dumpsys", "dumpsys activity activities", "Activity 任务栈"),
        new(CommandKind.Shell, "dumpsys", "dumpsys activity top", "前台 Activity 与视图层级"),
        new(CommandKind.Shell, "dumpsys", "dumpsys activity recents", "最近任务"),
        new(CommandKind.Shell, "dumpsys", "dumpsys activity broadcasts", "广播队列"),
        new(CommandKind.Shell, "dumpsys", "dumpsys activity provider", "ContentProvider"),
        new(CommandKind.Shell, "dumpsys", "dumpsys package {pkg}", "应用安装信息（权限、组件、版本）"),
        new(CommandKind.Shell, "dumpsys", "dumpsys meminfo {pkg}", "应用内存详情"),
        new(CommandKind.Shell, "dumpsys", "dumpsys meminfo", "整机内存概况"),
        new(CommandKind.Shell, "dumpsys", "dumpsys cpuinfo", "整机 CPU 概况"),
        new(CommandKind.Shell, "dumpsys", "dumpsys gfxinfo {pkg}", "帧率与卡顿统计"),
        new(CommandKind.Shell, "dumpsys", "dumpsys dbinfo {pkg}", "数据库打开情况"),
        new(CommandKind.Shell, "dumpsys", "dumpsys power", "电源与 wakelock"),
        new(CommandKind.Shell, "dumpsys", "dumpsys battery", "电池状态"),
        new(CommandKind.Shell, "dumpsys", "dumpsys wifi", "WiFi 状态"),
        new(CommandKind.Shell, "dumpsys", "dumpsys connectivity", "网络连接"),
        new(CommandKind.Shell, "dumpsys", "dumpsys notification", "通知与监听器"),
        new(CommandKind.Shell, "dumpsys", "dumpsys input_method", "输入法"),
        new(CommandKind.Shell, "dumpsys", "dumpsys usagestats", "应用使用统计"),
        new(CommandKind.Shell, "dumpsys", "dumpsys jobscheduler", "JobScheduler 任务"),
        new(CommandKind.Shell, "dumpsys", "dumpsys dropbox --print", "系统崩溃 / ANR 记录", true),

        // ── 应用与包 ──
        new(CommandKind.Shell, "应用与包", "pm list packages -3", "第三方应用包名"),
        new(CommandKind.Shell, "应用与包", "pm list packages -s", "系统应用包名"),
        new(CommandKind.Shell, "应用与包", "pm list packages -f", "包名与 APK 路径"),
        new(CommandKind.Shell, "应用与包", "pm path {pkg}", "APK 路径"),
        new(CommandKind.Shell, "应用与包", "pm list permissions -d -g", "危险权限分组"),
        new(CommandKind.Shell, "应用与包", "pm grant {pkg} android.permission.CAMERA", "授予运行时权限"),
        new(CommandKind.Shell, "应用与包", "pm revoke {pkg} android.permission.CAMERA", "收回运行时权限"),
        new(CommandKind.Shell, "应用与包", "pm clear {pkg}", "清除应用数据"),
        new(CommandKind.Shell, "应用与包", "pm disable-user --user 0 {pkg}", "禁用应用"),
        new(CommandKind.Shell, "应用与包", "pm enable {pkg}", "启用应用"),
        new(CommandKind.Shell, "应用与包", "am start -n {pkg}/{activity}", "启动指定组件"),
        new(CommandKind.Shell, "应用与包", "am start -a android.settings.APPLICATION_DETAILS_SETTINGS -d package:{pkg}", "打开应用详情页"),
        new(CommandKind.Shell, "应用与包", "am force-stop {pkg}", "强制停止应用"),
        new(CommandKind.Shell, "应用与包", "am kill {pkg}", "杀后台进程"),
        new(CommandKind.Shell, "应用与包", "am broadcast -a android.intent.action.BOOT_COMPLETED", "发送广播"),
        new(CommandKind.Shell, "应用与包", "am start-activity -a android.intent.action.VIEW -d \"{url}\"", "打开链接"),
        new(CommandKind.Shell, "应用与包", "monkey -p {pkg} --ignore-crashes --ignore-timeouts -s 1 -v 1000", "随机压测 1000 事件"),

        // ── 日志与异常 ──
        new(CommandKind.Shell, "日志与异常", "logcat -d -t 200", "最近 200 行（-d 打印后退出）"),
        new(CommandKind.Shell, "日志与异常", "logcat -d *:E", "只看 Error"),
        new(CommandKind.Shell, "日志与异常", "logcat -d -s {tag}", "只看某个 tag"),
        new(CommandKind.Shell, "日志与异常", "logcat -d -b crash", "crash 缓冲区"),
        new(CommandKind.Shell, "日志与异常", "logcat -d -b events", "events 缓冲区"),
        new(CommandKind.Shell, "日志与异常", "logcat -d -b radio", "radio 缓冲区"),
        new(CommandKind.Shell, "日志与异常", "logcat -c", "清空日志缓冲区"),
        new(CommandKind.Shell, "日志与异常", "logcat -d -v threadtime *:W", "告警及以上，threadtime 格式"),
        new(CommandKind.Shell, "日志与异常", "ls -l /data/anr", "ANR trace 文件", true),
        new(CommandKind.Shell, "日志与异常", "ls -l /data/tombstones", "native crash tombstone", true),
        new(CommandKind.Shell, "日志与异常", "cat /proc/{pid}/status", "进程状态详情"),

        // ── 本机 adb 子命令 ──
        new(CommandKind.Adb, "adb", "version", "adb 版本"),
        new(CommandKind.Adb, "adb", "devices -l", "设备与传输方式"),
        new(CommandKind.Adb, "adb", "get-state", "设备状态"),
        new(CommandKind.Adb, "adb", "get-serialno", "设备序列号"),
        new(CommandKind.Adb, "adb", "wait-for-device", "阻塞直到设备上线（注意超时设置）"),
        new(CommandKind.Adb, "adb", "root", "以 root 重启 adbd"),
        new(CommandKind.Adb, "adb", "unroot", "以普通身份重启 adbd"),
        new(CommandKind.Adb, "adb", "usb", "切回 USB 调试"),
        new(CommandKind.Adb, "adb", "tcpip 5555", "切到网络调试"),
        new(CommandKind.Adb, "adb", "connect {ip}:5555", "无线连接设备"),
        new(CommandKind.Adb, "adb", "disconnect", "断开全部网络调试连接"),
        new(CommandKind.Adb, "adb", "reboot", "重启设备"),
        new(CommandKind.Adb, "adb", "reboot bootloader", "进入 fastboot"),
        new(CommandKind.Adb, "adb", "reboot recovery", "进入 recovery"),
        new(CommandKind.Adb, "adb", "remount", "重新挂载分区为可写（需 root）"),
        new(CommandKind.Adb, "adb", "disable-verity", "关闭 dm-verity（需 root）"),
        new(CommandKind.Adb, "adb", "install -r {apk}", "覆盖安装 APK"),
        new(CommandKind.Adb, "adb", "uninstall {pkg}", "卸载应用"),
        new(CommandKind.Adb, "adb", "push {file} /sdcard/", "推送文件到设备"),
        new(CommandKind.Adb, "adb", "pull /sdcard/{file}", "从设备取文件"),
        new(CommandKind.Adb, "adb", "forward tcp:9999 tcp:8080", "端口转发：本机 → 设备"),
        new(CommandKind.Adb, "adb", "reverse tcp:9999 tcp:8080", "端口反向转发：设备 → 本机"),
        new(CommandKind.Adb, "adb", "forward --list", "查看端口转发列表"),
    ];

    static void Seed(StoreData data)
    {
        foreach (var cat in new[] { "shell", "dumpsys", "应用与包", "日志与异常", "adb" })
            data.Categories.Add(cat);
        foreach (var s in Seeds)
        {
            data.Favorites.Add(new CommandEntry
            {
                Category = s.Category,
                Command = s.Command,
                Remark = s.Remark,
                Kind = s.Kind,
                Root = s.Root,
            });
        }
    }

    // ── 分类 ──

    /// <summary>分类名列表（界面顺序即此顺序）。</summary>
    public List<string> Categories => _data.Categories;

    public bool HasCategory(string category) =>
        Categories.Any(c => string.Equals(c, category, StringComparison.Ordinal));

    /// <summary>新建分类；已存在或名字非法时返回 false。</summary>
    public bool AddCategory(string category)
    {
        category = (category ?? "").Trim();
        if (category.Length == 0 || HasCategory(category)) return false;
        Categories.Add(category);
        return true;
    }

    /// <summary>重命名分类，同步改写其下所有收藏的分类名。</summary>
    public bool RenameCategory(string oldName, string newName)
    {
        oldName = (oldName ?? "").Trim();
        newName = (newName ?? "").Trim();
        if (oldName.Length == 0 || newName.Length == 0) return false;
        if (oldName == newName) return false;
        if (!HasCategory(oldName) || HasCategory(newName)) return false;

        int i = Categories.IndexOf(oldName);
        if (i >= 0) Categories[i] = newName;
        foreach (var f in _data.Favorites.Where(f => f.Category == oldName))
            f.Category = newName;
        return true;
    }

    /// <summary>删除分类；其下的收藏一并删除。</summary>
    public bool RemoveCategory(string name)
    {
        int i = Categories.IndexOf(name ?? "");
        if (i < 0) return false;
        Categories.RemoveAt(i);
        _data.Favorites.RemoveAll(f => f.Category == name);
        return true;
    }

    /// <summary>该分类下的收藏数量。</summary>
    public int CountIn(string name) => _data.Favorites.Count(f => f.Category == name);

    // ── 收藏 ──

    public List<CommandEntry> Favorites => _data.Favorites;

    /// <summary>取某分类下的收藏；category 为 null 表示全部分类。</summary>
    public List<CommandEntry> FavoritesIn(string? category) =>
        string.IsNullOrEmpty(category)
            ? _data.Favorites.ToList()
            : _data.Favorites.Where(f => f.Category == category).ToList();

    /// <summary>同一条命令只认「命令正文 + 通道」，root 差异视为同一条。</summary>
    public CommandEntry? FindFavorite(string command, CommandKind kind) =>
        _data.Favorites.FirstOrDefault(f =>
            string.Equals(f.Command.Trim(), (command ?? "").Trim(), StringComparison.Ordinal) && f.Kind == kind);

    public bool IsFavorite(string command, CommandKind kind) => FindFavorite(command, kind) != null;

    /// <summary>新增收藏。已存在同命令时返回既有条目之外的 false，由调用方提示。</summary>
    public CommandEntry? AddFavorite(string category, string command, string? remark,
                                    CommandKind kind, bool root)
    {
        command = (command ?? "").Trim();
        if (command.Length == 0) return null;
        if (FindFavorite(command, kind) != null) return null;

        category = (category ?? "").Trim();
        if (category.Length == 0) category = FallbackCategory;
        if (!HasCategory(category)) Categories.Add(category);

        var entry = new CommandEntry
        {
            Category = category,
            Command = command,
            Remark = NullIfBlank(remark),
            Kind = kind,
            Root = root,
        };
        _data.Favorites.Insert(0, entry);
        while (_data.Favorites.Count > MaxFavorites) _data.Favorites.RemoveAt(_data.Favorites.Count - 1);
        return entry;
    }

    public bool UpdateFavorite(CommandEntry entry, string category, string command,
                               string? remark, CommandKind kind, bool root)
    {
        if (entry == null) return false;
        command = (command ?? "").Trim();
        if (command.Length == 0) return false;

        var dup = FindFavorite(command, kind);
        if (dup != null && !ReferenceEquals(dup, entry)) return false;

        category = (category ?? "").Trim();
        if (category.Length == 0) category = FallbackCategory;
        if (!HasCategory(category)) Categories.Add(category);

        entry.Category = category;
        entry.Command = command;
        entry.Remark = NullIfBlank(remark);
        entry.Kind = kind;
        entry.Root = root;
        return true;
    }

    public bool RemoveFavorite(CommandEntry entry) =>
        entry != null && _data.Favorites.Remove(entry);

    public void ClearFavorites() => _data.Favorites.Clear();

    // ── 最近使用 ──

    public List<CommandRun> History => _data.History;

    /// <summary>记一次执行：已有则提到最前并累加次数。</summary>
    public CommandRun RecordRun(string command, CommandKind kind, bool root)
    {
        command = (command ?? "").Trim();
        var existing = _data.History.FirstOrDefault(h =>
            string.Equals(h.Command, command, StringComparison.Ordinal) && h.Kind == kind);
        if (existing != null)
        {
            existing.LastUsed = DateTime.Now;
            existing.UseCount++;
            existing.Root = root;
            _data.History.Remove(existing);
            _data.History.Insert(0, existing);
        }
        else
        {
            existing = new CommandRun { Command = command, Kind = kind, Root = root };
            _data.History.Insert(0, existing);
        }
        while (_data.History.Count > MaxHistory) _data.History.RemoveAt(_data.History.Count - 1);
        return existing;
    }

    public bool RemoveRun(CommandRun run) => run != null && _data.History.Remove(run);

    public void ClearHistory() => _data.History.Clear();

    // ── 占位符记忆 ──

    /// <summary>占位符 {name} 上次填的值，用于下次默认。</summary>
    public string PlaceholderValue(string name) =>
        _data.Placeholders.TryGetValue(name ?? "", out var v) ? v : "";

    public void RememberPlaceholder(string name, string value)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return;
        _data.Placeholders[name] = value ?? "";
    }

    static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
