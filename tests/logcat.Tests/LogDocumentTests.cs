using System.Text;
using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// LogDocument：mmap 列式索引的构建、增量追加、多行合并、跨年时间戳推算。
/// 行文本统一用 TestData.Threadtime 生成，字段偏移量在注释里标注，便于核对。
/// </summary>
public class LogDocumentTests
{
    const string T1 = "09-23 18:00:00.000";
    const string T2 = "09-23 18:00:01.000";
    const string T3 = "09-23 18:00:10.000";
    const string T4 = "09-23 18:00:20.000";

    static string Line(string time, char level, string tag, string msg) =>
        TestData.Threadtime(time, 1, 2, level, tag, msg);

    static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    // ── 构建 ──

    [Fact]
    public void Build_解析出全部列()
    {
        using var tmp = new TempLogFile(Line(T1, 'E', "Tag", "crash"));
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(1, doc.RowCount);
        Assert.Equal(tmp.Path, doc.Path);
        Assert.Equal("utf-8", doc.EncodingName);
        Assert.Equal(tmp.Length, doc.Size);
        Assert.Equal(tmp.Length, doc.IndexedSize);

        Assert.Equal(1, doc.Pid[0]);
        Assert.Equal(2, doc.Tid[0]);
        Assert.Equal(LogParser.LVL_E, doc.Lvl[0]);
        Assert.Equal(0, doc.TagId[0]);
        Assert.Equal("Tag", doc.TagOf(0));
        Assert.False(doc.IsMultiline(0));

        // Ts 是含年份偏移的绝对毫秒，直接断言还原出的时间文本更贴近语义
        Assert.Equal("09-23 18:00:00.000", LogDocument.TsToText(doc.Ts[0], doc.BaseYear));
        Assert.Equal(0, doc.Offs[0]);
        Assert.Equal("09-23 18:00:00.000  1  2 E Tag: crash", Text(doc.LineBytes(0)));
    }

    [Fact]
    public void Build_mmdd格式行内无年份时HasYear为假()
    {
        // 样本行必须用「今天」的日期：GuessYear 会把落在「今天 +1 天以上」将来
        // 的时间戳推定为去年，写死日期的话这个测试一年里只有部分时期能通过
        var today = DateTime.Now.ToString("MM-dd");
        using var tmp = new TempLogFile(TestData.Threadtime($"{today} 18:00:00.000", 1, 2, 'I', "Tag", "a"));
        using var doc = LogDocument.Build(tmp.Path);

        Assert.False(doc.HasYear);
        Assert.Equal(DateTime.Now.Year, doc.BaseYear);   // 当天时间戳 → 推定为今年
    }

    [Fact]
    public void Build_带年份时HasYear为真且基准年取自行内()
    {
        using var tmp = new TempLogFile("2026-09-23 18:00:00.000  1  2 I Tag: a\n");
        using var doc = LogDocument.Build(tmp.Path);

        Assert.True(doc.HasYear);
        Assert.Equal(2026, doc.BaseYear);
        // 时间列文本由 ts + BaseYear 还原，应回到行内原始日期
        Assert.Equal("09-23 18:00:00.000", LogDocument.TsToText(doc.Ts[0], doc.BaseYear));
    }

    [Fact]
    public void Build_空文件()
    {
        using var tmp = new TempLogFile("");
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(0, doc.RowCount);
        Assert.Empty(doc.Tags);
        Assert.Equal(0, doc.Size);
    }

    [Fact]
    public void Build_只含换行的文件()
    {
        using var tmp = new TempLogFile("\n\n\n");
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(0, doc.RowCount);
    }

    [Fact]
    public void Build_CRLF行尾不把回车带进记录()
    {
        using var tmp = new TempLogFile(Line(T1, 'I', "Tag", "a").Replace("\n", "\r\n"));
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(1, doc.RowCount);
        Assert.Equal("09-23 18:00:00.000  1  2 I Tag: a", Text(doc.LineBytes(0)));
        Assert.DoesNotContain((byte)'\r', doc.LineBytes(0));
    }

    [Fact]
    public void Build_首行UTF8BOM被剥离()
    {
        using var tmp = new TempLogFile(Line(T1, 'I', "Tag", "a"), withBom: true);
        using var doc = LogDocument.Build(tmp.Path);

        // BOM 没被剥掉的话，首行会整行解析失败，Level 会停在 0
        Assert.Equal(1, doc.RowCount);
        Assert.Equal(LogParser.LVL_I, doc.Lvl[0]);
        Assert.Equal("Tag", doc.TagOf(0));
    }

    [Fact]
    public void Build_横幅行不进索引()
    {
        using var tmp = new TempLogFile(
            "--------- beginning of main\n" +
            "----- timezone:Asia/Kuala_Lumpur\n" +
            Line(T1, 'I', "Tag", "a"));
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(1, doc.RowCount);
        Assert.Equal("09-23 18:00:00.000  1  2 I Tag: a", Text(doc.LineBytes(0)));
    }

    [Fact]
    public void Build_空行不进索引()
    {
        using var tmp = new TempLogFile(Line(T1, 'I', "Tag", "a") + "\n" + Line(T2, 'I', "Tag", "b"));
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(2, doc.RowCount);
    }

