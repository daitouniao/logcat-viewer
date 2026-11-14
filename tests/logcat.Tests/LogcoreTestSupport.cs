using System.Diagnostics;
using logcat.Services.Logcore;
using Xunit;

namespace logcat.Tests;

[CollectionDefinition("Logcore", DisableParallelization = true)]
public sealed class LogcoreCollection
{
}

/// <summary>IT 公共接线：仓库根、debug dll、唯一临时目录与轮询等待。</summary>
internal static class LogcoreTestSupport
{
    static readonly Lazy<string> Root = new(FindRepoRoot);

    internal static string RepoRoot() => Root.Value;

    internal static string DebugDll() => Path.Combine(RepoRoot(), "logcore", "target", "debug", "logcore.dll");

    internal static string TestProxyExe() => Path.Combine(RepoRoot(), "logcore", "target", "debug", "logcore-test-proxy.exe");

    internal static void EnsureLibrary()
    {
        var dll = DebugDll();
        if (!File.Exists(dll))
        {
            throw new InvalidOperationException(
                $"缺少 {dll}；集成测试前先执行 cargo build -p logcore（见 docs/BUILDING.md）。");
        }
        Logcore.UseLibrary(dll);
    }

    internal static string NewTempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"logcore-it-{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal static string NewPipe(string tag) => $"logcat-it-{tag}-{Guid.NewGuid():N}";

    internal static string WriteLines(string dir, string name, IEnumerable<string> lines, string newline = "\n")
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, string.Join(newline, lines) + newline);
        return path;
    }

    internal static void WaitUntil(Func<bool> condition, TimeSpan? timeout = null, string? because = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(10);
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < limit)
        {
            if (condition())
            {
                return;
            }
            Thread.Sleep(10);
        }
        throw new Xunit.Sdk.XunitException(
            because ?? $"等待条件超时（{limit.TotalSeconds:0.#} s）");
    }

    internal static IReadOnlyList<LogcoreEvent> DrainUntil(
        LogcoreSource source, Func<IReadOnlyList<LogcoreEvent>, bool> done, TimeSpan? timeout = null)
    {
        var all = new List<LogcoreEvent>();
        WaitUntil(() =>
        {
            all.AddRange(source.PollEvents());
            return done(all);
        }, timeout, "等待事件超时");
        return all;
    }

    internal static void SafeDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    static string FindRepoRoot()
    {
        var injected = typeof(LogcoreTestSupport).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepoRoot")?.Value;
        if (!string.IsNullOrEmpty(injected))
        {
            return injected!;
        }
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "logcat-viewer", "logcat.csproj")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("找不到仓库根目录");
    }
}

/// <summary>每个 IT 独立临时根目录；构造时校验原生库可用。</summary>
public abstract class LogcoreIntegrationTest : IDisposable
{
    protected string Root { get; }

    protected LogcoreIntegrationTest()
    {
        LogcoreTestSupport.EnsureLibrary();
        Root = LogcoreTestSupport.NewTempDir(GetType().Name);
    }

    protected string Dir(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    protected LogcoreSource PlanFile(
        string path,
        string pipe,
        string? launchFile = null,
        IReadOnlyList<string>? args = null,
        int timeoutMs = 3000,
        string mode = "Auto",
        string? sidecarDir = null)
    {
        var request = LogcoreSourceRequest.ForFile(
            path,
            Root,
            pipe,
            launchFile ?? "<proxy>",
            args,
            timeoutMs,
            mode,
            sidecarDir ?? Dir("cache"));
        return LogcoreSource.Plan(request);
    }

    protected LogcoreSource PlanDevice(string? serial = null)
    {
        var source = LogcoreSource.Plan(LogcoreSourceRequest.ForDevice(Root, serial));
        source.Commit();
        return source;
    }

    protected static IReadOnlyList<byte[]> Lines(params string[] lines) =>
        lines.Select(System.Text.Encoding.UTF8.GetBytes).ToArray();

    protected static IReadOnlyList<string> Batch(string prefix, int count) =>
        Enumerable.Range(0, count)
            .Select(i => $"11-14 12:00:01.{i % 1000:000}  100  200 I Tag: {prefix}{i}")
            .ToArray();

    public void Dispose() => LogcoreTestSupport.SafeDelete(Root);
}