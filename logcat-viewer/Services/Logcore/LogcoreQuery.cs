using System.Runtime.InteropServices;

namespace logcat.Services.Logcore;

/// <summary>
/// 查询句柄（design D1/D7）：开启时已固化结果集，之后只读；与源句柄解耦，
/// 源关闭后仍读开启时的装载内容。对外坐标一律为真实文档行号。
/// </summary>
public sealed class LogcoreQuery : IDisposable
{
    readonly LogcoreNative.Exports _native;
    IntPtr _handle;

    internal LogcoreQuery(LogcoreNative.Exports native, IntPtr handle)
    {
        _native = native;
        _handle = handle;
    }

    /// <summary>结果集行数。</summary>
    public ulong RowCount()
    {
        var code = _native.QueryRowCount(Handle, out var rows);
        LogcoreSource.ThrowIfError(_native, code);
        return rows;
    }

    /// <summary>最近结果行的文档行号；空结果集返回 null，负锚点取首条。</summary>
    public long? Resolve(long docRow)
    {
        var code = _native.QueryResolve(Handle, docRow, out var resolved);
        LogcoreSource.ThrowIfError(_native, code);
        return resolved < 0 ? null : resolved;
    }

    /// <summary>向后取页：起点为真实文档行号；原生缓冲无论成败都立即释放。</summary>
    public IReadOnlyList<LogcorePageRow> GetPage(long start, int count)
    {
        var code = _native.QueryPage(Handle, start, count, out var buffer, out var length);
        return ReadPage(code, buffer, length);
    }

    /// <summary>向上取页：终点为排他文档行号，页内升序。</summary>
    public IReadOnlyList<LogcorePageRow> GetPageBackward(long end, int count)
    {
        var code = _native.QueryPageBackward(Handle, end, count, out var buffer, out var length);
        return ReadPage(code, buffer, length);
    }

    IReadOnlyList<LogcorePageRow> ReadPage(int code, IntPtr buffer, UIntPtr length)
    {
        try
        {
            LogcoreSource.ThrowIfError(_native, code);
            return LogcoreSource.ParsePage(buffer, length);
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                _native.FreePage(buffer, length);
            }
        }
    }

    /// <summary>释放查询句柄：null 安全；不持有源句柄，无需先关源。</summary>
    public void Free()
    {
        var query = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (query == IntPtr.Zero)
        {
            return;
        }
        LogcoreSource.ThrowIfError(_native, _native.FreeQuery(query));
    }

    public void Dispose() => Free();

    IntPtr Handle
    {
        get
        {
            var query = _handle;
            if (query == IntPtr.Zero)
            {
                throw new ObjectDisposedException(nameof(LogcoreQuery));
            }
            return query;
        }
    }
}