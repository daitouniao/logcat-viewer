using System.Text;
using System.Text.RegularExpressions;

namespace logcat.Services;

/// <summary>
/// logcat 行解析。支持 threadtime / time / long / brief / tag / ymd 六种格式自动识别。
/// 快速路径（字节级索引）+ 回退正则，保证千万行级扫描的吞吐。
/// </summary>
public static class LogParser
{
    // ── 级别编码 ──
    public const int LVL_UNKNOWN = 0;
    public const int LVL_V = 1, LVL_D = 2, LVL_I = 3, LVL_W = 4, LVL_E = 5, LVL_F = 6, LVL_A = 7;

    public static readonly string[] LEVEL_NAME = { "?", "V", "D", "I", "W", "E", "F", "A" };

    public static readonly int[] ALL_LEVELS = { 1, 2, 3, 4, 5, 6, 7 };

    // ── 字节常量 ──
    const byte DASH = 45, SP = 32, COLON = 58, DOT = 46, SLASH = 47;
    const byte LPAR = 40, RPAR = 41, LBRACKET = 91;
    const byte ZERO = 48, NINE = 57, TAB = 9, CR = 13;

    static readonly byte[] DASHES = "---------"u8.ToArray();

    public const long DAY_MS = 86_400_000;
    /// <summary>每月 1 日之前的累计天数（按闰年计算，1-based 月份索引）。</summary>
    static readonly int[] MDAYS = { 0, 0, 31, 60, 91, 121, 152, 182, 213, 244, 274, 305, 335 };
    public const long LEAP_BOUNDARY = 60 * DAY_MS;

    // ── 时间戳缓存 ──
    static readonly Dictionary<long, long> _tsCache = new(4_000_000);
    static readonly Dictionary<long, (long within, int year)> _tsCacheY = new(4_000_000);
    const int CACHE_LIMIT = 4_000_000;

    // ── 级别字符 -> 编码 ──
    static readonly Dictionary<byte, int> _lc = new()
    {
        [(byte)'V'] = LVL_V, [(byte)'D'] = LVL_D, [(byte)'I'] = LVL_I,
        [(byte)'W'] = LVL_W, [(byte)'E'] = LVL_E, [(byte)'F'] = LVL_F,
        [(byte)'A'] = LVL_A,
    };

    // ── 回退正则 ──
    const string DATE = @"(?:(\d{4})-)?(\d{2})-(\d{2}) ";
    // 小数秒：3 位（ms）/ 6 位（usec）/ 9 位（nsec），logcat -v usec/nsec 时出现
    const string TIME = @"(\d{2}):(\d{2}):(\d{2})[.,](\d{1,9})";

    static readonly Regex ReLong = new(
        @"^\[\s*" + DATE + TIME + @"\s+(?:(\d+):\s*)?(\d+)?\s*([VDIWEFA])/([^\]]*?)\s*\]\s?(.*)$",
        RegexOptions.Compiled | RegexOptions.Singleline);

    static readonly Regex ReThread = new(
        @"^" + DATE + TIME + @"\s+(\d+)\s+(\d+)\s+([VDIWEFA])\s+(.*)$",
        RegexOptions.Compiled | RegexOptions.Singleline);

    static readonly Regex ReTime = new(
        @"^" + DATE + TIME + @"\s+([VDIWEFA])/(.*)$",
        RegexOptions.Compiled | RegexOptions.Singleline);

    static readonly Regex ReBrief = new(
        @"^([VDIWEFA])/(.*)$",
        RegexOptions.Compiled | RegexOptions.Singleline);

    // ── 解析结果 ──
    public readonly struct ParseResult
    {
        public readonly long WithinMs;   // 年内毫秒，-1 表示无时间
        public readonly int Year;        // 行内年份，-1 表示未给出
        public readonly int Pid;         // -1 表示未知
        public readonly int Tid;         // -1 表示未知
        public readonly int Level;       // 0 表示未知
        public readonly string Tag;      // 原始 tag
        public readonly int MsgOffset;   // message 在行内的起始偏移

        public ParseResult(long withinMs, int year, int pid, int tid, int level, string tag, int msgOffset)
        {
            WithinMs = withinMs; Year = year; Pid = pid; Tid = tid;
            Level = level; Tag = tag; MsgOffset = msgOffset;
        }
    }

