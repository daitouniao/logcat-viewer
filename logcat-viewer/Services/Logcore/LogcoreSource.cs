using System.Runtime.InteropServices;
using System.Text.Json;

namespace logcat.Services.Logcore;

/// <summary>
/// 一个源的句柄（design D23 / D24）：plan 建句柄、commit 受理即返、
/// 结果经事件与描述读取；close 释放源资源、free 才释放句柄。
/// </summary>
public sealed class LogcoreSource : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct LogcoreLine
    {
        public IntPtr Data;
        public UIntPtr Len;
    }

    readonly LogcoreNative.Exports _native;
    IntPtr _handle;

    public LogcorePlan PlanInfo { get; }

    LogcoreSource(LogcoreNative.Exports native, IntPtr handle, LogcorePlan plan)
    {
        _native = native;
        _handle = handle;
        PlanInfo = plan;
    }

    /// <summary>第一段：构造请求并判定渠道；失败抛 <see cref="LogcoreException"/> 且不产生句柄。</summary>
    public static LogcoreSource Plan(LogcoreSourceRequest request) => Plan(request.ToJson());

    public static LogcoreSource Plan(string requestJson)
    {
        var native = LogcoreNative.Load();
        var text = Marshal.StringToCoTaskMemUTF8(requestJson);
        try
        {
            var code = native.Plan(text, out var handle, out var planPtr);
            var planJson = LogcoreNative.TakeJson(native, planPtr);
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

    /// <summary>第二段：校验前置条件后立即返回；装载在 core 内继续。</summary>
    public void Commit() => ThrowIfError(_native, _native.Commit(Handle));

    /// <summary>采集推入：行数组深拷贝入队即返；超单批上限整批拒绝（field = "lines"）。</summary>
    public void Feed(IReadOnlyList<byte[]> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var pinned = new GCHandle[lines.Count];
        var structs = new LogcoreLine[lines.Count];
        GCHandle arrayHandle = default;
        try
        {
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i] ?? throw new ArgumentException("行不得为 null", nameof(lines));
                pinned[i] = GCHandle.Alloc(line, GCHandleType.Pinned);
                structs[i] = new LogcoreLine
                {
                    Data = pinned[i].AddrOfPinnedObject(),
                    Len = (UIntPtr)(ulong)line.Length,
                };
            }
            arrayHandle = GCHandle.Alloc(structs, GCHandleType.Pinned);
            var code = _native.Feed(
                Handle,
                arrayHandle.AddrOfPinnedObject(),
                (UIntPtr)(ulong)structs.Length);
            ThrowIfError(_native, code);
        }
        finally
        {
            if (arrayHandle.IsAllocated)
            {
                arrayHandle.Free();
            }
            foreach (var handle in pinned)
            {
                if (handle.IsAllocated)
                {
                    handle.Free();
                }
            }
        }
    }

    /// <summary>停止：排空队列、刷盘、发 stopped；采集文件与锁标记保留。</summary>
    public void Stop() => ThrowIfError(_native, _native.Stop(Handle));

    /// <summary>关闭：作废未消化队列、释放源资源、不发终态事件；句柄保留至 Free。</summary>
    public void Close() => ThrowIfError(_native, _native.Close(Handle));

    /// <summary>取消：plan 阶段释放计划资源；装载完成前中止并转已关闭。</summary>
    public void Cancel() => ThrowIfError(_native, _native.Cancel(Handle));

    /// <summary>一次取走全部待处理事件；空数组合法。</summary>
    public IReadOnlyList<LogcoreEvent> PollEvents()
    {
        var code = _native.PollEvents(Handle, out var ptr);
        var json = LogcoreNative.TakeJson(_native, ptr);
        ThrowIfError(_native, code);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(LogcoreEvent.Parse).ToArray();
    }

    public LogcoreProgress GetProgress()
    {
        var code = _native.GetProgress(Handle, out var ptr);
        var json = LogcoreNative.TakeJson(_native, ptr);
        ThrowIfError(_native, code);
        using var doc = JsonDocument.Parse(json);
        return LogcoreProgress.Parse(doc.RootElement);
    }

    /// <summary>描述：受理后即可读；装载未完成时 rowCount/docSize 为当前值。</summary>
    public LogcoreDescriptor GetDescriptor()
    {
        var code = _native.GetDescriptor(Handle, out var ptr);
        var json = LogcoreNative.TakeJson(_native, ptr);
        ThrowIfError(_native, code);
        using var doc = JsonDocument.Parse(json);
        return LogcoreDescriptor.Parse(doc.RootElement);
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
            _native.Close(handle);
        }
        catch (Exception)
        {
            // 收尾尽力而为：句柄仍按 free 归还，避免泄漏。
        }
        var code = _native.FreeHandle(Interlocked.Exchange(ref _handle, IntPtr.Zero));
        if (code != LogcoreErrorCodes.Ok)
        {
            // 未关闭（如 close 失败）时 core 拒绝释放；句柄无法安全回收，交由调用方复查。
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

    static void ThrowIfError(LogcoreNative.Exports native, int code)
    {
        if (code == LogcoreErrorCodes.Ok)
        {
            return;
        }
        var take = native.TakeLastError(out var ptr);
        var json = LogcoreNative.TakeJson(native, ptr);
        if (take != LogcoreErrorCodes.Ok || json == "null")
        {
            throw new LogcoreException(
                code,
                new Dictionary<string, JsonElement>(StringComparer.Ordinal));
        }
        throw LogcoreException.FromJson(json);
    }
}

/// <summary>无句柄的进程级入口：dll 接线、启动维护与关闭。</summary>
public static class Logcore
{
    /// <summary>指定 logcore.dll 路径（部署接线 / 测试）；缺失时抛明确错误。</summary>
    public static void UseLibrary(string path) => LogcoreNative.UseLibrary(path);

    public static string? LibraryPath => LogcoreNative.LoadedPath;

    /// <summary>启动维护（design D19）：无句柄、不依赖活动源；部分失败不抛错。</summary>
    public static LogcoreMaintainResult StartupMaintain(LogcoreMaintainRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var native = LogcoreNative.Load();
        var text = Marshal.StringToCoTaskMemUTF8(request.ToJson());
        try
        {
            var code = native.StartupMaintain(text, out var ptr);
            var json = LogcoreNative.TakeJson(native, ptr);
            ThrowIfError(native, code);
            using var doc = JsonDocument.Parse(json);
            return LogcoreMaintainResult.Parse(doc.RootElement);
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    /// <summary>结束全部 core 拉起的代理并清空登记；幂等，不关闭源。</summary>
    public static void Shutdown()
    {
        var native = LogcoreNative.Load();
        ThrowIfError(native, native.Shutdown());
    }

    static void ThrowIfError(LogcoreNative.Exports native, int code)
    {
        if (code == LogcoreErrorCodes.Ok)
        {
            return;
        }
        var take = native.TakeLastError(out var ptr);
        var json = LogcoreNative.TakeJson(native, ptr);
        if (take != LogcoreErrorCodes.Ok || json == "null")
        {
            throw new LogcoreException(
                code,
                new Dictionary<string, JsonElement>(StringComparer.Ordinal));
        }
        throw LogcoreException.FromJson(json);
    }
}