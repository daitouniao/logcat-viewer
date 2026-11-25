using System.Runtime.InteropServices;
using System.Text.Json;

namespace Next.Interop;

/// <summary>取页抽象：真实源与测试假实现共用（视口请求范围的测试 seam）。</summary>
public interface IPageFetcher
{
    LogcorePageRow[] GetPage(long start, int count);
}

/// <summary>
/// 一个日志源句柄：plan 建句柄、commit 受理即返、结果经事件与描述读取；
/// close 释放源资源、free 才释放句柄。页调用按调用方语义串行化（core 状态机
/// 要求调用顺序合法，本壳一次只有一个装载中的源）。
/// </summary>
public sealed class LogcoreSource : IDisposable, IPageFetcher
{
    readonly Exports _native;
    readonly SemaphoreSlim _gate = new(1, 1);
    IntPtr _handle;

    public LogcorePlan PlanInfo { get; }

    LogcoreSource(Exports native, IntPtr handle, LogcorePlan plan)
    {
        _native = native;
        _handle = handle;
        PlanInfo = plan;
    }

    /// <summary>第一段：构造请求并判定路由；失败抛 <see cref="LogcoreException"/> 且不产生句柄。</summary>
    public static LogcoreSource Plan(string requestJson)
    {
        var native = LogcoreLibrary.Load();
        var text = Marshal.StringToCoTaskMemUTF8(requestJson);
        try
        {
            var code = native.Plan(text, out var handle, out var planPtr);
            var planJson = LogcoreJson.Take(native, planPtr);
            ThrowIfError(native, code);
            using var doc = JsonDocument.Parse(planJson);
            var root = doc.RootElement;
            var plan = new LogcorePlan(
                root.GetProperty("route").GetString()!,
                root.GetProperty("estimatedSize").ValueKind == JsonValueKind.Null
                    ? null
                    : root.GetProperty("estimatedSize").GetInt64());
            return new LogcoreSource(native, handle, plan);
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    /// <summary>第二段：受理即返，装载在 core 内继续，结果经事件上报。</summary>
    public void Commit()
    {
        _gate.Wait();
        try { ThrowIfError(_native, _native.Commit(Handle)); }
        finally { _gate.Release(); }
    }

    /// <summary>一次取走全部待处理事件；空数组合法。</summary>
    public IReadOnlyList<LogcoreEvent> PollEvents()
    {
        _gate.Wait();
        try
        {
            var code = _native.PollEvents(Handle, out var ptr);
            var json = LogcoreJson.Take(_native, ptr);
            ThrowIfError(_native, code);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.EnumerateArray().Select(LogcoreEvent.Parse).ToArray();
        }
        finally { _gate.Release(); }
    }

    public LogcoreProgress GetProgress()
    {
        _gate.Wait();
        try
        {
            var code = _native.GetProgress(Handle, out var ptr);
            var json = LogcoreJson.Take(_native, ptr);
            ThrowIfError(_native, code);
            using var doc = JsonDocument.Parse(json);
            return LogcoreProgress.Parse(doc.RootElement);
        }
        finally { _gate.Release(); }
    }

    /// <summary>描述：受理后即可读；装载未完成时 rowCount/docSize 为当前值。</summary>
    public LogcoreDescriptor GetDescriptor()
    {
        _gate.Wait();
        try
        {
            var code = _native.GetDescriptor(Handle, out var ptr);
            var json = LogcoreJson.Take(_native, ptr);
            ThrowIfError(_native, code);
            using var doc = JsonDocument.Parse(json);
            return LogcoreDescriptor.Parse(doc.RootElement);
        }
        finally { _gate.Release(); }
    }

    /// <summary>结果集行数：仅装载完成可用。</summary>
    public ulong RowCount()
    {
        _gate.Wait();
        try
        {
            var code = _native.RowCount(Handle, out var rows);
            ThrowIfError(_native, code);
            return rows;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// 取页：布局 v1 缓冲由 core 分配，解码为托管行模型后立即释放
    /// （design D6——不跨帧持有原生缓冲）。
    /// </summary>
    public LogcorePageRow[] GetPage(long start, int count)
    {
        _gate.Wait();
        try
        {
            var code = _native.Page(Handle, start, count, out var buffer, out var length);
            ThrowIfError(_native, code);
            if (buffer == IntPtr.Zero)
            {
                return [];
            }
            return LogcorePageDecoder.DecodeAndRelease(buffer, length, _native.FreePage);
        }
        finally { _gate.Release(); }
    }

    /// <summary>关闭：释放源资源；句柄保留至 <see cref="Free"/>。</summary>
    public void Close()
    {
        _gate.Wait();
        try { ThrowIfError(_native, _native.Close(Handle)); }
        finally { _gate.Release(); }
    }

    /// <summary>释放句柄：仅在已关闭时合法；未关闭抛 INVALID_STATE 且不释放。</summary>
    public void Free()
    {
        var handle = Handle;
        var code = _native.FreeHandle(handle);
        ThrowIfError(_native, code);
        Interlocked.CompareExchange(ref _handle, IntPtr.Zero, handle);
    }

    public void Dispose()
    {
        var handle = _handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }
        try
        {
            Close();
        }
        catch (LogcoreException)
        {
            // 收尾尽力而为；句柄仍按 free 归还，避免泄漏。
        }
        var code = _native.FreeHandle(Interlocked.Exchange(ref _handle, IntPtr.Zero));
        if (code != LogcoreErrorCodes.Ok)
        {
            _handle = handle;
        }
    }

    IntPtr Handle
    {
        get
        {
            var handle = _handle;
            if (handle == IntPtr.Zero)
            {
                throw new ObjectDisposedException(nameof(LogcoreSource));
            }
            return handle;
        }
    }

    static void ThrowIfError(Exports native, int code)
    {
        if (code == LogcoreErrorCodes.Ok)
        {
            return;
        }
        var take = native.TakeLastError(out var ptr);
        var json = LogcoreJson.Take(native, ptr);
        if (take != LogcoreErrorCodes.Ok || json == "null")
        {
            throw new LogcoreException(code, new Dictionary<string, JsonElement>(StringComparer.Ordinal));
        }
        throw LogcoreException.FromJson(json);
    }
}
