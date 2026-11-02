using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// 互斥量名可注入（design D6）：andtools 传自己的名字与 logcat 并存运行，两 exe 互不抢占；
/// 缺省构造拿 logcat 现名（既有 SingleInstanceTests 零改动覆盖该口径，此处只测注入名）。
/// </summary>
public class SingleInstanceNameTests : IDisposable
{
    readonly List<SingleInstance> _instances = new();

    SingleInstance Acquire(string name)
    {
        var inst = new SingleInstance(name);
        _instances.Add(inst);
        Assert.True(inst.TryAcquire(), $"首次获取 {name} 应成功");
        return inst;
    }

    [Fact]
    public void 注入名生效_同名互斥_异名并存()
    {
        var a = Acquire(SingleInstance.AndToolsMutexName);

        // 同名再取必失败（单实例语义对注入名同样成立）
        var second = new SingleInstance(SingleInstance.AndToolsMutexName);
        _instances.Add(second);
        Assert.False(second.TryAcquire());

        // 异名（logcat 现名）可同时持有：两 exe 并存运行
        var logcat = Acquire(SingleInstance.MutexName);
        Assert.True(a.Owned);
        Assert.True(logcat.Owned);
    }

    [Fact]
    public void 唤醒事件常量为固名()
    {
        // 事件名是对外协议（design D8 固化），改名会断掉跨版本唤醒
        Assert.Equal(@"Local\logcat-andtools-wake", SingleInstance.AndToolsWakeEventName);
        Assert.Equal(@"Local\logcat-andtools-single-instance", SingleInstance.AndToolsMutexName);
    }

    public void Dispose()
    {
        foreach (var i in _instances) i.Dispose();
    }
}
