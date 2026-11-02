using System.Text.Json;
using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// 只读 legacy DTO 键面完整性（design D3/D4）：旧版单文件 settings.json 的全部 22 键
/// = logcat 侧 10 键 + 安卓侧 11 键 + 语言 1 键——任何一键读不出，迁移就会静默丢设置。
/// </summary>
public class LegacySettingsDtoTests
{
    /// <summary>构造键面齐全的旧版 settings.json 内容（键名与 0.1.x AppSettings 序列化一致）。</summary>
    static string FullLegacyJson() => JsonSerializer.Serialize(new Dictionary<string, object?>
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

    [Fact]
    public void logcat侧10键完整读出()
    {
        var s = LegacySettings.Load(NewFile());

        Assert.False(s!.AutoApply);
        Assert.Equal(12, s.FontPt);
        Assert.Equal(11, s.WindowLocation.X);
        Assert.Equal(22, s.WindowLocation.Y);
        Assert.Equal(1000, s.WindowSize.Width);
        Assert.Equal(700, s.WindowSize.Height);
        Assert.Equal(2, s.WindowState);
        Assert.Equal("on", s.FileProxyMode);
        Assert.Equal("pipe-x", s.FileProxyPipeName);
        Assert.Equal("python.exe", s.FileProxyLaunchFile);
        Assert.Equal("--pipe {pipe} --x", s.FileProxyLaunchArgs);
        Assert.Equal(9000, s.FileProxyLaunchTimeoutMs);
    }

    [Fact]
    public void 安卓侧11键完整读出()
    {
        var s = LegacySettings.Load(NewFile());

        Assert.Equal("D:/dl", s!.LastLocalPath);
        Assert.Equal("/sdcard/x", s.LastRunAsRelayDir);
        var dir = Assert.Single(s.DeviceLastDirs);
        Assert.Equal("emu-1", dir.Serial);
        Assert.Equal("/sdcard/DCIM", dir.Path);
        Assert.Equal(new[] { 60, 240 }, s.DevicePaneColumnWidths);
        Assert.Equal(new[] { 80, 260 }, s.LocalPaneColumnWidths);
        Assert.True(s.ApkInstallViaPm);
        Assert.Equal("-r -d", s.ApkInstallFlags);
        Assert.Equal("/data/local/x", s.ApkTmpDir);
        Assert.True(s.ApkUninstallViaPm);
        Assert.True(s.ApkKeepData);
        Assert.True(s.ApkUseRoot);
    }

    [Fact]
    public void 共享语言键完整读出()
    {
        Assert.Equal("zh-CN", LegacySettings.Load(NewFile())!.Language);
    }

    [Fact]
    public void 文件缺失或损坏返回null()
    {
        using var missing = new TempStoreFile(null);
        using var broken = new TempStoreFile("{ 截断");

        Assert.Null(LegacySettings.Load(missing.Path));
        Assert.Null(LegacySettings.Load(broken.Path));
    }

    [Fact]
    public void DeviceLastDirs的JSONnull归一为空列表()
    {
        using var f = new TempStoreFile("{\"DeviceLastDirs\":null}");
        Assert.Empty(LegacySettings.Load(f.Path)!.DeviceLastDirs);
    }

    static string NewFile()
    {
        var f = new TempStoreFile(FullLegacyJson());
        return f.Path;   // 测试进程生命周期内由 TempRoot 统一清理，无需逐用例释放
    }
}
