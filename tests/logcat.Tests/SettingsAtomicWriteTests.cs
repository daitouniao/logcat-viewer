using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// 配置原子写原语（design D4）：所有配置落盘 tmp + File.Move，中途崩溃不产生半写的新布局文件；
/// 无版本号/不完整文件按未迁移分支重走，不静默丢键。
/// </summary>
public class SettingsAtomicWriteTests
{
    [Fact]
    public void 原子写后目标完整且无tmp残留()
    {
        using var f = new TempStoreFile(null);

        SettingsIO.AtomicWrite(f.Path, "{\"SchemaVersion\":2,\"FontPt\":12}");

        Assert.True(f.Exists);
        Assert.Equal("{\"SchemaVersion\":2,\"FontPt\":12}", f.Read());
        Assert.False(File.Exists(f.Path + ".tmp"));
    }

    [Fact]
    public void 原子写覆盖旧内容且不留半写()
    {
        using var f = new TempStoreFile("{\"SchemaVersion\":2,\"FontPt\":10}");

        SettingsIO.AtomicWrite(f.Path, "{\"SchemaVersion\":2,\"FontPt\":12}");

        Assert.Equal("{\"SchemaVersion\":2,\"FontPt\":12}", f.Read());
        Assert.False(File.Exists(f.Path + ".tmp"));
    }

    [Fact]
    public void 半写损坏文件按无legacy处理_不误迁移()
    {
        // 模拟旧版非原子写中途崩溃留下的半写 settings.json：JSON 截断、无版本号。
        // 语义：读不出新布局（重走迁移判定成立）；内容损坏无法恢复 → 不迁移、不产生派生文件、按默认值兜底。
        using var dir = new TempDir();
        var settingsPath = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(settingsPath, "{\"SchemaVersion\":2,\"FontPt\":1");

        Assert.False(SettingsIO.IsSchemaV2(settingsPath));
        Assert.Null(LegacySettings.Load(settingsPath));   // 损坏不可读 → 迁移分支按「无 legacy」处理

        LogcatSettings.EnsureSplit(settingsPath);
        Assert.False(File.Exists(Path.Combine(dir.Path, "language.json")));
        Assert.False(File.Exists(Path.Combine(dir.Path, "andtools.json")));
    }

    [Fact]
    public void 旧布局无版本号文件_重走迁移幂等()
    {
        using var dir = new TempDir();
        var settingsPath = Path.Combine(dir.Path, "settings.json");
        var languagePath = Path.Combine(dir.Path, "language.json");
        var andToolsPath = Path.Combine(dir.Path, "andtools.json");
        // 旧版完整 settings.json（无 SchemaVersion）
        File.WriteAllText(settingsPath, "{\"FontPt\":12,\"Language\":\"en\",\"ApkUseRoot\":true}");

        LogcatSettings.EnsureSplit(settingsPath);
        var firstSettings = File.ReadAllText(settingsPath);
        var firstLanguage = File.ReadAllText(languagePath);

        Assert.True(SettingsIO.IsSchemaV2(settingsPath));
        Assert.True(SettingsIO.IsSchemaV2(languagePath));
        Assert.True(SettingsIO.IsSchemaV2(andToolsPath));

        // 再走一遍（模拟重复启动）：不重搬、不改写既有新布局内容
        LogcatSettings.EnsureSplit(settingsPath);
        Assert.Equal(firstSettings, File.ReadAllText(settingsPath));
        Assert.Equal(firstLanguage, File.ReadAllText(languagePath));

        Assert.Equal(12, LogcatSettings.LoadFrom(settingsPath).FontPt);
        Assert.Equal("en", LanguageSettings.LoadFrom(languagePath).Language);
        Assert.True(AndToolsSettings.LoadFrom(andToolsPath).ApkUseRoot);
    }
}
