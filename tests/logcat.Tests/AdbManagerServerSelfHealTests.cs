using System.Net.Sockets;
using AdvancedSharpAdbClient;
using logcat.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

// 有意同步阻塞（GetAwaiter().GetResult()）：tstrun 反射 runner 不 await 返回 Task 的
// 测试方法——写成 async Task 会被静默跳过、断言根本不执行（假绿）。
#pragma warning disable xUnit1031
namespace logcat.Tests;

/// <summary>
/// AdbManager 的 adb server 自愈（<see cref="AdbManager.EnsureServerRunningAsync"/>）与
/// 监控重建（<see cref="AdbManager.RestartMonitor"/>）——change fix-adb-server-self-heal 任务 3.1。
/// 全程经内部构造注入假实现：不触真实 AdbServer、不派生 adb 进程、零 5037 连接。
/// </summary>
public class AdbManagerServerSelfHealTests
{
    static AdbManager NewManager(
        Func<bool>? statusProbe = null,
        Func<Task<int>>? startServer = null,
        Action? startupStart = null,
        Func<IAdbSocket>? socketFactory = null)
        => new(NullLogger<AdbManager>.Instance,
            statusProbe ?? (() => true),
            startServer ?? (() => Task.FromResult(0)),
            startupStart ?? (() => { }),
            socketFactory ?? ThrowOnUse);

    static IAdbSocket ThrowOnUse() => throw new NotSupportedException("本用例不应创建设备监控");

    // ── EnsureServerRunningAsync ──

    [Fact]
    public void server已运行时零副作用直接返回()
    {
        int starts = 0;
        using var mgr = NewManager(
            statusProbe: () => true,
            startServer: () => { starts++; return Task.FromResult(0); },
            startupStart: () => { });

        var result = mgr.EnsureServerRunningAsync().GetAwaiter().GetResult();

        Assert.Null(result.Prompt);
        Assert.False(result.Restarted);
        Assert.Equal(0, starts);   // 不拉起
    }

    [Fact]
    public void server未运行拉起成功复核通过则报告已拉起()
    {
        int probes = 0, starts = 0;
        using var mgr = NewManager(
            statusProbe: () => { probes++; return probes != 1; },   // 首次探测未运行，复核通过
            startServer: () => { starts++; return Task.FromResult(0); },
            startupStart: () => { });

        var result = mgr.EnsureServerRunningAsync().GetAwaiter().GetResult();

        Assert.Null(result.Prompt);
        Assert.True(result.Restarted);
        Assert.Equal(1, starts);   // 恰拉起一次
        Assert.Equal(2, probes);   // 探测 + 拉起后复核
    }

    [Fact]
    public void 拉起抛异常时返回含引导词与原始消息的提示()
    {
        using var mgr = NewManager(
            statusProbe: () => false,
            startServer: () => throw new InvalidOperationException("adb 可执行文件解析失败"),
            startupStart: () => { });

        var result = mgr.EnsureServerRunningAsync().GetAwaiter().GetResult();

        Assert.NotNull(result.Prompt);
        Assert.Contains("未能启动 adb server", result.Prompt);
        Assert.Contains("adb start-server", result.Prompt);
        Assert.Contains("adb 可执行文件解析失败", result.Prompt);   // 原始消息随提示带出
        Assert.False(result.Restarted);
    }

    [Fact]
    public void 拉起退出码非零时返回提示()
    {
        using var mgr = NewManager(
            statusProbe: () => false,
            startServer: () => Task.FromResult(1),
            startupStart: () => { });

        var result = mgr.EnsureServerRunningAsync().GetAwaiter().GetResult();

        Assert.NotNull(result.Prompt);
        Assert.Contains("adb start-server", result.Prompt);
        Assert.False(result.Restarted);
    }