    [Fact]
    public void Build_多字节UTF8的tag与正文()
    {
        using var tmp = new TempLogFile(Line(T1, 'I', "中文Tag", "你好，世界"));
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal("中文Tag", doc.TagOf(0));
        Assert.Equal("你好，世界", Text(doc.MessageBytes(0)));
    }

    [Fact]
    public void Build_相同tag只占一个字典项()
    {
        using var tmp = new TempLogFile(
            Line(T1, 'I', "Repeat", "a") + Line(T2, 'I', "Repeat", "b") + Line(T3, 'I', "Other", "c"));
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(2, doc.Tags.Count);
        Assert.Equal("Repeat", doc.Tags[0]);
        Assert.Equal("Other", doc.Tags[1]);
        Assert.Equal(doc.TagId[0], doc.TagId[1]);
        Assert.NotEqual(doc.TagId[0], doc.TagId[2]);
        Assert.True(doc.TagIndex.ContainsKey("Repeat"));
    }

    [Fact]
    public void Build_无tag的行TagId为负一()
    {
        using var tmp = new TempLogFile("garbage line without tag\n");
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(1, doc.RowCount);
        Assert.Equal(-1, doc.TagId[0]);
        Assert.Equal("", doc.TagOf(0));
    }

    [Fact]
    public void Build_异常行不中断整份索引()
    {
        // 月份越界会让 ParseLine 抛 ParseException，扫描逻辑必须吞掉并记为未知行
        using var tmp = new TempLogFile(
            "99-99 18:00:00.000  1  2 I Tag: bad\n" + Line(T1, 'I', "Tag", "good"));
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(2, doc.RowCount);
        Assert.Equal(LogParser.LVL_UNKNOWN, doc.Lvl[0]);
        Assert.Equal(LogParser.LVL_I, doc.Lvl[1]);
    }

    // ── 多行合并 ──

    [Fact]
    public void 多行合并_缩进续行并入上一条()
    {
        using var tmp = new TempLogFile(
            Line(T1, 'E', "Tag", "crash") +
            "    at Foo.bar(Foo.java:1)\n" +
            "    at Baz.qux(Baz.java:2)\n");
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(1, doc.RowCount);
        Assert.True(doc.IsMultiline(0));
        Assert.Equal("09-23 18:00:00.000  1  2 E Tag: crash\n    at Foo.bar(Foo.java:1)\n    at Baz.qux(Baz.java:2)",
            Text(doc.LineBytes(0)));
        Assert.Equal("crash\n    at Foo.bar(Foo.java:1)\n    at Baz.qux(Baz.java:2)",
            Text(doc.MessageBytes(0)));
    }

    [Fact]
    public void 多行合并_关闭合并后每行独立成条()
    {
        using var tmp = new TempLogFile(
            Line(T1, 'E', "Tag", "crash") +
            "    at Foo.bar(Foo.java:1)\n" +
            "    at Baz.qux(Baz.java:2)\n");
        using var doc = LogDocument.Build(tmp.Path, join: false);

        Assert.Equal(3, doc.RowCount);
        Assert.False(doc.IsMultiline(0));
        Assert.False(doc.IsMultiline(1));
    }

    [Fact]
    public void 多行合并_被判定为新记录开头的行会另起一条()
    {
        // brief 格式（L/Tag: msg）会被 LooksLikeNewRecord 判定为新记录
        using var tmp = new TempLogFile(Line(T1, 'I', "Tag", "first") + "I/Other: second\n");
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(2, doc.RowCount);
        Assert.False(doc.IsMultiline(0));
    }

    /// <summary>
    /// 记录当前行为：续行判定只看「缩进 / 已知时间戳前缀 / 横幅」等特征，
    /// 既非缩进又不符合任何格式前缀的普通文本行会被并入上一条记录（不是新记录）。
    /// </summary>
    [Fact]
    public void 多行合并_不符合新记录特征的正文行会被并入上一条()
    {
        using var tmp = new TempLogFile(Line(T1, 'I', "Tag", "first") + "plain second line\n");
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(1, doc.RowCount);
        Assert.True(doc.IsMultiline(0));
        Assert.Equal("09-23 18:00:00.000  1  2 I Tag: first\nplain second line", Text(doc.LineBytes(0)));
    }

    [Fact]
    public void 多行合并_横幅行不打断续行合并()
    {
        using var tmp = new TempLogFile(
            Line(T1, 'E', "Tag", "crash") +
            "--------- beginning of main\n" +
            "    at Foo.bar(Foo.java:1)\n");
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(1, doc.RowCount);
        Assert.True(doc.IsMultiline(0));
        Assert.Contains("at Foo.bar", Text(doc.LineBytes(0)));
    }

    // ── 读取辅助 ──

    [Fact]
    public void HeaderBytes只取时间戳部分()
    {
        // threadtime 行头部还含 pid/tid，Time 列只应显示时间戳（曾把 PID 带进 Time 列）
        // 用多位 PID（如 1444）而非 Threadtime 默认的 1，确保时间戳被截断在 PID 之前
        using var tmp = new TempLogFile("09-23 18:00:00.000  1444  2272 E CameraService: crash\n");
        using var doc = LogDocument.Build(tmp.Path);

        var header = doc.HeaderBytes(0);
        Assert.Equal("09-23 18:00:00.000", Text(header));
        // 确认 PID 本身解析正确（与 HeaderBytes 互不干扰）
        Assert.Equal(1444, doc.Pid[0]);
        Assert.Equal(2272, doc.Tid[0]);
    }

