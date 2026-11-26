namespace Next.App.Storage;

/// <summary>
/// 持久化根解析结果（design D2/D3）：设置、诊断日志与派生缓存共用同一个根。
/// 根不可用（仅会话内）时：设置不落盘、日志不写、缓存退回系统临时目录。
/// </summary>
public sealed class StorageRoots
{
    public const string SettingsFileName = "next-settings.json";
    public const string LogFileName = "next-startup.log";

    internal StorageRoots(string? root, string tempDirectory)
    {
        Root = root;
        TempDir = tempDirectory;
        if (root is null)
        {
            CacheDir = Path.Combine(tempDirectory, "sidecar");
            return;
        }

        SettingsPath = Path.Combine(root, SettingsFileName);
        LogPath = Path.Combine(root, LogFileName);
        CacheDir = Path.Combine(root, "cache", "core");
    }

    /// <summary>持久化根；仅会话内时为 null。</summary>
    public string? Root { get; }

    public string? SettingsPath { get; }

    public string? LogPath { get; }

    /// <summary>派生缓存目录：持久化根下；仅会话内时退到系统临时目录。</summary>
    public string CacheDir { get; }

    /// <summary>临时产物目录：始终为系统临时目录下的应用子目录。</summary>
    public string TempDir { get; }

    public bool SessionOnly => Root is null;
}
