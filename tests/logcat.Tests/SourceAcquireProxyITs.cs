using logcat.Services.Logcore;
using Xunit;

namespace logcat.Tests;

[Collection("Logcore")]
public sealed class SourceAcquireProxySpoolIT : LogcoreIntegrationTest
{
    [Fact]
    public void External_proxy_on_base_name_is_reused_and_spools()
    {
        var dir = Dir("logs");
        var path = LogcoreTestSupport.WriteLines(dir, "proxy.log", Batch("p", 50));
        using var fake = FakeProxy.StartAsync(LogcoreTestSupport.NewPipe("spool")).GetAwaiter().GetResult();
        var cache = Dir("proxy-cache");

        var source = PlanFile(path, fake.PipeName, mode: "On", sidecarDir: cache);
        Assert.Equal("ProxySpool", source.PlanInfo.Route);
        source.Commit();
        LogcoreTestSupport.DrainUntil(source, es => es.Any(e => e.Type == "done"));

        var desc = source.GetDescriptor();
        Assert.Equal("FileProxy", desc.Channel);
        Assert.Null(desc.Identity);
        Assert.Equal(path, desc.DisplayPath);
        Assert.Equal("Ephemeral", desc.LeaseKind);
        Assert.NotNull(desc.LeaseId);
        Assert.Equal("Progressive", desc.LoadMode);
        Assert.Equal((ulong)50, desc.RowCount);
        Assert.Contains(fake.OpenedPaths, p => p == path);
        Assert.Equal(File.ReadAllText(path), File.ReadAllText(desc.SourcePath));
        Assert.Empty(Directory.GetFiles(cache));
        source.Close();
        source.Free();
    }

    [Fact]
    public void Core_launch_listens_on_derived_pipe_name()
    {
        var dir = Dir("logs");
        var path = LogcoreTestSupport.WriteLines(dir, "owned.log", Batch("o", 20));
        var baseName = LogcoreTestSupport.NewPipe("owned");
        var record = Path.Combine(Root, "spawn.txt");
        var proxy = LogcoreTestSupport.TestProxyExe();
        Assert.True(File.Exists(proxy), "缺少 logcore-test-proxy.exe，先 cargo build");

        var source = PlanFile(
            path,
            baseName,
            launchFile: proxy,
            args: new[] { "--serve", dir, "--record", record },
            timeoutMs: 5000,
            mode: "On");
        source.Commit();
        LogcoreTestSupport.DrainUntil(source, es => es.Any(e => e.Type == "done"));
        var desc = source.GetDescriptor();
        Assert.Null(desc.Identity);
        Assert.Contains("logcat_spool_", desc.SourcePath);
        Assert.Equal((ulong)20, desc.RowCount);
        source.Close();
        source.Free();
        Logcore.Shutdown();

        Assert.True(File.Exists(record), "未被拉起就不会有 argv 记录");
        Assert.Contains($"{baseName}-{Environment.ProcessId}", File.ReadAllText(record));
    }
}

[Collection("Logcore")]
public sealed class SourceAcquireProxyConcurrencyIT : LogcoreIntegrationTest
{
    [Fact]
    public void Two_sources_share_external_proxy_and_shutdown_keeps_it()
    {
        var dir = Dir("logs");
        var one = LogcoreTestSupport.WriteLines(dir, "one.log", Batch("1", 10));
        var two = LogcoreTestSupport.WriteLines(dir, "two.log", Batch("2", 10));
        using var fake = FakeProxy.StartAsync(LogcoreTestSupport.NewPipe("concurrent")).GetAwaiter().GetResult();

        var a = PlanFile(one, fake.PipeName, mode: "On");
        var b = PlanFile(two, fake.PipeName, mode: "On");
        a.Commit();
        b.Commit();
        LogcoreTestSupport.DrainUntil(a, es => es.Any(e => e.Type == "done"));
        LogcoreTestSupport.DrainUntil(b, es => es.Any(e => e.Type == "done"));
        Assert.Equal((ulong)10, a.GetDescriptor().RowCount);
        Assert.Equal((ulong)10, b.GetDescriptor().RowCount);

        Logcore.Shutdown();
        Logcore.Shutdown();
        var stillServing = PlanFile(one, fake.PipeName, mode: "On");
        stillServing.Commit();
        LogcoreTestSupport.DrainUntil(stillServing, es => es.Any(e => e.Type == "done"));
        Assert.Equal((ulong)10, stillServing.GetDescriptor().RowCount);

        stillServing.Close();
        stillServing.Free();
        b.Close();
        b.Free();
        a.Close();
        a.Free();
    }