    public static ParseResult ParseLine(ReadOnlySpan<byte> line)
    {
        int n = line.Length;
        if (n == 0)
            return new ParseResult(-1, -1, -1, -1, LVL_UNKNOWN, "", 0);

        // ── 快速路径 ──
        if (line[0] != LBRACKET)
        {
            long ts = -1;
            int year = -1;
            int p = 0;

            // 头部：mm-dd hh:mm:ss.<1~9位小数秒>（3位=ms，6位=usec，9位=nsec）
            if (n >= 16 && line[2] == DASH && line[5] == SP
                && line[8] == COLON && line[11] == COLON && line[14] == DOT)
            {
                int f = 15;
                while (f < n && line[f] >= ZERO && line[f] <= NINE) f++;
                if (f > 15)
                {
                    (ts, year) = TsMmdd(line, f);
                    p = f;
                }
            }
            else if (n >= 21 && line[4] == DASH && line[7] == DASH && line[10] == SP
                     && line[13] == COLON && line[16] == COLON && line[19] == DOT)
            {
                int f = 20;
                while (f < n && line[f] >= ZERO && line[f] <= NINE) f++;
                if (f > 20)
                {
                    (ts, year) = TsYyyy(line, f);
                    p = f;
                }
            }

            // 跳过字段间空白
            while (p < n && line[p] == SP) p++;

            if (p < n)
            {
                // threadtime: pid tid L Tag: msg
                int q = p;
                while (q < n && line[q] >= ZERO && line[q] <= NINE) q++;
                if (q > p && q < n && line[q] == SP)
                {
                    int r = q;
                    while (r < n && line[r] == SP) r++;
                    int s = r;
                    while (s < n && line[s] >= ZERO && line[s] <= NINE) s++;
                    if (s > r && s < n && (line[s] == SP || line[s] == SLASH))
                    {
                        int t = s;
                        while (t < n && line[t] == SP) t++;
                        int lvl = t < n && _lc.TryGetValue(line[t], out var lv) ? lv : 0;
                        if (lvl != 0 && t + 1 < n && line[t + 1] == SP)
                        {
                            var (tag, mo) = SplitTagMsg(line, t + 2, n);
                            return new ParseResult(ts, year, BytesToInt(line[p..q]), BytesToInt(line[r..s]), lvl, tag, mo);
                        }
                        if (lvl != 0 && t + 1 < n && line[t + 1] == SLASH)
                        {
                            int u = t + 2;
                            int b = IndexOf(line, LPAR, u);
                            if (b > 0)
                            {
                                string tag = Encoding.UTF8.GetString(line[u..b]);
                                int e = IndexOf(line, RPAR, b);
                                if (e > 0)
                                {
                                    int pid = TryParseInt(line[(b + 1)..e]);
                                    int mo = e + 1;
                                    if (mo + 1 < n && line[mo] == COLON)
                                    {
                                        mo++;
                                        if (mo < n && line[mo] == SP) mo++;
                                    }
                                    return new ParseResult(ts, year, pid, BytesToInt(line[r..s]), lvl, tag, mo);
                                }
                            }
                            int c = IndexOf(line, COLON, u);
                            if (c > 0)
                            {
                                string tag = Encoding.UTF8.GetString(line[u..c]);
                                int mo = c + 2 < n && line[c + 1] == SP ? c + 2 : c + 1;
                                return new ParseResult(ts, year, BytesToInt(line[p..q]), BytesToInt(line[r..s]), lvl, tag, mo);
                            }
                        }
                    }
                }

                // time / brief / tag: L/Tag(pid): msg
                int lvl2 = _lc.TryGetValue(line[p], out var lv2) ? lv2 : 0;
                if (lvl2 != 0 && p + 1 < n && line[p + 1] == SLASH)
                {
                    int u = p + 2;
                    int b = IndexOf(line, LPAR, u);
                    int c = IndexOf(line, COLON, u);
                    if (b > 0 && (c < 0 || b < c))
                    {
                        string tag = Encoding.UTF8.GetString(line[u..b]);
                        int pid = -1;
                        int mo = n;
                        int e = IndexOf(line, RPAR, b);
                        if (e > 0)
                        {
                            pid = TryParseInt(line[(b + 1)..e]);
                            mo = e + 1;
                            if (mo < n && line[mo] == COLON)
                            {
                                mo++;
                                if (mo < n && line[mo] == SP) mo++;
                            }
                        }
                        return new ParseResult(ts, year, pid, -1, lvl2, tag, mo);
                    }
                    if (c > 0)
                    {
                        string tag = Encoding.UTF8.GetString(line[u..c]);
                        int mo = c + 1;
                        if (mo < n && line[mo] == SP) mo++;
                        return new ParseResult(ts, year, -1, -1, lvl2, tag, mo);
                    }
                    return new ParseResult(ts, year, -1, -1, lvl2, Encoding.UTF8.GetString(line[u..]), n);
                }
            }
            return new ParseResult(ts, year, -1, -1, LVL_UNKNOWN, "", 0);
        }

        // ── 回退：正则 ──
        return ParseSlow(line, n);
    }

