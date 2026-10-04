using System;
using System.Drawing;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace logcat.Services;

/// <summary>
/// 用户级设置（窗口几何、显示选项、上次路径、安装/卸载窗口选项）。
/// 以 JSON 持久化到 exe 同目录 settings.json（%LOCALAPPDATA% 在部分安全软件管控环境下写入被拒，
/// 且异常被吞导致设置静默丢失，故与 StartupLog 一样落在程序目录）。
///
/// 替代原先的 Properties.Settings（ApplicationSettingsBase / LocalFileSettingsProvider）：
/// 后者在 .NET 运行时下以程序集的 URL 身份哈希作为存储目录
/// （%LOCALAPPDATA%\logcat\logcat_Url_&lt;hash&gt;\&lt;version&gt;\user.config），
/// 未强命名、未安装时哈希随构建输出目录或运行方式（dotnet run vs 发布 exe）变化，
/// 导致保存与读取落到不同目录、设置跨运行丢失。JSON 存储路径固定，规避此问题。
/// </summary>
public sealed class AppSettings
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static readonly string DefaultPath = Path.Combine(
        AppContext.BaseDirectory, "settings.json");

    static AppSettings? _default;

    /// <summary>本实例的落盘路径。可注入，便于单元测试用临时文件（见 InternalsVisibleTo）。</summary>
    [JsonIgnore]
    public string StorePath { get; private set; } = DefaultPath;

    /// <summary>进程内共享实例（首次访问时从磁盘加载）。</summary>
    public static AppSettings Default => _default ??= LoadFrom(DefaultPath);

    public AppSettings() { }

    /// <summary>internal：指定落盘路径（单元测试传临时文件，见 InternalsVisibleTo）。</summary>
    internal AppSettings(string path) => StorePath = path;

    // ── 显示选项 ──
    public bool Join { get; set; } = true;
    public bool SingleLineExport { get; set; }
    public bool AutoApply { get; set; } = true;
    public string NewlineVis { get; set; } = "↵";
    public int FontPt { get; set; } = 10;

    // ── 窗口几何（多屏安全：RestoreBounds 统一保存正常态矩形）──
    public Point WindowLocation { get; set; }
    public Size WindowSize { get; set; }
    public int WindowState { get; set; }

    // ── 上次路径 ──
    public string LastRemotePath { get; set; } = "/sdcard";
    public string LastLocalPath { get; set; } = "";
    public string LastRunAsRelayDir { get; set; } = "/sdcard/Download";

    // ── 文件浏览列宽（设备端 / 本机面板各一组，元素为各列宽度，按列序对应）──
    public List<int> DevicePaneColumnWidths { get; set; } = new();
    public List<int> LocalPaneColumnWidths { get; set; } = new();

    // ── 安装/卸载窗口选项 ──
    public bool ApkInstallViaPm { get; set; }
    public string ApkInstallFlags { get; set; } = "-r";
    public string ApkTmpDir { get; set; } = "/data/local/tmp";
    public bool ApkUninstallViaPm { get; set; }
    public bool ApkKeepData { get; set; }
    public bool ApkUseRoot { get; set; }

    /// <summary>internal：从指定路径加载（单元测试传临时文件，见 InternalsVisibleTo）。</summary>
    internal static AppSettings LoadFrom(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOpts);
                if (s != null)
                {
                    s.StorePath = path;    // 反序列化走无参构造，路径要重新绑到实际加载的文件
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            // 文件损坏时回退默认值，但要留痕便于排查
            StartupLog.Write($"[AppSettings] 加载失败 path={path} err={ex.Message}");
        }
        return new AppSettings(path);
    }

    public void Save() => SaveTo(StorePath);

    /// <summary>internal：写回指定路径（单元测试传临时文件，见 InternalsVisibleTo）。</summary>
    internal void SaveTo(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch (Exception ex)
        {
            // 磁盘不可写时忽略，设置仅在当前会话生效，但要留痕便于排查
            StartupLog.Write($"[AppSettings] 保存失败 path={path} err={ex.Message}");
        }
    }
}