    [Fact]
    public void Owned_single_instance_spawns_once_and_survives_first_close()
    {
        var dir = Dir("logs");
        var one = LogcoreTestSupport.WriteLines(dir, "owned-1.log", Batch("a", 10));
        var two = LogcoreTestSupport.WriteLines(dir, "owned-2.log", Batch("b", 10));
        var baseName = LogcoreTestSupport.NewPipe("owned-pair");
        var record = Path.Combine(Root, "spawn-pair.txt");
        var proxy = LogcoreTestSupport.TestProxyExe();
        Assert.True(File.Exists(proxy), "缺少 logcore-test-proxy.exe，先 cargo build");
        var args = new[] { "--serve", dir, "--record", record };

        var a = PlanFile(one, baseName, launchFile: proxy, args: args, timeoutMs: 5000, mode: "On");
        var b = PlanFile(two, baseName, launchFile: proxy, args: args, timeoutMs: 5000, mode: "On");
        a.Commit();
        b.Commit();
        LogcoreTestSupport.DrainUntil(a, es => es.Any(e => e.Type == "done"));
        LogcoreTestSupport.DrainUntil(b, es => es.Any(e => e.Type == "done"));

        a.Close();
        a.Free();
        b.Close();
        b.Free();
        Logcore.Shutdown();
        var spawned = File.ReadAllLines(record);
        Assert.Contains("--pipe", spawned);
        Assert.Contains(spawned, line => line.Contains($"{baseName}-{Environment.ProcessId}"));
    }

    [Fact]
    public void Shutdown_interrupts_active_spool_and_next_acquisition_respawns()
    {
        var dir = Dir("logs");
        var path = LogcoreTestSupport.WriteLines(dir, "slow.log", Batch("s", 2000));
        var baseName = LogcoreTestSupport.NewPipe("shutdown-spool");
        var record = Path.Combine(Root, "spawn-shutdown.txt");
        var proxy = LogcoreTestSupport.TestProxyExe();
        Assert.True(File.Exists(proxy), "缺少 logcore-test-proxy.exe，先 cargo build");

        var source = PlanFile(
            path,
            baseName,
            launchFile: proxy,
            args: new[] { "--serve", dir, "--record", record, "--pace-ms", "20" },
            timeoutMs: 5000,
            mode: "On");
        source.Commit();
        LogcoreTestSupport.WaitUntil(() => File.Exists(record), TimeSpan.FromSeconds(5), "代理未被拉起");

        Logcore.Shutdown();
        var events = LogcoreTestSupport.DrainUntil(source, es => es.Any(e => e.Type == "error"));
        var error = events.First(e => e.Type == "error");
        Assert.Equal(LogcoreErrorCodes.ProxyDisconnect, error.Code);
        Assert.True(error.Params!.ContainsKey("reason"));
        source.Close();
        source.Free();

        var again = PlanFile(
            path,
            LogcoreTestSupport.NewPipe("shutdown-again"),
            launchFile: proxy,
            args: new[] { "--serve", dir },
            timeoutMs: 5000,
            mode: "On");
        again.Commit();
        LogcoreTestSupport.DrainUntil(again, es => es.Any(e => e.Type == "done"));
        Assert.Equal((ulong)2000, again.GetDescriptor().RowCount);
        again.Close();
        again.Free();
        Logcore.Shutdown();
    }
}