    // ── 慢路径正则解析 ──
    static ParseResult ParseSlow(ReadOnlySpan<byte> line, int n)
    {
        string text = Encoding.UTF8.GetString(line);

        var m = ReLong.Match(text);
        if (m.Success)
        {
            int? y = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : null;
            int mo = int.Parse(m.Groups[2].Value), d = int.Parse(m.Groups[3].Value);
            int h = int.Parse(m.Groups[4].Value), mi = int.Parse(m.Groups[5].Value);
            int s = int.Parse(m.Groups[6].Value), ms = NormMs(m.Groups[7].Value);
            long within; int year;
            try { (within, year) = MsOf(y, mo, d, h, mi, s, ms); }
            catch { within = -1; year = -1; }
            int pid = m.Groups[8].Success ? int.Parse(m.Groups[8].Value) : -1;
            int tid = m.Groups[9].Success ? int.Parse(m.Groups[9].Value) : -1;
            int lvl = _lc.TryGetValue((byte)m.Groups[10].Value[0], out var lv) ? lv : 0;
            string tag = m.Groups[11].Value;
            string msgText = m.Groups[12].Value;
            int msgOff = n - msgText.Length;
            return new ParseResult(within, year, pid, tid, lvl, tag, msgOff);
        }

        m = ReThread.Match(text);
        if (m.Success)
        {
            int? y = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : null;
            int mo = int.Parse(m.Groups[2].Value), d = int.Parse(m.Groups[3].Value);
            int h = int.Parse(m.Groups[4].Value), mi = int.Parse(m.Groups[5].Value);
            int s = int.Parse(m.Groups[6].Value), ms = NormMs(m.Groups[7].Value);
            long within; int year;
            try { (within, year) = MsOf(y, mo, d, h, mi, s, ms); }
            catch { within = -1; year = -1; }
            int pid = int.Parse(m.Groups[8].Value);
            int tid = int.Parse(m.Groups[9].Value);
            int lvl = _lc.TryGetValue((byte)m.Groups[10].Value[0], out var lv) ? lv : 0;
            string rest = m.Groups[11].Value;
            byte[] restBytes = Encoding.UTF8.GetBytes(rest);
            var (tag, mo2) = SplitTagMsg(restBytes, 0, restBytes.Length);
            int prefixLen = n - rest.Length;
            return new ParseResult(within, year, pid, tid, lvl, tag, prefixLen + mo2);
        }

        m = ReTime.Match(text);
        if (m.Success)
        {
            int? y = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : null;
            int mo = int.Parse(m.Groups[2].Value), d = int.Parse(m.Groups[3].Value);
            int h = int.Parse(m.Groups[4].Value), mi = int.Parse(m.Groups[5].Value);
            int s = int.Parse(m.Groups[6].Value), ms = NormMs(m.Groups[7].Value);
            long within; int year;
            try { (within, year) = MsOf(y, mo, d, h, mi, s, ms); }
            catch { within = -1; year = -1; }
            int lvl = _lc.TryGetValue((byte)m.Groups[8].Value[0], out var lv) ? lv : 0;
            string rest = m.Groups[9].Value;
            int bIdx = rest.IndexOf('(');
            string tag = bIdx > 0 ? rest[..bIdx] : rest;
            int pid = -1;
            int off = n;
            if (bIdx > 0)
            {
                int eIdx = rest.IndexOf(')', bIdx);
                if (eIdx > 0)
                {
                    pid = TryParseInt(rest[(bIdx + 1)..eIdx]);
                    off = (n - rest.Length) + eIdx + 1;
                    if (off < n && line[off] == COLON)
                    {
                        off++;
                        if (off < n && line[off] == SP) off++;
                    }
                }
            }
            return new ParseResult(within, year, pid, -1, lvl, tag, off);
        }

        m = ReBrief.Match(text);
        if (m.Success)
        {
            int lvl = _lc.TryGetValue((byte)m.Groups[1].Value[0], out var lv) ? lv : 0;
            string rest = m.Groups[2].Value;
            int bIdx = rest.IndexOf('(');
            string tag = bIdx > 0 ? rest[..bIdx] : rest;
            int pid = -1;
            int off = n;
            if (bIdx > 0)
            {
                int eIdx = rest.IndexOf(')', bIdx);
                if (eIdx > 0)
                {
                    pid = TryParseInt(rest[(bIdx + 1)..eIdx]);
                    off = (n - rest.Length) + eIdx + 1;
                    if (off < n && line[off] == COLON)
                    {
                        off++;
                        if (off < n && line[off] == SP) off++;
                    }
                }
            }
            return new ParseResult(-1, -1, pid, -1, lvl, tag, off);
        }

        return new ParseResult(-1, -1, -1, -1, LVL_UNKNOWN, "", 0);
    }

