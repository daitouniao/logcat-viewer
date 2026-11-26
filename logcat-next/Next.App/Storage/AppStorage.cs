namespace Next.App.Storage;

/// <summary>应用级存储入口：解析结果与设置服务在 Program 启动早期装配一次。</summary>
public static class AppStorage
{
    static StorageRoots? _roots;
    static NextSettingsService? _settings;

    public static StorageRoots Roots =>
        _roots ?? throw new InvalidOperationException("存储根尚未初始化");

    public static NextSettingsService Settings =>
        _settings ?? throw new InvalidOperationException("设置服务尚未初始化");

    public static void Initialize(StorageRoots roots, NextSettingsService settings)
    {
        _roots = roots;
        _settings = settings;
    }
}