[Collection("Logcore")]
public sealed class SourceAcquireFailureIT : LogcoreIntegrationTest
{
    [Fact]
    public void Launch_timeout_is_synchronous_code_10_without_events()
    {
        var dir = Dir("logs");
        var path = LogcoreTestSupport.WriteLines(dir, "timeout.log", Batch("t", 2));
        var proxy = LogcoreTestSupport.TestProxyExe();
        var error = Assert.Throws<LogcoreException>(() =>
            PlanFile(path, LogcoreTestSupport.NewPipe("timeout"), launchFile: proxy,
                args: new[] { "--hold-ms", "5000" }, timeoutMs: 300, mode: "On"));
        Assert.Equal(LogcoreErrorCodes.ProxyLaunchTimeout, error.Code);
        Assert.Equal(300L, error.Params["timeoutMs"].GetInt64());
    }

    [Fact]
    public void Missing_launch_file_reports_proxy_path_and_os_error()
    {
        var dir = Dir("logs");
        var path = LogcoreTestSupport.WriteLines(dir, "missing-proxy.log", Batch("m", 2));
        var missing = Path.Combine(Root, "no-such-proxy.exe");
        var error = Assert.Throws<LogcoreException>(() =>
            PlanFile(path, LogcoreTestSupport.NewPipe("missing-proxy"), launchFile: missing, mode: "On"));
        Assert.Equal(LogcoreErrorCodes.ProxyLaunchFailed, error.Code);
        Assert.Equal(missing, error.Params["proxyPath"].GetString());
        Assert.True(error.Params.ContainsKey("osError"));
    }

    [Fact]
    public void Missing_source_reports_not_found_with_path()
    {
        var missing = Path.Combine(Root, "logs", "missing.log");
        var error = Assert.Throws<LogcoreException>(() =>
            PlanFile(missing, LogcoreTestSupport.NewPipe("missing-src")));
        Assert.Equal(LogcoreErrorCodes.NotFound, error.Code);
        Assert.Equal(missing, error.Params["path"].GetString());
    }

    [Fact]
    public void Post_accept_direct_open_failure_is_event_only()
    {
        var path = LogcoreTestSupport.WriteLines(Dir("logs"), "gone.log", Batch("g", 4));
        var source = PlanFile(path, LogcoreTestSupport.NewPipe("gone"));
        File.Delete(path);
        source.Commit();
        var events = LogcoreTestSupport.DrainUntil(source, es => es.Any(e => e.Type == "error"));
        var error = events.First(e => e.Type == "error");
        Assert.Equal(LogcoreErrorCodes.DirectOpenFailed, error.Code);
        Assert.Equal(path, error.Params!["path"].GetString());
        Assert.Equal("Local", source.GetDescriptor().Channel);
        source.Close();
        source.Free();
    }

    [Fact]
    public void Direct_non_text_is_rejected_without_proxy()
    {
        var dir = Dir("logs");
        var binary = Path.Combine(dir, "binary.log");
        File.WriteAllBytes(binary, Enumerable.Repeat((byte)0, 4096).ToArray());
        using var fake = FakeProxy.StartAsync(LogcoreTestSupport.NewPipe("reject")).GetAwaiter().GetResult();
        var error = Assert.Throws<LogcoreException>(() => PlanFile(binary, fake.PipeName, mode: "Auto"));
        Assert.Equal(LogcoreErrorCodes.UnsupportedFormat, error.Code);
        Assert.Equal("Binary", error.Params["format"].GetString());
        Assert.Equal(0, fake.ConnectionCount);
    }

    [Fact]
    public void Mid_spool_disconnect_is_reported_only_as_event()
    {
        var dir = Dir("logs");
        var path = LogcoreTestSupport.WriteLines(dir, "abort.log", Batch("x", 5000));
        using var fake = new FakeProxy(LogcoreTestSupport.NewPipe("abort")) { AbortAfterBytes = 64 };
        var source = PlanFile(path, fake.PipeName, mode: "On");
        source.Commit();
        var events = LogcoreTestSupport.DrainUntil(source, es => es.Any(e => e.Type == "error"));
        var error = events.First(e => e.Type == "error");
        Assert.Equal(LogcoreErrorCodes.ProxyDisconnect, error.Code);
        Assert.True(error.Params!.ContainsKey("reason"));
        source.Close();
        source.Free();
    }
}