    // ── 时间戳解析 ──
    /// <summary>小数秒 [start, end) 截断为毫秒：不足 3 位右侧补零，超出 3 位截断。</summary>
    static int FracToMs(ReadOnlySpan<byte> line, int start, int end)
    {
        int ms = 0;
        for (int i = start, scale = 100; i < end && scale > 0; i++, scale /= 10)
            ms += (line[i] - ZERO) * scale;
        return ms;
    }

    static int NormMs(string frac)
    {
        int ms = 0;
        for (int i = 0; i < 3; i++)
            ms = ms * 10 + (i < frac.Length ? frac[i] - '0' : 0);
        return ms;
    }

    static (long within, int year) TsMmdd(ReadOnlySpan<byte> line, int fracEnd)
    {
        int mo = (line[0] - ZERO) * 10 + (line[1] - ZERO);
        int d = (line[3] - ZERO) * 10 + (line[4] - ZERO);
        int h = (line[6] - ZERO) * 10 + (line[7] - ZERO);
        int mi = (line[9] - ZERO) * 10 + (line[10] - ZERO);
        int s = (line[12] - ZERO) * 10 + (line[13] - ZERO);
        int ms = FracToMs(line, 15, fracEnd);

        if (mo < 1 || mo > 12 || d < 1 || d > 31 || h > 23 || mi > 59 || s > 60)
            throw new ParseException("bad mmdd timestamp");

        // 缓存键必须覆盖全部时间字段。早期实现是把 line[0..fracEnd) 按 key = key * 256 + b
        // 累加成 long：前缀有 18~21 字节，而 long 只有 64 位，高位被截断后键实际只由末尾
        // 8 字节（"分钟个位:秒.毫秒"）决定，于是 09-23 18:06:48.123 与 09-24 18:06:48.123
        // 会命中同一条缓存，时间戳被错算成先出现的那一行；越界行也因为命中脏缓存而不抛异常。
        // 改为按字段拼接：各字段位段互不重叠，不可能碰撞。
        long key = (((((long)mo * 32 + d) * 24 + h) * 60 + mi) * 60 + s) * 1000 + ms;

        lock (_tsCache)
        {
            if (_tsCache.TryGetValue(key, out long hit))
                return (hit, -1);
        }

        var (within, year) = MsOf(null, mo, d, h, mi, s, ms);
        lock (_tsCache)
        {
            if (_tsCache.Count < CACHE_LIMIT)
                _tsCache[key] = within;
        }
        return (within, year);
    }

    static (long within, int year) TsYyyy(ReadOnlySpan<byte> line, int fracEnd)
    {
        int y = (line[0] - ZERO) * 1000 + (line[1] - ZERO) * 100 + (line[2] - ZERO) * 10 + (line[3] - ZERO);
        int mo = (line[5] - ZERO) * 10 + (line[6] - ZERO);
        int d = (line[8] - ZERO) * 10 + (line[9] - ZERO);
        int h = (line[11] - ZERO) * 10 + (line[12] - ZERO);
        int mi = (line[14] - ZERO) * 10 + (line[15] - ZERO);
        int s = (line[17] - ZERO) * 10 + (line[18] - ZERO);
        int ms = FracToMs(line, 20, fracEnd);

        if (mo < 1 || mo > 12 || d < 1 || d > 31 || h > 23 || mi > 59 || s > 60)
            throw new ParseException("bad yyyy timestamp");

        // 同上：按字段拼接缓存键，避免高位截断造成的碰撞。
        long key = ((((((long)y * 100 + mo) * 32 + d) * 24 + h) * 60 + mi) * 60 + s) * 1000 + ms;

        lock (_tsCacheY)
        {
            if (_tsCacheY.TryGetValue(key, out var hit))
                return hit;
        }

        var result = MsOf(y, mo, d, h, mi, s, ms);
        lock (_tsCacheY)
        {
            if (_tsCacheY.Count < CACHE_LIMIT)
                _tsCacheY[key] = result;
        }
        return result;
    }

