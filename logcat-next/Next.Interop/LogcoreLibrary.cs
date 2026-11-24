using System.Runtime.InteropServices;

namespace Next.Interop;

/// <summary>
/// logcore.dll 的装载与导出表。dll 探测顺序：环境变量 → 应用目录 → 开发树回退
/// （向上 8 层找 logcore/target/&lt;配置&gt;/logcore.dll）；导出按名解析，缺失即明确报错。
/// </summary>
public static class LogcoreLibrary
{
    const string LibraryName = "logcore.dll";

    // C ABI 签名（谁分配谁释放：core 分配的字符串经 FreeString 释放，页缓冲经 FreePage 释放）
    public delegate int PlanFn(IntPtr request, out IntPtr handle, out IntPtr planJson);
    public delegate int HandleFn(IntPtr handle);
    public delegate int JsonOutFn(IntPtr handle, out IntPtr json);
    public delegate int RowCountFn(IntPtr handle, out ulong rows);
    public delegate int PageFn(IntPtr handle, long start, long count, out IntPtr buffer, out UIntPtr length);
    public delegate void FreePageFn(IntPtr buffer, UIntPtr length);
    public delegate void FreeStringFn(IntPtr text);
    public delegate int TakeErrorFn(out IntPtr json);

    static readonly object Gate = new();
    static Exports? _cache;
    static string? _explicitPath;

    public static string? LoadedPath
    {
        get { lock (Gate) return _cache?.Path; }
    }

    /// <summary>显式指定 dll 路径（部署与测试接线）；路径不存在立即抛明确错误。</summary>
    public static void UseLibrary(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new LogcoreUnavailableException(MissingMessage(full));
        }
        lock (Gate)
        {
            if (!string.Equals(_cache?.Path, full, StringComparison.OrdinalIgnoreCase))
            {
                _cache?.Dispose();
                _cache = null;
            }
            _explicitPath = full;
        }
    }

    public static Exports Load()
    {
        lock (Gate)
        {
            if (_cache != null)
            {
                return _cache;
            }
            var path = _explicitPath ?? ResolveDefault();
            if (!File.Exists(path))
            {
                throw new LogcoreUnavailableException(MissingMessage(path));
            }
            try
            {
                _cache = new Exports(path);
            }
            catch (BadImageFormatException e)
            {
                throw new LogcoreUnavailableException($"logcore 原生库不是有效的 x64 DLL：{path}（{e.Message}）");
            }
            catch (DllNotFoundException e)
            {
                throw new LogcoreUnavailableException($"logcore 原生库依赖缺失，无法装载：{path}（{e.Message}）");
            }
            return _cache;
        }
    }

    static string ResolveDefault()
    {
        var env = Environment.GetEnvironmentVariable("LOGCORE_DLL");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return Path.GetFullPath(env);
        }

        var baseDir = AppContext.BaseDirectory;
        var direct = Path.Combine(baseDir, LibraryName);
        if (File.Exists(direct))
        {
            return direct;
        }

        var nested = Path.Combine(baseDir, "logcore", LibraryName);
        if (File.Exists(nested))
        {
            return nested;
        }

        // 开发树：输出在仓库内，向上找 cargo 产物，免去手工拷贝 dll。
        var dir = new DirectoryInfo(baseDir);
        for (var depth = 0; depth < 8 && dir != null; depth++, dir = dir.Parent)
        {
            foreach (var cfg in new[] { "debug", "release" })
            {
                var candidate = Path.Combine(dir.FullName, "logcore", "target", cfg, LibraryName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return direct;
    }

    static string MissingMessage(string path) =>
        $"找不到 logcore 原生库：{path}。请先执行 cargo build" +
        $"（产物位于 logcore/target/<配置>/logcore.dll），或设置环境变量 LOGCORE_DLL 指向该文件。";
}

/// <summary>原生库缺失或导出不完整时抛出；消息含探测路径与缺失导出名。</summary>
public sealed class LogcoreUnavailableException : Exception
{
    public LogcoreUnavailableException(string message) : base(message) { }
}

/// <summary>进程级导出表。任一必需导出缺失即抛 <see cref="LogcoreUnavailableException"/>。</summary>
public sealed class Exports : IDisposable
{
    readonly IntPtr _library;

    public string Path { get; }

    public LogcoreLibrary.PlanFn Plan { get; }
    public LogcoreLibrary.HandleFn Commit { get; }
    public LogcoreLibrary.HandleFn Close { get; }
    public LogcoreLibrary.JsonOutFn PollEvents { get; }
    public LogcoreLibrary.JsonOutFn GetProgress { get; }
    public LogcoreLibrary.JsonOutFn GetDescriptor { get; }
    public LogcoreLibrary.RowCountFn RowCount { get; }
    public LogcoreLibrary.PageFn Page { get; }
    public LogcoreLibrary.FreePageFn FreePage { get; }
    public LogcoreLibrary.HandleFn FreeHandle { get; }
    public LogcoreLibrary.FreeStringFn FreeString { get; }
    public LogcoreLibrary.TakeErrorFn TakeLastError { get; }

    public Exports(string path)
    {
        Path = path;
        _library = NativeLibrary.Load(path);
        try
        {
            Plan = Bind<LogcoreLibrary.PlanFn>("logcore_plan");
            Commit = Bind<LogcoreLibrary.HandleFn>("logcore_commit");
            Close = Bind<LogcoreLibrary.HandleFn>("logcore_close");
            PollEvents = Bind<LogcoreLibrary.JsonOutFn>("logcore_poll_events");
            GetProgress = Bind<LogcoreLibrary.JsonOutFn>("logcore_get_progress");
            GetDescriptor = Bind<LogcoreLibrary.JsonOutFn>("logcore_get_descriptor");
            RowCount = Bind<LogcoreLibrary.RowCountFn>("logcore_row_count");
            Page = Bind<LogcoreLibrary.PageFn>("logcore_page");
            FreePage = Bind<LogcoreLibrary.FreePageFn>("logcore_free_page");
            FreeHandle = Bind<LogcoreLibrary.HandleFn>("logcore_free_handle");
            FreeString = Bind<LogcoreLibrary.FreeStringFn>("logcore_free_string");
            TakeLastError = Bind<LogcoreLibrary.TakeErrorFn>("logcore_take_last_error");
        }
        catch (EntryPointNotFoundException e)
        {
            NativeLibrary.Free(_library);
            throw new LogcoreUnavailableException($"logcore.dll 缺少必需导出，原生库与绑定层版本不匹配：{e.Message}");
        }
        catch
        {
            NativeLibrary.Free(_library);
            throw;
        }
    }

    T Bind<T>(string name) where T : Delegate
    {
        try
        {
            return Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));
        }
        catch (EntryPointNotFoundException e)
        {
            throw new EntryPointNotFoundException($"导出 {name} 缺失：{e.Message}");
        }
    }

    public void Dispose() => NativeLibrary.Free(_library);
}

/// <summary>取走 core 分配的 UTF-8 JSON 字符串并释放。</summary>
public static class LogcoreJson
{
    public static string Take(Exports exports, IntPtr ptr)
    {
        if (ptr == IntPtr.Zero)
        {
            return "null";
        }
        try
        {
            return Marshal.PtrToStringUTF8(ptr) ?? "null";
        }
        finally
        {
            exports.FreeString(ptr);
        }
    }
}
