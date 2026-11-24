using System.Text;
using logcat.Services.Logcore;
using Xunit;

namespace logcat.Tests;

internal static class PageFixture
{
    internal static string[] Lines() => new[]
    {
        "11-14 12:00:01.123  100  200 I Main: hello",
        "11-14 12:00:02.000  100  200 E AndroidRuntime: FATAL EXCEPTION: main",
        "11-14 12:00:02.001  100  200 E AndroidRuntime: Process: com.example.app, PID: 100",
        "11-14 12:00:02.002  100  200 E AndroidRuntime: java.lang.RuntimeException: boom",
        "11-14 12:00:03.000  300  400 F libc: signal 11 (SIGSEGV), code 1",
        "garbage line without timestamp",
    };

    internal static string[] Snapshot(LogcoreSource source) =>
        source.GetPage(0, 100).Select(r =>
            string.Join('|',
                r.DocRow,
                r.Level,
                r.Pid,
                r.Tid,
                r.Tag,
                r.Time,
                r.Message,
                Convert.ToHexString(r.Raw),
                r.Crash is null ? "<none>" : $"{r.Crash.Kind}:{r.Crash.ExceptionType}:{r.Crash.Package}"))
            .ToArray();
}

[Collection("Logcore")]
public sealed class LogcorePageContentIT : LogcoreIntegrationTest
{
    [Fact]
    public void Page_returns_slices_and_crash_fields()
    {
        var lines = PageFixture.Lines();
        var path = LogcoreTestSupport.WriteLines(Dir("logs"), "content.log", lines);
        var source = PlanFile(path, LogcoreTestSupport.NewPipe("page-content"));
        source.Commit();
        LogcoreTestSupport.DrainUntil(source, es => es.Any(e => e.Type == "done"));

        var rows = source.GetPage(0, 10);
        Assert.Equal(lines.Length, rows.Count);
        Assert.Equal(Enumerable.Range(0, lines.Length).Select(i => (uint)i), rows.Select(r => r.DocRow));
        Assert.Equal((ulong)lines.Length, source.RowCount());

        var first = rows[0];
        Assert.Equal((byte)3, first.Level);
        Assert.Equal(100, first.Pid);
        Assert.Equal(200, first.Tid);
        Assert.Equal("Main", first.Tag);
        Assert.Equal("11-14 12:00:01.123", first.Time);
        Assert.Equal("hello", first.Message);
        Assert.Equal(lines[0], Encoding.UTF8.GetString(first.Raw));
        Assert.Null(first.Crash);

        var crash = rows[1];
        Assert.NotNull(crash.Crash);
        Assert.Equal((byte)0, crash.Crash!.Kind);
        Assert.Equal("java.lang.RuntimeException", crash.Crash.ExceptionType);
        Assert.Equal("com.example.app", crash.Crash.Package);
        Assert.Equal("FATAL EXCEPTION: main", crash.Message);
        Assert.Equal("AndroidRuntime", crash.Tag);
        Assert.Null(rows[2].Crash);
        Assert.Null(rows[3].Crash);

        source.Close();
        source.Free();
    }

    [Fact]
    public void Page_tail_returns_short_page()
    {
        var path = LogcoreTestSupport.WriteLines(Dir("logs"), "tail.log", PageFixture.Lines());
        var source = PlanFile(path, LogcoreTestSupport.NewPipe("page-tail"));
        source.Commit();
        LogcoreTestSupport.DrainUntil(source, es => es.Any(e => e.Type == "done"));

        var tail = source.GetPage(4, 10);
        Assert.Equal(new uint[] { 4, 5 }, tail.Select(r => r.DocRow));
        Assert.Equal("11-14 12:00:03.000", tail[0].Time);
        source.Close();
        source.Free();
    }