    static (long within, int year) MsOf(int? y, int mo, int d, int h, int mi, int s, int ms)
    {
        if (mo < 1 || mo > 12 || d < 1 || d > 31 || h > 23 || mi > 59 || s > 60 || ms > 999)
            throw new ParseException("bad time field");
        long within = ((long)MDAYS[mo] + d - 1) * DAY_MS + h * 3_600_000L + mi * 60_000L + s * 1000L + ms;
        return (within, y ?? -1);
    }

    // ── Tag / Message 切分 ──
    static (string tag, int msgOffset) SplitTagMsg(ReadOnlySpan<byte> line, int start, int n)
    {
        int c = IndexOf(line, COLON, start);
        if (c > 0)
        {
            // tag 内不会出现空格；若有，则是右对齐格式的填充，如 "netd    : msg"
            int tagEnd = c;
            while (tagEnd > start && line[tagEnd - 1] == SP) tagEnd--;
            int mo = c + 1;
            if (mo < n && line[mo] == SP) mo++;
            return (Encoding.UTF8.GetString(line[start..tagEnd]), mo);
        }
        int sp2 = IndexOf(line, SP, start);
        if (sp2 < 0)
            return (Encoding.UTF8.GetString(line[start..]), n);
        return (Encoding.UTF8.GetString(line[start..sp2]), sp2 + 1);
    }

    // ── 横幅行判定 ──
    /// <summary>
    /// 判定一行是否为 logcat 分段横幅 / ROM 自报头。
    /// 典型：<c>--------- beginning of main</c>、<c>--------- switch to system</c>、
    /// <c>----- timezone:Asia/Kuala_Lumpur</c>。
    /// 这类行不是日志记录：不参与索引，也不参与续行合并。
    /// </summary>
    public static bool IsBanner(ReadOnlySpan<byte> line)
    {
        int d = 0;
        while (d < line.Length && line[d] == DASH) d++;
        if (d < 5) return false;
        return d >= line.Length || line[d] == SP || line[d] == TAB;
    }

    /// <inheritdoc cref="IsBanner(ReadOnlySpan{byte})"/>
    public static bool IsBanner(string line)
    {
        int i = 0;
        if (line.Length > 0 && line[0] == '\uFEFF') i = 1;   // 文本读取器可能带出 BOM
        int d = 0;
        while (i + d < line.Length && line[i + d] == '-') d++;
        if (d < 5) return false;
        int j = i + d;
        return j >= line.Length || line[j] == ' ' || line[j] == '\t';
    }

    // ── 续行判定 ──
    /// <summary>
    /// 判定一行是否像「一条新记录的开头」。
    /// 缩进的堆栈帧、Caused by: 等视为续行。
    /// </summary>
    public static bool LooksLikeNewRecord(ReadOnlySpan<byte> line)
    {
        int n = line.Length;
        if (n == 0) return true;
        if (IsBanner(line)) return true;
        byte c = line[0];
        if (c == SP || c == TAB) return false;
        if (c == LBRACKET) return true;
        if (n >= 9 && line[..9].SequenceEqual(DASHES)) return true;
        if (n >= 19 && line[2] == DASH && line[5] == SP && line[8] == COLON && line[11] == COLON) return true;
        if (n >= 24 && line[4] == DASH && line[7] == DASH && line[10] == SP && line[13] == COLON) return true;
        if (n >= 3 && line[1] == SLASH && _lc.ContainsKey(c)) return true;
        return false;
    }

    // ── 辅助 ──
    static int IndexOf(ReadOnlySpan<byte> span, byte value, int start)
    {
        int idx = span[start..].IndexOf(value);
        return idx < 0 ? -1 : start + idx;
    }

    static int BytesToInt(ReadOnlySpan<byte> span)
    {
        int result = 0;
        for (int i = 0; i < span.Length; i++)
            result = result * 10 + (span[i] - ZERO);
        return result;
    }

    static int TryParseInt(ReadOnlySpan<byte> span)
    {
        int result = 0;
        for (int i = 0; i < span.Length; i++)
        {
            byte b = span[i];
            if (b < ZERO || b > NINE) return -1;
            result = result * 10 + (b - ZERO);
        }
        return result;
    }

    static int TryParseInt(string s)
    {
        return int.TryParse(s.Trim(), out int v) ? v : -1;
    }

    public static bool IsLeap(int year) =>
        year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);
}

public class ParseException : Exception
{
    public ParseException(string message) : base(message) { }
}
