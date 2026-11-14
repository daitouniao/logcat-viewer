using System.Diagnostics;
using logcat.Services.Logcore;
using Xunit;
using Xunit.Abstractions;

namespace logcat.Tests;

[Collection("Logcore")]
public sealed class SourceAcquireFeedIT : LogcoreIntegrationTest
{
    [Fact]
    public void Feed_lands_lines_in_order_and_rejects_over_limit_batch()
    {
        var source = PlanDevice();
        var path = source.GetDescriptor().SourcePath;
        var lines = Lines(
            "11-14 12:00:01.000  1  2 I A: one",
            "11-14 12:00:02.000  1  2 I A: two",
            "11-14 12:00:03.000  1  2 I A: three").ToArray();
        source.Feed(lines);
        foreach (var line in lines)
        {
            Array.Fill(line, (byte)'Z');
        }
        LogcoreTestSupport.WaitUntil(() => source.GetDescriptor().RowCount == 3);

        source.PollEvents();
        var tooMany = Enumerable.Range(0, LogcoreErrorCodes.MaxFeedLines + 1)
            .Select(_ => System.Text.Encoding.UTF8.GetBytes("x"))
            .ToArray();
        var error = Assert.Throws<LogcoreException>(() => source.Feed(tooMany));
        Assert.Equal(LogcoreErrorCodes.InvalidArg, error.Code);
        Assert.Equal("lines", error.Field);
        Thread.Sleep(200);
        Assert.Equal((ulong)3, source.GetDescriptor().RowCount);
        Assert.DoesNotContain(source.PollEvents(), e => e.Type == "appended");

        source.Stop();
        var written = File.ReadAllText(path).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[]
        {
            "11-14 12:00:01.000  1  2 I A: one",
            "11-14 12:00:02.000  1  2 I A: two",
            "11-14 12:00:03.000  1  2 I A: three",
        }, written);

        source.Close();
        source.Free();
    }
}

[Collection("Logcore")]
public sealed class SourceAcquireEventsIT : LogcoreIntegrationTest
{
    [Fact]
    public void Device_events_carry_incremental_and_cumulative_rows()
    {
        var source = PlanDevice();
        source.Feed(Lines(Batch("a", 10).ToArray()));
        var first = LogcoreTestSupport.DrainUntil(source,
            es => es.Any(e => e.Type == "appended" && e.TotalRows == 10));
        var appended = first.Where(e => e.Type == "appended").ToArray();
        Assert.NotEmpty(appended);
        Assert.All(appended, e => Assert.True(e.Rows > 0));
        Assert.Equal(10UL, appended.Aggregate(0UL, (acc, e) => acc + e.Rows!.Value));

        Assert.Empty(source.PollEvents());
        source.Feed(Lines(Batch("b", 5).ToArray()));
        var second = LogcoreTestSupport.DrainUntil(source,
            es => es.Any(e => e.Type == "appended" && e.TotalRows == 15));
        Assert.Equal(5UL, second.Where(e => e.Type == "appended").Aggregate(0UL, (acc, e) => acc + e.Rows!.Value));

        source.Stop();
        var stopped = LogcoreTestSupport.DrainUntil(source, es => es.Any(e => e.Type == "stopped"));
        Assert.Single(stopped, e => e.Type == "stopped");
        source.Close();
        source.Free();
    }

    [Fact]
    public void File_load_emits_no_appended_and_done_is_terminal()
    {
        var path = LogcoreTestSupport.WriteLines(Dir("logs"), "events.log", Batch("f", 30));
        var source = PlanFile(path, LogcoreTestSupport.NewPipe("events-file"));
        source.Commit();
        var events = LogcoreTestSupport.DrainUntil(source, es => es.Any(e => e.Type == "done"));
        Assert.DoesNotContain(events, e => e.Type == "appended");
        Assert.Single(events, e => e.Type == "done");
        Assert.DoesNotContain(events, e => e.Type == "stopped");
        Assert.Equal((ulong)30, source.GetDescriptor().RowCount);
        source.Close();
        source.Free();
    }
}

