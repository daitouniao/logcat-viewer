using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// 代理使用判定（spec「代理使用判定」九格真值表 + 「模式配置与默认关闭」）。
/// 与 <see cref="AppSettingsTests"/> 的分工：那边管持久化字段与默认值，这边管判定语义。
/// Off 模式「不进分流段 / 装载路径逐字节一致」的集成锚随装载入口接入（tasks 5.2）补进本类。
/// </summary>
public class FileProxyModeTests
{
    [Theory]
    // 开启：恒走代理（含「可直接读取的文本文件」——跳过直接读）
    [InlineData(FileProxyMode.On, true, true, true)]
    [InlineData(FileProxyMode.On, true, false, true)]
    [InlineData(FileProxyMode.On, false, true, true)]
    [InlineData(FileProxyMode.On, false, false, true)]
    // 自动：直接打开失败或嗅探非文本才走代理
    [InlineData(FileProxyMode.Auto, true, true, false)]
    [InlineData(FileProxyMode.Auto, true, false, true)]
    [InlineData(FileProxyMode.Auto, false, true, true)]
    [InlineData(FileProxyMode.Auto, false, false, true)]
    // 关闭：恒不走（九格全 false，零行为变化）
    [InlineData(FileProxyMode.Off, true, true, false)]
    [InlineData(FileProxyMode.Off, true, false, false)]
    [InlineData(FileProxyMode.Off, false, true, false)]
    [InlineData(FileProxyMode.Off, false, false, false)]
    public void 九格真值表(FileProxyMode mode, bool directOpenOk, bool looksText, bool expectProxy)
        => Assert.Equal(expectProxy, FileProxyDecision.ShouldUseProxy(mode, directOpenOk, looksText));

    [Theory]
    [InlineData("", FileProxyMode.Off)]
    [InlineData("off", FileProxyMode.Off)]
    [InlineData("on", FileProxyMode.On)]
    [InlineData("auto", FileProxyMode.Auto)]
    [InlineData("On ", FileProxyMode.Off)]
    [InlineData("xyz", FileProxyMode.Off)]
    public void 模式解析_不识别一律回退关闭(string raw, FileProxyMode expect)
        => Assert.Equal(expect, FileProxyModes.Parse(raw));

    [Fact]
    public void Off模式_任何输入组合都不走代理()
    {
        foreach (var openOk in new[] { true, false })
            foreach (var text in new[] { true, false })
                Assert.False(FileProxyDecision.ShouldUseProxy(FileProxyMode.Off, openOk, text));
    }
}
