using System.Text;
using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// LogParser：logcat 行解析（六种格式）、横幅行判定、续行判定、时间戳换算。
/// 断言里用到的偏移量都是手数字符串下标得出的，改格式字符串时请同步核对。
/// </summary>
public class LogParserTests
{
    static LogParser.ParseResult Parse(string line) =>
        LogParser.ParseLine(Encoding.UTF8.GetBytes(line));

    static bool BannerOf(string line) => LogParser.IsBanner(Encoding.UTF8.GetBytes(line));

    static bool LooksNew(string line) => LogParser.LooksLikeNewRecord(Encoding.UTF8.GetBytes(line));

    // ── 常量 ──

    [Fact]
    public void 级别名称与编码一致()
    {
        Assert.Equal(8, LogParser.LEVEL_NAME.Length);
        Assert.Equal("?", LogParser.LEVEL_NAME[LogParser.LVL_UNKNOWN]);
        Assert.Equal("V", LogParser.LEVEL_NAME[LogParser.LVL_V]);
        Assert.Equal("A", LogParser.LEVEL_NAME[LogParser.LVL_A]);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7 }, LogParser.ALL_LEVELS);
    }

    // ── threadtime：mm-dd hh:mm:ss.SSS  pid  tid L Tag: msg ──

    [Fact]
    public void threadtime格式_解析全部字段()
    {
        var r = Parse("09-23 18:06:48.123  1234  5678 I ActivityManager: message here");

        // MDAYS[9] = 244（1~8 月累计天数，按闰年编码）
        long expectedWithin = (244L + 23 - 1) * LogParser.DAY_MS
                              + 18 * 3_600_000L + 6 * 60_000L + 48 * 1000L + 123;
        Assert.Equal(expectedWithin, r.WithinMs);
        Assert.Equal(-1, r.Year);              // mm-dd 格式行内无年份
        Assert.Equal(1234, r.Pid);
        Assert.Equal(5678, r.Tid);
        Assert.Equal(LogParser.LVL_I, r.Level);
        Assert.Equal("ActivityManager", r.Tag);
    }

    [Fact]
    public void threadtime格式_message偏移落在正文起始()
    {
        const string line = "09-23 18:06:48.123  1234  5678 I ActivityManager: message here";
        var r = Parse(line);
        Assert.Equal("message here", line[r.MsgOffset..]);
    }

    [Theory]
    [InlineData("09-23 18:06:48.123  1234  5678 V Tag: m", LogParser.LVL_V)]
    [InlineData("09-23 18:06:48.123  1234  5678 D Tag: m", LogParser.LVL_D)]
    [InlineData("09-23 18:06:48.123  1234  5678 W Tag: m", LogParser.LVL_W)]
    [InlineData("09-23 18:06:48.123  1234  5678 E Tag: m", LogParser.LVL_E)]
    [InlineData("09-23 18:06:48.123  1234  5678 F Tag: m", LogParser.LVL_F)]
    [InlineData("09-23 18:06:48.123  1234  5678 A Tag: m", LogParser.LVL_A)]
    public void threadtime格式_各日志级别(string line, int expected)
    {
        Assert.Equal(expected, Parse(line).Level);
    }

    [Fact]
    public void threadtime格式_同时写入时间戳缓存后命中()
    {
        // 首次解析走完整路径，第二次命中 _tsCache；两次结果必须一致
        var a = Parse("09-23 18:06:48.123  1234  5678 I Tag: m");
        var b = Parse("09-23 18:06:48.123  9999  1111 D Other: m");
        Assert.Equal(a.WithinMs, b.WithinMs);
    }

    /// <summary>
    /// 回归：缓存 key 早期用 line[0..fracEnd) 做 key*256+b 累加成 long，18~21 字节超过
    /// 64 位后高位被截断，键实际只由末尾 8 字节（分钟个位:秒.毫秒）决定，
    /// 于是不同日期的同一时刻会命中同一条缓存，时间戳被错算成先出现的那一行。
    /// </summary>
    [Fact]
    public void 时间戳缓存不因日期不同而串号()
    {
        var a = Parse("09-23 18:06:48.123  1  2 I Tag: a");
        var b = Parse("09-24 18:06:48.123  1  2 I Tag: b");

        Assert.NotEqual(a.WithinMs, b.WithinMs);
        Assert.Equal(LogParser.DAY_MS, b.WithinMs - a.WithinMs);   // 相差正好一天
    }

    /// <summary>回归：同一天内「分钟后一位 + 秒 + 毫秒」相同的不同时刻同样不应共用缓存。</summary>
    [Fact]
    public void 时间戳缓存不因末尾字节相同而串号()
    {
        var a = Parse("09-23 18:06:48.123  1  2 I Tag: a");
        var b = Parse("09-23 08:16:48.123  1  2 I Tag: b");

        // 18:06 与 08:16 相差 9 小时 50 分
        Assert.Equal(590 * 60_000L, a.WithinMs - b.WithinMs);
    }

    /// <summary>回归：非法时间字段必须抛异常，不能因为命中缓存而返回脏值。</summary>
    [Fact]
    public void 非法时间字段不会命中缓存而绕过校验()
    {
        Parse("09-23 18:06:48.123  1  2 I Tag: a");           // 先预热缓存
        Assert.Throws<ParseException>(() => Parse("99-99 18:06:48.123  1  2 I Tag: m"));
    }

    /// <summary>回归：带年份的 yyyy 格式同样不能因键截断而串号。</summary>
    [Fact]
    public void 年份格式缓存不因日期不同而串号()
    {
        var a = Parse("2026-09-23 18:06:48.123  1  2 I Tag: a");
        var b = Parse("2026-09-24 18:06:48.123  1  2 I Tag: b");

        Assert.Equal(LogParser.DAY_MS, b.WithinMs - a.WithinMs);
    }

    [Fact]
    public void 年份格式_命中时间戳缓存后返回相同结果()
    {
        var a = Parse("2026-09-23 18:06:48.123  1  2 I Tag: a");
        var b = Parse("2026-09-23 18:06:48.123  9  9 D Other: b");   // 命中 _tsCacheY

        Assert.Equal(a.WithinMs, b.WithinMs);
        Assert.Equal(a.Year, b.Year);
        Assert.Equal(2026, b.Year);
    }

    // ── threadtime 里 tag 采用「L/Tag」写法的变体 ──

    [Fact]
    public void threadtime格式_级别后紧跟斜杠并用括号带pid()
    {
        const string line = "09-23 18:00:00.000  1  2 I/ActivityManager(4321): msg";
        var r = Parse(line);

        Assert.Equal(4321, r.Pid);            // 括号里的 pid 覆盖了前面的字段
        Assert.Equal(2, r.Tid);
        Assert.Equal(LogParser.LVL_I, r.Level);
        Assert.Equal("ActivityManager", r.Tag);
        Assert.Equal("msg", line[r.MsgOffset..]);
    }

    [Fact]
    public void threadtime格式_级别后紧跟斜杠且不带括号()
    {
        const string line = "09-23 18:00:00.000  1  2 I/ActivityManager: msg";
        var r = Parse(line);

        Assert.Equal(1, r.Pid);               // 没有括号时沿用前面的 pid 字段
        Assert.Equal(2, r.Tid);
        Assert.Equal(LogParser.LVL_I, r.Level);
        Assert.Equal("ActivityManager", r.Tag);
        Assert.Equal("msg", line[r.MsgOffset..]);
    }

    [Fact]
    public void threadtime格式_斜杠写法下括号后没有冒号时正文紧贴括号()
    {
        const string line = "09-23 18:00:00.000  1  2 E/Tag(7)boom";
        var r = Parse(line);

        Assert.Equal(7, r.Pid);
        Assert.Equal(LogParser.LVL_E, r.Level);
        Assert.Equal("Tag", r.Tag);
        Assert.Equal("boom", line[r.MsgOffset..]);
    }

    [Fact]
    public void threadtime格式_微秒小数秒截断为毫秒()
    {
        // logcat -v usec：6 位小数秒，只取前 3 位
        var r = Parse("09-23 18:06:48.123456  1  2 I Tag: m");
        long expected = (244L + 23 - 1) * LogParser.DAY_MS
                        + 18 * 3_600_000L + 6 * 60_000L + 48 * 1000L + 123;
        Assert.Equal(expected, r.WithinMs);
    }

    [Fact]
    public void threadtime格式_纳秒小数秒截断为毫秒()
    {
        var r = Parse("09-23 18:06:48.123456789  1  2 I Tag: m");
        long expected = (244L + 23 - 1) * LogParser.DAY_MS
                        + 18 * 3_600_000L + 6 * 60_000L + 48 * 1000L + 123;
        Assert.Equal(expected, r.WithinMs);
    }

    // ── tag 与 message 的多种分隔形式 ──

    [Fact]
    public void threadtime格式_tag右对齐有填充空格时被裁掉()
    {
        var r = Parse("09-23 18:06:48.123  1234  5678 I netd    : msg");
        Assert.Equal("netd", r.Tag);
    }

    [Fact]
    public void threadtime格式_tag冒号后无空格()
    {
        const string line = "09-23 18:06:48.123  1234  5678 I Tag:msg";
        var r = Parse(line);
        Assert.Equal("Tag", r.Tag);
        Assert.Equal("msg", line[r.MsgOffset..]);
    }

    [Fact]
    public void threadtime格式_无冒号时退化为按空格切分()
    {
        var r = Parse("09-23 18:06:48.123  1234  5678 I Tag message here");
        Assert.Equal("Tag", r.Tag);
    }

    [Fact]
    public void threadtime格式_tag后为空时整段作为tag()
    {
        var r = Parse("09-23 18:06:48.123  1234  5678 I OnlyTag");
        Assert.Equal("OnlyTag", r.Tag);
    }

    // ── time 格式：mm-dd hh:mm:ss.SSS L/Tag(pid): msg ──

    [Fact]
    public void time格式_无pid无tid()
    {
        const string line = "09-23 18:06:48.123 I/ActivityManager: msg";
        var r = Parse(line);
        Assert.Equal(-1, r.Pid);
        Assert.Equal(-1, r.Tid);
        Assert.Equal(LogParser.LVL_I, r.Level);
        Assert.Equal("ActivityManager", r.Tag);
        Assert.Equal("msg", line[r.MsgOffset..]);
    }

    [Fact]
    public void time格式_带pid时解析出pid()
    {
        const string line = "09-23 18:06:48.123 I/ActivityManager(4321): msg";
        var r = Parse(line);
        Assert.Equal(4321, r.Pid);
        Assert.Equal(-1, r.Tid);
        Assert.Equal("msg", line[r.MsgOffset..]);
    }

    [Fact]
    public void time格式_只有级别与斜杠时无tag残留()
    {
        var r = Parse("09-23 18:06:48.123 I/");
        Assert.Equal(LogParser.LVL_I, r.Level);
        Assert.Equal("", r.Tag);
    }

    // ── brief 格式：L/Tag(pid): msg ──

    [Fact]
    public void brief格式_带pid()
    {
        const string line = "I/ActivityManager(1234): msg";
        var r = Parse(line);
        Assert.Equal(-1, r.WithinMs);          // brief 无时间
        Assert.Equal(1234, r.Pid);
        Assert.Equal(-1, r.Tid);
        Assert.Equal(LogParser.LVL_I, r.Level);
        Assert.Equal("ActivityManager", r.Tag);
        Assert.Equal("msg", line[r.MsgOffset..]);
    }

    [Fact]
    public void brief格式_pid右对齐带填充空格()
    {
        // 真实 logcat -v brief 的 pid 是右对齐的：括号内有前导空格
        const string line = "I/ActivityManager( 1234): msg";
        var r = Parse(line);
        Assert.Equal(1234, r.Pid);
        Assert.Equal(-1, r.Tid);
        Assert.Equal(LogParser.LVL_I, r.Level);
        Assert.Equal("ActivityManager", r.Tag);
        Assert.Equal("msg", line[r.MsgOffset..]);
    }

    [Fact]
    public void time格式_级别斜杠tag右对齐pid()
    {
        // mm-dd 时间 + brief 式 "L/Tag( pid): msg"
        const string line = "09-26 18:25:06.960 D/ImageView( 1798): xxxx";
        var r = Parse(line);
        Assert.Equal(-1, r.Year);              // mm-dd 格式行内无年份
        Assert.Equal(LogParser.LVL_D, r.Level);
        Assert.Equal("ImageView", r.Tag);
        Assert.Equal(1798, r.Pid);
        Assert.Equal("xxxx", line[r.MsgOffset..]);
    }

    [Fact]
    public void brief格式_括号内全是空格时pid未知()
    {
        const string line = "I/Tag(    ): msg";
        var r = Parse(line);
        Assert.Equal(-1, r.Pid);
        Assert.Equal("Tag", r.Tag);
        Assert.Equal("msg", line[r.MsgOffset..]);
    }

    [Theory]
    [InlineData("I/Tag(abc): msg")]        // 纯字母
    [InlineData("I/Tag(12a): msg")]        // 数字后跟字母
    [InlineData("I/Tag(a12): msg")]        // 字母后跟数字
    [InlineData("I/Tag(-1): msg")]         // 带符号
    [InlineData("I/Tag(1 2): msg")]        // 中间空格
    public void brief格式_括号内非数字时pid未知但其余字段照常解析(string line)
    {
        // TryParseInt 里 `if (b < ZERO || b > NINE) return -1;` 这条脏数据分支：
        // 此前只测了「纯空格」，把该判断改成 if (false) 变异测试全绿。
        // 注意断言的是「解析不抛异常、其余字段仍可用」，而非具体 msg 内容——
        // 脏 pid 时 MsgOffset 的落点由实现决定，锁死会脆。
        var r = Parse(line);
        Assert.Equal(-1, r.Pid);
        Assert.Equal("Tag", r.Tag);
        Assert.Equal(LogParser.LVL_I, r.Level);
    }

    [Fact]
    public void brief格式_无pid()
    {
        const string line = "W/SomeTag: hi";
        var r = Parse(line);
        Assert.Equal(-1, r.Pid);
        Assert.Equal(LogParser.LVL_W, r.Level);
        Assert.Equal("SomeTag", r.Tag);
        Assert.Equal("hi", line[r.MsgOffset..]);
    }

    [Fact]
    public void brief格式_只有级别斜杠时剩余整段作为tag()
    {
        // "I/onlytag" → 无 ':' 也无 '('，落到最后兜底分支：level 仍能识别，tag 是斜杠后的整段
        var r = Parse("I/onlytag");
        Assert.Equal(-1, r.WithinMs);
        Assert.Equal(LogParser.LVL_I, r.Level);
        Assert.Equal("onlytag", r.Tag);
    }

    // ── long 格式（回退正则）：[ mm-dd hh:mm:ss.SSS pid:tid L/Tag ] msg ──

    [Fact]
    public void long格式_解析全部字段()
    {
        const string line = "[ 09-23 18:06:48.123  1234: 5678 I/ActivityManager ] msg";
        var r = Parse(line);
        Assert.Equal(1234, r.Pid);
        Assert.Equal(5678, r.Tid);
        Assert.Equal(LogParser.LVL_I, r.Level);
        Assert.Equal("ActivityManager", r.Tag);
        Assert.Equal("msg", line[r.MsgOffset..]);
    }

    [Fact]
    public void long格式_带年份()
    {
        var r = Parse("[ 2026-09-23 18:06:48.123  1234: 5678 I/Tag ] msg");
        Assert.Equal(2026, r.Year);
        Assert.Equal(1234, r.Pid);
    }

    [Fact]
    public void long格式_省略pid与tid()
    {
        var r = Parse("[ 09-23 18:06:48.123 I/Tag ] msg");
        Assert.Equal(-1, r.Pid);
        Assert.Equal(-1, r.Tid);
        Assert.Equal(LogParser.LVL_I, r.Level);
        Assert.Equal("Tag", r.Tag);
    }

    [Fact]
    public void long格式_微秒小数秒按前三位取毫秒()
    {
        var r = Parse("[ 09-23 18:06:48.123456  1: 2 I/Tag ] msg");
        long expected = (244L + 23 - 1) * LogParser.DAY_MS
                        + 18 * 3_600_000L + 6 * 60_000L + 48 * 1000L + 123;
        Assert.Equal(expected, r.WithinMs);
    }

    [Fact]
    public void long格式_不足三位小数秒右侧补零()
    {
        var r = Parse("[ 09-23 18:06:48.5  1: 2 I/Tag ] msg");
        long expected = (244L + 23 - 1) * LogParser.DAY_MS
                        + 18 * 3_600_000L + 6 * 60_000L + 48 * 1000L + 500;
        Assert.Equal(expected, r.WithinMs);
    }

    [Fact]
    public void long格式_时间字段越界时时间戳置为负一()
    {
        // 25 点 → MsOf 抛异常 → ParseSlow 内部 catch 后 within = -1
        var r = Parse("[ 09-23 25:06:48.123  1: 2 I/Tag ] msg");
        Assert.Equal(-1, r.WithinMs);
        Assert.Equal(LogParser.LVL_I, r.Level);   // 其余字段仍然解析出来
    }

    // ── 完全无法解析 ──

    [Theory]
    [InlineData("garbage line")]
    [InlineData("hello world")]
    [InlineData("2026/09/23 18:06:48")]
    public void 无法识别的行返回未知(string line)
    {
        var r = Parse(line);
        Assert.Equal(-1, r.WithinMs);
        Assert.Equal(-1, r.Pid);
        Assert.Equal(-1, r.Tid);
        Assert.Equal(LogParser.LVL_UNKNOWN, r.Level);
        Assert.Equal("", r.Tag);
    }

    [Fact]
    public void 空行返回未知()
    {
        var r = LogParser.ParseLine(ReadOnlySpan<byte>.Empty);
        Assert.Equal(-1, r.WithinMs);
        Assert.Equal(-1, r.Year);
        Assert.Equal(LogParser.LVL_UNKNOWN, r.Level);
        Assert.Equal(0, r.MsgOffset);
    }

    // ── 时间戳异常 ──

    [Fact]
    public void 快速路径_月份越界抛ParseException()
    {
        var ex = Assert.Throws<ParseException>(() => Parse("99-99 18:06:48.123  1  2 I Tag: m"));
        Assert.Equal("bad mmdd timestamp", ex.Message);
    }

    [Fact]
    public void 快速路径_小时越界抛ParseException()
    {
        Assert.Throws<ParseException>(() => Parse("09-23 25:06:48.123  1  2 I Tag: m"));
    }

    [Fact]
    public void 快速路径_年份格式越界抛ParseException()
    {
        var ex = Assert.Throws<ParseException>(() => Parse("2026-99-99 18:06:48.123  1  2 I Tag: m"));
        Assert.Equal("bad yyyy timestamp", ex.Message);
    }

    // ── 头部缺少时间戳但仍要认级别 ──

    [Fact]
    public void 无时间戳的threadtime片段仍可解析级别与tag()
    {
        var r = Parse("1234  5678 I Tag: msg");
        Assert.Equal(-1, r.WithinMs);
        Assert.Equal(1234, r.Pid);
        Assert.Equal(5678, r.Tid);
        Assert.Equal(LogParser.LVL_I, r.Level);
        Assert.Equal("Tag", r.Tag);
    }

    // ── IsBanner ──

    [Theory]
    [InlineData("--------- beginning of main")]
    [InlineData("--------- switch to main")]
    [InlineData("----- timezone:Asia/Kuala_Lumpur")]
    [InlineData("----------")]
    [InlineData("-----")]
    public void 横幅行_判定为真(string line)
    {
        Assert.True(BannerOf(line));
        Assert.True(LogParser.IsBanner(line));
    }

    [Theory]
    [InlineData("----")]                 // 只有 4 个短横
    [InlineData("-----abc")]             // 短横后紧跟非空白
    [InlineData("")]
    [InlineData("09-23 18:06:48.123  1  2 I Tag: m")]
    [InlineData("-- --")]
    public void 非横幅行_判定为假(string line)
    {
        Assert.False(BannerOf(line));
        Assert.False(LogParser.IsBanner(line));
    }

    [Fact]
    public void 横幅行_制表符分隔也算横幅()
    {
        // 两个重载都要覆盖：byte[] 走文件扫描路径，string 走文本读取器路径
        Assert.True(BannerOf("-----\tbeginning of main"));
        Assert.True(LogParser.IsBanner("-----\tbeginning of main"));
    }

    [Fact]
    public void 字符串版横幅判定_制表符分隔()
    {
        // 变异测试实测：string 重载去掉 line[j]=='\t' 后全绿，说明此前只测了 byte[] 重载。
        // 语义与 byte[] 重载一致：TAB 本身就是合法分隔符，其后是否跟内容不影响判定
        // （对照「非横幅行」用例里的 "-----abc"：分隔符是字母才判否）。
        Assert.True(LogParser.IsBanner("-----\tbeginning of main"));
        Assert.True(LogParser.IsBanner("-----\t"));
        Assert.True(LogParser.IsBanner("-----\tabc"));
        Assert.False(LogParser.IsBanner("----abc"));   // 5 个短横后直接跟字母才是非横幅
    }

    [Fact]
    public void 字符串版横幅判定_剥离UTF8BOM()
    {
        Assert.True(LogParser.IsBanner("\uFEFF----- beginning of main"));
    }

    // ── LooksLikeNewRecord ──

    [Theory]
    [InlineData("09-23 18:06:48.123  1234  5678 I Tag: m")]  // threadtime
    [InlineData("2026-09-23 18:06:48.123  1  2 I Tag: m")]   // ymd
    [InlineData("[ 09-23 18:06:48.123  1: 2 I/Tag ] m")]     // long
    [InlineData("--------- beginning of main")]              // 横幅
    [InlineData("I/Tag: m")]                                 // brief
    [InlineData("")]
    public void 新记录开头判定为真(string line)
    {
        Assert.True(LooksNew(line));
    }

    [Theory]
    [InlineData("    at com.example.Foo.bar(Foo.java:42)")]  // 缩进的堆栈帧
    [InlineData("\tat com.example.Foo.bar(Foo.java:42)")]    // tab 缩进
    [InlineData("Caused by: java.lang.IllegalStateException")] // 非缩进且非已知前缀
    [InlineData("plain text")]
    public void 续行判定为假(string line)
    {
        Assert.False(LooksNew(line));
    }

    // ── 慢路径回退 ──

    /// <summary>
    /// 只有「行首为 '['」才会走慢路径正则；连方括号形式也不匹配时返回空结果，
    /// 由调用方按续行 / 正文处理。其余格式（日期起首、级别起首）都由快速路径处理。
    /// </summary>
    [Fact]
    public void 方括号开头但格式不匹配时返回空结果()
    {
        var r = Parse("[ 这不是一条方括号记录");

        Assert.Equal(-1, r.WithinMs);
        Assert.Equal(-1, r.Year);
        Assert.Equal(-1, r.Pid);
        Assert.Equal(-1, r.Tid);
        Assert.Equal(LogParser.LVL_UNKNOWN, r.Level);
        Assert.Equal("", r.Tag);
        Assert.Equal(0, r.MsgOffset);
    }

    // ── IsLeap ──

    [Theory]
    [InlineData(2024, true)]
    [InlineData(2023, false)]
    [InlineData(2000, true)]    // 400 整除
    [InlineData(1900, false)]   // 100 整除但非 400
    [InlineData(2026, false)]
    public void 闰年判定(int year, bool expected)
    {
        Assert.Equal(expected, LogParser.IsLeap(year));
    }

    [Fact]
    public void 闰年边界常量等于二月底累计毫秒()
    {
        Assert.Equal(60 * LogParser.DAY_MS, LogParser.LEAP_BOUNDARY);
    }
}