[Collection("Logcore")]
public sealed class SourceAcquireStopDrainIT : LogcoreIntegrationTest
{
    [Fact]
    public void Stop_drains_to_completion_and_is_idempotent()
    {
        var source = PlanDevice();
        for (var i = 0; i < 4; i++)
        {
            source.Feed(Lines(Batch("d", 50_000).ToArray()));
        }
        var stop = Task.Run(source.Stop);

        var sawStopped = false;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                source.Feed(Lines("11-14 12:00:09.999  1  2 I Late: x"));
            }
            catch (LogcoreException e) when (e.Code == LogcoreErrorCodes.Stopped)
            {
                sawStopped = true;
                break;
            }
            if (stop.IsCompleted)
            {
                break;
            }
        }
        stop.GetAwaiter().GetResult();
        if (!sawStopped)
        {
            var error = Assert.Throws<LogcoreException>(() =>
                source.Feed(Lines("11-14 12:00:09.999  1  2 I Late: y")));
            Assert.Equal(LogcoreErrorCodes.Stopped, error.Code);
        }

        source.Stop();
        var progress = source.GetProgress();
        Assert.Equal("Draining", progress.Stage);
        Assert.NotNull(progress.Total);
        Assert.Equal(progress.Total, progress.Value);

        var events = source.PollEvents();
        var totalRows = events.Where(e => e.Type == "appended").LastOrDefault()?.TotalRows;
        var descriptor = source.GetDescriptor();
        if (totalRows.HasValue)
        {
            Assert.Equal(totalRows, descriptor.RowCount);
        }
        Assert.True(File.Exists(descriptor.SourcePath));
        Assert.True(File.Exists(descriptor.SourcePath + ".lock"), "stop 保留锁标记");
        source.Close();
        source.Free();
    }
}

[Collection("Logcore")]
public sealed class SourceAcquireCloseDiscardIT : LogcoreIntegrationTest
{
    [Fact]
    public void Close_discards_queue_deletes_artifacts_and_locks_out_reads()
    {
        var source = PlanDevice();
        var path = source.GetDescriptor().SourcePath;
        source.Feed(Lines(Batch("c", 1000).ToArray()));
        source.Close();
        Assert.False(File.Exists(path), "close 删除采集文件");
        Assert.False(File.Exists(path + ".lock"), "close 释放锁标记");
        var error = Assert.Throws<LogcoreException>(source.PollEvents);
        Assert.Equal(LogcoreErrorCodes.Closed, error.Code);
        var feed = Assert.Throws<LogcoreException>(() => source.Feed(Lines("x")));
        Assert.Equal(LogcoreErrorCodes.Closed, feed.Code);
        var progress = Assert.Throws<LogcoreException>(source.GetProgress);
        Assert.Equal(LogcoreErrorCodes.Closed, progress.Code);
        source.Free();
    }

    [Fact]
    public void Close_during_drain_aborts_and_blocked_stop_returns_closed()
    {
        var source = PlanDevice();
        for (var i = 0; i < 4; i++)
        {
            source.Feed(Lines(Batch("q", 50_000).ToArray()));
        }
        var stopping = Task.Run(() =>
        {
            try
            {
                source.Stop();
                return null;
            }
            catch (LogcoreException e)
            {
                return e;
            }
        });
        LogcoreTestSupport.WaitUntil(() =>
        {
            try
            {
                return source.GetProgress().Stage == "Draining";
            }
            catch (LogcoreException)
            {
                return false;
            }
        }, TimeSpan.FromSeconds(10), "未进入排空阶段");

        source.Close();
        var error = stopping.GetAwaiter().GetResult();
        Assert.NotNull(error);
        Assert.Equal(LogcoreErrorCodes.Closed, error!.Code);
        var poll = Assert.Throws<LogcoreException>(source.PollEvents);
        Assert.Equal(LogcoreErrorCodes.Closed, poll.Code);
        source.Free();
    }

    [Fact]
    public void Close_is_best_effort_when_capture_file_is_held()
    {
        var source = PlanDevice();
        source.Feed(Lines(Batch("h", 50).ToArray()));
        LogcoreTestSupport.WaitUntil(() => source.GetDescriptor().RowCount == 50);
        source.Stop();
        var path = source.GetDescriptor().SourcePath;

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            source.Close();
            Assert.True(File.Exists(path), "外部占用时残留交启动维护");
            Assert.False(File.Exists(path + ".lock"), "标记仍应解除");
        }
        source.Free();
    }
}

[Collection("Logcore")]
public sealed class SourceAcquireCloseConcurrencyIT : LogcoreIntegrationTest
{
    [Fact]
    public void Progress_polling_during_close_is_safe_and_then_closed()
    {
        var source = PlanDevice();
        source.Feed(Lines(Batch("z", 20_000).ToArray()));
        var closing = Task.Run(source.Close);
        while (!closing.IsCompleted)
        {
            try
            {
                source.GetProgress();
            }
            catch (LogcoreException e)
            {
                Assert.Equal(LogcoreErrorCodes.Closed, e.Code);
                break;
            }
        }
        closing.GetAwaiter().GetResult();
        var error = Assert.Throws<LogcoreException>(source.GetProgress);
        Assert.Equal(LogcoreErrorCodes.Closed, error.Code);
        source.Free();
    }
}

