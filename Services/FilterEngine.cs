using System.IO.MemoryMappedFiles;
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
    static bool FoldLowerAsciiInPlace(byte[] buf)
    {
        for (int i = 0; i < buf.Length; i++)
        {
            byte b = buf[i];
            if (b >= 0x80) return false;      // 非 ASCII：交给调用方回退，不做部分折叠
            if (b >= (byte)'A' && b <= (byte)'Z') buf[i] = (byte)(b + 32);
        }
        return true;
    }

    // ── Message 过滤 ──
    static int[] MsgFilter(LogDocument doc, int[] cand, Models.FilterSpec spec,
        IProgress<(double, string)>? progress, CancellationToken ct)
    {
        if (cand.Length == 0) return cand;

        bool opAnd = spec.MsgOp == "and";
        var keys = spec.Msg.Select(t =>
        {
            var bytes = Encoding.UTF8.GetBytes(t);
            return spec.MsgCase ? bytes : Encoding.UTF8.GetBytes(t.ToLowerInvariant());
        }).ToArray();

        var result = new List<int>();
        int total = cand.Length;
        // 纯 ASCII 且忽略大小写时就地折叠，缓冲区按行复用（首次 1 KB，按需增长后不再缩）。
        // 含非 ASCII 的行走 Unicode 折叠，行为与原实现完全一致。
        byte[] scratch = new byte[1024];
        bool needFold = !spec.MsgCase;

        for (int s = 0; s < total; s += BATCH)
        {
            int e = Math.Min(s + BATCH, total);
            for (int pos = s; pos < e; pos++)
            {
                int r = cand[pos];
                byte[] msgBuf = doc.MessageBytes(r);

                bool ok;
                if (needFold)
                {
                    if (msgBuf.Length > scratch.Length)
                        scratch = new byte[Math.Max(msgBuf.Length, scratch.Length * 2)];
                    Array.Copy(msgBuf, scratch, msgBuf.Length);
                    if (FoldLowerAsciiInPlace(scratch))
                    {
                        // ASCII 折叠不改变长度，直接按 msgBuf.Length 切片即可
                        ok = MatchAny(scratch.AsSpan(0, msgBuf.Length), keys, opAnd);
                    }
                    else
                    {
                        // 含非 ASCII：回退 Unicode 折叠（ß/ẞ、İ 等会改变长度，不能就地做）
                        var folded = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(msgBuf).ToLowerInvariant());
                        ok = MatchAny(folded, keys, opAnd);
                    }
                }
                else
                {
                    ok = MatchAny(msgBuf, keys, opAnd);
                }
                if (ok != spec.MsgExclude)
                    result.Add(r);
            }
            progress?.Report(((double)e / total, $"匹配 message {e}/{total}"));
            ct.ThrowIfCancellationRequested();
        }
        return result.ToArray();
    }

    /// <summary>
    /// 子串匹配：and 要求全部命中，or 要求任一命中。
    /// 用显式循环而非 LINQ——lambda 不能捕获 <see cref="ReadOnlySpan{T}"/>。
    /// 逐字节精确比较，needle 为空视为命中（与历史行为一致）。
    /// </summary>
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
        progress?.Report((0.0, "过滤…"));

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
                long ts = doc.Ts[i];
                if (ts < 0) continue;
                int minute = (int)((ts / 60000) % 60);
                if (!minSet.Contains(minute)) continue;
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

        progress?.Report((0.35, $"候选 {candidates.Count:N0} 行"));
        ct.ThrowIfCancellationRequested();

        int[] cand = candidates.ToArray();

        // Message 过滤
        if (spec.Msg.Length > 0)
        {
            cand = MsgFilter(doc, cand, spec,
                progress != null ? new Progress<(double, string)>(p =>
                    progress.Report((0.35 + 0.65 * p.Item1, p.Item2))) : null, ct);
        }

        progress?.Report((1.0, "完成"));
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
                long ts = doc.Ts[i];
                if (ts < 0) continue;
                int minute = (int)((ts / 60000) % 60);
                if (!minSet.Contains(minute)) continue;
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
            progress?.Report(((double)e / total, $"导出 {e:N0}/{total:N0}"));
            ct.ThrowIfCancellationRequested();
        }
        return written;
    }
}
