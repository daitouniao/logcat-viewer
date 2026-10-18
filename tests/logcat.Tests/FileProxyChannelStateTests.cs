using System;
using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// 渠道状态三态互斥判定（任务 6.4，spec「渠道状态区」）：
/// 4 个输入位的 16 格真值表全跑（ADB 在途优先 → spool 在途或代理文档 → 直接读 → 空，含 spool
/// 进行中未就位、spool 结束后代理文档等边界格），另覆盖三渠道文案映射。文案断言锁中文。
/// </summary>
// 文案映射断言锁中文（SetLangGuard），须与其它改 Loc 全局语言的类串行
[Collection(LangCollection.Name)]
public class FileProxyChannelStateTests
{
    [Fact]
    public void 真值表_16格全跑_三态互斥()
    {
        // 依次枚举 (adbActive, spoolActive, hasDoc, docProxied) 的 16 个组合。
        // tstrun 不支持 MemberData/ClassData（静默跳过），用循环逐格断言并带出组合签名。
        for (int mask = 0; mask < 16; mask++)
        {
            bool adb = (mask & 1) != 0;
            bool spool = (mask & 2) != 0;
            bool hasDoc = (mask & 4) != 0;
            bool proxied = (mask & 8) != 0;

            var ch = ProxyChannelStates.Decide(adb, spool, hasDoc, proxied);
            string combo = $"adb={adb} spool={spool} hasDoc={hasDoc} proxied={proxied}";

            // ADB 在途优先级最高：其余输入无关
            if (adb) { Assert.True(ch == ProxyChannel.Adb, combo); continue; }

            // spool 在途或代理文档 → 文件代理读（spool 进行中即使文档尚未就位也切代理读）
            if (spool || (hasDoc && proxied)) { Assert.True(ch == ProxyChannel.FileProxy, combo); continue; }

            // 直接读：有非代理文档
            if (hasDoc) { Assert.True(ch == ProxyChannel.LocalFile, combo); continue; }

            // 空
            Assert.True(ch == ProxyChannel.None, combo);
        }
    }

    [Fact]
    public void 空渠道给空文本占位_三渠道文案映射()
    {
        var zh = Loc.SetLangGuard(UiLang.ZhCn);
        try
        {
            Assert.Equal("", ProxyChannelStates.Text(ProxyChannel.None));   // 空文本占位不隐藏
            Assert.Equal("直接读文件", ProxyChannelStates.Text(ProxyChannel.LocalFile));
            Assert.Equal("文件代理读", ProxyChannelStates.Text(ProxyChannel.FileProxy));
            Assert.Equal("ADB 采集", ProxyChannelStates.Text(ProxyChannel.Adb));
        }
        finally { zh(); }
    }
}