    [Fact]
    public void HeaderBytes支持yyyy格式()
    {
        using var tmp = new TempLogFile("2026-09-23 18:00:00.000  1  2 I Tag: a\n");
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal("2026-09-23 18:00:00.000", Text(doc.HeaderBytes(0)));
    }

    [Fact]
    public void HeaderBytes保留高精度小数秒()
    {
        // usec/nsec 格式的小数秒超出 TsToText 的 3 位毫秒，须原样返回
        using var tmp = new TempLogFile("09-23 18:00:00.000123  1  2 I Tag: a\n");
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal("09-23 18:00:00.000123", Text(doc.HeaderBytes(0)));
    }

    [Fact]
    public void HeaderBytes在无头部时返回空()
    {
        // message 紧贴行首（moff == 0）时没有可显示的头部
        using var tmp = new TempLogFile("garbage\n");
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Empty(doc.HeaderBytes(0));
    }

    [Fact]
    public void Decode按UTF8还原字节()
    {
        using var tmp = new TempLogFile(Line(T1, 'I', "标签", "正文"));
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal("正文", doc.Decode(doc.MessageBytes(0)));
    }

    [Fact]
    public void TagOf越界返回空串()
    {
        using var tmp = new TempLogFile(Line(T1, 'I', "Tag", "a"));
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal("", doc.TagOf(-1));
        Assert.Equal("", doc.TagOf(99));
    }

    [Fact]
    public void Close之后读取返回空()
    {
        using var tmp = new TempLogFile(Line(T1, 'I', "Tag", "a"));
        var doc = LogDocument.Build(tmp.Path);
        doc.Close();

        Assert.Empty(doc.LineBytes(0));
        Assert.Empty(doc.MessageBytes(0));
        Assert.Empty(doc.HeaderBytes(0));
        doc.Dispose();
    }

    // ── 异步 / 进度 / 取消 ──

    [Fact]
    public async Task BuildAsync可用()
    {
        using var tmp = new TempLogFile(Line(T1, 'I', "Tag", "a"));
        using var doc = await LogDocument.BuildAsync(tmp.Path);

        Assert.Equal(1, doc.RowCount);
    }

    [Fact]
    public void Build上报扫描与合并进度()
    {
        using var tmp = new TempLogFile(Line(T1, 'I', "Tag", "a") + Line(T2, 'I', "Tag", "b"));
        var progress = new ProgressRecorder();
        using var doc = LogDocument.Build(tmp.Path, progress: progress);

        Assert.Contains(progress.Items, p => p.msg.Contains("扫描行"));
        Assert.Contains(progress.Items, p => p.msg.Contains("合并索引"));
    }

