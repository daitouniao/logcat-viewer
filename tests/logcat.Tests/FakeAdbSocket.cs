using System.Runtime.CompilerServices;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;

namespace logcat.Tests;

/// <summary>
/// 假 <see cref="IAdbSocket"/>：让 <see cref="DeviceMonitor"/> 在不触真实 5037 连接的前提下被驱动
/// （自愈单测零触网的半边缝；另半边是状态探测 / 拉起 / 构造时拉起的委托注入）。
/// </summary>
/// <remarks>
/// 监控循环对 socket 的调用序列（AdvancedSharpAdbClient 3.6.16 实证）：
/// <c>SendAdbRequestAsync(host:track-devices)</c> → <c>ReadAdbResponseAsync</c> → 循环 <c>ReadStringAsync</c>。
/// 本假实现把前两步做成恒成功，读可编程：
/// <list type="bullet">
/// <item><see cref="BlockingRead"/> 为 false：恒返回空设备列表（首批解析即通过，监控进入循环）。</item>
/// <item>为 true：读返回一个仅随取消才结束的任务（僵尸态阻塞读，<see cref="ReadCancelled"/> 记录取消）。</item>
/// </list>
/// 其余成员不应被监控循环触达，一律 NotSupportedException——真触达了说明循环形态变了，测试应显式失败。
/// </remarks>
internal sealed class FakeAdbSocket : IAdbSocket, IDisposable
{
    /// <summary>为 true 时读阻塞（仅随取消结束），模拟 server 被杀后监控循环挂住不收事件的僵尸态。</summary>
    public bool BlockingRead { get; init; }

    /// <summary>阻塞读观察到取消时置 true（测试等待包装层处理完 OCE 的前置信号）。</summary>
    public bool ReadCancelled { get; private set; }

    /// <summary>ReadStringAsync 被进入过（含阻塞读尚未取消）——测试先等循环真正挂住再停，
    /// 避免取消赶在 Task.Run 的 lambda 执行之前（那时循环还没起，取消无从传播）。</summary>
    public bool ReadEntered { get; private set; }

    public bool Connected => true;

    // ── 监控循环会走到的三个点 ──

    public Task SendAdbRequestAsync(string request, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<AdbResponse> ReadAdbResponseAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new AdbResponse());

    public Task<string> ReadStringAsync(CancellationToken cancellationToken)
    {
        ReadEntered = true;
        if (!BlockingRead) return Task.FromResult("");
        var delay = Task.Delay(Timeout.Infinite, cancellationToken);
        _ = delay.ContinueWith(t => { if (t.IsCanceled) ReadCancelled = true; });
        return delay.ContinueWith(_ => "", TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    // ── 以下不应被触达 ──

    public void Reconnect(bool restart) => throw new NotSupportedException();
    public Task ReconnectAsync(bool restart, CancellationToken cancellationToken) => throw new NotSupportedException();

    public void Send(byte[] data) => throw new NotSupportedException();
    public void Send(byte[] data, int offset) => throw new NotSupportedException();
    public void Send(byte[] data, int offset, int length) => throw new NotSupportedException();
    public void Send(ReadOnlySpan<byte> data) => throw new NotSupportedException();

    public Task SendAsync(byte[] data, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task SendAsync(byte[] data, int offset, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task SendAsync(byte[] data, int offset, int length, CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) => throw new NotSupportedException();

    public void SendSyncRequest(SyncCommand command, UnixFileStatus fileStatus, SyncFlags flags) => throw new NotSupportedException();
    public void SendSyncRequest(SyncCommand command, string remotePath, UnixFileStatus fileStatus) => throw new NotSupportedException();
    public void SendSyncRequest(SyncCommand command, string remotePath) => throw new NotSupportedException();
    public void SendSyncRequest(SyncCommand command, int length) => throw new NotSupportedException();

    public Task SendSyncRequestAsync(SyncCommand command, UnixFileStatus fileStatus, SyncFlags flags, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task SendSyncRequestAsync(SyncCommand command, string remotePath, UnixFileStatus fileStatus, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task SendSyncRequestAsync(SyncCommand command, string remotePath, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task SendSyncRequestAsync(SyncCommand command, int length, CancellationToken cancellationToken) => throw new NotSupportedException();

    public void SendAdbRequest(string request) => throw new NotSupportedException();
    public void SendAdbRequest(DefaultInterpolatedStringHandler request) => throw new NotSupportedException();

    public int Read(byte[] data) => throw new NotSupportedException();
    public int Read(byte[] data, int offset) => throw new NotSupportedException();
    public int Read(byte[] data, int offset, int length) => throw new NotSupportedException();
    public int Read(Span<byte> data) => throw new NotSupportedException();

    public Task<int> ReadAsync(byte[] data, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<int> ReadAsync(byte[] data, int offset, CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<int> ReadAsync(Memory<byte> data, CancellationToken cancellationToken) => throw new NotSupportedException();

    public string ReadString() => throw new NotSupportedException();
    public string ReadSyncString() => throw new NotSupportedException();
    public Task<string> ReadSyncStringAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public SyncCommand ReadSyncResponse() => throw new NotSupportedException();
    public Task<SyncCommand> ReadSyncResponseAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public AdbResponse ReadAdbResponse() => throw new NotSupportedException();

    public void SetDevice(DeviceData device) => throw new NotSupportedException();
    public Task SetDeviceAsync(DeviceData device, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Stream GetShellStream() => throw new NotSupportedException();
    public void Close() { }
    public void Dispose() { }
}
