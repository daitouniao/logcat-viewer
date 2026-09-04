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

    static readonly Dictionary<(string text, bool caseSensitive), Regex> _reCache = new();

    static Regex CompileRegex(string text, bool caseSensitive)
    {
        var key = (text, caseSensitive);
        if (_reCache.TryGetValue(key, out var pat)) return pat;
        var opts = RegexOptions.Compiled | RegexOptions.Singleline;
        if (!caseSensitive) opts |= RegexOptions.IgnoreCase;
        pat = new Regex(text, opts);
        if (_reCache.Count < 512) _reCache[key] = pat;
        return pat;
    }

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
        Regex[]? pats = null;
        byte[][]? keys = null;

        if (spec.TagRegex)
        {
            pats = spec.Tags.Select(t => CompileRegex(t, spec.TagCase)).ToArray();
        }
        else
        {
            keys = spec.Tags.Select(t =>
            {
                var bytes = Encoding.UTF8.GetBytes(t);
                return spec.TagCase ? bytes : Encoding.UTF8.GetBytes(t.ToLowerInvariant());
            }).ToArray();
        }

        for (int i = 0; i < doc.Tags.Count; i++)
        {
            var raw = Encoding.UTF8.GetBytes(doc.Tags[i]);
            bool ok;
            if (pats != null)
            {
                string tagStr = doc.Tags[i];
                ok = opAnd
                    ? pats.All(p => p.IsMatch(tagStr))
                    : pats.Any(p => p.IsMatch(tagStr));
            }
            else
            {
                var probe = spec.TagCase ? raw : Encoding.UTF8.GetBytes(doc.Tags[i].ToLowerInvariant());
                ok = opAnd
                    ? keys!.All(k => ContainsBytes(probe, k))
                    : keys!.Any(k => ContainsBytes(probe, k));
            }
            if (ok) result.Add(i);
        }
        return result;
    }

    static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0) return true;
        if (needle.Length > haystack.Length) return false;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool found = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { found = false; break; }
            }
            if (found) return true;
        }
        return false;
    }

    // ── Message 过滤 ──
    static int[] MsgFilter(LogDocument doc, int[] cand, Models.FilterSpec spec,
        IProgress<(double, string)>? progress, CancellationToken ct)
    {
        if (cand.Length == 0) return cand;

        bool opAnd = spec.MsgOp == "and";
        Regex[]? pats = null;
        byte[][]? keys = null;

        if (spec.MsgRegex)
            pats = spec.Msg.Select(t => CompileRegex(t, spec.MsgCase)).ToArray();
        else
            keys = spec.Msg.Select(t =>
            {
                var bytes = Encoding.UTF8.GetBytes(t);
                return spec.MsgCase ? bytes : Encoding.UTF8.GetBytes(t.ToLowerInvariant());
            }).ToArray();

        var result = new List<int>();
        int total = cand.Length;

        for (int s = 0; s < total; s += BATCH)
        {
            int e = Math.Min(s + BATCH, total);
            for (int pos = s; pos < e; pos++)
            {
                int r = cand[pos];
                byte[] msgBuf = doc.MessageBytes(r);
                if (!spec.MsgCase)
                    msgBuf = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(msgBuf).ToLowerInvariant());

                bool ok;
                if (pats != null)
                {
                    string msgStr = Encoding.UTF8.GetString(msgBuf);
                    ok = opAnd
                        ? pats.All(p => p.IsMatch(msgStr))
                        : pats.Any(p => p.IsMatch(msgStr));
                }
                else
                {
                    ok = opAnd
                        ? keys!.All(k => ContainsBytes(msgBuf, k))
                        : keys!.Any(k => ContainsBytes(msgBuf, k));
                }
                if (ok != spec.MsgExclude)
                    result.Add(r);
            }
            progress?.Report(((double)e / total, $"匹配 message {e}/{total}"));
            ct.ThrowIfCancellationRequested();
        }
        return result.ToArray();
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
