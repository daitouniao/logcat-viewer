using System;
using System.Drawing;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace logcat.Services;

/// <summary>
/// 用户级设置（窗口几何、显示选项、上次路径、安装/卸载窗口选项）。
/// 以 JSON 持久化到 %LOCALAPPDATA%\logcat\settings.json。
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

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "logcat", "settings.json");

    static AppSettings? _default;

    /// <summary>进程内共享实例（首次访问时从磁盘加载）。</summary>
    public static AppSettings Default => _default ??= Load();

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

    // ── 安装/卸载窗口选项 ──
    public bool ApkInstallViaPm { get; set; }
    public string ApkInstallFlags { get; set; } = "-r";
    public string ApkTmpDir { get; set; } = "/data/local/tmp";
    public bool ApkUninstallViaPm { get; set; }
    public bool ApkKeepData { get; set; }
    public bool ApkUseRoot { get; set; }

    static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOpts);
                if (s != null) return s;
            }
        }
        catch
        {
            // 文件损坏时回退默认值
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch
        {
            // 磁盘不可写时忽略，设置仅在当前会话生效
        }
    }
}