    [Fact]
    public void 拉起后复核仍不可达时返回提示()
    {
        // 退出码 0 但 server 仍不在（进程退出 ≠ server 真的在听 5037）
        using var mgr = NewManager(
            statusProbe: () => false,
            startServer: () => Task.FromResult(0),
            startupStart: () => { });

        var result = mgr.EnsureServerRunningAsync().GetAwaiter().GetResult();

        Assert.NotNull(result.Prompt);
        Assert.False(result.Restarted);
    }

    [Fact]
    public void 探测抛异常按未运行处理仍尝试拉起()
    {
        int probes = 0, starts = 0;
        using var mgr = NewManager(
            statusProbe: () =>
            {
                probes++;
                if (probes == 1) throw new InvalidOperationException("GetStatus 失败");
                return true;
            },
            startServer: () => { starts++; return Task.FromResult(0); },
            startupStart: () => { });

        var result = mgr.EnsureServerRunningAsync().GetAwaiter().GetResult();

        Assert.Null(result.Prompt);
        Assert.True(result.Restarted);
        Assert.Equal(1, starts);
    }

    [Fact]
    public void 构造时拉起失败不向外抛且管理器仍可用()
    {
        using var mgr = NewManager(
            statusProbe: () => true,
            startServer: () => Task.FromResult(0),
            startupStart: () => throw new InvalidOperationException("adb 不在 PATH"));

        // 构造不抛即通过（失败仅日志）；对象仍可正常探测
        var result = mgr.EnsureServerRunningAsync().GetAwaiter().GetResult();
        Assert.Null(result.Prompt);
    }

    // ── 监控重建 ──

    [Fact]
    public void 监控启动失败即置空且可再次重建()
    {
        // 真形态：new AdbSocket() 构造即急切连接，server 不在时抛 SocketException
        int factories = 0;
        using var mgr = NewManager(
            statusProbe: () => true,
            startServer: () => Task.FromResult(0),
            startupStart: () => { },
            socketFactory: () =>
            {
                factories++;
                throw new SocketException((int)SocketError.ConnectionRefused);
            });

        mgr.StartMonitor();   // 工厂抛 → 同步 catch 置空
        mgr.StartMonitor();   // 已置空 → 应再次尝试（可重建）

        Assert.Equal(2, factories);
    }

    [Fact]
    public void 监控启动取消不置空已重建的监控()
    {
        // 僵尸态实证的推论：StopMonitor 取消让旧监控的 StartAsync 以 OCE 收场，
        // 包装层必须静默且不碰 _monitor——否则会把 RestartMonitor 刚新建的监控置空
        int factories = 0;
        var created = new List<FakeAdbSocket>();
        using var mgr = NewManager(
            statusProbe: () => true,
            startServer: () => Task.FromResult(0),
            startupStart: () => { },
            socketFactory: () =>
            {
                factories++;
                var fake = new FakeAdbSocket { BlockingRead = true };
                created.Add(fake);
                return fake;
            });

        mgr.StartMonitor();    // #1：阻塞在监控循环的读，StartAsync 任务挂起，包装层在等
        WaitUntil(() => created[0].ReadEntered, 2000);   // 等循环真正挂住再停（取消赶在 Task.Run 之前会无从传播）
        mgr.StopMonitor();     // 取消 → #1 的 StartAsync 任务取消 → 包装层观察到 OCE
        mgr.StartMonitor();    // #2：重建的新监控
        // 等 #1 的包装层把 OCE 处理完（其阻塞读被取消是前置信号，留余量给后续 task 续程）
        WaitUntil(() => created[0].ReadCancelled, 2000);
        Thread.Sleep(100);
        mgr.StartMonitor();    // #3：必须早退——证明 #2 没被 #1 的 OCE 冒泡置空

        Assert.Equal(2, factories);
    }

    /// <summary>轮询等待条件成立（同步测试里没有 await 可用）。</summary>
    static void WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 >= deadline)
                throw new TimeoutException("等待条件在超时内未成立");
            Thread.Sleep(20);
        }
    }
}