    [Fact]
    public void Page_rows_have_unknown_sentinels()
    {
        var lines = PageFixture.Lines();
        var path = LogcoreTestSupport.WriteLines(Dir("logs"), "unknown.log", lines);
        var source = PlanFile(path, LogcoreTestSupport.NewPipe("page-unknown"));
        source.Commit();
        LogcoreTestSupport.DrainUntil(source, es => es.Any(e => e.Type == "done"));

        var row = source.GetPage(lines.Length - 1, 1).Single();
        Assert.Equal(0, row.Level);
        Assert.Equal(-1, row.Pid);
        Assert.Equal(-1, row.Tid);
        Assert.Null(row.Tag);
        Assert.Equal("", row.Time);
        Assert.Equal(lines[^1], row.Message);
        Assert.Equal(lines[^1], Encoding.UTF8.GetString(row.Raw));
        Assert.Null(row.Crash);
        source.Close();
        source.Free();
    }

    [Fact]
    public void Page_empty_page_returns_empty_array()
    {
        var path = LogcoreTestSupport.WriteLines(Dir("logs"), "empty.log", PageFixture.Lines());
        var source = PlanFile(path, LogcoreTestSupport.NewPipe("page-empty"));
        source.Commit();
        LogcoreTestSupport.DrainUntil(source, es => es.Any(e => e.Type == "done"));

        Assert.Empty(source.GetPage(PageFixture.Lines().Length, 10));
        Assert.Empty(source.GetPage(0, 0));
        Assert.Empty(source.GetPage(1_000_000, 1));
        source.Close();
        source.Free();
    }
}

[Collection("Logcore")]
public sealed class LogcorePageGuardsIT : LogcoreIntegrationTest
{
    [Fact]
    public void RowCount_before_loaded_reports_invalid_state()
    {
        var path = LogcoreTestSupport.WriteLines(Dir("logs"), "guard.log", PageFixture.Lines());
        var source = PlanFile(path, LogcoreTestSupport.NewPipe("guard-rowcount"));

        var error = Assert.Throws<LogcoreException>(() => source.RowCount());
        Assert.Equal(LogcoreErrorCodes.InvalidState, error.Code);
        source.Cancel();
        source.Free();
    }

    [Fact]
    public void Page_before_loaded_reports_invalid_state()
    {
        var path = LogcoreTestSupport.WriteLines(Dir("logs"), "guard-page.log", PageFixture.Lines());
        var source = PlanFile(path, LogcoreTestSupport.NewPipe("guard-page"));

        var error = Assert.Throws<LogcoreException>(() => source.GetPage(0, 1));
        Assert.Equal(LogcoreErrorCodes.InvalidState, error.Code);
        source.Cancel();
        source.Free();
    }

    [Fact]
    public void Page_rejects_count_over_max()
    {
        var path = LogcoreTestSupport.WriteLines(Dir("logs"), "max.log", PageFixture.Lines());
        var source = PlanFile(path, LogcoreTestSupport.NewPipe("guard-max"));
        source.Commit();
        LogcoreTestSupport.DrainUntil(source, es => es.Any(e => e.Type == "done"));

        var overMax = Assert.Throws<LogcoreException>(
            () => source.GetPage(0, LogcoreErrorCodes.MaxPageRows + 1));
        Assert.Equal(LogcoreErrorCodes.InvalidArg, overMax.Code);
        Assert.Equal("count", overMax.Field);

        var negativeStart = Assert.Throws<LogcoreException>(() => source.GetPage(-1, 1));
        Assert.Equal("start", negativeStart.Field);

        var negativeCount = Assert.Throws<LogcoreException>(() => source.GetPage(0, -1));
        Assert.Equal("count", negativeCount.Field);

        Assert.Equal((ulong)PageFixture.Lines().Length, source.RowCount());
        source.Close();
        source.Free();
    }

    [Fact]
    public void Page_after_close_reports_closed()
    {
        var path = LogcoreTestSupport.WriteLines(Dir("logs"), "closed.log", PageFixture.Lines());
        var source = PlanFile(path, LogcoreTestSupport.NewPipe("guard-closed"));
        source.Commit();
        LogcoreTestSupport.DrainUntil(source, es => es.Any(e => e.Type == "done"));
        source.Close();

        var error = Assert.Throws<LogcoreException>(() => source.GetPage(0, 1));
        Assert.Equal(LogcoreErrorCodes.Closed, error.Code);
        source.Free();
    }
}

