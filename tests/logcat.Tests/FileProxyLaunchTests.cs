using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using logcat.Services;
using Xunit;

// 有意同步阻塞（GetAwaiter().GetResult()）：tstrun 反射 runner 不 await 返回 Task 的
// 测试方法——写成 async Task 会被静默跳过、断言根本不执行（假绿）。
#pragma warning disable xUnit1031
namespace logcat.Tests;

/// <summary>
/// 代理自动拉起编排（任务 4.1）与退出杀进程树（任务 6.3）：
/// {pipe} 占位符替换、管道名含空格自动引号、非法管道名拒绝、空启动文件报
/// no_proxy_not_running、命令行拼装引号路径、timeoutMs 注入小值验证超时路径、
/// 拉起/杀进程路径（用 cmd ping 作长命替身进程，不依赖真实代理例子）。
///
/// 手动代理「先试连」路径用 FakeProxy（进程内）覆盖：连上就用，与是否配置启动命令无关。
/// </summary>
// 消息断言锁中文（SetLangGuard），须与其它改 Loc 全局语言的类串行
[Collection(LangCollection.Name)]
public class FileProxyLaunchTests : IDisposable
{
    readonly Action _zhGuard = Loc.SetLangGuard(UiLang.ZhCn);   // 消息断言锁中文（ctor 固定，Dispose 还原）

    public void Dispose() => _zhGuard();

    // ── 纯函数：占位符与命令行 ──

    [Fact]
    public void SubstPipe_占位符替换_管道名含空格自动引号()
    {
        Assert.Equal("--pipe abc", FileProxyService.SubstPipe("--pipe {pipe}", "abc"));
        Assert.Equal("--pipe \"a b\"", FileProxyService.SubstPipe("--pipe {pipe}", "a b"));
        // 多处出现同样替换；无占位符原样保留
        Assert.Equal("-n \"x y\" -n \"x y\"", FileProxyService.SubstPipe("-n {pipe} -n {pipe}", "x y"));
        Assert.Equal("--verbose", FileProxyService.SubstPipe("--verbose", "abc"));
        Assert.Equal("", FileProxyService.SubstPipe(null, "abc"));
    }

    [Fact]
    public void BuildCommandLine_引号路径拼装()
    {
        // 可执行文件含空格加引号；参数含空格同样
        Assert.Equal("\"C:\\Program Files\\proxy.exe\" --pipe \"my pipe\"",
            FileProxyService.BuildCommandLine("C:\\Program Files\\proxy.exe", "--pipe {pipe}", "my pipe"));
        // 无空格不加引号；无参数只留可执行文件
        Assert.Equal("C:\\tools\\proxy.exe --pipe p1",
            FileProxyService.BuildCommandLine("C:\\tools\\proxy.exe", "--pipe {pipe}", "p1"));
        Assert.Equal("proxy.exe", FileProxyService.BuildCommandLine("proxy.exe", "", "p1"));
    }

    [Fact]
    public void ValidatePipeName_非法值拒绝()
    {
        // tstrun 不支持 Theory（静默跳过），非法值循环展开在单个 Fact 内
        foreach (var bad in new string?[] { null, "", "   ", "bad\\name", "bad/name" })
            Assert.True(FileProxyService.ValidatePipeName(bad) != null, $"非法值 {bad} 未被拒绝");
    }

    [Fact]
    public void ValidatePipeName_超长拒绝_合法值放行()
    {
        Assert.NotNull(FileProxyService.ValidatePipeName(new string('a', FileProxyService.PipeNameMaxLen + 1)));
        Assert.Null(FileProxyService.ValidatePipeName(new string('a', FileProxyService.PipeNameMaxLen)));
        Assert.Null(FileProxyService.ValidatePipeName("logcat-fileproxy"));
    }

    // ── EnsureProxyAsync 编排 ──

    [Fact]
    public void EnsureProxy_手动代理可用直接连上_不起进程()
    {
        using var proxy = FakeProxy.StartAsync().GetAwaiter().GetResult();
        int launchedBefore = FileProxyService.LaunchedForTest.Count;

        // 未配启动命令（LaunchFile 空）：纯手动代理场景
        var settings = new FileProxySettings(proxy.PipeName, "", "", TimeoutMs: 5000);
        using var client = FileProxyService.EnsureProxyAsync(settings).GetAwaiter().GetResult();
        Assert.Equal("test-fileproxy/1.0", client.ProxyName);
        Assert.Equal(launchedBefore, FileProxyService.LaunchedForTest.Count);   // 未拉起任何进程
    }

