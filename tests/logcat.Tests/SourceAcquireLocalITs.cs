using System.Diagnostics;
using logcat.Services.Logcore;
using Xunit;

namespace logcat.Tests;

[Collection("Logcore")]
public sealed class SourceAcquireCommitAsyncIT : LogcoreIntegrationTest
{
    [Fact]
    public void Commit_returns_immediately_and_descriptor_converges_after_done()
    {
        var path = LogcoreTestSupport.WriteLines(Dir("logs"), "big.log", Batch("row", 100_000));
        var source = PlanFile(path, LogcoreTestSupport.NewPipe("async"));
        var watch = Stopwatch.StartNew();
        source.Commit();
        watch.Stop();
        Assert.True(watch.ElapsedMilliseconds < 2000, $"commit 受理耗时 {watch.ElapsedMilliseconds} ms");

        var accept = source.GetDescriptor();
        Assert.Equal("Local", accept.Channel);
        Assert.Equal(path, accept.DisplayPath);
        Assert.True(accept.RowCount <= 100_000);

        var events = LogcoreTestSupport.DrainUntil(source, es => es.Any(e => e.Type == "done"));
        var final = source.GetDescriptor();
        Assert.Equal((ulong)100_000, final.RowCount);
        Assert.Equal((ulong)new FileInfo(path).Length, final.DocSize);
        Assert.Equal("Local", final.Channel);
        Assert.Equal(1, events.Count(e => e.Type == "done"));
        Assert.DoesNotContain(events, e => e.Type == "appended");
        source.Close();
        source.Free();
    }

    [Fact]
    public void Cancel_before_load_completes_turns_closed()
    {
        var path = LogcoreTestSupport.WriteLines(Dir("logs"), "huge.log", Batch("row", 400_000));
        var source = PlanFile(path, LogcoreTestSupport.NewPipe("cancel-early"));
        source.Commit();
        source.Cancel();
        var error = Assert.Throws<LogcoreException>(source.PollEvents);
        Assert.Equal(LogcoreErrorCodes.Closed, error.Code);
        source.Free();
    }
}

[Collection("Logcore")]
public sealed class SourceAcquireLocalDirectIT : LogcoreIntegrationTest
{
    [Fact]
    public void Descriptor_matches_known_content()
    {
        var lines = new[]
        {
            "--------- beginning of main",
            "11-14 12:00:01.123  100  200 I Main: hello",
            "11-14 12:00:02.456  100  200 E Worker: boom",
        };
        var path = LogcoreTestSupport.WriteLines(Dir("logs"), "known.log", lines);
        var source = PlanFile(path, LogcoreTestSupport.NewPipe("local"));
        Assert.Equal("LocalDirect", source.PlanInfo.Route);
        Assert.Equal((long?)new FileInfo(path).Length, source.PlanInfo.EstimatedSize);
        source.Commit();
        LogcoreTestSupport.DrainUntil(source, es => es.Any(e => e.Type == "done"));

        var desc = source.GetDescriptor();
        Assert.Equal((ulong)2, desc.RowCount);
        Assert.Equal((ulong)new FileInfo(path).Length, desc.DocSize);
        Assert.Equal("Utf8", desc.Encoding);
        Assert.Equal("Logcat", desc.Format);
        Assert.Equal("ReloadOnly", desc.AppendMode);
        Assert.Equal("None", desc.LeaseKind);
        Assert.Null(desc.LeaseId);
        Assert.Equal("Original", desc.TimeSemantics);
        Assert.Equal(new[] { "time", "level", "pid", "tid", "tag", "message" }, desc.Columns.Select(c => c.Id));
        Assert.NotNull(desc.Identity);
        Assert.Equal((ulong)new FileInfo(path).Length, desc.Identity!.Size);
        source.Close();
        source.Free();
    }
}

