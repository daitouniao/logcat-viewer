using System.Text.Json;
using logcat.AndTools;
using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// 配置拆分迁移四场景（tasks 1.6 / spec settings-storage）：旧布局 logcat 先跑拆三份、
/// 工具箱先跑且 logcat 从未启动语言不丢、新布局重复启动幂等、Language 抽出后旧键清除。
/// 全部走临时目录注入路径，不触碰真实配置文件。
/// </summary>
public class ConfigMigrationTests
{
    static string LegacyJson() => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        // ── logcat 侧 10 键 ──
        ["AutoApply"] = false,
        ["FontPt"] = 12,
        ["WindowLocation"] = new Dictionary<string, int> { ["X"] = 11, ["Y"] = 22 },
        ["WindowSize"] = new Dictionary<string, int> { ["Width"] = 1000, ["Height"] = 700 },
        ["WindowState"] = 2,
        ["FileProxyMode"] = "on",
        ["FileProxyPipeName"] = "pipe-x",
        ["FileProxyLaunchFile"] = "python.exe",
        ["FileProxyLaunchArgs"] = "--pipe {pipe} --x",
        ["FileProxyLaunchTimeoutMs"] = 9000,
        // ── 安卓侧 11 键 ──
        ["LastLocalPath"] = "D:/dl",
        ["LastRunAsRelayDir"] = "/sdcard/x",
        ["DeviceLastDirs"] = new object[] { new Dictionary<string, string> { ["Serial"] = "emu-1", ["Path"] = "/sdcard/DCIM" } },
        ["DevicePaneColumnWidths"] = new[] { 60, 240 },
        ["LocalPaneColumnWidths"] = new[] { 80, 260 },
        ["ApkInstallViaPm"] = true,
        ["ApkInstallFlags"] = "-r -d",
        ["ApkTmpDir"] = "/data/local/x",
        ["ApkUninstallViaPm"] = true,
        ["ApkKeepData"] = true,
        ["ApkUseRoot"] = true,
        // ── 共享键 1 个 ──
        ["Language"] = "zh-CN",
    });

    /// <summary>在临时目录写一份旧布局 settings.json，返回三份文件的完整路径。</summary>
    static (string Settings, string AndTools, string Language) NewLegacyLayout(TempDir dir)
    {
        var settings = System.IO.Path.Combine(dir.Path, "settings.json");
        var andTools = System.IO.Path.Combine(dir.Path, "andtools.json");
        var language = System.IO.Path.Combine(dir.Path, "language.json");
        File.WriteAllText(settings, LegacyJson());
        return (settings, andTools, language);
    }

    static Dictionary<string, JsonElement> Read(string path)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path))!;

    // ── 场景①：旧布局 logcat 先跑 → 拆三份 ──

    [Fact]
    public void 旧布局logcat先跑_拆成三份且键值完整保留()
    {
        using var dir = new TempDir();
        var (settings, andTools, language) = NewLegacyLayout(dir);

        LogcatSettings.EnsureSplit(settings);

        // settings.json：重写为 logcat 侧 10 键 + SchemaVersion=2
        var s = Read(settings);
        Assert.Equal(2, s["SchemaVersion"].GetInt32());
        Assert.False(s["AutoApply"].GetBoolean());
        Assert.Equal(12, s["FontPt"].GetInt32());
        Assert.Equal(2, s["WindowState"].GetInt32());
        Assert.Equal("on", s["FileProxyMode"].GetString());

        // andtools.json：安卓侧 11 键完整复制
        var a = Read(andTools);
        Assert.Equal(2, a["SchemaVersion"].GetInt32());
        Assert.Equal("D:/dl", a["LastLocalPath"].GetString());
        Assert.Equal("/sdcard/x", a["LastRunAsRelayDir"].GetString());
        Assert.Equal("-r -d", a["ApkInstallFlags"].GetString());
        Assert.True(a["ApkUseRoot"].GetBoolean());
        Assert.Equal(60, a["DevicePaneColumnWidths"][0].GetInt32());

        // language.json：语言键抽出
        var l = Read(language);
        Assert.Equal(2, l["SchemaVersion"].GetInt32());
        Assert.Equal("zh-CN", l["Language"].GetString());
    }

    // ── 场景②：工具箱先跑、logcat 从未启动 → 语言不丢 ──

    [Fact]
    public void 工具箱先跑_语言与安卓键不丢_且不动settingsjson()
    {
        using var dir = new TempDir();
        var (settings, andTools, language) = NewLegacyLayout(dir);
        var settingsBefore = File.ReadAllText(settings);

        AndToolsMigration.EnsureSplit(settings, andTools, language);

        // language.json：legacy 语言完整带出（logcat 不启动也不丢）
        var l = Read(language);
        Assert.Equal("zh-CN", l["Language"].GetString());

        // andtools.json：11 键完整带出
        var a = Read(andTools);
        Assert.Equal("D:/dl", a["LastLocalPath"].GetString());
        Assert.True(a["ApkKeepData"].GetBoolean());

        // settings.json 归 logcat 分支清理（复制不移动）：原样保留，仍非新布局
        Assert.Equal(settingsBefore, File.ReadAllText(settings));
        Assert.False(SettingsIO.IsSchemaV2(settings));
    }

    // ── 场景③：新布局重复启动幂等 ──

    [Fact]
    public void 新布局重复启动_三份文件一律不动()
    {
        using var dir = new TempDir();
        var (settings, andTools, language) = NewLegacyLayout(dir);
        LogcatSettings.EnsureSplit(settings);
        AndToolsMigration.EnsureSplit(settings, andTools, language);

        var s1 = File.ReadAllText(settings);
        var a1 = File.ReadAllText(andTools);
        var l1 = File.ReadAllText(language);

        LogcatSettings.EnsureSplit(settings);
        AndToolsMigration.EnsureSplit(settings, andTools, language);

        Assert.Equal(s1, File.ReadAllText(settings));
        Assert.Equal(a1, File.ReadAllText(andTools));
        Assert.Equal(l1, File.ReadAllText(language));
    }

    // ── 场景④：Language 抽出后旧键清除 ──

    [Fact]
    public void Language抽出后_settingsjson旧键清除()
    {
        using var dir = new TempDir();
        var (settings, andTools, language) = NewLegacyLayout(dir);

        LogcatSettings.EnsureSplit(settings);

        var keys = Read(settings).Keys;
        Assert.DoesNotContain("Language", keys);          // 语言键已搬走
        Assert.DoesNotContain("LastLocalPath", keys);     // 安卓键清除（复制不移动后的重写）
        Assert.DoesNotContain("ApkUseRoot", keys);
        Assert.DoesNotContain("DeviceLastDirs", keys);
        Assert.Equal(11, keys.Count);                     // 10 键 + SchemaVersion
    }
}
