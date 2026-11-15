using System.Runtime.InteropServices;

namespace logcat.Services.Logcore;

/// <summary>原生库缺失或无法加载时抛出；消息含探测路径与构建指引。</summary>
public sealed class LogcoreUnavailableException : Exception
{
    public LogcoreUnavailableException(string message) : base(message) { }
}

/// <summary>
/// logcore.dll 的装载与导出表（design D2 / D8）。字符串由 core 分配、经
/// <c>logcore_free_string</c> 释放；句柄仅在 close 后经 <c>logcore_free_handle</c> 释放。
/// </summary>
internal static class LogcoreNative
{
    const string LibraryName = "logcore.dll";

    static readonly object Gate = new();
    static Exports? _cache;
    static string? _explicitPath;

    internal static string? LoadedPath
    {
        get { lock (Gate) return _cache?.Path; }
    }

    /// <summary>显式指定 dll 路径（部署与测试接线）；路径不存在立即抛明确错误。</summary>
    internal static void UseLibrary(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        if (!System.IO.File.Exists(full))
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

    internal static Exports Load()
    {
        lock (Gate)
        {
            if (_cache != null)
            {
                return _cache;
            }
            var path = _explicitPath ?? ResolveDefault();
            if (!System.IO.File.Exists(path))
            {
                throw new LogcoreUnavailableException(MissingMessage(path));
            }
            try
            {
                _cache = new Exports(path);
            }
            catch (BadImageFormatException e)
            {
                throw new LogcoreUnavailableException(
                    $"logcore 原生库不是有效的 x64 DLL：{path}（{e.Message}）");
            }
            catch (DllNotFoundException e)
            {
                throw new LogcoreUnavailableException(
                    $"logcore 原生库依赖缺失，无法装载：{path}（{e.Message}）");
            }
            return _cache;
        }
    }

    static string ResolveDefault()
    {
        var env = Environment.GetEnvironmentVariable("LOGCORE_DLL");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return System.IO.Path.GetFullPath(env);
        }

        var baseDir = AppContext.BaseDirectory;
        var direct = System.IO.Path.Combine(baseDir, LibraryName);
        if (System.IO.File.Exists(direct))
        {
            return direct;
        }

        var nested = System.IO.Path.Combine(baseDir, "logcore", LibraryName);
        if (System.IO.File.Exists(nested))
        {
            return nested;
        }

        var native = System.IO.Path.Combine(baseDir, "runtimes", "win-x64", "native", LibraryName);
        if (System.IO.File.Exists(native))
        {
            return native;
        }

        // 开发树：测试/调试输出在仓库内，向上找 cargo 产物，免去手工拷贝 dll。
        var dir = new DirectoryInfo(baseDir);
        for (var depth = 0; depth < 8 && dir != null; depth++, dir = dir.Parent)
        {
            foreach (var cfg in new[] { "debug", "release" })
            {
                var candidate = System.IO.Path.Combine(dir.FullName, "logcore", "target", cfg, LibraryName);
                if (System.IO.File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return direct;
    }

    static string MissingMessage(string path) =>
        $"找不到 logcore 原生库：{path}。请先执行 cargo build --manifest-path logcore/Cargo.toml" +
        $"（产物位于 logcore/target/<配置>/logcore.dll），或设置环境变量 LOGCORE_DLL 指向该文件。";

    internal delegate int PlanFn(IntPtr request, out IntPtr handle, out IntPtr planJson);
    internal delegate int HandleFn(IntPtr handle);
    internal delegate int FeedFn(IntPtr handle, IntPtr lines, UIntPtr count);
    internal delegate int JsonOutFn(IntPtr handle, out IntPtr json);
    internal delegate int StartupMaintainFn(IntPtr request, out IntPtr json);
    internal delegate int ShutdownFn();
    internal delegate void FreeStringFn(IntPtr text);
    internal delegate int TakeErrorFn(out IntPtr json);

    internal sealed class Exports : IDisposable
    {
        readonly IntPtr _library;

        public string Path { get; }
        public PlanFn Plan { get; }
        public HandleFn Commit { get; }
        public FeedFn Feed { get; }
        public HandleFn Stop { get; }
        public HandleFn Close { get; }
        public HandleFn Cancel { get; }
        public JsonOutFn PollEvents { get; }
        public JsonOutFn GetProgress { get; }
        public JsonOutFn GetDescriptor { get; }
        public StartupMaintainFn StartupMaintain { get; }
        public ShutdownFn Shutdown { get; }
        public HandleFn FreeHandle { get; }
        public FreeStringFn FreeString { get; }
        public TakeErrorFn TakeLastError { get; }

        public Exports(string path)
        {
            Path = path;
            _library = NativeLibrary.Load(path);
            Plan = Bind<PlanFn>("logcore_plan");
            Commit = Bind<HandleFn>("logcore_commit");
            Feed = Bind<FeedFn>("logcore_feed");
            Stop = Bind<HandleFn>("logcore_stop");
            Close = Bind<HandleFn>("logcore_close");
            Cancel = Bind<HandleFn>("logcore_cancel");
            PollEvents = Bind<JsonOutFn>("logcore_poll_events");
            GetProgress = Bind<JsonOutFn>("logcore_get_progress");
            GetDescriptor = Bind<JsonOutFn>("logcore_get_descriptor");
            StartupMaintain = Bind<StartupMaintainFn>("logcore_startup_maintain");
            Shutdown = Bind<ShutdownFn>("logcore_shutdown");
            FreeHandle = Bind<HandleFn>("logcore_free_handle");
            FreeString = Bind<FreeStringFn>("logcore_free_string");
            TakeLastError = Bind<TakeErrorFn>("logcore_take_last_error");
        }

        T Bind<T>(string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

        public void Dispose()
        {
            NativeLibrary.Free(_library);
        }
    }

    /// <summary>取走 core 分配的 UTF-8 JSON 字符串并释放。</summary>
    internal static string TakeJson(Exports exports, IntPtr ptr)
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