[Collection("Logcore")]
public sealed class SourceAcquireIdentityIT : LogcoreIntegrationTest
{
    [Fact]
    public void Identity_is_stable_across_reopen_and_refreshes_once_on_change()
    {
        var dir = Dir("logs");
        var path = LogcoreTestSupport.WriteLines(dir, "id.log", Batch("a", 10));

        var first = PlanFile(path, LogcoreTestSupport.NewPipe("id-1"));
        first.Commit();
        LogcoreTestSupport.DrainUntil(first, es => es.Any(e => e.Type == "done"));
        var before = first.GetDescriptor().Identity!;
        first.Close();
        first.Free();

        var second = PlanFile(path, LogcoreTestSupport.NewPipe("id-2"));
        second.Commit();
        LogcoreTestSupport.DrainUntil(second, es => es.Any(e => e.Type == "done"));
        var same = second.GetDescriptor().Identity!;
        Assert.Equal(before, same);
        second.Close();
        second.Free();

        File.AppendAllText(path, string.Join("\n", Batch("b", 5)) + "\n");
        var third = PlanFile(path, LogcoreTestSupport.NewPipe("id-3"));
        third.Commit();
        LogcoreTestSupport.DrainUntil(third, es => es.Any(e => e.Type == "done"));
        var after = third.GetDescriptor().Identity!;
        Assert.NotEqual(before.Size, after.Size);
        Assert.Equal((ulong)File.ReadAllText(path).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length,
            third.GetDescriptor().RowCount);
        third.Close();
        third.Free();
    }
}

