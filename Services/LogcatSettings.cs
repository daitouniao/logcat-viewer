using System;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace logcat.Services;

/// <summary>
/// logcat.exe 侧用户级设置（显示选项、主窗几何、fileproxy 配置）。
/// 以 JSON 持久化到 exe 同目录 settings.json（%LOCALAPPDATA% 在部分安全软件管控环境下写入被拒，
/// 且异常被吞导致设置静默丢失，故与 StartupLog 一样落在程序目录）。
///
/// 与 <see cref="AndToolsSettings"/>（安卓侧 11 键）、<see cref="LanguageSettings"/>（语言 1 键）
/// 三份文件按归属拆分、各带 SchemaVersion：两应用并存保存互不覆盖（spec settings-storage）。
///
/// 首次访问 Default 时先跑 logcat 侧落盘分支 <see cref="EnsureSplit"/>：旧版单文件布局
/// 自动拆三份（语言抽出、安卓键复制进 andtools.json、本文件只留 logcat 侧键并置 SchemaVersion=2），
/// 幂等且不丢键；全新安装（无旧配置）不迁移。
///
/// 替代原先的 Properties.Settings（ApplicationSettingsBase / LocalFileSettingsProvider）：
/// 后者在 .NET 运行时下以程序集的 URL 身份哈希作为存储目录
/// （%LOCALAPPDATA%\logcat\logcat_Url_&lt;hash&gt;\&lt;version&gt;\user.config），
/// 未强命名、未安装时哈希随构建输出目录或运行方式（dotnet run vs 发布 exe）变化，
/// 导致保存与读取落到不同目录、设置跨运行丢失。JSON 存储路径固定，规避此问题。
/// </summary>
public sealed class LogcatSettings
{
    static readonly string DefaultPath = Path.Combine(
        AppContext.BaseDirectory, "settings.json");
    static readonly string LanguagePath = Path.Combine(
        AppContext.BaseDirectory, "language.json");
    static readonly string AndToolsPath = Path.Combine(
        AppContext.BaseDirectory, "andtools.json");

    static LogcatSettings? _default;

    public int SchemaVersion { get; set; } = SettingsIO.CurrentSchemaVersion;

    /// <summary>本实例的落盘路径。可注入，便于单元测试用临时文件（见 InternalsVisibleTo）。</summary>
    [JsonIgnore]
    public string StorePath { get; private set; } = DefaultPath;

    /// <summary>进程内共享实例（首次访问时先完成旧布局拆分迁移，再从磁盘加载）。</summary>
    public static LogcatSettings Default => _default ??= LoadFrom(DefaultPath, migrate: true);

    public LogcatSettings() { }

    /// <summary>internal：指定落盘路径（单元测试传临时文件，见 InternalsVisibleTo）。</summary>
    internal LogcatSettings(string path) => StorePath = path;

    // ── 显示选项 ──
    public bool AutoApply { get; set; } = true;
    public int FontPt { get; set; } = 10;

    // ── 窗口几何（多屏安全：RestoreBounds 统一保存正常态矩形）──
    public Point WindowLocation { get; set; }
    public Size WindowSize { get; set; }
    public int WindowState { get; set; }

    /// <summary>
    /// logcat 侧落盘分支（design D4）：settings.json 尚非新布局且 legacy 可读时，
    /// ①语言抽出写 language.json ②安卓侧 11 键复制进 andtools.json ③本文件重写为只含
    /// logcat 侧 10 键的 SchemaVersion=2 布局（语言键与安卓键由此清除）。
    /// 已是新布局的文件一律不动（幂等）；无 legacy（全新安装或损坏）不迁移。
    /// </summary>
    internal static void EnsureSplit(string path)
    {
        if (SettingsIO.IsSchemaV2(path)) return;
        var legacy = LegacySettings.Load(path);
        if (legacy == null) return;

        var dir = Path.GetDirectoryName(path);
        var languagePath = string.IsNullOrEmpty(dir)
            ? "language.json"
            : Path.Combine(dir, "language.json");
        var andToolsPath = string.IsNullOrEmpty(dir)
            ? "andtools.json"
            : Path.Combine(dir, "andtools.json");

        ConfigMigration.WriteLanguageFromLegacyIfMissing(legacy, languagePath);
        ConfigMigration.WriteAndToolsFromLegacyIfMissing(legacy, andToolsPath);

        var s = new LogcatSettings(path)
        {
            AutoApply = legacy.AutoApply,
            FontPt = legacy.FontPt,
            WindowLocation = legacy.WindowLocation,
            WindowSize = legacy.WindowSize,
            WindowState = legacy.WindowState,
            FileProxyMode = legacy.FileProxyMode,
            FileProxyPipeName = legacy.FileProxyPipeName,
            FileProxyLaunchFile = legacy.FileProxyLaunchFile,
            FileProxyLaunchArgs = legacy.FileProxyLaunchArgs,
            FileProxyLaunchTimeoutMs = legacy.FileProxyLaunchTimeoutMs,
            SchemaVersion = SettingsIO.CurrentSchemaVersion,
        };
        s.Save();
    }

    // ── 文件代理（fileproxy，默认全关闭）──
    // 模式原始串（"off" / "on" / "auto"），解析见 FileProxyModes.Parse；
    // 默认空串，空/off/乱码一律按 Off——解析失败不得导致代理被启用（安全侧默认，spec「模式配置与默认关闭」）
    public string FileProxyMode { get; set; } = "";
    /// <summary>命名管道名（不含 \\.\pipe\ 前缀）。</summary>
    public string FileProxyPipeName { get; set; } = "logcat-fileproxy";
    /// <summary>启动命令：可执行文件（解释器或 exe 完整路径）。空 = 不自动拉起。</summary>
    public string FileProxyLaunchFile { get; set; } = "";
    /// <summary>启动命令：参数模板，{pipe} 占位符替换为实际管道名。</summary>
    public string FileProxyLaunchArgs { get; set; } = "--pipe {pipe}";
    /// <summary>自动拉起后轮询等待管道就绪的超时（毫秒）。</summary>
    public int FileProxyLaunchTimeoutMs { get; set; } = 5000;

    /// <summary>internal：从指定路径加载（单元测试传临时文件，见 InternalsVisibleTo）。
    /// 默认不做迁移，保持纯读写的可测语义；迁移只随 Default 触发或显式调 <see cref="EnsureSplit"/>。</summary>
    internal static LogcatSettings LoadFrom(string path, bool migrate = false)
    {
        if (migrate) EnsureSplit(path);
        var s = SettingsIO.TryLoad<LogcatSettings>(path);
        if (s != null)
        {
            s.StorePath = path;    // 反序列化走无参构造，路径要重新绑到实际加载的文件
            return s;
        }
        return new LogcatSettings(path);
    }

    public void Save() => SaveTo(StorePath);

    /// <summary>internal：写回指定路径（单元测试传临时文件，见 InternalsVisibleTo）。
    /// 原子写（tmp+rename，见 <see cref="SettingsIO.AtomicWrite"/>）；磁盘不可写时吞异常仅留痕，
    /// 设置仅在当前会话生效——与既有存储类同一容错口径。</summary>
    internal void SaveTo(string path)
    {
        try
        {
            SettingsIO.AtomicWrite(path, JsonSerializer.Serialize(this, SettingsIO.JsonOpts));
        }
        catch (Exception ex)
        {
            StartupLog.Write($"[LogcatSettings] 保存失败 path={path} err={ex.Message}");
        }
    }
}