[Collection("Logcore")]
public sealed class LogcorePagePathsIT : LogcoreIntegrationTest
{
    [Fact]
    public void Page_after_truncation_reports_io_error_and_state_unchanged()
    {
        var path = LogcoreTestSupport.WriteLines(Dir("logs"), "truncate.log", PageFixture.Lines());
        var source = PlanFile(path, LogcoreTestSupport.NewPipe("path-truncate"));
        source.Commit();
        LogcoreTestSupport.DrainUntil(source, es => es.Any(e => e.Type == "done"));

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.SetLength(0);
        }

        var error = Assert.Throws<LogcoreException>(() => source.GetPage(0, 1));
        Assert.Equal(LogcoreErrorCodes.IoError, error.Code);
        Assert.True(error.Params.ContainsKey("path"), "IO_ERROR 应带 path 参数");
        Assert.Equal((ulong)PageFixture.Lines().Length, source.RowCount());
        source.Close();
        source.Free();
    }

    [Fact]
    public void Page_after_cache_hit_matches_full_parse()
    {
        var cache = Path.Combine(Root, "sidecar");
        var path = LogcoreTestSupport.WriteLines(Dir("logs"), "cache.log", PageFixture.Lines());

        var first = PlanFile(path, LogcoreTestSupport.NewPipe("path-cache-1"), sidecarDir: cache);
        first.Commit();
        LogcoreTestSupport.DrainUntil(first, es => es.Any(e => e.Type == "done"));
        var expected = PageFixture.Snapshot(first);
        first.Close();
        first.Free();
        // 缓存落盘是后台线程（Done 事件已发即返回，不等待），必须等工件落盘后再开第二次会话，
        // 否则 CI（2 核慢盘）上第二次 open 探不到缓存降级 Progressive，本地则常因先落盘而侥幸通过。
        LogcoreTestSupport.WaitForSidecarEntries(cache, 1);

        var second = PlanFile(path, LogcoreTestSupport.NewPipe("path-cache-2"), sidecarDir: cache);
        second.Commit();
        LogcoreTestSupport.DrainUntil(second, es => es.Any(e => e.Type == "done"));
        Assert.Equal("OneShot", second.GetDescriptor().LoadMode);
        Assert.Equal(expected, PageFixture.Snapshot(second));
        second.Close();
        second.Free();
    }

    [Fact]
    public void Page_after_source_renamed_still_returns_loaded_content()
    {
        var path = LogcoreTestSupport.WriteLines(Dir("logs"), "rename.log", PageFixture.Lines());
        var source = PlanFile(path, LogcoreTestSupport.NewPipe("path-rename"));
        source.Commit();
        LogcoreTestSupport.DrainUntil(source, es => es.Any(e => e.Type == "done"));
        var expected = PageFixture.Snapshot(source);

        File.Move(path, path + ".moved");
        Assert.Equal(expected, PageFixture.Snapshot(source));
        source.Close();
        source.Free();
    }

    [Fact]
    public void Page_repeated_requests_stable()
    {
        var path = LogcoreTestSupport.WriteLines(Dir("logs"), "repeat.log", Batch("row", 100));
        var source = PlanFile(path, LogcoreTestSupport.NewPipe("path-repeat"));
        source.Commit();
        LogcoreTestSupport.DrainUntil(source, es => es.Any(e => e.Type == "done"));

        var first = PageFixture.Snapshot(source);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(first, PageFixture.Snapshot(source));
        }

        var all = new List<LogcorePageRow>();
        for (long start = 0; start < 100; start += 7)
        {
            all.AddRange(source.GetPage(start, 7));
        }
        Assert.Equal(100, all.Count);
        Assert.Equal(Enumerable.Range(0, 100).Select(i => (uint)i), all.Select(r => r.DocRow));
        Assert.Equal(Enumerable.Range(0, 100).Select(i => $"row{i}"), all.Select(r => r.Message));
        source.Close();
        source.Free();
    }
}