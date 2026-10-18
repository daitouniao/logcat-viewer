using System;
using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// 单实例守卫（任务 6.1，spec「单实例」，决策 D13 / BREAKING 行为变更）：
/// 已持有时 TryAcquire 返回 false、释放后可获取、两个守卫对象互斥（第二实例启动即退出
/// 的判定路径）。真双进程冒烟属维护者手工项（自动化覆盖同进程语义，互斥量语义等价）。
/// </summary>
public class SingleInstanceTests
{
    [Fact]
    public void TryAcquire_首次成功_已持有再取返回false()
    {
        using var guard = new SingleInstance();
        Assert.True(guard.TryAcquire());
        Assert.True(guard.Owned);
        Assert.False(guard.TryAcquire());   // 已持有：再取必失败（真值表判据）
        Assert.True(guard.Owned);
    }

    [Fact]
    public void 双守卫互斥_第二实例判定失败()
    {
        using var first = new SingleInstance();
        Assert.True(first.TryAcquire());

        // 第二个守卫对象 = 第二实例视角：拿不到
        using var second = new SingleInstance();
        Assert.False(second.TryAcquire());
        Assert.False(second.Owned);
    }

    [Fact]
    public void 释放后可重新获取()
    {
        var first = new SingleInstance();
        Assert.True(first.TryAcquire());
        first.Dispose();
        Assert.False(first.Owned);

        using var next = new SingleInstance();   // 新进程/新守卫视角
        Assert.True(next.TryAcquire());
        next.Dispose();
    }

    [Fact]
    public void Dispose可重复调用不抛()
    {
        var guard = new SingleInstance();
        Assert.True(guard.TryAcquire());
        guard.Dispose();
        guard.Dispose();   // 幂等
        Assert.False(guard.Owned);
    }

    [Fact]
    public void 未Acquire直接Dispose不抛()
    {
        var guard = new SingleInstance();
        guard.Dispose();
        Assert.False(guard.Owned);
    }
}