    [Fact]
    public void EnsureProxy_先试连手动代理_配了命令也不重复拉起()
    {
        using var proxy = FakeProxy.StartAsync().GetAwaiter().GetResult();
        // 启动命令指向不存在的 exe：若误走拉起分支会报「启动失败」——
        // 实际应先试连手动代理成功，根本不碰启动命令
        var settings = new FileProxySettings(proxy.PipeName, "Z:\\no-such-proxy.exe", "--pipe {pipe}", TimeoutMs: 500);
        using var client = FileProxyService.EnsureProxyAsync(settings).GetAwaiter().GetResult();
        Assert.Equal("test-fileproxy/1.0", client.ProxyName);
    }

    [Fact]
    public void EnsureProxy_无代理且未配启动命令报no_proxy_not_running()
    {
        var settings = new FileProxySettings("lp-nobody-" + Guid.NewGuid().ToString("N"), "", "", TimeoutMs: 500);
        var ex = Trap<FileProxyException>(
            () => FileProxyService.EnsureProxyAsync(settings).GetAwaiter().GetResult(), out _);
        Assert.Equal(FileProxyErrorCode.NoProxyNotRunning, ex.Code);
        Assert.Contains("文件代理", ex.Message);
    }

    [Fact]
    public void EnsureProxy_管道名非法拒绝()
    {
        // 配了启动文件 → 走到管道名校验分支
        var bad = new FileProxySettings("bad\\name", "whatever.exe", "--pipe {pipe}", TimeoutMs: 500);
        var ex = Trap<FileProxyException>(
            () => FileProxyService.EnsureProxyAsync(bad).GetAwaiter().GetResult(), out _);
        Assert.Equal(FileProxyErrorCode.Io, ex.Code);
        Assert.Contains("管道名", ex.Message);

        var long1 = new FileProxySettings(new string('a', FileProxyService.PipeNameMaxLen + 1), "whatever.exe", "--pipe {pipe}", TimeoutMs: 500);
        var ex2 = Trap<FileProxyException>(
            () => FileProxyService.EnsureProxyAsync(long1).GetAwaiter().GetResult(), out _);
        Assert.Equal(FileProxyErrorCode.Io, ex2.Code);
    }

    [Fact]
    public void EnsureProxy_拉起后连不上_超时报LaunchTimeout含命令行_杀进程树()
    {
        // 拉起一个长命替身（cmd：先 title 把窗口标题设为管道名——无害且让命令行含替换后的管道名，
        // 再 ping ~29s 保活），TimeoutMs 注入小值 → 必超时
        string pipe = "lp-launch-" + Guid.NewGuid().ToString("N");
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        string args = "/c title {pipe} & ping -n 30 127.0.0.1";
        var settings = new FileProxySettings(pipe, cmd, args, TimeoutMs: 600);

        var ex = Trap<FileProxyException>(
            () => FileProxyService.EnsureProxyAsync(settings).GetAwaiter().GetResult(), out _);
        Assert.Equal(FileProxyErrorCode.LaunchTimeout, ex.Code);
        // 错误附完整命令行（含替身可执行文件与替换后的管道名）
        Assert.Contains("cmd.exe", ex.Message);
        Assert.Contains(pipe, ex.Message);
        Assert.Equal(FileProxyService.BuildCommandLine(cmd, args, pipe), ex.CommandLine);

        // 杀进程路径（任务 6.3）：整树杀掉替身进程。
        // 注意 KillLaunchedProcesses 会顺带 Dispose 列表内 Process 对象——Kill 前取快照（存活态 + PID），
        // Kill 后用 PID 重新查系统进程表验证消失，不得复用原对象。
        var launched = FileProxyService.LaunchedForTest;
        Assert.NotEmpty(launched);
        var victim = launched[^1];
        Assert.False(victim.HasExited);   // ping -n 30 在 600ms 时点必然还活着
        int victimPid = victim.Id;

        FileProxyService.KillLaunchedProcesses();
        bool gone;
        try { gone = Process.GetProcessById(victimPid).HasExited; }   // 查得到再看存活态
        catch (Exception) { gone = true; }                            // 进程已不存在 = 已被杀
        Assert.True(gone, $"替身进程 PID {victimPid} 未被杀进程树终止");
        // 幂等：再杀一次不抛
        FileProxyService.KillLaunchedProcesses();
    }

    [Fact]
    public void EnsureProxy_启动命令指向不存在的exe报Io含命令行()
    {
        string pipe = "lp-badexe-" + Guid.NewGuid().ToString("N");
        var settings = new FileProxySettings(pipe, "Z:\\no-such-proxy.exe", "--pipe {pipe}", TimeoutMs: 400);
        var ex = Trap<FileProxyException>(
            () => FileProxyService.EnsureProxyAsync(settings).GetAwaiter().GetResult(), out _);
        Assert.Equal(FileProxyErrorCode.Io, ex.Code);
        Assert.Contains("Z:\\no-such-proxy.exe", ex.Message);
        Assert.Contains(pipe, ex.Message);
    }

    static T Trap<T>(Action act, out Exception? ex) where T : Exception
    {
        try { act(); ex = null; return null!; }
        catch (Exception e) { ex = e; return (T)e; }
    }
}
