using System.IO;
using logcat.Services;

namespace logcat.AndTools;

/// <summary>
/// andtools 侧落盘分支（design D3/D4）：工具箱启动时把旧版单文件 settings.json 拆分到
/// andtools.json（安卓侧 11 键）与 language.json（语言键）。幂等：目标已是新布局不动；
/// 全新安装（无旧配置）不迁移。settings.json 本体归 logcat 分支清理（复制不移动）。
/// </summary>
public static class AndToolsMigration
{
    public static void EnsureSplit(string legacyPath, string andToolsPath, string languagePath)
    {
        if (SettingsIO.IsSchemaV2(andToolsPath)) return;
        var legacy = LegacySettings.Load(legacyPath);
        if (legacy == null) return;

        ConfigMigration.WriteLanguageFromLegacyIfMissing(legacy, languagePath);
        ConfigMigration.WriteAndToolsFromLegacyIfMissing(legacy, andToolsPath);
    }
}
