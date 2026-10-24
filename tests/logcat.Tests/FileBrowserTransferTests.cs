using System.Reflection;
using logcat.Controls;
using logcat.Forms;
using logcat.Models;
using logcat.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

// 用例刻意保持同步方法（tstrun 反射 runner 不 await async Task，会静默跳过，见任务 1.3 说明）；
// 被驱动路径全部同步完成，阻塞等待无死锁风险
#pragma warning disable xUnit1031

namespace logcat.Tests;

/// <summary>
/// 文件浏览对话框传输编排的编排层单测（change file-transfer-progress 任务 4.5，design D6/D9）。
/// 可测性先经探针验证（无设备环境下可实例化对话框，见 design D6 探针结论），
/// 设备面板以干净 ListAsync 的桩替换，避免真实通道与错误弹窗。
/// 文案断言一律与 <see cref="Loc"/> 输出对比（本地 zh / CI en 均成立），不硬编码中文。
/// 用同步方法驱动（GetAwaiter().GetResult()），全部路径同步完成。
/// </summary>
public class FileBrowserTransferTests
{
    /// <summary>设备面板桩：ListAsync 恒返回空集且计数，让 RefreshAsync 干净完成、刷新可观测。</summary>
    class StubDevicePane : DeviceFilePane
    {
        public int ListCalls;
        public StubDevicePane(AdbManager manager) : base(manager, "stub", RootMode.None) { }
        protected override Task<List<FileEntry>> ListAsync(string path, CancellationToken ct)
        {
            ListCalls++;
            return Task.FromResult(new List<FileEntry>());
        }
    }

    static AdbManager NewOfflineManager() => new(
        NullLogger<AdbManager>.Instance,
        statusProbe: () => false,
        startServer: () => Task.FromResult(0),
        startupStart: () => { },
        socketFactory: () => throw new InvalidOperationException("单测不应触网"));

    static FieldInfo Field(object obj, string name) =>
        obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException($"字段不存在: {name}");

    static string StatusOf(FileBrowserDialog dlg) =>
        ((Label)Field(dlg, "_lblStat").GetValue(dlg)!).Text;

    static void SetBusy(FileBrowserDialog dlg, bool busy) =>
        Field(dlg, "_transferring").SetValue(dlg, busy);

    // ── 互斥（design D9）──

    [Fact]
    public void 传输中再次触发被拒并提示()
    {
        using var manager = NewOfflineManager();
        using var dlg = new FileBrowserDialog(new StubDevicePane(manager), new LocalFilePane(), "stub");

        SetBusy(dlg, true);
        dlg.RunTransferAsync([@"C:\不存在的源\a.log"], @"C:\不存在的源", "/sdcard", toDevice: true)
            .GetAwaiter().GetResult();

        Assert.Equal(Loc.T("正在传输，请稍候…"), StatusOf(dlg));
        // 早退：未进入编排，闸门状态未被触碰
        Assert.True((bool)Field(dlg, "_transferring").GetValue(dlg)!);
        Assert.Equal(0, ((StubDevicePane)typeof(FileBrowserDialog)
            .GetField("_remote", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(dlg)!).ListCalls);
    }

    [Fact]
    public void 本机复制在传输中触发同样被拒()
    {
        using var manager = NewOfflineManager();
        using var dlg = new FileBrowserDialog(new StubDevicePane(manager), new LocalFilePane(), "stub");

        SetBusy(dlg, true);
        dlg.CopyLocalFilesAsync(new PathsDroppedArgs([@"C:\不存在的源\a.log"], @"C:\不存在的源", @"C:\dst"))
            .GetAwaiter().GetResult();

        Assert.Equal(Loc.T("正在传输，请稍候…"), StatusOf(dlg));
        Assert.True((bool)Field(dlg, "_transferring").GetValue(dlg)!);
    }

    // ── 空集路径：说明 + 刷新 + 可再次触发 ──

    [Fact]
    public void 空集路径说明无可传输文件并刷新目标栏()
    {
        using var manager = NewOfflineManager();
        var remote = new StubDevicePane(manager);
        using var dlg = new FileBrowserDialog(remote, new LocalFilePane(), "stub");

        dlg.RunTransferAsync([@"C:\不存在的源\a.log"], @"C:\不存在的源", "/sdcard", toDevice: true)
            .GetAwaiter().GetResult();

        Assert.Equal(Loc.T("没有可传输的文件"), StatusOf(dlg));
        Assert.True(remote.ListCalls >= 1, "空集路径也必须刷新目标栏（design D7）");
        Assert.False((bool)Field(dlg, "_transferring").GetValue(dlg)!);
    }

    [Fact]
    public void 传输结束后可再次触发()
    {
        using var manager = NewOfflineManager();
        var remote = new StubDevicePane(manager);
        using var dlg = new FileBrowserDialog(remote, new LocalFilePane(), "stub");

        var args = new[] { @"C:\不存在的源\a.log" };
        dlg.RunTransferAsync(args, @"C:\不存在的源", "/sdcard", toDevice: true).GetAwaiter().GetResult();
        dlg.RunTransferAsync(args, @"C:\不存在的源", "/sdcard", toDevice: true).GetAwaiter().GetResult();

        Assert.Equal(2, remote.ListCalls);
    }
}