[Collection("Logcore")]
public sealed class SourceAcquirePathFormIT : LogcoreIntegrationTest
{
    [Fact]
    public void Long_display_path_is_kept_verbatim_and_opens()
    {
        var display = Root;
        for (var i = 0; display.Length < 290; i++)
        {
            display = Path.Combine(display, $"d{i:000}");
        }
        Directory.CreateDirectory(@"\\?\" + display);
        var path = Path.Combine(display, "long.log");
        File.WriteAllText(@"\\?\" + path, "11-14 12:00:01.123  1  2 I Tag: long\n");
        Assert.True(path.Length > 260);

        var source = PlanFile(path, LogcoreTestSupport.NewPipe("longpath"));
        source.Commit();
        LogcoreTestSupport.DrainUntil(source, es => es.Any(e => e.Type == "done"));
        var desc = source.GetDescriptor();
        Assert.Equal(path, desc.DisplayPath);
        Assert.Equal(@"\\?\" + path, desc.SourcePath);
        Assert.Equal((ulong)1, desc.RowCount);
        source.Close();
        source.Free();
    }
}

[Collection("Logcore")]
public sealed class SourceAcquireSidecarIT : LogcoreIntegrationTest
{
    [Fact]
    public void Second_open_hits_cache_and_plan_probe_is_read_only()
    {
        var dir = Dir("logs");
        var cache = Path.Combine(Root, "sidecar");
        var path = LogcoreTestSupport.WriteLines(dir, "cache.log", Batch("c", 20));

        var probe = PlanFile(path, LogcoreTestSupport.NewPipe("side-probe"), sidecarDir: cache);
        Assert.False(Directory.Exists(cache), "plan 探测不得创建缓存目录");
        probe.Cancel();
        probe.Free();

        var first = PlanFile(path, LogcoreTestSupport.NewPipe("side-1"), sidecarDir: cache);
        first.Commit();
        LogcoreTestSupport.DrainUntil(first, es => es.Any(e => e.Type == "done"));
        Assert.Equal("Progressive", first.GetDescriptor().LoadMode);
        first.Close();
        first.Free();
        LogcoreTestSupport.WaitForSidecarEntries(cache, 1);
        var entries = Directory.GetFiles(cache);
        Assert.NotEmpty(entries);

        var second = PlanFile(path, LogcoreTestSupport.NewPipe("side-2"), sidecarDir: cache);
        second.Commit();
        LogcoreTestSupport.DrainUntil(second, es => es.Any(e => e.Type == "done"));
        var hit = second.GetDescriptor();
        Assert.Equal("OneShot", hit.LoadMode);
        Assert.Equal((ulong)20, hit.RowCount);
        second.Close();
        second.Free();
        Assert.NotEmpty(Directory.GetFiles(cache));
        var entry = Directory.GetFiles(cache)
            .Single(f => f.EndsWith(".lcs", StringComparison.OrdinalIgnoreCase));
        var stampBefore = File.GetLastWriteTimeUtc(entry);

        File.AppendAllText(path, "11-14 12:00:03.000  1  2 I T: extra\n");
        var third = PlanFile(path, LogcoreTestSupport.NewPipe("side-3"), sidecarDir: cache);
        third.Commit();
        LogcoreTestSupport.DrainUntil(third, es => es.Any(e => e.Type == "done"));
        var changed = third.GetDescriptor();
        Assert.Equal("Progressive", changed.LoadMode);
        Assert.Equal((ulong)21, changed.RowCount);
        third.Close();
        third.Free();
        // 身份变化原地重写同一条目；等后台落盘收尾再动缓存，避免撞上半成品。
        LogcoreTestSupport.WaitForSidecarRewrite(cache, entry, stampBefore);

        foreach (var file in Directory.GetFiles(cache)
                     .Where(f => f.EndsWith(".lcs", StringComparison.OrdinalIgnoreCase)))
        {
            File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
        }
        var corrupted = PlanFile(path, LogcoreTestSupport.NewPipe("side-4"), sidecarDir: cache);
        corrupted.Commit();
        LogcoreTestSupport.DrainUntil(corrupted, es => es.Any(e => e.Type == "done"));
        Assert.Equal((ulong)21, corrupted.GetDescriptor().RowCount);
        corrupted.Close();
        corrupted.Free();
    }

    [Fact]
    public void Version_mismatch_degrades_to_full_parse()
    {
        var dir = Dir("logs");
        var cache = Path.Combine(Root, "sidecar-version");
        var path = LogcoreTestSupport.WriteLines(dir, "version.log", Batch("v", 5));
        var first = PlanFile(path, LogcoreTestSupport.NewPipe("ver-1"), sidecarDir: cache);
        first.Commit();
        LogcoreTestSupport.DrainUntil(first, es => es.Any(e => e.Type == "done"));
        first.Close();
        first.Free();
        LogcoreTestSupport.WaitForSidecarEntries(cache, 1);

        var entry = Directory.GetFiles(cache)
            .Single(f => f.EndsWith(".lcs", StringComparison.OrdinalIgnoreCase));
        var bytes = File.ReadAllBytes(entry);
        BitConverter.GetBytes(999).CopyTo(bytes, 4);
        File.WriteAllBytes(entry, bytes);

        var second = PlanFile(path, LogcoreTestSupport.NewPipe("ver-2"), sidecarDir: cache);
        second.Commit();
        LogcoreTestSupport.DrainUntil(second, es => es.Any(e => e.Type == "done"));
        Assert.Equal("Progressive", second.GetDescriptor().LoadMode);
        Assert.Equal((ulong)5, second.GetDescriptor().RowCount);
        second.Close();
        second.Free();
    }
}

[Collection("Logcore")]
public sealed class SourceAcquireCancellationIT : LogcoreIntegrationTest
{
    [Fact]
    public void Plan_then_cancel_and_plan_then_close_release_proxy_alike()
    {
        var dir = Dir("logs");
        var path = LogcoreTestSupport.WriteLines(dir, "p.log", Batch("p", 3));
        using var fake = FakeProxy.StartAsync(LogcoreTestSupport.NewPipe("cancel")).GetAwaiter().GetResult();

        var cancelled = PlanFile(path, fake.PipeName, mode: "On");
        Assert.Equal("ProxySpool", cancelled.PlanInfo.Route);
        cancelled.Cancel();
        LogcoreTestSupport.WaitUntil(() => fake.ClosedHandles.Count > 0, because: "取消后应关闭代理句柄");
        cancelled.Free();

        var closed = PlanFile(path, fake.PipeName, mode: "On");
        closed.Close();
        LogcoreTestSupport.WaitUntil(() => fake.ClosedHandles.Count > 1, because: "close 与取消同效");
        closed.Free();
    }

    [Fact]
    public void Local_plan_close_and_cancel_both_end_closed()
    {
        var path = LogcoreTestSupport.WriteLines(Dir("logs"), "l.log", Batch("l", 2));
        var a = PlanFile(path, LogcoreTestSupport.NewPipe("cancel-a"));
        a.Cancel();
        Assert.Throws<LogcoreException>(a.GetProgress);
        a.Free();

        var b = PlanFile(path, LogcoreTestSupport.NewPipe("cancel-b"));
        b.Close();
        Assert.Throws<LogcoreException>(b.GetProgress);
        b.Free();
    }
}

[Collection("Logcore")]
public sealed class SourceAcquireConfigSnapshotIT : LogcoreIntegrationTest
{
    [Fact]
    public void Missing_required_and_relative_paths_report_field()
    {
        var missingTemp = Assert.Throws<LogcoreException>(() =>
            LogcoreSource.Plan("""{"kind":"Device","options":{}}"""));
        Assert.Equal("options.tempDir", missingTemp.Field);

        var relativeJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            kind = "File",
            path = "rel.log",
            proxy = new { pipeName = "p", launchFile = "x", launchTimeoutMs = 100 },
            options = new { tempDir = Root },
        });
        var relative = Assert.Throws<LogcoreException>(() => LogcoreSource.Plan(relativeJson));
        Assert.Equal("path", relative.Field);

        var emptyLaunchJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            kind = "File",
            path = Path.Combine(Root, "a.log"),
            proxy = new { pipeName = "p", launchFile = "", launchTimeoutMs = 100 },
            options = new { tempDir = Root },
        });
        var emptyLaunch = Assert.Throws<LogcoreException>(() => LogcoreSource.Plan(emptyLaunchJson));
        Assert.Equal("proxy.launchFile", emptyLaunch.Field);
    }

    [Fact]
    public void Defaults_apply_and_unknown_fields_are_ignored()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(
            System.Text.Json.JsonSerializer.Serialize(new
            {
                kind = "Device",
                options = new { tempDir = Root },
            }))!;
        node["futureField"] = new System.Text.Json.Nodes.JsonObject { ["x"] = 1 };
        var json = node.ToJsonString();
        var source = LogcoreSource.Plan(json);
        source.Commit();
        var desc = source.GetDescriptor();
        Assert.Equal("AdbCapture", desc.Channel);
        source.Stop();
        source.Close();
        source.Free();
    }
}

