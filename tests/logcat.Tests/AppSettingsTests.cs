using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// 文件代理设置的持久化与默认值（spec「模式配置与默认关闭」），锚死 DEC-fp-3 三条：
/// UI 默认关闭、反序列化缺字段得 off、解析失败一律回退 off（解析语义本身见 <see cref="FileProxyModeTests"/>）。
/// </summary>
public class AppSettingsTests
{
    [Fact]
    public void 默认值_模式关闭且各字段为约定默认()
    {
        var s = new AppSettings();
        Assert.Equal(FileProxyMode.Off, FileProxyModes.Parse(s.FileProxyMode));
        Assert.Equal("logcat-fileproxy", s.FileProxyPipeName);
        Assert.Equal("", s.FileProxyLaunchFile);
        Assert.Equal("--pipe {pipe}", s.FileProxyLaunchArgs);
        Assert.Equal(5000, s.FileProxyLaunchTimeoutMs);
    }

    [Fact]
    public void 旧配置缺文件代理字段时反序列化得默认值()
    {
        // 模拟 V0.1.4 及更早的 settings.json：只有既有字段，无任何 FileProxy* 键
        using var f = new TempStoreFile("{\"Join\":false,\"FontPt\":12}");
        var s = AppSettings.LoadFrom(f.Path);
        Assert.Equal(FileProxyMode.Off, FileProxyModes.Parse(s.FileProxyMode));
        Assert.Equal("logcat-fileproxy", s.FileProxyPipeName);
        Assert.Equal("", s.FileProxyLaunchFile);
        Assert.Equal("--pipe {pipe}", s.FileProxyLaunchArgs);
        Assert.Equal(5000, s.FileProxyLaunchTimeoutMs);
    }

    [Fact]
    public void 模式落盘串与解析_round_trip()
    {
        foreach (var m in new[] { FileProxyMode.Off, FileProxyMode.On, FileProxyMode.Auto })
            Assert.Equal(m, FileProxyModes.Parse(FileProxyModes.ToRaw(m)));
    }

    [Fact]
    public void 保存后能读回文件代理字段()
    {
        using var f = new TempStoreFile(null);
        var s = AppSettings.LoadFrom(f.Path);
        s.FileProxyMode = FileProxyModes.ToRaw(FileProxyMode.Auto);
        s.FileProxyPipeName = "my proxy";
        s.FileProxyLaunchFile = @"<解释器路径>\python.exe";
        s.FileProxyLaunchArgs = "<脚本路径> --pipe {pipe}";
        s.FileProxyLaunchTimeoutMs = 8000;
        s.SaveTo(f.Path);

        var again = AppSettings.LoadFrom(f.Path);
        Assert.Equal(FileProxyMode.Auto, FileProxyModes.Parse(again.FileProxyMode));
        Assert.Equal("my proxy", again.FileProxyPipeName);
        Assert.Equal(@"<解释器路径>\python.exe", again.FileProxyLaunchFile);
        Assert.Equal("<脚本路径> --pipe {pipe}", again.FileProxyLaunchArgs);
        Assert.Equal(8000, again.FileProxyLaunchTimeoutMs);
    }
}