[Collection("Logcore")]
public sealed class SourceAcquireStartupMaintainIT : LogcoreIntegrationTest
{
    [Fact]
    public void Maintain_cleans_cache_and_temp_without_touching_unmatched()
    {
        var cache = Dir("cache");
        var temp = Dir("temp");
        File.WriteAllBytes(Path.Combine(cache, "bad_0123456789abcdef.lcs"), new byte[] { 1, 2, 3 });

        var spool = Path.Combine(temp, "logcat_spool_000001");
        Directory.CreateDirectory(spool);
        File.WriteAllText(Path.Combine(spool, "part.log"), "leftover");
        File.WriteAllText(spool + ".lock", "");

        var live = Path.Combine(temp, "logcat_live_20260101_000000_000002.log");
        File.WriteAllText(live, "capture");
        var orphanMarker = Path.Combine(temp, "logcat_live_20260101_000000_000003.log.lock");
        File.WriteAllText(orphanMarker, "");
        var unmatched = Path.Combine(temp, "keep.txt");
        File.WriteAllText(unmatched, "keep");

        var result = Logcore.StartupMaintain(new LogcoreMaintainRequest { CacheDir = cache, TempDir = temp });
        Assert.False(result.IsPartial, "本次全部可删，应为 Ok");
        Assert.Equal((ulong)1, result.RemovedCorrupt);
        Assert.True(result.RemovedOrphans >= 1);
        Assert.Equal((ulong)1, result.RemovedTempWrites);
        Assert.False(Directory.Exists(spool));
        Assert.False(File.Exists(spool + ".lock"));
        Assert.False(File.Exists(live));
        Assert.False(File.Exists(orphanMarker));
        Assert.True(File.Exists(unmatched), "不匹配命名的条目不得触碰");

        var again = Logcore.StartupMaintain(new LogcoreMaintainRequest { CacheDir = cache, TempDir = temp });
        Assert.False(again.IsPartial);
        Assert.Equal((ulong)0, again.RemovedCorrupt);
        Assert.Equal((ulong)0, again.RemovedOrphans);
        Assert.Equal((ulong)0, again.RemovedTempWrites);
    }

    [Fact]
    public void Held_marker_skips_artifact_and_missing_dirs_are_empty()
    {
        var cache = Dir("cache2");
        var temp = Dir("temp2");
        var spool = Path.Combine(temp, "logcat_spool_000010");
        Directory.CreateDirectory(spool);
        File.WriteAllText(Path.Combine(spool, "part.log"), "data");
        var marker = spool + ".lock";
        File.WriteAllText(marker, "");

        using (new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var held = Logcore.StartupMaintain(new LogcoreMaintainRequest { CacheDir = cache, TempDir = temp });
            Assert.True(held.SkippedInUse >= 1);
            Assert.True(Directory.Exists(spool));
        }
        var after = Logcore.StartupMaintain(new LogcoreMaintainRequest { CacheDir = cache, TempDir = temp });
        Assert.False(Directory.Exists(spool));

        var missing = Logcore.StartupMaintain(new LogcoreMaintainRequest
        {
            CacheDir = Path.Combine(Root, "no-cache"),
            TempDir = Path.Combine(Root, "no-temp"),
        });
        Assert.Equal("Ok", missing.Status);
    }
}

[Collection("Logcore")]
public sealed class SourceAcquireFeedThroughputIT : LogcoreIntegrationTest
{
    readonly ITestOutputHelper _output;

    public SourceAcquireFeedThroughputIT(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Hundred_thousand_lines_are_not_dropped()
    {
        var source = PlanDevice();
        var watch = Stopwatch.StartNew();
        const int total = 100_000;
        const int batchSize = 10_000;
        for (var i = 0; i < total; i += batchSize)
        {
            source.Feed(Lines(Batch($"p{i}-", batchSize).ToArray()));
        }
        LogcoreTestSupport.WaitUntil(() => source.GetDescriptor().RowCount == total,
            TimeSpan.FromSeconds(30), "推入 10 万行未在 30 s 内收敛");
        watch.Stop();
        _output.WriteLine($"feed {total} lines in {watch.ElapsedMilliseconds} ms " +
                          $"({total / Math.Max(1, watch.ElapsedMilliseconds) * 1000} lines/s, 含 D8 深拷贝)");
        Assert.Equal((ulong)total, source.GetDescriptor().RowCount);
        source.Stop();
        var progress = source.GetProgress();
        Assert.Equal(progress.Total, progress.Value);
        source.Close();
        source.Free();
    }
}