[Collection("Logcore")]
public sealed class SourceAcquireStateMatrixIT : LogcoreIntegrationTest
{
    [Fact]
    public void Plan_commit_and_free_guards_follow_d23()
    {
        var path = LogcoreTestSupport.WriteLines(Dir("logs"), "state.log", Batch("s", 4));
        var source = PlanFile(path, LogcoreTestSupport.NewPipe("state"));
        Assert.Empty(source.PollEvents());
        var progress = source.GetProgress();
        Assert.Equal("Opening", progress.Stage);
        Assert.Equal((ulong)0, progress.Value);
        var descriptorAtPlan = Assert.Throws<LogcoreException>(source.GetDescriptor);
        Assert.Equal(LogcoreErrorCodes.InvalidState, descriptorAtPlan.Code);

        source.Commit();
        var twice = Assert.Throws<LogcoreException>(source.Commit);
        Assert.Equal(LogcoreErrorCodes.InvalidState, twice.Code);
        LogcoreTestSupport.DrainUntil(source, es => es.Any(e => e.Type == "done"));

        var cancelLate = Assert.Throws<LogcoreException>(source.Cancel);
        Assert.Equal(LogcoreErrorCodes.InvalidState, cancelLate.Code);
        var freeEarly = Assert.Throws<LogcoreException>(source.Free);
        Assert.Equal(LogcoreErrorCodes.InvalidState, freeEarly.Code);
        source.Close();
        source.Free();
    }

    [Fact]
    public void Lost_plan_connection_fails_commit_and_keeps_plan_ready()
    {
        var dir = Dir("logs");
        var path = LogcoreTestSupport.WriteLines(dir, "lost.log", Batch("l", 2));
        var fake = FakeProxy.StartAsync(LogcoreTestSupport.NewPipe("lost")).GetAwaiter().GetResult();
        var source = PlanFile(path, fake.PipeName, mode: "On");
        fake.DisposeAsync().AsTask().GetAwaiter().GetResult();

        var error = Assert.Throws<LogcoreException>(source.Commit);
        Assert.Equal(LogcoreErrorCodes.ProxyDisconnect, error.Code);
        Assert.Equal("planConnectionLost", error.Params["reason"].GetString());
        var again = Assert.Throws<LogcoreException>(source.Commit);
        Assert.Equal(LogcoreErrorCodes.InvalidState, again.Code);
        source.Close();
        source.Free();
    }
}

[Collection("Logcore")]
public sealed class SourceAcquireMultiSourceIT : LogcoreIntegrationTest
{
    [Fact]
    public void Closing_one_source_does_not_affect_other()
    {
        var a = PlanDevice();
        var b = PlanDevice();
        a.Feed(Lines("11-14 12:00:01.000  1  2 I A: one"));
        b.Feed(Lines("11-14 12:00:01.000  1  2 I B: two"));
        LogcoreTestSupport.WaitUntil(() => a.GetDescriptor().RowCount == 1);
        LogcoreTestSupport.WaitUntil(() => b.GetDescriptor().RowCount == 1);

        a.Close();
        a.Free();
        b.Feed(Lines("11-14 12:00:02.000  1  2 I B: three"));
        LogcoreTestSupport.WaitUntil(() => b.GetDescriptor().RowCount == 2);
        b.Stop();
        b.Close();
        b.Free();
    }
}