    [Fact]
    public void 已取消的令牌会中断构建()
    {
        using var tmp = new TempLogFile(Line(T1, 'I', "Tag", "a"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            LogDocument.Build(tmp.Path, ct: cts.Token));
    }

    // ── 跨年时间戳 ──

    [Fact]
    public void 时间戳回退超过半天判定为跨年()
    {
        using var tmp = new TempLogFile(
            "12-31 23:59:59.000  1  2 I Tag: a\n" +
            "01-01 00:00:01.000  1  2 I Tag: b\n");
        using var doc = LogDocument.Build(tmp.Path);

        // 跨年没被识别的话，第二条会被算成同一年，两行相差将近 -365 天
        Assert.Equal(2000, doc.Ts[1] - doc.Ts[0]);
    }

    [Fact]
    public void 时间戳回退不足半天视为同一天内乱序()
    {
        using var tmp = new TempLogFile(
            "09-23 18:00:00.000  1  2 I Tag: a\n" +
            "09-23 06:00:00.000  1  2 I Tag: b\n");
        using var doc = LogDocument.Build(tmp.Path);

        // 同一年的乱序：仍在同一年内，差值就是 -12 小时
        Assert.Equal(-12 * 3_600_000L, doc.Ts[1] - doc.Ts[0]);
    }

    /// <summary>
    /// 跨年判定的阈值是「回退超过半天」（HALF_DAY = DAY_MS / 2）。
    /// 上面两个用例一个测 -12 小时、一个测跨年，都落在阈值的**远端**，
    /// 把 HALF_DAY 从半天改成 30 天后它们照样通过（变异实测全绿）。
    /// 这里贴着阈值测：-20 小时必须判跨年（改成 30 天就抓到了）。
    /// </summary>
    [Fact]
    public void 时间戳回退接近半天时按跨年处理()
    {
        using var tmp = new TempLogFile(
            "09-23 18:00:00.000  1  2 I Tag: a\n" +
            "09-22 22:00:00.000  1  2 I Tag: b\n");      // 回退 20 小时 > 半天
        using var doc = LogDocument.Build(tmp.Path);

        // 判为跨年 → 第二行落到上一年，差值变成「一年减去 20 小时」（约 +364 天），
        // 而不是同一年内的 -20 小时。
        long diff = doc.Ts[1] - doc.Ts[0];
        Assert.True(diff > 300 * LogParser.DAY_MS, $"应判跨年（差值约 +364 天），实际 {diff} ms");
    }

    /// <summary>
    /// 阈值另一侧：回退 11 小时（不足半天）必须**不**判跨年。
    /// 与上一条成对，锁住「半天」这个具体取值，而不是「某个很大的值」。
    /// </summary>
    [Fact]
    public void 时间戳回退十一个小时不判跨年()
    {
        using var tmp = new TempLogFile(
            "09-23 18:00:00.000  1  2 I Tag: a\n" +
            "09-23 07:00:00.000  1  2 I Tag: b\n");      // 回退 11 小时 < 半天
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(-11 * 3_600_000L, doc.Ts[1] - doc.Ts[0]);
    }

    // ── 静态时间工具 ──

    [Fact]
    public void TsToText基本格式()
    {
        Assert.Equal("", LogDocument.TsToText(-1, 2026));
        Assert.Equal("01-01 00:00:00.000", LogDocument.TsToText(0, 2026));
        Assert.Equal("01-01 00:00:00", LogDocument.TsToText(0, 2026, withMs: false));
        Assert.Equal("01-01 00:00:01.500", LogDocument.TsToText(1500, 2026));
    }

    [Fact]
    public void TsToText超范围时返回空串()
    {
        Assert.Equal("", LogDocument.TsToText(long.MaxValue, 1));
    }

    [Fact]
    public void TsFromParts基本换算()
    {
        Assert.Equal(0, LogDocument.TsFromParts(2026, 1, 1, 0, 0, 0, 0, 2026));
        // 2026 非闰年：3 月 1 日距 1 月 1 日 59 天
        Assert.Equal(59 * LogParser.DAY_MS, LogDocument.TsFromParts(2026, 3, 1, 0, 0, 0, 0, 2026));
        Assert.Equal(3_723_004, LogDocument.TsFromParts(2026, 1, 1, 1, 2, 3, 4, 2026));
    }

    [Fact]
    public void TsFromParts年份为零时回退到基准年()
    {
        Assert.Equal(0, LogDocument.TsFromParts(0, 1, 1, 0, 0, 0, 0, 2026));
    }

    [Fact]
    public void TsFromParts月份为负一时按参考时间取当天()
    {
        long refTs = 10 * LogParser.DAY_MS;
        long actual = LogDocument.TsFromParts(2026, -1, 0, 1, 2, 3, 4, 2026, refTs);
        Assert.Equal(refTs + 3_723_004, actual);
    }

    [Fact]
    public void TsFromParts缺少参考时间时抛异常()
    {
        Assert.Throws<ArgumentException>(() =>
            LogDocument.TsFromParts(2026, -1, 0, 0, 0, 0, 0, 2026));
        Assert.Throws<ArgumentException>(() =>
            LogDocument.TsFromParts(2026, -1, 0, 0, 0, 0, 0, 2026, refTs: -1));
    }

    [Theory]
    [InlineData(24, 0, 0, 0)]     // 小时越界
    [InlineData(0, 60, 0, 0)]     // 分钟越界
    [InlineData(0, 0, 61, 0)]     // 秒越界
    [InlineData(0, 0, 0, 1000)]   // 毫秒越界
    public void TsFromParts时间字段越界抛异常(int h, int mi, int s, int ms)
    {
        Assert.Throws<ArgumentException>(() =>
            LogDocument.TsFromParts(2026, 1, 1, h, mi, s, ms, 2026));
    }

    [Theory]
    [InlineData(13, 1)]   // 月越界
    [InlineData(0, 1)]    // 月下界
    [InlineData(1, 32)]   // 日越界
    [InlineData(1, 0)]    // 日下界
    public void TsFromParts日期字段越界抛异常(int month, int day)
    {
        Assert.Throws<ArgumentException>(() =>
            LogDocument.TsFromParts(2026, month, day, 0, 0, 0, 0, 2026));
    }

    // ── 定位 ──

    [Fact]
    public void NearestPos_空集合返回零()
    {
        using var tmp = new TempLogFile(Line(T1, 'I', "Tag", "a"));
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(0, doc.NearestPos(ReadOnlySpan<int>.Empty, 0));
    }

    [Fact]
    public void NearestPos_取时间上最接近的行()
    {
        using var tmp = new TempLogFile(
            Line(T1, 'I', "Tag", "a") + Line(T3, 'I', "Tag", "b") + Line(T4, 'I', "Tag", "c"));
        using var doc = LogDocument.Build(tmp.Path);

        ReadOnlySpan<int> rows = stackalloc int[] { 0, 1, 2 };
        long ts0 = doc.Ts[0];

        Assert.Equal(0, doc.NearestPos(rows, ts0 + 1_000));       // 离第 0 行更近
        Assert.Equal(1, doc.NearestPos(rows, ts0 + 9_000));       // 离第 1 行更近
        Assert.Equal(2, doc.NearestPos(rows, ts0 + 19_000));      // 离第 2 行更近
        Assert.Equal(0, doc.NearestPos(rows, ts0 - 999_999));     // 早于全部 → 第一行
        Assert.Equal(2, doc.NearestPos(rows, ts0 + 999_999));     // 晚于全部 → 最后一行
    }

    // ── 增量：Reload / Append ──

    [Fact]
    public void Reload_文件未变化时原样返回()
    {
        using var tmp = new TempLogFile(Line(T1, 'I', "Tag", "a"));
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal("unchanged", doc.Reload());
        Assert.Equal(1, doc.RowCount);
    }

    [Fact]
    public void Reload_追加新行时走增量索引()
    {
        using var tmp = new TempLogFile(Line(T1, 'I', "Tag", "a"));
        using var doc = LogDocument.Build(tmp.Path);

        tmp.Append(Line(T2, 'I', "Tag", "b"));
        Assert.Equal("appended", doc.Reload());

        Assert.Equal(2, doc.RowCount);
        Assert.Equal("09-23 18:00:01.000  1  2 I Tag: b", Text(doc.LineBytes(1)));
        Assert.Equal(1000, doc.Ts[1] - doc.Ts[0]);
    }

    [Fact]
    public void Reload_文件变小时重建()
    {
        using var tmp = new TempLogFile(Line(T1, 'I', "Tag", "a") + Line(T2, 'I', "Tag", "b"));
        using var doc = LogDocument.Build(tmp.Path);
        Assert.Equal(2, doc.RowCount);

        // Windows 上无法截断已被 mmap 打开的文件，先释放映射再改写
        doc.Close();
        tmp.Overwrite(Line(T1, 'I', "Tag", "a"));
        Assert.Equal("rebuilt", doc.Reload());
        Assert.Equal(1, doc.RowCount);
    }

    [Fact]
    public void Reload_空文件变有内容时重建()
    {
        using var tmp = new TempLogFile("");
        using var doc = LogDocument.Build(tmp.Path);
        Assert.Equal(0, doc.RowCount);

        tmp.Append(Line(T1, 'I', "Tag", "a"));
        Assert.Equal("rebuilt", doc.Reload());
        Assert.Equal(1, doc.RowCount);
    }

    /// <summary>
    /// 长度不变时 Reload 直接判定 unchanged，不比对内容——这是有意的性能取舍，
    /// 流式采集只会让文件变长，等长改写不在支持范围内。
    ///
    /// 另注：Reload 里「文件变长但头部内容不同 → 重建」那条分支在单测中无法构造。
    /// Windows 的内存映射与文件写入共享页缓存，写盘后 _view 立刻能读到新字节，
    /// 头部比对恒等；除非替换文件本身，而映射中的文件无法被重命名/删除。
    /// 该分支属于防御性代码，由代码审查而非单测保障。
    /// </summary>
    [Fact]
    public void Reload_文件长度不变时不感知等长改写()
    {
        using var tmp = new TempLogFile(Line(T1, 'I', "Tag", "a"));
        using var doc = LogDocument.Build(tmp.Path);

        tmp.WriteInPlace(Line(T1, 'I', "Tag", "z"));    // 等长改写
        Assert.Equal("unchanged", doc.Reload());

        // 索引没重建，但底层 mmap 与文件共享页缓存，读出来的字节已是改写后的内容
        Assert.Equal("09-23 18:00:00.000  1  2 I Tag: z", Text(doc.LineBytes(0)));
    }

    /// <summary>
    /// 回归：早期实现的 start 对齐用「start 自身首字节」判断是否切在行中间，
    /// 导致每次增量追加都吞掉新内容的第一行。正确依据是 start-1 处是否为换行。
    /// </summary>
    [Fact]
    public void Reload_增量追加不会吞掉新内容的第一行()
    {
        using var tmp = new TempLogFile(Line(T1, 'I', "Tag", "a"));
        using var doc = LogDocument.Build(tmp.Path);

        tmp.Append(Line(T2, 'I', "Tag", "b") + Line(T3, 'I', "Tag", "c"));
        Assert.Equal("appended", doc.Reload());

        Assert.Equal(3, doc.RowCount);
        Assert.Equal("09-23 18:00:01.000  1  2 I Tag: b", Text(doc.LineBytes(1)));
        Assert.Equal("09-23 18:00:10.000  1  2 I Tag: c", Text(doc.LineBytes(2)));
    }

    /// <summary>
    /// 回归：流式写入可能在行中间 flush，末尾的不完整行必须留待下次追加，
    /// 否则同一行会被拆成两条记录。
    /// </summary>
    [Fact]
    public void Reload_末尾不完整行暂不索引_补全后才索引()
    {
        using var tmp = new TempLogFile(Line(T1, 'I', "Tag", "a"));
        using var doc = LogDocument.Build(tmp.Path);

        // 追加半行（无换行）→ 不索引
        tmp.Append("09-23 18:00:01.000  1  2 I Tag: partial");
        Assert.Equal("unchanged", doc.Reload());
        Assert.Equal(1, doc.RowCount);

        // 补上换行 → 该行才被索引
        tmp.Append("\n");
        Assert.Equal("appended", doc.Reload());
        Assert.Equal(2, doc.RowCount);
        Assert.Equal("09-23 18:00:01.000  1  2 I Tag: partial", Text(doc.LineBytes(1)));
    }

    /// <summary>
    /// 回归：批次起点的续行必须并回上一批末尾那条记录（CarryLines 机制），
    /// 否则崩溃堆栈会在批次边界被切成两条。
    /// </summary>
    [Fact]
    public void Reload_跨批次续行并回上一条记录()
    {
        using var tmp = new TempLogFile(Line(T1, 'E', "Tag", "crash"));
        using var doc = LogDocument.Build(tmp.Path);

        tmp.Append("    at Foo.bar(Foo.java:1)\n    at Baz.qux(Baz.java:2)\n");
        Assert.Equal("appended", doc.Reload());

        Assert.Equal(1, doc.RowCount);
        Assert.True(doc.IsMultiline(0));
        Assert.Equal("09-23 18:00:00.000  1  2 E Tag: crash\n    at Foo.bar(Foo.java:1)\n    at Baz.qux(Baz.java:2)",
            Text(doc.LineBytes(0)));
    }

    /// <summary>
    /// LastAppendCarried：实时采集增量过滤据此决定是否回退全量。
    /// 纯尾部追加（无续行并回）为 false；批次起点出现续行为 true（末记录 message 变了）。
    /// </summary>
    [Fact]
    public void Reload_跨批次续行时报告Carry()
    {
        using var tmp = new TempLogFile(Line(T1, 'I', "Tag", "a"));
        using var doc = LogDocument.Build(tmp.Path);

        // 普通追加：无 carry
        tmp.Append(Line(T2, 'I', "Tag", "b"));
        Assert.Equal("appended", doc.Reload());
        Assert.False(doc.LastAppendCarried);
        Assert.Equal(2, doc.RowCount);

        // 批次起点是续行：并回上一条记录，carry = true
        tmp.Append("    at Foo.bar(Foo.java:1)\n");
        Assert.Equal("appended", doc.Reload());
        Assert.True(doc.LastAppendCarried);
        Assert.Equal(2, doc.RowCount);

        // 未变化：保持上次的值，调用方只在 kind == "appended" 时读取
        Assert.Equal("unchanged", doc.Reload());
        Assert.True(doc.LastAppendCarried);
    }

    /// <summary>整批都是续行时也要返回 appended，且新字节必须可读（需要重映射）。</summary>
    [Fact]
    public void Reload_整批续行时新内容可读()
    {
        using var tmp = new TempLogFile(Line(T1, 'E', "Tag", "crash"));
        using var doc = LogDocument.Build(tmp.Path);
        long before = doc.LineBytes(0).Length;

        tmp.Append("    at Foo.bar(Foo.java:1)\n");
        Assert.Equal("appended", doc.Reload());

        Assert.Equal(1, doc.RowCount);
        Assert.True(doc.LineBytes(0).Length > before);
        Assert.Contains("at Foo.bar", Text(doc.LineBytes(0)));
    }

    [Fact]
    public void Reload_可显式切换合并开关()
    {
        using var tmp = new TempLogFile(Line(T1, 'E', "Tag", "crash"));
        using var doc = LogDocument.Build(tmp.Path, join: false);

        tmp.Append("    at Foo.bar(Foo.java:1)\n");
        doc.Reload(join: true);

        Assert.True(doc.Join);
        Assert.Equal(1, doc.RowCount);
        Assert.True(doc.IsMultiline(0));
    }

    [Fact]
    public void Reload_取消令牌时放弃本次追加()
    {
        using var tmp = new TempLogFile(Line(T1, 'I', "Tag", "a"));
        using var doc = LogDocument.Build(tmp.Path);

        tmp.Append(Line(T2, 'I', "Tag", "b"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Append 内以 ct.IsCancellationRequested 判定取消，返回 unchanged 而非抛异常
        Assert.Equal("unchanged", doc.Reload(ct: cts.Token));
        Assert.Equal(1, doc.RowCount);
    }

    /// <summary>增量索引与一次性全量索引必须得出完全一致的记录。</summary>
    [Fact]
    public void 增量索引与全量索引结果一致()
    {
        string full = Line(T1, 'I', "Tag", "a")
                    + Line(T2, 'E', "Other", "crash")
                    + "    at Foo.bar(Foo.java:1)\n"
                    + Line(T3, 'W', "Tag", "warn")
                    + Line(T4, 'D', "Last", "done");

        using var tmpFull = new TempLogFile(full);
        using var docFull = LogDocument.Build(tmpFull.Path);

        using var tmpInc = new TempLogFile(Line(T1, 'I', "Tag", "a"));
        using var docInc = LogDocument.Build(tmpInc.Path);
        tmpInc.Append(Line(T2, 'E', "Other", "crash") + "    at Foo.bar(Foo.java:1)\n");
        docInc.Reload();
        tmpInc.Append(Line(T3, 'W', "Tag", "warn"));
        docInc.Reload();
        tmpInc.Append(Line(T4, 'D', "Last", "done"));
        docInc.Reload();

        Assert.Equal(docFull.RowCount, docInc.RowCount);
        for (int i = 0; i < docFull.RowCount; i++)
        {
            Assert.Equal(Text(docFull.LineBytes(i)), Text(docInc.LineBytes(i)));
            Assert.Equal(docFull.IsMultiline(i), docInc.IsMultiline(i));
            Assert.Equal(docFull.Ts[i], docInc.Ts[i]);
            Assert.Equal(docFull.Lvl[i], docInc.Lvl[i]);
            Assert.Equal(docFull.TagOf(i), docInc.TagOf(i));
        }
    }

    // ── 扫描区间的行边界对齐（ScanRange 的 start / end 对齐分支）──

    /// <summary>
    /// 回归：上次索引结束时文件末尾正好停在「半行」中间（末尾没有换行），
    /// 下一次追加时 start 就落在行中间。ScanRange 必须按「start-1 是否为换行」判断，
    /// 并跳到下一个换行之后；判断依据取错（看 start 自身首字节）就会白跳一行、吞掉新内容。
    ///
    /// 说明：Build 是一次性全量扫描，末尾没有换行的残行同样会被索引成一条
    /// （与 Append 路径「半行留待下次追加」的策略不同，见 Reload_末尾不完整行暂不索引_补全后才索引）。
    /// 本用例锁定的是「对齐之后不吞掉新行」这一不变式。
    /// </summary>
    [Fact]
    public void Reload_Build时末尾停在半行_追加时不吞新行()
    {
        using var tmp = new TempLogFile(Line(T1, 'I', "Tag", "a")
                                      + "09-23 18:00:01.000  1  2 I Tag: par");   // 末尾无换行
        using var doc = LogDocument.Build(tmp.Path);
        Assert.Equal(2, doc.RowCount);        // 残行也被全量扫描收进索引

        // 先补上换行让残行完整，再追加一条完整记录
        tmp.Append("\n" + Line(T3, 'I', "Tag", "c"));
        Assert.Equal("appended", doc.Reload());

        Assert.Equal(3, doc.RowCount);
        Assert.Equal("09-23 18:00:01.000  1  2 I Tag: par", Text(doc.LineBytes(1)));
        Assert.Equal("09-23 18:00:10.000  1  2 I Tag: c", Text(doc.LineBytes(2)));
    }

    /// <summary>
    /// ScanRange 的 end 对齐：end 已在行边界（end-1 是换行）时不动；落在行中间时**回退到该行行首**。
    /// 不能向后推进——那会把 Append 里 TrimPartial 刚裁掉的尾部残行又读回来，
    /// 使「完整行 + 末尾残行」与「只写残行」两种写入形态得出不同结果。
    /// </summary>
    [Fact]
    public void Reload_一次追加完整行与末尾残行()
    {
        using var tmp = new TempLogFile(Line(T1, 'I', "Tag", "a"));
        using var doc = LogDocument.Build(tmp.Path);
        Assert.Equal(1, doc.RowCount);

        // 完整行 + 无换行的残行，一次写入（流式采集常见的 flush 形态）
        tmp.Append(Line(T2, 'I', "Tag", "b") + "09-23 18:00:10.000  1  2 I Tag: par");
        Assert.Equal("appended", doc.Reload());

        // 完整行照常进索引，残行留待下次追加（与「只写残行」的 unchanged 行为一致）
        Assert.Equal(2, doc.RowCount);
        Assert.Equal("09-23 18:00:01.000  1  2 I Tag: b", Text(doc.LineBytes(1)));

        // 补上换行后残行才进入索引
        tmp.Append("\n");
        Assert.Equal("appended", doc.Reload());
        Assert.Equal(3, doc.RowCount);
        Assert.Equal("09-23 18:00:10.000  1  2 I Tag: par", Text(doc.LineBytes(2)));
    }

    /// <summary>
    /// 直接验证区间扫描的 end 对齐：end 落在行中间时回退到该行行首。
    /// 不能把不完整的尾行当成一条记录——否则流式写入的残行会先进索引，补齐后再来一条，
    /// 同一行裂成两条（Append 路径靠 TrimPartial 规避的正是这个）。
    /// </summary>
    [Fact]
    public void ScanRange_end落在行中间时回退到行首()
    {
        string line1 = Line(T1, 'I', "Tag", "a");
        string line2 = Line(T2, 'I', "Tag", "b");
        using var tmp = new TempLogFile(line1 + line2 + Line(T3, 'I', "Tag", "c"));

        // end 切在第二行中间 → 第二行不完整，只应索引第一行
        var part = LogDocument.ScanRange(tmp.Path, 0, line1.Length + 5, join: true);

        Assert.Single(part.Offs);
        Assert.Equal(0, part.Offs[0]);
        Assert.Equal(line1.Length - 1, part.Lens[0]);      // 记录长度不含行尾换行
    }

    /// <summary>列属性（供 UI / 过滤使用）长度必须跟随索引行数。</summary>
    [Fact]
    public void 列属性长度跟随索引行数()
    {
        using var tmp = new TempLogFile(Line(T1, 'I', "Tag", "a") + Line(T2, 'E', "Other", "b"));
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(2, doc.RowCount);
        Assert.Equal(doc.RowCount, doc.Lens.Length);
        Assert.Equal(doc.RowCount, doc.Moff.Length);
        Assert.Equal(doc.RowCount, doc.Flags.Length);
        Assert.Equal(doc.RowCount, doc.Ts.Length);
    }

    /// <summary>
    /// 长日志（&gt;4 MB）扫描的进度上报与中途取消：进度回调里取消令牌后，
    /// 扫描应在下一个检查点（每 5000 行）抛出，而不是把整份索引跑完。
    /// </summary>
    [Fact]
    public void Build_长日志上报进度并支持中途取消()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 120_000; i++) sb.Append(Line(T1, 'I', "Tag", "filler line " + i));
        using var tmp = new TempLogFile(sb.ToString());
        Assert.True(tmp.Length > (1 << 22), "样本需超过进度上报阈值（4 MB）");

        var progress = new ProgressRecorder();
        using (var doc = LogDocument.Build(tmp.Path, progress: progress))
            Assert.Equal(120_000, doc.RowCount);
        Assert.Contains(progress.Items, p => p.msg.Contains("扫描行") && p.pct > 0);

        using var cts = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(
            () => LogDocument.Build(tmp.Path, progress: new CancelOnFirstReport(cts), ct: cts.Token));
    }

    /// <summary>
    /// 扫描**途中**就要能取消，而不是等扫完整份才在收尾处抛。
    ///
    /// 背景：ScanRange 里每 5000 行有一个取消检查点，把 % 5000 改成 % 1000000
    /// 是等价于「取消能力」的退化 —— 但 DoBuild 收尾处也有一次
    /// ThrowIfCancellationRequested，所以「抛了OperationCanceledException」
    /// 这条断言在两种情况下都成立，抓不住（变异实测全绿）。
    ///
    /// 判据设计（样本 17 万行 ≈ 8.27 MB，进度阈值 4 MB，递进 nextReport = pos + 4 MB）：
    ///   上报序列 = pct 0（扫描开始）→ 0.484（pos 到4 MB）→ 0.968（pos 到 8 MB）→ 0.95（Assemble 收尾）
    ///   在 pct=0.484 处取消后：
    ///     · 原样（每 5000 行 ≈ 0.24 MB）→ 下一个检查点立刻抛，**再无任何上报**
    ///     · 变异（每 100 万行 ≈ 48.6 MB）→ 8.27 MB 样本永远碰不到检查点，
    ///       一路扫完并再报一次 0.968，然后才在收尾处抛
    /// 所以「只收到 1 次 pct&gt;0 的扫描期上报」就是有效判据。
    /// 样本必须 &gt; 8 MB（否则没有第 2 次扫描期上报，两种情况无法区分），
    /// 又必须 &lt; 48.6 MB（否则变异也能碰到检查点）。
    /// </summary>
    [Fact]
    public void Build_取消发生在扫描途中而非扫完后()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 170_000; i++) sb.Append(Line(T1, 'I', "Tag", "filler line " + i));
        using var tmp = new TempLogFile(sb.ToString());
        Assert.True(tmp.Length > (2 << 22), "样本需超过两个进度阈值（8 MB），否则无法区分");
        Assert.True(tmp.Length< (48 << 20), "样本需远小于 100 万行（48.6 MB），否则变异也能碰到检查点");

