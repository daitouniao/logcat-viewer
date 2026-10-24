using logcat.Controls;
using logcat.Services;
using Xunit;

// 用例刻意保持同步方法（tstrun 反射 runner 不 await async Task，会静默跳过，见任务 1.3 说明）；
// 被驱动路径全部同步完成，阻塞等待无死锁风险
#pragma warning disable xUnit1031

namespace logcat.Tests;

/// <summary>
/// 传输阶段编排（<see cref="DeviceFilePane.RunPushStagesAsync"/> / <see cref="DeviceFilePane.RunPullStagesAsync"/>）
/// 的阶段序列形状单测（change file-transfer-progress 任务 2.4，design D4/D6）。
/// 契约：中转路径 = 准备 → 字节 → 收尾（pct=NaN 表示非字节阶段）；直连路径无阶段上报，只有字节事件。
/// 文案断言一律与 <see cref="Loc"/> 输出对比（本地 zh / CI en 均成立），不硬编码中文。
/// 用同步方法驱动（GetAwaiter().GetResult()），委托全部同步完成，无死锁风险。
/// </summary>
public class TransferStageTests
{
    sealed class RecordingProgress : IProgress<(double pct, string msg)>
    {
        public List<(double pct, string msg)> Events { get; } = [];
        public void Report((double pct, string msg) value) => Events.Add(value);
    }

    static readonly DeviceFilePane.TransferStepAsync Nop = () => Task.CompletedTask;

    // ── 上传：中转路径 ──

    [Fact]
    public void PushStages_中转路径_准备字节收尾()
    {
        var progress = new RecordingProgress();
        var calls = new List<string>();

        DeviceFilePane.RunPushStagesAsync(true, progress,
            prepare: () => { calls.Add("prepare"); return Task.CompletedTask; },
            bytes: () =>
            {
                progress.Report((42.0, ""));
                calls.Add("bytes");
                return Task.CompletedTask;
            },
            finalize: () => { calls.Add("finalize"); return Task.CompletedTask; }
        ).GetAwaiter().GetResult();

        Assert.Equal(["prepare", "bytes", "finalize"], calls);

        Assert.Equal(3, progress.Events.Count);
        Assert.True(double.IsNaN(progress.Events[0].pct), "准备阶段应报不确定态");
        Assert.Equal(Loc.T("正在准备传输：查询属主、创建目录…"), progress.Events[0].msg);
        Assert.Equal(42.0, progress.Events[1].pct);
        Assert.Equal("", progress.Events[1].msg);
        Assert.True(double.IsNaN(progress.Events[2].pct), "收尾阶段应报不确定态");
        Assert.Equal(Loc.T("正在收尾传输：设备端拷贝与权限设置…"), progress.Events[2].msg);
    }

    // ── 上传：直连路径 ──

    [Fact]
    public void PushStages_直连路径_无阶段只有字节()
    {
        var progress = new RecordingProgress();
        var calls = new List<string>();

        DeviceFilePane.RunPushStagesAsync(false, progress,
            prepare: () => { calls.Add("prepare"); return Task.CompletedTask; },
            bytes: () =>
            {
                progress.Report((42.0, ""));
                calls.Add("bytes");
                return Task.CompletedTask;
            },
            finalize: () => { calls.Add("finalize"); return Task.CompletedTask; }
        ).GetAwaiter().GetResult();

        // 直连路径不产生阶段，但准备动作（建目录）仍要执行
        Assert.Equal(["prepare", "bytes"], calls);
        Assert.Single(progress.Events);
        Assert.Equal(42.0, progress.Events[0].pct);
        Assert.Equal("", progress.Events[0].msg);
    }

    // ── 下载：中转路径 ──

    [Fact]
    public void PullStages_中转路径_准备然后字节()
    {
        var progress = new RecordingProgress();
        var calls = new List<string>();

        DeviceFilePane.RunPullStagesAsync(progress,
            prepare: () => { calls.Add("prepare"); return Task.CompletedTask; },
            bytes: () =>
            {
                progress.Report((75.0, ""));
                calls.Add("bytes");
                return Task.CompletedTask;
            }
        ).GetAwaiter().GetResult();

        Assert.Equal(["prepare", "bytes"], calls);
        Assert.Equal(2, progress.Events.Count);
        Assert.True(double.IsNaN(progress.Events[0].pct), "准备阶段应报不确定态");
        Assert.Equal(Loc.T("正在准备传输：设备端复制中转文件…"), progress.Events[0].msg);
        Assert.Equal(75.0, progress.Events[1].pct);
    }

    // ── progress 为 null 时静默直通 ──

    [Fact]
    public void PushStages_无进度回调时不抛异常且各步执行()
    {
        var calls = new List<string>();
        DeviceFilePane.RunPushStagesAsync(true, null,
            prepare: () => { calls.Add("prepare"); return Task.CompletedTask; },
            bytes: Nop,
            finalize: () => { calls.Add("finalize"); return Task.CompletedTask; }
        ).GetAwaiter().GetResult();

        Assert.Equal(["prepare", "finalize"], calls);
    }
}
