using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// 文件代理设置的持久化与解析（spec「模式配置与默认关闭」）。
/// 锚死 DEC-fp-3 三条：UI 默认关闭、反序列化缺字段得 off、解析失败一律回退 off。
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

    [Theory]
    [InlineData("", FileProxyMode.Off)]        // 空串（默认值形态）
    [InlineData("off", FileProxyMode.Off)]
    [InlineData("On ", FileProxyMode.Off)]     //  spec 场景逐字点名的「看着像其实不是」
    [InlineData("ON", FileProxyMode.Off)]
    [InlineData(" on", FileProxyMode.Off)]
    [InlineData("on", FileProxyMode.On)]
    [InlineData("auto", FileProxyMode.Auto)]
    [InlineData("xyz", FileProxyMode.Off)]
    [InlineData("开启", FileProxyMode.Off)]     // 只认小写英文原始串，中文不认
    public void 模式字符串解析_不识别一律回退关闭(string raw, FileProxyMode expect)
        => Assert.Equal(expect, FileProxyModes.Parse(raw));

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

    [Fact]
    public void 管道名校验_null给出错误键()
        => Assert.NotNull(FileProxyService.ValidatePipeName(null));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has\\backslash")]
    public void 管道名校验_非法值给出错误键(string? bad)
        => Assert.NotNull(FileProxyService.ValidatePipeName(bad));

    [Fact]
    public void 管道名校验_超长拒绝()
    {
        var tooLong = new string('x', FileProxyService.PipeNameMaxLen + 1);
        Assert.NotNull(FileProxyService.ValidatePipeName(tooLong));
    }

    [Theory]
    [InlineData("logcat-fileproxy")]
    [InlineData("my proxy")]        // 含空格合法（启动命令侧自动引号）
    [InlineData("中文管道名")]
    public void 管道名校验_合法值通过(string ok)
        => Assert.Null(FileProxyService.ValidatePipeName(ok));
}
