using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace logcat.Services;

/// <summary>
/// 界面语言设置，持久化到 exe 同目录 <c>language.json</c>——两 exe 共读共写的唯一键面（design D5）。
/// 任意一侧 <see cref="Loc.SetLang"/> 写入后，另一侧重启生效（不做跨进程实时通知，spec ui-language）。
/// Default 单例不做迁移——两个 exe 的落盘分支都会在首次启动时把语言从 legacy 抽出（幂等）。
/// </summary>
public sealed class LanguageSettings
{
    static readonly string DefaultPath = Path.Combine(
        AppContext.BaseDirectory, "language.json");

    static LanguageSettings? _default;

    public int SchemaVersion { get; set; } = SettingsIO.CurrentSchemaVersion;

    /// <summary>本实例的落盘路径。可注入，便于单元测试用临时文件（见 InternalsVisibleTo）。</summary>
    [JsonIgnore]
    public string StorePath { get; private set; } = DefaultPath;

    /// <summary>进程内共享实例（首次访问时从磁盘加载）。</summary>
    public static LanguageSettings Default => _default ??= LoadFrom(DefaultPath);

    public LanguageSettings() { }

    /// <summary>internal：指定落盘路径（单元测试与迁移原语传临时文件，见 InternalsVisibleTo）。</summary>
    internal LanguageSettings(string path) => StorePath = path;

    internal static LanguageSettings LoadFrom(string path)
    {
        var s = SettingsIO.TryLoad<LanguageSettings>(path);
        if (s != null)
        {
            s.StorePath = path;    // 反序列化走无参构造，路径要重新绑到实际加载的文件
            return s;
        }
        return new LanguageSettings(path);
    }

    /// <summary>界面语言（"zh-CN" / "en"）。留空表示按系统区域推断（见 <c>Loc.DetectLang</c>）。</summary>
    public string Language { get; set; } = "";

    public void Save()
    {
        try
        {
            SettingsIO.AtomicWrite(StorePath, JsonSerializer.Serialize(this, SettingsIO.JsonOpts));
        }
        catch (Exception ex)
        {
            StartupLog.Write($"[LanguageSettings] 保存失败 path={StorePath} err={ex.Message}");
        }
    }
}
