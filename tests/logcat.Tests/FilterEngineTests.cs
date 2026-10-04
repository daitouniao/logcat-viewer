using System.Text;
using logcat.Models;
using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// FilterEngine：条件切分、过滤主流程（级别/tag/msg/pid/tid/分钟/标记）、
/// 实时增量过滤、导出行，以及「防误滤」不变式（不应被过滤的行必须保留）。
/// </summary>
public class FilterEngineTests
{
    /// <summary>
    /// 6 行样本。行号 → 内容：
    /// 0  pid1 tid2 I ActivityManager "started"        18:00 分钟 0
    /// 1  pid1 tid3 E ActivityManager "failed to start" 18:00 分钟 0
    /// 2  pid2 tid4 D NetworkService  "connecting"      18:00 分钟 0
    /// 3  pid2 tid5 W NetworkService  "timeout"         18:00 分钟 0
    /// 4  pid3 tid6 V DebugTag        "verbose detail"  18:00 分钟 0
    /// 5  pid3 tid7 I DebugTag        "another"         18:01 分钟 1
    /// </summary>
    const string Data =
        "09-23 18:00:00.000  1  2 I ActivityManager: started\n" +
        "09-23 18:00:01.000  1  3 E ActivityManager: failed to start\n" +
        "09-23 18:00:02.000  2  4 D NetworkService: connecting\n" +
        "09-23 18:00:03.000  2  5 W NetworkService: timeout\n" +
        "09-23 18:00:04.000  3  6 V DebugTag: verbose detail\n" +
        "09-23 18:01:00.000  3  7 I DebugTag: another\n";

    static int[] Apply(LogDocument doc, FilterSpec spec, HashSet<int>? marked = null,
                       IProgress<(double, string)>? progress = null, CancellationToken ct = default)
        => FilterEngine.ApplyFilter(doc, spec, marked, progress, ct);

    // ── SplitTerms ──

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void SplitTerms_空白输入返回空列表(string text)
    {
        Assert.Empty(FilterEngine.SplitTerms(text, "and"));
    }

    [Fact]
    public void SplitTerms_按空白逗号分号换行切分()
    {
        Assert.Equal(new[] { "a", "b", "c", "d", "e" },
            FilterEngine.SplitTerms("a b,c;d\ne", "and"));
        Assert.Equal(new[] { "a", "b" }, FilterEngine.SplitTerms("a   b", "or"));
    }

    [Fact]
    public void SplitTerms_引号内容作为一个词且保留内部空格()
    {
        Assert.Equal(new[] { "foo bar", "baz" },
            FilterEngine.SplitTerms("\"foo bar\" baz", "and"));
        Assert.Equal(new[] { "foo bar", "baz" },
            FilterEngine.SplitTerms("'foo bar' baz", "and"));
    }

    [Fact]
    public void SplitTerms_引号内的分隔符不被切分()
    {
        Assert.Equal(new[] { "a,b", "c" }, FilterEngine.SplitTerms("\"a,b\" c", "and"));
        Assert.Equal(new[] { "a b" }, FilterEngine.SplitTerms("\"a b\"", "or"));
    }

    [Fact]
    public void SplitTerms_未闭合引号时把剩余内容当作一个词()
    {
        Assert.Equal(new[] { "abc" }, FilterEngine.SplitTerms("\"abc", "and"));
        Assert.Equal(new[] { "abc def" }, FilterEngine.SplitTerms("\"abc def", "and"));
    }

    [Fact]
    public void SplitTerms_结果会去掉首尾空白()
    {
        Assert.Equal(new[] { "a", "b" }, FilterEngine.SplitTerms("  a  b  ", "and"));
    }

    // ── ParseMinutes ──

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseMinutes_空白输入返回空数组(string text)
    {
        Assert.Empty(FilterEngine.ParseMinutes(text));
    }

    [Fact]
    public void ParseMinutes_逗号空白分号皆可作分隔()
    {
        Assert.Equal(new[] { 0, 59, 30 }, FilterEngine.ParseMinutes("0,59; 30"));
    }

    [Theory]
    [InlineData("60")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("1,a")]
    public void ParseMinutes_非法输入抛异常(string text)
    {
        Assert.Throws<ArgumentException>(() => FilterEngine.ParseMinutes(text));
    }

    [Fact]
    public void ParseMinutes_异常消息带上非法片段()
    {
        var ex = Assert.Throws<ArgumentException>(() => FilterEngine.ParseMinutes("61"));
        Assert.Contains("61", ex.Message);
    }

    // ── ParseInts ──

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void ParseInts_空白输入返回空数组(string text)
    {
        Assert.Empty(FilterEngine.ParseInts(text, "pid"));
    }

    [Fact]
    public void ParseInts_多种分隔符且允许负数()
    {
        Assert.Equal(new[] { 1, 2, 3, 4 }, FilterEngine.ParseInts("1,2 3;4", "pid"));
        Assert.Equal(new[] { -5 }, FilterEngine.ParseInts("-5", "pid"));
    }

    [Fact]
    public void ParseInts_非法输入抛异常且消息带字段名()
    {
        var ex = Assert.Throws<ArgumentException>(() => FilterEngine.ParseInts("1,x", "tid"));
        Assert.Contains("tid", ex.Message);
    }

    // ── ApplyFilter：无过滤 / 各级别 ──