        using var cts = new CancellationTokenSource();
        var t = new CancelOnFirstMidScanReport(cts);

        Assert.Throws<OperationCanceledException>(
            () => LogDocument.Build(tmp.Path, progress: t, ct: cts.Token));

        // 取消发生在扫描中途，不是起点（0）也不是收尾（0.95）
        Assert.InRange(t.CancelPct, 0.05, 0.9);

        // 关键判据：取消后不得再收到任何上报。
        // 若检查点被改到极稀疏，扫描会继续跑到末尾并再报一次（甚至报 0.95 收尾阶段）。
        Assert.Single(t.Log, p => p > 0);
        Assert.DoesNotContain(t.Log, p => p >= 0.9);
    }

    /// <summary>
    /// EnsureCap：初始容量是「首次索引行数 × 1.25 + 1024」，增量追加超过它时要整体扩容
    /// （9 个列数组 + _cap）。扩容后旧数据与新数据都必须能按行号正确读回，不能错位。
    /// </summary>
    [Fact]
    public void Reload_增量超过初始容量时扩容且数据不错位()
    {
        using var tmp = new TempLogFile(Line(T1, 'I', "Tag", "line0"));
        using var doc = LogDocument.Build(tmp.Path);      // 1 行 → _cap = 1 + 0 + 1024

        const int extra = 1100;                            // 1 + 1100 > 1025，必然触发扩容
        var sb = new StringBuilder();
        for (int i = 1; i <= extra; i++) sb.Append(Line(T2, 'I', "Tag", "line" + i));
        tmp.Append(sb.ToString());
        Assert.Equal("appended", doc.Reload());

        Assert.Equal(extra + 1, doc.RowCount);
        Assert.Equal("09-23 18:00:00.000  1  2 I Tag: line0", Text(doc.LineBytes(0)));      // 扩容前的旧数据
        Assert.Equal("09-23 18:00:01.000  1  2 I Tag: line1", Text(doc.LineBytes(1)));      // 扩容后的新数据
        Assert.Equal("09-23 18:00:01.000  1  2 I Tag: line1100", Text(doc.LineBytes(extra)));
    }
}
