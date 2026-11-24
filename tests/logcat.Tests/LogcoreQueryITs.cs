using logcat.Services.Logcore;
using Xunit;

namespace logcat.Tests;

[Collection("Logcore")]
public sealed class LogcoreQueryIT : LogcoreIntegrationTest
{
    LogcoreSource Loaded(string tag, IEnumerable<string> lines)
    {
        var path = LogcoreTestSupport.WriteLines(Dir("logs"), tag + ".log", lines);
        var source = PlanFile(path, LogcoreTestSupport.NewPipe(tag));
        source.Commit();
        LogcoreTestSupport.DrainUntil(source, es => es.Any(e => e.Type == "done"));
        return source;
    }

    [Fact]
    public void Query_returns_filtered_rows()
    {
        var source = Loaded("query-filter", PageFixture.Lines());
        using var query = source.OpenQuery("""{"pids":{"values":[100]}}""");
        Assert.Equal((ulong)4, query.RowCount());
        var rows = query.GetPage(0, 10);
        Assert.Equal(new uint[] { 0, 1, 2, 3 }, rows.Select(r => r.DocRow));
        Assert.All(rows, r => Assert.Equal(100, r.Pid));
        source.Close();
        source.Free();
    }

    [Fact]
    public void Query_page_starts_at_real_doc_row()
    {
        var source = Loaded("query-page-start", PageFixture.Lines());
        using var query = source.OpenQuery("""{"pids":{"values":[100]}}""");
        var rows = query.GetPage(2, 2);
        Assert.Equal(new uint[] { 2, 3 }, rows.Select(r => r.DocRow));
        Assert.Equal("11-14 12:00:02.001", rows[0].Time);
        source.Close();
        source.Free();
    }

    [Fact]
    public void Query_page_backward_returns_previous_rows()
    {
        var source = Loaded("query-backward", PageFixture.Lines());
        using var query = source.OpenQuery("""{"pids":{"values":[100]}}""");
        Assert.Equal(new uint[] { 2, 3 }, query.GetPageBackward(4, 2).Select(r => r.DocRow));
        Assert.Equal(new uint[] { 0, 1 }, query.GetPageBackward(2, 2).Select(r => r.DocRow));
        source.Close();
        source.Free();
    }

    [Fact]
    public void Query_jump_to_tail_renders_tail_rows()
    {
        var lines = PageFixture.Lines();
        var source = Loaded("query-tail", lines);
        using var query = source.OpenQuery("{}");
        var tail = query.GetPageBackward(lines.Length, 2);
        Assert.Equal(new uint[] { 4, 5 }, tail.Select(r => r.DocRow));
        Assert.Equal(lines[^1], tail[^1].Message);
        source.Close();
        source.Free();
    }

    [Fact]
    public void Query_resolve_returns_nearest_doc_row()
    {
        var source = Loaded("query-resolve", PageFixture.Lines());
        using var query = source.OpenQuery("""{"pids":{"values":[300]}}""");
        Assert.Equal((ulong)1, query.RowCount());
        Assert.Equal(4L, query.Resolve(0));
        Assert.Equal(4L, query.Resolve(-1));
        Assert.Equal(4L, query.Resolve(100));
        source.Close();
        source.Free();
    }

    [Fact]
    public void Query_survives_source_close()
    {
        var source = Loaded("query-after-close", PageFixture.Lines());
        var query = source.OpenQuery("""{"pids":{"values":[100]}}""");
        source.Close();
        Assert.Equal((ulong)4, query.RowCount());
        Assert.Equal(new uint[] { 0, 1 }, query.GetPage(0, 2).Select(r => r.DocRow));
        query.Free();
        source.Free();
    }

    [Fact]
    public void Query_invalid_filter_reports_field()
    {
        var source = Loaded("query-bad-filter", PageFixture.Lines());
        var error = Assert.Throws<LogcoreException>(
            () => source.OpenQuery("""{"levels":[0]}"""));
        Assert.Equal(LogcoreErrorCodes.InvalidArg, error.Code);
        Assert.Equal("levels", error.Field);
        source.Close();
        source.Free();
    }

    [Fact]
    public void Query_page_tail_and_empty()
    {
        var lines = PageFixture.Lines();
        var source = Loaded("query-empty", lines);
        using var query = source.OpenQuery("{}");
        Assert.Empty(query.GetPage(lines.Length, 10));
        Assert.Empty(query.GetPage(0, 0));
        Assert.Empty(query.GetPageBackward(0, 10));
        var badStart = Assert.Throws<LogcoreException>(() => query.GetPage(-1, 1));
        Assert.Equal("start", badStart.Field);
        var badCount = Assert.Throws<LogcoreException>(() => query.GetPageBackward(lines.Length, -1));
        Assert.Equal("count", badCount.Field);
        source.Close();
        source.Free();
    }
}