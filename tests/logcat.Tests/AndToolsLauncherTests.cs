using System;
using System.Collections.Generic;
using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// 工具箱启动器探测判定（tasks 2.4 / spec android-toolbox「工具箱缺失时入口禁用」）：
/// 存在 → 启动一次返回 true；缺失 → 返回 false 且不启动（点击不产生异常，提示由调用方给）。
/// 探测路径与启动调用以委托注入，不触真实文件系统与进程。
/// </summary>
public class AndToolsLauncherTests : IDisposable
{
    readonly Func<string, bool> _origExists = AndToolsLauncher.FileExists;
    readonly Action<string> _origStart = AndToolsLauncher.Start;

    readonly List<string> _started = new();
    bool _exists;

    public AndToolsLauncherTests()
    {
        AndToolsLauncher.FileExists = _ => _exists;
        AndToolsLauncher.Start = p => _started.Add(p);
    }

    public void Dispose()
    {
        AndToolsLauncher.FileExists = _origExists;
        AndToolsLauncher.Start = _origStart;
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void 主程序存在_启动一次并返回true()
    {
        _exists = true;

        Assert.True(AndToolsLauncher.TryLaunch());
        var started = Assert.Single(_started);
        Assert.EndsWith("logcat-andtools.exe", started);
    }

    [Fact]
    public void 主程序缺失_返回false且不启动()
    {
        _exists = false;

        Assert.False(AndToolsLauncher.TryLaunch());
        Assert.Empty(_started);   // 点击不产生异常、不误启动
    }
}