    [Fact]
    public void 空条件返回全部行()
    {
        using var tmp = new TempLogFile(Data);
        using var doc = LogDocument.Build(tmp.Path);

        Assert.True(new FilterSpec().IsEmpty());
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, Apply(doc, new FilterSpec()));
    }

    [Fact]
    public void 空文档返回空数组()
    {
        using var tmp = new TempLogFile("");
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Empty(Apply(doc, new FilterSpec()));
    }

    [Fact]
    public void 按单个级别过滤()
    {
        using var tmp = new TempLogFile(Data);
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(new[] { 1 }, Apply(doc, new FilterSpec { Levels = new[] { LogParser.LVL_E } }));
        Assert.Equal(new[] { 4 }, Apply(doc, new FilterSpec { Levels = new[] { LogParser.LVL_V } }));
    }

    [Fact]
    public void 按多个级别过滤时按行号升序返回()
    {
        using var tmp = new TempLogFile(Data);
        using var doc = LogDocument.Build(tmp.Path);

        var spec = new FilterSpec { Levels = new[] { LogParser.LVL_W, LogParser.LVL_E } };
        Assert.Equal(new[] { 1, 3 }, Apply(doc, spec));
    }

    [Fact]
    public void 传全集级别等同于不过滤()
    {
        using var tmp = new TempLogFile(Data);
        using var doc = LogDocument.Build(tmp.Path);

        var spec = new FilterSpec { Levels = new[] { 7, 1, 5, 3, 2, 6, 4 } };   // 乱序的全集
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, Apply(doc, spec));
    }

    // ── ApplyFilter：tag ──

    [Fact]
    public void 按tag过滤()
    {
        using var tmp = new TempLogFile(Data);
        using var doc = LogDocument.Build(tmp.Path);

        var spec = new FilterSpec { Tags = new[] { "ActivityManager" } };
        Assert.Equal(new[] { 0, 1 }, Apply(doc, spec));
    }

    [Fact]
    public void tag排除()
    {
        using var tmp = new TempLogFile(Data);
        using var doc = LogDocument.Build(tmp.Path);

        var spec = new FilterSpec { Tags = new[] { "ActivityManager" }, TagExclude = true };
        Assert.Equal(new[] { 2, 3, 4, 5 }, Apply(doc, spec));
    }

    [Fact]
    public void tag默认忽略大小写_开启区分大小写后不匹配()
    {
        using var tmp = new TempLogFile(Data);
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(new[] { 0, 1 }, Apply(doc, new FilterSpec { Tags = new[] { "activitymanager" } }));
        Assert.Empty(Apply(doc, new FilterSpec { Tags = new[] { "activitymanager" }, TagCase = true }));
    }

    [Fact]
    public void tag多值时and与or语义()
    {
        using var tmp = new TempLogFile(Data);
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(new[] { 2, 3 },
            Apply(doc, new FilterSpec { Tags = new[] { "Network", "Service" }, TagOp = "and" }));
        Assert.Equal(new[] { 2, 3, 4, 5 },
            Apply(doc, new FilterSpec { Tags = new[] { "Network", "Debug" }, TagOp = "or" }));
    }

    // ── ApplyFilter：message ──

    [Fact]
    public void 按message子串过滤()
    {
        using var tmp = new TempLogFile(Data);
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(new[] { 0 }, Apply(doc, new FilterSpec { Msg = new[] { "started" } }));
        Assert.Equal(new[] { 0, 1 }, Apply(doc, new FilterSpec { Msg = new[] { "start" } }));
    }

    [Fact]
    public void message默认忽略大小写()
    {
        using var tmp = new TempLogFile(Data);
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(new[] { 4 }, Apply(doc, new FilterSpec { Msg = new[] { "VERBOSE" } }));
        Assert.Empty(Apply(doc, new FilterSpec { Msg = new[] { "VERBOSE" }, MsgCase = true }));
    }

    [Fact]
    public void message多值and与or语义()
    {
        using var tmp = new TempLogFile(Data);
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(new[] { 1 },
            Apply(doc, new FilterSpec { Msg = new[] { "start", "failed" }, MsgOp = "and" }));
        Assert.Equal(new[] { 0, 1, 3 },
            Apply(doc, new FilterSpec { Msg = new[] { "start", "timeout" }, MsgOp = "or" }));
    }

    [Fact]
    public void message排除()
    {
        using var tmp = new TempLogFile(Data);
        using var doc = LogDocument.Build(tmp.Path);

        var spec = new FilterSpec { Msg = new[] { "start" }, MsgExclude = true };
        Assert.Equal(new[] { 2, 3, 4, 5 }, Apply(doc, spec));
    }

    // ── ApplyFilter：pid / tid / 分钟 / 标记 ──

    [Fact]
    public void 按pid过滤与排除()
    {
        using var tmp = new TempLogFile(Data);
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(new[] { 0, 1 }, Apply(doc, new FilterSpec { Pids = new[] { 1 } }));
        Assert.Equal(new[] { 2, 3, 4, 5 },
            Apply(doc, new FilterSpec { Pids = new[] { 1 }, PidExclude = true }));
    }

    [Fact]
    public void 按tid过滤与排除()
    {
        using var tmp = new TempLogFile(Data);
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(new[] { 1 }, Apply(doc, new FilterSpec { Tids = new[] { 3 } }));
        Assert.Equal(new[] { 0, 2, 3, 4, 5 },
            Apply(doc, new FilterSpec { Tids = new[] { 3 }, TidExclude = true }));
    }

    [Fact]
    public void 按分钟过滤()
    {
        using var tmp = new TempLogFile(Data);
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, Apply(doc, new FilterSpec { Minutes = new[] { 0 } }));
        Assert.Equal(new[] { 5 }, Apply(doc, new FilterSpec { Minutes = new[] { 1 } }));
        Assert.Empty(Apply(doc, new FilterSpec { Minutes = new[] { 2 } }));
    }

    [Fact]
    public void 分钟过滤跳过无时间戳的行()
    {
        using var tmp = new TempLogFile("garbage without timestamp\n" + "09-23 18:00:00.000  1  2 I Tag: ok\n");
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(new[] { 1 }, Apply(doc, new FilterSpec { Minutes = new[] { 0 } }));
    }

    [Fact]
    public void 仅标记行()
    {
        using var tmp = new TempLogFile(Data);
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(new[] { 0, 2 },
            Apply(doc, new FilterSpec { MarkedOnly = true }, marked: new HashSet<int> { 0, 2 }));
        Assert.Empty(Apply(doc, new FilterSpec { MarkedOnly = true }, marked: null));
    }

    // ── ApplyFilter：组合与进度 ──

    [Fact]
    public void 多条件之间为and()
    {
        using var tmp = new TempLogFile(Data);
        using var doc = LogDocument.Build(tmp.Path);

        var spec = new FilterSpec
        {
            Levels = new[] { LogParser.LVL_E, LogParser.LVL_W },
            Tags = new[] { "NetworkService" },
        };
        Assert.Equal(new[] { 3 }, Apply(doc, spec));
    }

    [Fact]
    public void 过滤会上报进度()
    {
        using var tmp = new TempLogFile(Data);
        using var doc = LogDocument.Build(tmp.Path);
        var progress = new ProgressRecorder();

        Apply(doc, new FilterSpec { Msg = new[] { "start" } }, progress: progress);

        Assert.NotEmpty(progress.Items);
        Assert.Equal(1.0, progress.Items[^1].pct);
    }

    [Fact]
    public void 已取消的令牌中断过滤()
    {
        using var tmp = new TempLogFile(Data);
        using var doc = LogDocument.Build(tmp.Path);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => Apply(doc, new FilterSpec(), ct: cts.Token));
    }

    [Fact]
    public async Task ApplyFilterAsync与同步结果一致()
    {
        using var tmp = new TempLogFile(Data);
        using var doc = LogDocument.Build(tmp.Path);
        var spec = new FilterSpec { Levels = new[] { LogParser.LVL_E, LogParser.LVL_W } };

        var sync = Apply(doc, spec);
        var async = await FilterEngine.ApplyFilterAsync(doc, spec, null, null, CancellationToken.None);

        Assert.Equal(sync, async);
    }

    // ── FilterTail ──

    [Fact]
    public void FilterTail从指定行号往后过滤()
    {
        using var tmp = new TempLogFile(Data);
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(new[] { 3, 4, 5 }, FilterEngine.FilterTail(doc, new FilterSpec(), 3));
    }

    [Fact]
    public void FilterTail起始行越界时返回空()
    {
        using var tmp = new TempLogFile(Data);
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Empty(FilterEngine.FilterTail(doc, new FilterSpec(), 6));
        Assert.Empty(FilterEngine.FilterTail(doc, new FilterSpec(), 99));
    }

    [Fact]
    public void FilterTail应用全部条件()
    {
        using var tmp = new TempLogFile(Data);
        using var doc = LogDocument.Build(tmp.Path);

        var spec = new FilterSpec { Tags = new[] { "DebugTag" }, Minutes = new[] { 1 } };
        Assert.Equal(new[] { 5 }, FilterEngine.FilterTail(doc, spec, 0));

        var msgSpec = new FilterSpec { Msg = new[] { "detail" } };
        Assert.Equal(new[] { 4 }, FilterEngine.FilterTail(doc, msgSpec, 0));
    }

    [Fact]
    public void FilterTail支持仅标记行()
    {
        using var tmp = new TempLogFile(Data);
        using var doc = LogDocument.Build(tmp.Path);

        var spec = new FilterSpec { MarkedOnly = true };
        Assert.Equal(new[] { 4 }, FilterEngine.FilterTail(doc, spec, 0, new HashSet<int> { 4 }));
    }

    /// <summary>
    /// 增量路径的 tid 排除分支：FilterTail 里 TidExclude 与 PidExclude 是各自独立的判定，
    /// 漏掉一种会让实时采集与一次性过滤得出不同结果（滚动视图凭空多行/少行）。
    /// </summary>
    [Fact]
    public void FilterTail支持tid排除()
    {
        using var tmp = new TempLogFile(Data);
        using var doc = LogDocument.Build(tmp.Path);

        // 样本 tid：行0=2 行1=3 行2=4 行3=5 行4=6 行5=7 → 排除 2、3 后保留行 2~5
        var spec = new FilterSpec { Tids = new[] { 2, 3 }, TidExclude = true };
        Assert.Equal(new[] { 2, 3, 4, 5 }, Apply(doc, spec));
        Assert.Equal(new[] { 2, 3, 4, 5 }, FilterEngine.FilterTail(doc, spec, 0));
        Assert.Equal(new[] { 3, 4, 5 }, FilterEngine.FilterTail(doc, spec, 3));
    }

    // ── ExportRows ──

    static string OutPath(TempLogFile tmp) => tmp.Path + ".out";

    [Fact]
    public void 导出指定行()
    {
        using var tmp = new TempLogFile(Data);
        using var doc = LogDocument.Build(tmp.Path);
        var outPath = OutPath(tmp);
        try
        {
            int written = FilterEngine.ExportRows(doc, new[] { 0, 2 }, outPath);
            Assert.Equal(2, written);
            Assert.Equal(
                "09-23 18:00:00.000  1  2 I ActivityManager: started\n" +
                "09-23 18:00:02.000  2  4 D NetworkService: connecting\n",
                File.ReadAllText(outPath));
        }
        finally { File.Delete(outPath); }
    }

    [Fact]
    public void 导出空行集时生成空文件()
    {
        using var tmp = new TempLogFile(Data);
        using var doc = LogDocument.Build(tmp.Path);
        var outPath = OutPath(tmp);
        try
        {
            Assert.Equal(0, FilterEngine.ExportRows(doc, Array.Empty<int>(), outPath));
            Assert.True(File.Exists(outPath));
            Assert.Equal(0, new FileInfo(outPath).Length);
        }
        finally { File.Delete(outPath); }
    }

    [Fact]
    public void 单行模式把多行记录压成一行()
    {
        using var tmp = new TempLogFile(
            "09-23 18:00:00.000  1  2 E Tag: crash\n    at Foo.bar(Foo.java:1)\n");
        using var doc = LogDocument.Build(tmp.Path);
        var outPath = OutPath(tmp);
        try
        {
            FilterEngine.ExportRows(doc, new[] { 0 }, outPath, singleLine: true);
            Assert.Equal("09-23 18:00:00.000  1  2 E Tag: crash\\n    at Foo.bar(Foo.java:1)\n",
                File.ReadAllText(outPath));
        }
        finally { File.Delete(outPath); }
    }

    [Fact]
    public void 导出取消时抛出异常()
    {
        using var tmp = new TempLogFile(Data);
        using var doc = LogDocument.Build(tmp.Path);
        var outPath = OutPath(tmp);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try
        {
            Assert.Throws<OperationCanceledException>(() =>
                FilterEngine.ExportRows(doc, new[] { 0 }, outPath, ct: cts.Token));
        }
        finally { File.Delete(outPath); }
    }

    [Fact]
    public async Task ExportRowsAsync与同步结果一致()
    {
        using var tmp = new TempLogFile(Data);
        using var doc = LogDocument.Build(tmp.Path);
        var outPath = OutPath(tmp);
        try
        {
            int written = await FilterEngine.ExportRowsAsync(doc, new[] { 1, 3 }, outPath);
            Assert.Equal(2, written);
            Assert.Contains("failed to start", File.ReadAllText(outPath));
        }
        finally { File.Delete(outPath); }
    }

    // ── 防误滤：不应该被过滤掉的行必须保留 ──
    // 核心不变式：「排除」语义下，缺失字段（无 tag / 无 pid / 无 tid / 空正文）的行
    // 一律视为不命中、必须保留；msg 只搜正文、tag 只看 tag 列，不得越界匹配头部。

    const string GarbageLine = "totally garbage line\n";

    [Fact]
    public void tag过滤只看tag列_不碰message与头部()
    {
        using var tmp = new TempLogFile(Data);
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Empty(Apply(doc, new FilterSpec { Tags = new[] { "started" } }));   // message 内容
        Assert.Empty(Apply(doc, new FilterSpec { Tags = new[] { "18:00" } }));     // 时间头部
        Assert.Empty(Apply(doc, new FilterSpec { Tags = new[] { "  1  2" } }));    // pid/tid 头部
    }

    [Fact]
    public void message过滤只看正文_不碰tag与头部()
    {
        using var tmp = new TempLogFile(Data);
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Empty(Apply(doc, new FilterSpec { Msg = new[] { "ActivityManager" } }));   // tag 列
        Assert.Empty(Apply(doc, new FilterSpec { Msg = new[] { "18:00:00" } }));          // 时间头部
        Assert.Empty(Apply(doc, new FilterSpec { Msg = new[] { "000  1" } }));            // pid/tid 头部
    }

    [Fact]
    public void tag排除时无tag的行仍保留()
    {
        using var tmp = new TempLogFile(GarbageLine + Data);
        using var doc = LogDocument.Build(tmp.Path);

        // 行 0 无 tag：不命中 ActivityManager，排除语义下必须保留
        Assert.Equal(new[] { 0, 3, 4, 5, 6 },
            Apply(doc, new FilterSpec { Tags = new[] { "ActivityManager" }, TagExclude = true }));
    }

    [Fact]
    public void tag排除时整个文档没有tag也保留全部行()
    {
        // doc.Tags 为空时 TagIds 返回空集，排除语义不能把所有行都滤光
        using var tmp = new TempLogFile(GarbageLine + "another garbage line\n");
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(new[] { 0, 1 },
            Apply(doc, new FilterSpec { Tags = new[] { "anything" }, TagExclude = true }));
    }

    [Fact]
    public void pid与tid排除时未知字段的行仍保留()
    {
        using var tmp = new TempLogFile(GarbageLine + Data);
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(new[] { 0, 3, 4, 5, 6 },
            Apply(doc, new FilterSpec { Pids = new[] { 1 }, PidExclude = true }));
        Assert.Equal(new[] { 0, 1, 3, 4, 5, 6 },
            Apply(doc, new FilterSpec { Tids = new[] { 3 }, TidExclude = true }));
    }

    [Fact]
    public void message排除时无正文的行仍保留()
    {
        // 行 0 只有 tag 没有正文（冒号结尾），空正文不命中 "start"，排除时必须保留
        using var tmp = new TempLogFile("09-23 18:00:00.000  9  9 I Tag:\n" + Data);
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(new[] { 0, 3, 4, 5, 6 },
            Apply(doc, new FilterSpec { Msg = new[] { "start" }, MsgExclude = true }));
    }

    [Fact]
    public void message过滤能命中多行记录的续行内容()
    {
        using var tmp = new TempLogFile(
            "09-23 18:00:00.000  1  2 E Tag: crash\n    at Foo.bar(Foo.java:1)\n" + Data);
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(new[] { 0 }, Apply(doc, new FilterSpec { Msg = new[] { "Foo.bar" } }));
        Assert.Equal(new[] { 0 }, Apply(doc, new FilterSpec { Msg = new[] { "java:1" } }));
    }

    [Fact]
    public void 多字节中文正文与tag都能按子串匹配()
    {
        using var tmp = new TempLogFile("09-23 18:00:00.000  1  2 I 中文Tag: 数据库连接失败\n");
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(new[] { 0 }, Apply(doc, new FilterSpec { Msg = new[] { "连接失败" } }));
        Assert.Equal(new[] { 0 }, Apply(doc, new FilterSpec { Tags = new[] { "中文" } }));
        // 大小写不敏感模式走 ToLowerInvariant，不得破坏多字节序列
        Assert.Equal(new[] { 0 }, Apply(doc, new FilterSpec { Msg = new[] { "数据库" } }));
    }

    [Fact]
    public void 排除条件不命中任何行时保留全部()
    {
        using var tmp = new TempLogFile(Data);
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 },
            Apply(doc, new FilterSpec { Tags = new[] { "no-such-tag" }, TagExclude = true }));
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 },
            Apply(doc, new FilterSpec { Pids = new[] { 99 }, PidExclude = true }));
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 },
            Apply(doc, new FilterSpec { Tids = new[] { 99 }, TidExclude = true }));
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 },
            Apply(doc, new FilterSpec { Msg = new[] { "no-such-text" }, MsgExclude = true }));
    }

    [Fact]
    public void 未知级别的行只在空条件或全集级别时保留()
    {
        using var tmp = new TempLogFile(GarbageLine + Data);
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6 }, Apply(doc, new FilterSpec()));
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6 },
            Apply(doc, new FilterSpec { Levels = new[] { 7, 1, 5, 3, 2, 6, 4 } }));
        // 指定级别时未知级别的行被剔除是预期行为（级别过滤本身必须生效）
        Assert.Equal(new[] { 2 }, Apply(doc, new FilterSpec { Levels = new[] { LogParser.LVL_E } }));
    }

    [Fact]
    public void 仅标记行与其他条件为and()
    {
        using var tmp = new TempLogFile(Data);
        using var doc = LogDocument.Build(tmp.Path);

        var marked = new HashSet<int> { 0, 1, 3 };
        Assert.Equal(new[] { 1, 3 },
            Apply(doc, new FilterSpec { MarkedOnly = true, Levels = new[] { LogParser.LVL_W, LogParser.LVL_E } }, marked));
        // 满足级别条件但未标记的行不保留
        Assert.Empty(Apply(doc, new FilterSpec { MarkedOnly = true, Levels = new[] { LogParser.LVL_D } }, marked));
    }

    [Fact]
    public void 仅标记行与分钟tag消息同时生效()
    {
        using var tmp = new TempLogFile(Data);
        using var doc = LogDocument.Build(tmp.Path);

        // 标记行 {0,1,3,5}；tag 用 OR 列表、msg 用 OR 列表，让每个条件都各自剔除不同的行
        var marked = new HashSet<int> { 0, 1, 3, 5 };
        var spec = new FilterSpec
        {
            MarkedOnly = true,
            Minutes = new[] { 0 },                          // 剔除行 5（18:01）
            Tags = new[] { "ActivityManager", "DebugTag" }, // 剔除行 3（NetworkService）
            MsgOp = "or",
            Msg = new[] { "start", "time" },                // 剔除行 5（"another" 不含 start/time）
        };

        // 全量：分钟 0 → {0,1,3}，tag → {0,1}，msg → {0,1}
        Assert.Equal(new[] { 0, 1 }, Apply(doc, spec, marked));

        // 逐个去掉条件，结果都应变宽，证明四个条件确实同时参与过滤
        Assert.Equal(new[] { 0, 1, 3, 5 }, Apply(doc, new FilterSpec { MarkedOnly = true }, marked));
        Assert.Equal(new[] { 0, 1, 3 },
            Apply(doc, new FilterSpec { MarkedOnly = true, Minutes = spec.Minutes }, marked));
        Assert.Equal(new[] { 0, 1, 5 },
            Apply(doc, new FilterSpec { MarkedOnly = true, Tags = spec.Tags }, marked));
        Assert.Equal(new[] { 0, 1, 3 },
            Apply(doc, new FilterSpec { MarkedOnly = true, MsgOp = "or", Msg = spec.Msg }, marked));

        // 增量路径（FilterTail）与全量结果一致
        Assert.Equal(new[] { 0, 1 }, FilterEngine.FilterTail(doc, spec, 0, marked));
    }

    [Fact]
    public void 增量FilterTail与全量ApplyFilter结果一致()
    {
        using var tmp = new TempLogFile(Data);
        using var doc = LogDocument.Build(tmp.Path);

        // 实时采集走 FilterTail，一次性过滤走 ApplyFilter，
        // 两者对同一份条件必须得出一致的结果，否则滚动视图会凭空丢行
        var spec = new FilterSpec
        {
            Levels = new[] { LogParser.LVL_I, LogParser.LVL_W, LogParser.LVL_E },
            Tags = new[] { "Manager", "Debug" },
            Pids = new[] { 1, 3 },
            Minutes = new[] { 0, 1 },
            Msg = new[] { "t" },
        };
        var full = Apply(doc, spec);
        Assert.Equal(new[] { 0, 1, 5 }, full);
        for (int start = 0; start <= 6; start++)
            Assert.Equal(full.Where(r => r >= start).ToArray(), FilterEngine.FilterTail(doc, spec, start));

        var exclSpec = new FilterSpec { Msg = new[] { "start" }, MsgExclude = true };
        var exclFull = Apply(doc, exclSpec);
        Assert.Equal(new[] { 2, 3, 4, 5 }, exclFull);
        for (int start = 0; start <= 6; start++)
            Assert.Equal(exclFull.Where(r => r >= start).ToArray(), FilterEngine.FilterTail(doc, exclSpec, start));
    }

    // ── 大小写折叠的字符集边界 ──
    // 这一节针对「不区分大小写」的折叠实现：过滤引擎在比较前把文本折成小写，
    // 任何加速（字节级比较、ASCII 快速路径、去掉多余编解码）都不能改变这些结论。
    // 样本刻意混了纯 ASCII、中文、重音拉丁（É/é）、土耳其无点 i（ı）、德语 ß / 大写 ẞ。
    // 所有期望值均由真实实现跑出（临时 harness 直接编译 Services 源文件），不是推断。

    const string CaseData =
        "09-23 18:00:00.000  1  1 I CaseTag: PLAIN ASCII UPPER\n" +           // 0
        "09-23 18:00:01.000  1  2 I CaseTag: plain ascii lower\n" +           // 1
        "09-23 18:00:02.000  1  3 I CaseTag: MiXeD CaSe TeXt\n" +             // 2
        "09-23 18:00:03.000  1  4 I CaseTag: 中文日志内容\n" +                 // 3
        "09-23 18:00:04.000  1  5 I CaseTag: CAFÉ crème brûlée\n" +          // 4
        "09-23 18:00:05.000  1  6 I CaseTag: cafe plain no accent\n" +         // 5
        "09-23 18:00:06.000  1  7 I CaseTag: Turkish dotless ı and I\n" +      // 6
        "09-23 18:00:07.000  1  8 I CaseTag: German Straße STRASSE\n" +       // 7
        "09-23 18:00:08.000  1  9 I CaseTag: ß lowercase ß\n" +               // 8
        "09-23 18:00:09.000  1 10 I CaseTag: ẞ uppercase ẞ\n" +               // 9
        "09-23 18:00:10.000  1 11 I CaseTag: 中文Mixed大小写\n" +             // 10
        "09-23 18:00:11.000  1 12 I CaseTag: dotless ı\n";                    // 11 含 ı 但无任何 ASCII i/I

    /// <summary>纯 ASCII 折叠：任意大小写写法等价；MsgCase=true 时严格区分。</summary>
    [Fact]
    public void message忽略大小写_纯ASCII双向对称()
    {
        using var tmp = new TempLogFile(CaseData);
        using var doc = LogDocument.Build(tmp.Path);

        // 四种写法互相等价（命中 0/1；第 5 行 "cafe plain no accent" 也含 plain）
        Assert.Equal(new[] { 0, 1, 5 }, Apply(doc, new FilterSpec { Msg = new[] { "plain" } }));
        Assert.Equal(new[] { 0, 1, 5 }, Apply(doc, new FilterSpec { Msg = new[] { "PLAIN" } }));
        Assert.Equal(new[] { 0, 1, 5 }, Apply(doc, new FilterSpec { Msg = new[] { "PlAiN" } }));
        Assert.Equal(new[] { 0, 1, 5 }, Apply(doc, new FilterSpec { Msg = new[] { "pLaIn" } }));

        // MsgCase=true 时严格区分：全大写只在第 0 行，全小写命中 1/5
        Assert.Equal(new[] { 0 }, Apply(doc, new FilterSpec { Msg = new[] { "PLAIN" }, MsgCase = true }));
        Assert.Equal(new[] { 1, 5 }, Apply(doc, new FilterSpec { Msg = new[] { "plain" }, MsgCase = true }));
        // 第 2 行是 MiXeD，既不含全大写也不含全小写
        Assert.Empty(Apply(doc, new FilterSpec { Msg = new[] { "mixed" }, MsgCase = true }));
        Assert.Empty(Apply(doc, new FilterSpec { Msg = new[] { "MIXED" }, MsgCase = true }));
        // 第 2 行确为 MiXeD：用完整写法可命中
        Assert.Equal(new[] { 2 }, Apply(doc, new FilterSpec { Msg = new[] { "MiXeD" }, MsgCase = true }));
    }

    /// <summary>
    /// 折叠只改大小写，不去重音：CAFÉ ≠ cafe，CRÛME ≠ crème。
    /// 这是防「按位或 0x20 折叠」的护栏——0xC3|0x20==0xE3 会把不同字节改成同一个。
    /// </summary>
    [Fact]
    public void message忽略大小写_重音不与非重音等价()
    {
        using var tmp = new TempLogFile(CaseData);
        using var doc = LogDocument.Build(tmp.Path);

        // 重音 É 与非重音 E 是不同字符串，互不命中
        Assert.Equal(new[] { 4 }, Apply(doc, new FilterSpec { Msg = new[] { "CAFÉ" } }));
        Assert.Equal(new[] { 5 }, Apply(doc, new FilterSpec { Msg = new[] { "cafe" } }));
        Assert.Empty(Apply(doc, new FilterSpec { Msg = new[] { "café" }, MsgCase = true }));
        Assert.Equal(new[] { 4 }, Apply(doc, new FilterSpec { Msg = new[] { "CAFÉ" }, MsgCase = true }));

        // Û 与 È 不能互串：搜全大写重音串命中第 4 行，搜错重音（CRÛME 对 crème）不命中
        Assert.Equal(new[] { 4 }, Apply(doc, new FilterSpec { Msg = new[] { "CAFÉ CRÈME BRÛLÉE" } }));
        Assert.Empty(Apply(doc, new FilterSpec { Msg = new[] { "CRÛME BRÛLÉE" } }));
        // brûlée 的大小写两种写法等价
        Assert.Equal(new[] { 4 }, Apply(doc, new FilterSpec { Msg = new[] { "brûlée" } }));
        Assert.Equal(new[] { 4 }, Apply(doc, new FilterSpec { Msg = new[] { "BRÛLÉE" } }));
    }

    /// <summary>中文无大小写，但含中文的 term 必须能匹配，且中英混排两种写法等价。</summary>
    [Fact]
    public void message忽略大小写_中文term正常匹配()
    {
        using var tmp = new TempLogFile(CaseData);
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(new[] { 3 }, Apply(doc, new FilterSpec { Msg = new[] { "日志" } }));
        Assert.Equal(new[] { 10 }, Apply(doc, new FilterSpec { Msg = new[] { "大小写" } }));
        // 中英混排：英文部分大小写等价
        Assert.Equal(new[] { 10 }, Apply(doc, new FilterSpec { Msg = new[] { "中文mixed大小写" } }));
        Assert.Equal(new[] { 10 }, Apply(doc, new FilterSpec { Msg = new[] { "中文MIXED大小写" } }));
        // 中文内容不受 MsgCase 影响
        Assert.Equal(new[] { 3 }, Apply(doc, new FilterSpec { Msg = new[] { "日志" }, MsgCase = true }));
        Assert.Equal(new[] { 10 }, Apply(doc, new FilterSpec { Msg = new[] { "大小写" }, MsgCase = true }));
    }

    /// <summary>
    /// ß(U+00DF) 与 ẞ(U+1E9E) 在 Unicode 里互为大小写，折叠后必须等价；
    /// 另外 Straße / STRASSE 都在第 7 行，搜 ß 与 ẞ 都会连带命中它。
    /// </summary>
    [Fact]
    public void message忽略大小写_德语eszett大小写等价()
    {
        using var tmp = new TempLogFile(CaseData);
        using var doc = LogDocument.Build(tmp.Path);

        // 忽略大小写时 ß 与ẞ 等价，都命中 7/8/9
        Assert.Equal(new[] { 7, 8, 9 }, Apply(doc, new FilterSpec { Msg = new[] { "ß" } }));
        Assert.Equal(new[] { 7, 8, 9 }, Apply(doc, new FilterSpec { Msg = new[] { "ẞ" } }));

        // 区分大小写时：第 7 行含 ß 小写 + STRASSE，8 行全 ß，9 行全 ẞ
        Assert.Equal(new[] { 7, 8 }, Apply(doc, new FilterSpec { Msg = new[] { "ß" }, MsgCase = true }));
        Assert.Equal(new[] { 9 }, Apply(doc, new FilterSpec { Msg = new[] { "ẞ" }, MsgCase = true }));

        // ß 不等价于 ss：第 7 行是 Straße（ß）而 STRASSE 是 ss 写法，两者都命中
        Assert.Equal(new[] { 7 }, Apply(doc, new FilterSpec { Msg = new[] { "straße" } }));
        Assert.Equal(new[] { 7 }, Apply(doc, new FilterSpec { Msg = new[] { "STRASSE" } }));
        // 区分大小写时 Straße（第 7 行首字母大写）不命中全小写 straße
        Assert.Empty(Apply(doc, new FilterSpec { Msg = new[] { "straße" }, MsgCase = true }));
        Assert.Equal(new[] { 7 }, Apply(doc, new FilterSpec { Msg = new[] { "STRASSE" }, MsgCase = true }));
    }

    /// <summary>
    /// 土耳其无点 i（U+0131）折成小写后仍是 ı，不等于 ASCII 的 i。
    /// 第 11 行含 ı 但不含任何 ASCII i/I，是干净隔离这一点的关键样本。
    /// </summary>
    [Fact]
    public void message忽略大小写_土耳其无点i不等于ASCII_I()
    {
        using var tmp = new TempLogFile(CaseData);
        using var doc = LogDocument.Build(tmp.Path);

        // 搜无点 ı：命中 6/11（两行都含 ı）
        Assert.Equal(new[] { 6, 11 }, Apply(doc, new FilterSpec { Msg = new[] { "ı" } }));
        // 搜 ASCII I（折成 i）：不能把 ı 当成 i——第 11 行只有 ı，不该出现
        var hitI = Apply(doc, new FilterSpec { Msg = new[] { "I" } });
        Assert.Equal(new[] { 0, 1, 2, 5, 6, 10 }, hitI);
        Assert.DoesNotContain(11, hitI);
        // 区分大小写：搜 ASCII I 命中第 0 行（ASCII UPPER 里的 I）和第 6 行末尾的 " and I"
        Assert.Equal(new[] { 0, 6 }, Apply(doc, new FilterSpec { Msg = new[] { "I" }, MsgCase = true }));
    }

    /// <summary>tag 侧走同一套折叠逻辑，行为必须与 message 一致。</summary>
    [Fact]
    public void tag忽略大小写与message行为一致()
    {
        const string TagData =
            "09-23 18:00:00.000  1  1 I MyTag: first\n" +
            "09-23 18:00:01.000  1  2 I mytag: second\n" +
            "09-23 18:00:02.000  1  3 I MYTAG: third\n" +
            "09-23 18:00:03.000  1  4 I 中文Tag: fourth\n";

        using var tmp = new TempLogFile(TagData);
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(new[] { 0, 1, 2 }, Apply(doc, new FilterSpec { Tags = new[] { "mytag" } }));
        Assert.Equal(new[] { 0, 1, 2 }, Apply(doc, new FilterSpec { Tags = new[] { "MyTaG" } }));
        Assert.Equal(new[] { 0, 1, 2 }, Apply(doc, new FilterSpec { Tags = new[] { "MYTAG" } }));
        // 区分大小写时逐条只命中自身
        Assert.Equal(new[] { 0 }, Apply(doc, new FilterSpec { Tags = new[] { "MyTag" }, TagCase = true }));
        Assert.Equal(new[] { 1 }, Apply(doc, new FilterSpec { Tags = new[] { "mytag" }, TagCase = true }));
        Assert.Equal(new[] { 2 }, Apply(doc, new FilterSpec { Tags = new[] { "MYTAG" }, TagCase = true }));
        // 中文 tag
        Assert.Equal(new[] { 3 }, Apply(doc, new FilterSpec { Tags = new[] { "中文" } }));
    }

    /// <summary>
    /// 折叠正确性不能只在全量路径成立：FilterTail 复用同一套匹配，
    /// 各类 term（含重音、中文、ß、无点 i）的大小写结论在增量路径上必须完全一致。
    /// </summary>
    [Fact]
    public void 增量路径的忽略大小写结论与全量一致()
    {
        using var tmp = new TempLogFile(CaseData);
        using var doc = LogDocument.Build(tmp.Path);

        foreach (var term in new[] { "plain", "PLAIN", "mixed", "café", "CAFÉ", "cafe", "日志", "大小写",
                                     "ß", "ẞ", "ı", "I", "straße", "STRASSE", "dotless" })
        foreach (var op in new[] { "and", "or" })
        foreach (var msgCase in new[] { false, true })
        {
            var spec = new FilterSpec { Msg = new[] { term }, MsgOp = op, MsgCase = msgCase };
            var full = Apply(doc, spec);
            for (int start = 0; start <= doc.RowCount + 1; start++)
                Assert.Equal(full.Where(r => r >= start).ToArray(), FilterEngine.FilterTail(doc, spec, start));
        }
    }

    /// <summary>
    /// 排除语义在大小写折叠下同样成立：命中的行被剔除，未命中的全留。
    /// 「不应被过滤掉的行必须保留」是最重要的不变量。
    /// </summary>
    [Fact]
    public void message排除在大小写折叠下语义正确()
    {
        using var tmp = new TempLogFile(CaseData);
        using var doc = LogDocument.Build(tmp.Path);

        // plain 命中 0/1/5，排除后剩 2,3,4,6,7,8,9,10,11
        Assert.Equal(new[] { 2, 3, 4, 6, 7, 8, 9, 10, 11 },
            Apply(doc, new FilterSpec { Msg = new[] { "plain" }, MsgExclude = true }));
        Assert.Equal(new[] { 2, 3, 4, 6, 7, 8, 9, 10, 11 },
            Apply(doc, new FilterSpec { Msg = new[] { "PLAIN" }, MsgExclude = true }));
        // 排除 [plain,case] or：命中 0,1,2,5,8,9，MsgExclude 保留的是**未命中**的行
        Assert.Equal(new[] { 3, 4, 6, 7, 10, 11 },
            Apply(doc, new FilterSpec { Msg = new[] { "plain", "case" }, MsgOp = "or", MsgExclude = true }));
        // 排除不存在的 term：全部保留
        Assert.Equal(Enumerable.Range(0, doc.RowCount).ToArray(),
            Apply(doc, new FilterSpec { Msg = new[] { "no-such-term" }, MsgExclude = true }));

        // 排除 CAFÉ：命中 4，剩其余全部
        Assert.Equal(new[] { 0, 1, 2, 3, 5, 6, 7, 8, 9, 10, 11 },
            Apply(doc, new FilterSpec { Msg = new[] { "CAFÉ" }, MsgExclude = true }));
    }
}
