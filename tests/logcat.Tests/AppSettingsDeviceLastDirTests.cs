using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// 按设备记忆的文件浏览目录（change device-scoped-file-browser-path 任务 1.1–1.4）。
///
/// <para>
/// 覆盖：同设备重复记录只留一条且置首、超上限淘汰最旧、查不到返回空、
/// 旧配置缺字段/显式 null 得空集合、持久化往返、旧配置里的
/// <c>LastRemotePath</c> 键平滑消失。
/// </para>
/// </summary>
public class AppSettingsDeviceLastDirTests
{
    [Fact]
    public void 同设备重复记录只留一条且置首()
    {
        var s = new AppSettings();

        s.RecordDeviceLastDir("dev-a", "/data/local/tmp");
        s.RecordDeviceLastDir("dev-b", "/sdcard/Download");
        s.RecordDeviceLastDir("dev-a", "/data/local/tmp2");

        Assert.Equal(2, s.DeviceLastDirs.Count);
        // 同设备的旧条目被移除后置首，不留两条
        Assert.Equal("dev-a", s.DeviceLastDirs[0].Serial);
        Assert.Equal("/data/local/tmp2", s.DeviceLastDirs[0].Path);
        Assert.Equal("dev-b", s.DeviceLastDirs[1].Serial);
    }

    [Fact]
    public void 查不到设备时返回空()
    {
        var s = new AppSettings();
        s.RecordDeviceLastDir("dev-a", "/data/local/tmp");

        Assert.Equal("/data/local/tmp", s.GetDeviceLastDir("dev-a"));
        Assert.Equal("", s.GetDeviceLastDir("dev-not-exist"));
        Assert.Equal("", s.GetDeviceLastDir(""));
    }

    [Fact]
    public void 超过上限时淘汰最久未用者()
    {
        var s = new AppSettings();

        // 依次记录 20 台设备，序列号即记录顺序（每条都置首，故末尾是最早记录的）
        for (int i = 0; i < 20; i++)
            s.RecordDeviceLastDir("dev-" + i, "/p" + i);

        Assert.Equal(AppSettings.MaxDeviceLastDirs, s.DeviceLastDirs.Count);
        // 最近记录的在最前，最早的 4 条（dev-0…dev-3）已被淘汰
        Assert.Equal("dev-19", s.DeviceLastDirs[0].Serial);
        Assert.Equal("dev-4", s.DeviceLastDirs[^1].Serial);
        Assert.Equal("", s.GetDeviceLastDir("dev-0"));
        Assert.Equal("", s.GetDeviceLastDir("dev-3"));
        Assert.Equal("/p19", s.GetDeviceLastDir("dev-19"));
    }

    [Fact]
    public void 旧配置缺少该字段时得空集合()
    {
        using var file = new TempStoreFile("{\"Join\":false,\"FontPt\":12}");
        var s = AppSettings.LoadFrom(file.Path);

        Assert.Empty(s.DeviceLastDirs);
        Assert.Equal("", s.GetDeviceLastDir("dev-a"));
        // 后续记录操作正常
        s.RecordDeviceLastDir("dev-a", "/data/local/tmp");
        Assert.Equal("/data/local/tmp", s.GetDeviceLastDir("dev-a"));
    }

    [Fact]
    public void 旧配置该字段显式为null时得空集合()
    {
        using var file = new TempStoreFile("{\"DeviceLastDirs\":null}");
        var s = AppSettings.LoadFrom(file.Path);

        Assert.Empty(s.DeviceLastDirs);
        s.RecordDeviceLastDir("dev-a", "/data/local/tmp");
        Assert.Equal("/data/local/tmp", s.GetDeviceLastDir("dev-a"));
    }

    [Fact]
    public void 落盘再读回后该设备仍在首位()
    {
        using var file = new TempStoreFile(null);
        var s = AppSettings.LoadFrom(file.Path);

        s.RecordDeviceLastDir("dev-a", "/data/local/tmp");
        s.RecordDeviceLastDir("dev-b", "/sdcard/Download");
        s.SaveTo(file.Path);

        var again = AppSettings.LoadFrom(file.Path);
        Assert.Equal(2, again.DeviceLastDirs.Count);
        Assert.Equal("dev-b", again.DeviceLastDirs[0].Serial);
        Assert.Equal("/sdcard/Download", again.DeviceLastDirs[0].Path);
        Assert.Equal("/data/local/tmp", again.GetDeviceLastDir("dev-a"));
    }

    [Fact]
    public void 旧配置里的LastRemotePath键被忽略且保存后消失()
    {
        // 升级前的配置：全局单值字段仍在
        using var file = new TempStoreFile("{\"LastRemotePath\":\"/some/path\"}");
        var s = AppSettings.LoadFrom(file.Path);

        // 反序列化不抛异常，且旧键不落到任何字段上
        s.RecordDeviceLastDir("dev-a", "/data/local/tmp");
        s.SaveTo(file.Path);

        var json = file.Read();
        Assert.DoesNotContain("LastRemotePath", json);
    }
}
