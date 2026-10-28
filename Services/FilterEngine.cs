using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using logcat.Services;

namespace logcat.Services;

/// <summary>
/// 过滤引擎：把一份 FilterSpec 变成「升序的文档行号数组」。
/// </summary>
public static class FilterEngine
{
    const int BLOCK_SIZE = 1 << 22;   // 4 MB 预筛分块
    const int BATCH = 20_000;

    // 注意：本文件的 Regex 仅用于 SplitTerms/ParseMinutes/ParseInts 的词法切分，
    // 过滤匹配一律走字节级子串比较（MatchAny），不支持用户侧正则。

    // ── 文本切分 ──
    // 统一用空格分割多个词；引号包裹的短语视为一个词；逗号/分号/换行仍作为额外分隔符兼容旧用法
    public static List<string> SplitTerms(string text, string op)
    {
        text = text.Trim();
        if (string.IsNullOrEmpty(text)) return new();
        var out_ = new List<string>();
        var buf = new StringBuilder();
        char quote = '\0';

        void Flush()
        {
            var s = buf.ToString().Trim();
            buf.Clear();
            if (!string.IsNullOrEmpty(s)) out_.Add(s);
        }

        foreach (char ch in text)
        {
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0';
                else buf.Append(ch);
                continue;
            }
            if (ch == '"' || ch == '\'') { quote = ch; continue; }
            if (ch == ' ' || ch == ',' || ch == ';' || ch == '\n' || ch == '\r') { Flush(); continue; }
            buf.Append(ch);
        }
        Flush();
        return out_;
    }

    public static int[] ParseMinutes(string text)
    {
        text = text.Trim();
        if (string.IsNullOrEmpty(text)) return Array.Empty<int>();
        var out_ = new List<int>();
        foreach (var part in Regex.Split(text, @"[,\s;]+"))
        {
            var p = part.Trim();
            if (string.IsNullOrEmpty(p)) continue;
            if (!int.TryParse(p, out int v))
                throw new ArgumentException($"分钟不是整数：{p}");
            if (v < 0 || v > 59)
                throw new ArgumentException($"分钟应在 0~59 之间：{v}");
            out_.Add(v);
        }
        return out_.ToArray();
    }

    public static int[] ParseInts(string text, string name)
    {
        text = text.Trim();
        if (string.IsNullOrEmpty(text)) return Array.Empty<int>();
        var out_ = new List<int>();
        foreach (var part in Regex.Split(text, @"[,;\s]+"))
        {
            var p = part.Trim();
            if (string.IsNullOrEmpty(p)) continue;
            if (!int.TryParse(p, out int v))
                throw new ArgumentException($"{name} 不是整数：{p}");
            out_.Add(v);
        }
        return out_.ToArray();
    }

    // ── Tag 匹配 ──
    static HashSet<int> TagIds(LogDocument doc, Models.FilterSpec spec)
    {
        var result = new HashSet<int>();
        if (spec.Tags.Length == 0 || doc.Tags.Count == 0) return result;

        bool opAnd = spec.TagOp == "and";

        var keys = spec.Tags.Select(t =>
        {
            var bytes = Encoding.UTF8.GetBytes(t);
            return spec.TagCase ? bytes : Encoding.UTF8.GetBytes(t.ToLowerInvariant());
        }).ToArray();

        for (int i = 0; i < doc.Tags.Count; i++)
        {
            string tagStr = doc.Tags[i];
            // Tag 列表固定，probe 只需编码一次（term 已在 keys 里预编码好）
            var probe = spec.TagCase
                ? Encoding.UTF8.GetBytes(tagStr)
                : Encoding.UTF8.GetBytes(tagStr.ToLowerInvariant());
            bool ok = opAnd
                ? keys.All(k => probe.AsSpan().IndexOf(k) >= 0)
                : keys.Any(k => probe.AsSpan().IndexOf(k) >= 0);
            if (ok) result.Add(i);
        }
        return result;
    }

    /// <summary>
    /// 就地把纯 ASCII 缓冲区折成小写；含非 ASCII 字节时原样返回 false（调用方需回退）。
    /// 依据：UTF-8 所有多字节序列的首字节与后续字节都 >= 0x80，因此「字节 &lt; 0x80」
    /// 精确等价于「该字节是单字节 ASCII 字符」，其大小写折叠与 Unicode 结论一致。
    /// 非 ASCII 区域必须整体回退 —— 那里有 ß/ẞ、İ 等会改变长度或字节数的规则。
    /// 已用 11190 组输入（含 ßẞ、İı、Σσς、非法 UTF-8 序列）验证与
    /// <c>GetBytes(GetString(x).ToLowerInvariant())</c> 逐字节一致。
    /// </summary>
    internal static bool FoldLowerAsciiInPlace(Span<byte> buf)
    {
        for (int i = 0; i < buf.Length; i++)
        {
            byte b = buf[i];
            if (b >= 0x80) return false;      // 非 ASCII：交给调用方回退，不做部分折叠
            if (b >= (byte)'A' && b <= (byte)'Z') buf[i] = (byte)(b + 32);
        }
        return true;
    }

    /// <summary>对切片的转发，逻辑单源；调用方持有数组时避免维护第二份折叠实现。</summary>
    internal static bool FoldLowerAsciiInPlace(byte[] buf) => FoldLowerAsciiInPlace(buf.AsSpan());

    // 忽略大小写路径的 ASCII 快路径累计「完整扫描并判定为纯 ASCII」的字节数；
    // internal 只读，仅供测试观测（钉住「每行只扫自身正文字节」不变式），
    // 测试间用差值比较，不参与任何过滤行为。
    // 非 ASCII 行的 Ascii.IsValid 是提前退出的部分扫描，无法精确计量，故不计入。
    private static long _asciiScanBytes;
    internal static long AsciiScanBytesForTest => _asciiScanBytes;

    // ── Message 过滤 ──
    static int[] MsgFilter(LogDocument doc, int[] cand, Models.FilterSpec spec,
        IProgress<(double, string)>? progress, CancellationToken ct)
    {
        if (cand.Length == 0) return cand;

        bool opAnd = spec.MsgOp == "and";
        bool exclude = spec.MsgExclude;
        bool needFold = !spec.MsgCase;

        // needle 预编码：区分大小写按原始字节；忽略大小写按 ToLowerInvariant
        // （与改造前一致——needle 自身的 Unicode 语义由它保证，ß 与 ẞ 都编码成 ß 的字节序列）
        var keys = spec.Msg.Select(t =>
        {
            var bytes = Encoding.UTF8.GetBytes(t);
            return spec.MsgCase ? bytes : Encoding.UTF8.GetBytes(t.ToLowerInvariant());
        }).ToArray();

        // 忽略大小写时预判每条 needle 是否全 ASCII：字节快路径只适用于全 ASCII needle。
        // 含 ≥0x80 字节的 needle 在 ASCII 行上不可能命中（改造前同理：折叠后的 ASCII 行
        // 不含 ≥0x80 字节，IndexOf 恒为 -1）——判为不命中，不进候选定位。
        bool[] keyAscii = needFold
            ? keys.Select(k => Ascii.IsValid(k)).ToArray()
            : Array.Empty<bool>();

        var result = new List<int>();
        int total = cand.Length;

        // 零拷贝读取会话：复用文档当前映射视图（容量随 Append 重映射），
        // 一次 AcquirePointer/ReleasePointer 配对，替换「每行 new byte[] + ReadArray 拷贝」
        using var session = doc.OpenMessageSession();

        for (int s = 0; s < total; s += BATCH)
        {
            int e = Math.Min(s + BATCH, total);
            for (int pos = s; pos < e; pos++)
            {
                int r = cand[pos];
                // 热循环走会话的 internal 直连接口（同边界同护栏，span 只在本语句内消费）；
                // 对外回调契约见 LogDocument.MessageSession.ReadMessage
                var msg = session.MessageSpan(r);
                bool ok = needFold
                    ? MatchFolded(msg, keys, keyAscii, opAnd)
                    : MatchAny(msg, keys, opAnd);
                if (ok != exclude)
                    result.Add(r);
            }
            progress?.Report(((double)e / total, Loc.F("匹配 message {0}/{1}", e, total)));
            ct.ThrowIfCancellationRequested();
        }
        return result.ToArray();
    }

    /// <summary>
    /// 忽略大小写的按行分派匹配（与改造前逐位等价）：
    /// 行内全部字节 &lt; 0x80 → 字节级忽略大小写比较（候选首字节定位 + 逐字节验证）；
    /// 行内含 ≥ 0x80 字节 → 整行 Unicode 小写化后按字节找（ß/ẞ、İ/ı、重音的结论
    /// 只在这条路径下成立，故非 ASCII 行走 Unicode 是语义要求而非实现细节）。
    /// 空 needle 语义与 <see cref="MatchAny"/> 一致：or 立即成立，and 跳过不否决。
    /// </summary>
    // MatchAny / MatchFolded / ContainsAsciiInsensitive 均带 AggressiveInlining：
    // 千万级逐行过滤的热循环里，非内联的调用层在 Release 下可占一成以上耗时
    // （JIT 实测：不带特性时 Tier1-OSR 码保留三次真实 call/行）。
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool MatchFolded(ReadOnlySpan<byte> msg, byte[][] keys, bool[] keyAscii, bool opAnd)
    {
        if (Ascii.IsValid(msg))
        {
            _asciiScanBytes += msg.Length;
            for (int i = 0; i < keys.Length; i++)
            {
                var needle = keys[i].AsSpan();
                if (needle.Length == 0)
                {
                    if (!opAnd) return true;
                    continue;
                }
                // 非 ASCII needle 在 ASCII 行上不可能命中：两种大小写形态退化为「判不命中」
                bool hit = keyAscii[i] && ContainsAsciiInsensitive(msg, needle);
                if (opAnd && !hit) return false;
                if (!opAnd && hit) return true;
            }
            return opAnd;
        }

        // 含非 ASCII：整行 Unicode 小写化（ß/ẞ、İ 等会改变字节数，不能就地折），按字节找
        var folded = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(msg).ToLowerInvariant());
        return MatchAny(folded, keys, opAnd);
    }

    /// <summary>
    /// 纯 ASCII 行上的字节级忽略大小写子串查找：先用 needle 首字节的小写与大写两种形态
    /// 定位候选位置（向量化 <see cref="MemoryExtensions.IndexOfAny(ReadOnlySpan{Byte}, Byte, Byte)"/>），
    /// 再对候选位置逐字节验证（行侧 A–Z 折成小写后与 needle 字节比较）。
    /// needle 必须全 ASCII（调用方经 keyAscii 保证）且已按 ToLowerInvariant 预编码。
    /// 与「整行 A–Z 折叠后 IndexOf(needle)」逐位等价；needle 首字节非 ASCII 字母时
    /// 两种形态退化为一种（该字节无大小写之分），仍完备。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool ContainsAsciiInsensitive(ReadOnlySpan<byte> hay, ReadOnlySpan<byte> needle)
    {
        if (needle.Length == 0) return true;      // 与 IndexOf(空 needle) 返回 0 一致
        if (needle.Length > hay.Length) return false;
        byte lo = needle[0];
        byte hi = (byte)(lo >= (byte)'a' && lo <= (byte)'z' ? lo - 32 : lo);
        // 候选窗口右界：验证时读 [at, at+needle.Length)，必须不越 hay 的界
        int last = hay.Length - needle.Length;
        int from = 0;
        while (from <= last)
        {
            int idx = hay.Slice(from, last - from + 1).IndexOfAny(lo, hi);
            if (idx < 0) return false;
            int at = from + idx;
            if (VerifyAsciiInsensitive(hay, at, needle)) return true;
            from = at + 1;
        }
        return false;
    }

    /// <summary>候选位置逐字节验证：行侧 A–Z 折成小写后与 needle（已小写）比较。</summary>
    static bool VerifyAsciiInsensitive(ReadOnlySpan<byte> hay, int at, ReadOnlySpan<byte> needle)
    {
        for (int i = 0; i < needle.Length; i++)
        {
            byte h = hay[at + i];
            if (h >= (byte)'A' && h <= (byte)'Z') h = (byte)(h + 32);
            if (h != needle[i]) return false;
        }
        return true;
    }

    /// <summary>
    /// 子串匹配：and 要求全部命中，or 要求任一命中。
    /// 用显式循环而非 LINQ——lambda 不能捕获 <see cref="ReadOnlySpan{T}"/>。
    /// 逐字节精确比较，needle 为空视为命中（与历史行为一致）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool MatchAny(ReadOnlySpan<byte> hay, byte[][] keys, bool opAnd)
    {
        for (int i = 0; i < keys.Length; i++)
        {
            var needle = keys[i].AsSpan();
            // 空 needle 视为命中（与历史 ContainsBytes 行为一致）：
            // or 模式立即成立，and 模式直接跳过、不构成否决
            if (needle.Length == 0)
            {
                if (!opAnd) return true;
                continue;
            }
            bool hit = hay.IndexOf(needle) >= 0;
            if (opAnd && !hit) return false;
            if (!opAnd && hit) return true;
        }
        return opAnd;
    }

    // ── 主入口 ──
    public static async Task<int[]> ApplyFilterAsync(LogDocument doc, Models.FilterSpec spec,
        HashSet<int>? marked, IProgress<(double, string)>? progress, CancellationToken ct)
    {
        return await Task.Run(() => ApplyFilter(doc, spec, marked, progress, ct), ct);
    }

    public static int[] ApplyFilter(LogDocument doc, Models.FilterSpec spec,
        HashSet<int>? marked, IProgress<(double, string)>? progress, CancellationToken ct)
    {
        int n = doc.RowCount;
        if (n == 0) return Array.Empty<int>();
        progress?.Report((0.0, Loc.T("过滤…")));

        var allLevels = LogParser.ALL_LEVELS;
        var candidates = new List<int>(n);

        // 快速预筛
        var levelSet = spec.Levels.Length > 0
            ? new HashSet<int>(spec.Levels)
            : new HashSet<int>(allLevels);
        bool filterLevels = spec.Levels.Length > 0 && !spec.Levels.OrderBy(x => x).SequenceEqual(allLevels.OrderBy(x => x));

        var pidSet = spec.Pids.Length > 0 ? new HashSet<int>(spec.Pids) : null;
        var tidSet = spec.Tids.Length > 0 ? new HashSet<int>(spec.Tids) : null;
        var minSet = spec.Minutes.Length > 0 ? new HashSet<int>(spec.Minutes) : null;
        var tagIds = spec.Tags.Length > 0 ? TagIds(doc, spec) : null;

        for (int i = 0; i < n; i++)
        {
            // Level
            if (filterLevels && !levelSet.Contains(doc.Lvl[i])) continue;

            // PID
            if (pidSet != null)
            {
                bool hit = pidSet.Contains(doc.Pid[i]);
                if (spec.PidExclude ? hit : !hit) continue;
            }

            // TID
            if (tidSet != null)
            {
                bool hit = tidSet.Contains(doc.Tid[i]);
                if (spec.TidExclude ? hit : !hit) continue;
            }

            // Minutes
            if (minSet != null)
            {
                // 分钟列哨兵 255 不可能是分钟集合成员，无时间戳行天然不命中
                if (!minSet.Contains(doc.Minute[i])) continue;
            }

            // Marked only
            if (spec.MarkedOnly && (marked == null || !marked.Contains(i))) continue;

            // Tag
            if (tagIds != null)
            {
                bool hit = tagIds.Contains(doc.TagId[i]);
                if (spec.TagExclude ? hit : !hit) continue;
            }

            candidates.Add(i);
        }

        progress?.Report((0.35, Loc.F("候选 {0:N0} 行", candidates.Count)));
        ct.ThrowIfCancellationRequested();

        int[] cand = candidates.ToArray();

        // Message 过滤
        if (spec.Msg.Length > 0)
        {
            cand = MsgFilter(doc, cand, spec,
                progress != null ? new Progress<(double, string)>(p =>
                    progress.Report((0.35 + 0.65 * p.Item1, p.Item2))) : null, ct);
        }

        progress?.Report((1.0, Loc.T("完成")));
        return cand;
    }

    // ── 增量过滤（实时采集用）──
    public static int[] FilterTail(LogDocument doc, Models.FilterSpec spec, int start, HashSet<int>? marked = null)
    {
        int n = doc.RowCount;
        if (n <= start) return Array.Empty<int>();

        var allLevels = LogParser.ALL_LEVELS;
        bool filterLevels = spec.Levels.Length > 0 && !spec.Levels.OrderBy(x => x).SequenceEqual(allLevels.OrderBy(x => x));
        var levelSet = filterLevels ? new HashSet<int>(spec.Levels) : null;
        var pidSet = spec.Pids.Length > 0 ? new HashSet<int>(spec.Pids) : null;
        var tidSet = spec.Tids.Length > 0 ? new HashSet<int>(spec.Tids) : null;
        var minSet = spec.Minutes.Length > 0 ? new HashSet<int>(spec.Minutes) : null;
        var tagIds = spec.Tags.Length > 0 ? TagIds(doc, spec) : null;

        var result = new List<int>();
        for (int i = start; i < n; i++)
        {
            if (filterLevels && !levelSet!.Contains(doc.Lvl[i])) continue;
            if (spec.MarkedOnly && (marked == null || !marked.Contains(i))) continue;
            if (pidSet != null)
            {
                bool hit = pidSet.Contains(doc.Pid[i]);
                if (spec.PidExclude ? hit : !hit) continue;
            }
            if (tidSet != null)
            {
                bool hit = tidSet.Contains(doc.Tid[i]);
                if (spec.TidExclude ? hit : !hit) continue;
            }
            if (minSet != null)
            {
                // 分钟列哨兵 255 不可能是分钟集合成员，无时间戳行天然不命中
                if (!minSet.Contains(doc.Minute[i])) continue;
            }
            if (tagIds != null)
            {
                bool hit = tagIds.Contains(doc.TagId[i]);
                if (spec.TagExclude ? hit : !hit) continue;
            }
            result.Add(i);
        }

        // Message 过滤
        if (spec.Msg.Length > 0)
            return MsgFilter(doc, result.ToArray(), spec, null, CancellationToken.None);

        return result.ToArray();
    }

    // ── 导出 ──
    public static async Task<int> ExportRowsAsync(LogDocument doc, int[] rows, string outPath,
        bool singleLine = false, IProgress<(double, string)>? progress = null, CancellationToken ct = default)
    {
        return await Task.Run(() => ExportRows(doc, rows, outPath, singleLine, progress, ct), ct);
    }

    public static int ExportRows(LogDocument doc, int[] rows, string outPath,
        bool singleLine = false, IProgress<(double, string)>? progress = null, CancellationToken ct = default)
    {
        int total = rows.Length;
        if (total == 0)
        {
            File.WriteAllBytes(outPath, Array.Empty<byte>());
            return 0;
        }

        int written = 0;
        using var f = new FileStream(outPath, FileMode.Create, FileAccess.Write);
        for (int s = 0; s < total; s += BATCH)
        {
            int e = Math.Min(s + BATCH, total);
            for (int i = s; i < e; i++)
            {
                byte[] line = doc.LineBytes(rows[i]);
                if (singleLine)
                {
                    string text = Encoding.UTF8.GetString(line);
                    text = text.Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");
                    line = Encoding.UTF8.GetBytes(text);
                }
                f.Write(line, 0, line.Length);
                f.WriteByte((byte)'\n');
            }
            written = e;
            progress?.Report(((double)e / total, Loc.F("导出 {0:N0}/{1:N0}", e, total)));
            ct.ThrowIfCancellationRequested();
        }
        return written;
    }
}
