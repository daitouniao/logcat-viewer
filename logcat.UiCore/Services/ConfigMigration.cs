namespace logcat.Services;

/// <summary>
/// 配置拆分迁移原语（design D3/D4）：共享库只放「从 legacy 派生 andtools.json / language.json」的
/// 可复用写原语；各 exe 的落盘分支（何时调用、settings.json 本体如何重写）在自工程——
/// logcat 分支见 exe 侧 <c>LogcatSettings.EnsureSplit</c>，andtools 分支见 logcat.AndTools。
/// </summary>
public static class ConfigMigration
{
    /// <summary>
    /// 从 legacy 复制安卓侧 11 键写出 <c>andtools.json</c>（复制不移动：legacy 原样保留，
    /// 由 logcat 分支负责清理）。目标已是新布局时不动——它可能比 legacy 新（权威不回填，幂等）。
    /// </summary>
    /// <returns>实际写出了文件返回 true；目标已是新布局返回 false。</returns>
    public static bool WriteAndToolsFromLegacyIfMissing(LegacySettings legacy, string path)
    {
        if (SettingsIO.IsSchemaV2(path)) return false;
        var s = new AndToolsSettings(path)
        {
            LastLocalPath = legacy.LastLocalPath,
            LastRunAsRelayDir = legacy.LastRunAsRelayDir,
            DeviceLastDirs = legacy.DeviceLastDirs,
            DevicePaneColumnWidths = legacy.DevicePaneColumnWidths,
            LocalPaneColumnWidths = legacy.LocalPaneColumnWidths,
            ApkInstallViaPm = legacy.ApkInstallViaPm,
            ApkInstallFlags = legacy.ApkInstallFlags,
            ApkTmpDir = legacy.ApkTmpDir,
            ApkUninstallViaPm = legacy.ApkUninstallViaPm,
            ApkKeepData = legacy.ApkKeepData,
            ApkUseRoot = legacy.ApkUseRoot,
            SchemaVersion = SettingsIO.CurrentSchemaVersion,
        };
        s.Save();
        return true;
    }

    /// <summary>
    /// 从 legacy 抽出语言键写出 <c>language.json</c>。目标已是新布局时不动——
    /// 可能已有一侧改过语言，legacy 里的值是旧的（幂等，两 exe 先后启动语言不丢）。
    /// </summary>
    /// <returns>实际写出了文件返回 true；目标已是新布局返回 false。</returns>
    public static bool WriteLanguageFromLegacyIfMissing(LegacySettings legacy, string path)
    {
        if (SettingsIO.IsSchemaV2(path)) return false;
        var s = new LanguageSettings(path)
        {
            Language = legacy.Language,
            SchemaVersion = SettingsIO.CurrentSchemaVersion,
        };
        s.Save();
        return true;
    }
}
