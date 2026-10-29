using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
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

    // ── 块扫描分派与形态常量（全量 Message 过滤的大文档路径）──
    // 数值为探针量级起点（探索笔记《UTF8-BYTE-SIMD-SEARCH》§7.2：1582 万行 / 1 GB 文件），
    // 允许由基准任务标定后直接调整（change filter-msgfilter-block-scan 决策 5）。
    /// <summary>分派阈值：文档行数达到它才考虑块扫描（小文件固定开销不划算）。</summary>
    internal const int BLOCK_SCAN_MIN_ROWS = 2_000_000;
    /// <summary>分派阈值：候选数须 &gt; 行数/该值（候选占比过低时逐行更便宜）。</summary>
    internal const int BLOCK_SCAN_RATIO = 8;
    /// <summary>分派阈值：非空关键词数上限（每词一轮块扫描，轮次成本按词数线性增长）。</summary>
    internal const int BLOCK_SCAN_MAX_KEYS = 8;
    /// <summary>块扫描并行度上限（取核数与该值较小者；内存带宽墙使更多线程无收益）。</summary>
    internal const int BLOCK_SCAN_MAX_DOP = 16;
    /// <summary>块扫描分块大小：32 MB（探针量级 16–64 MB 区间的起点）。</summary>
    internal const int BLOCK_SCAN_BLOCK = 1 << 25;

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
        => MsgFilterCore(doc, cand, spec, progress, ct);

    /// <summary>
    /// Message 过滤核心。<b>全量</b>路径按 <see cref="BlockScanEligible"/> 的三条件在
    /// 「逐条匹配」（现状路径，原样保留）与「块扫描」之间分派；
    /// <see cref="FilterTail"/>（增量过滤）以 forceBlockScan=false 固定走逐条匹配——
    /// 增量行数少，逐条已是最优，且块扫描的整文档语义不适配增量。
    /// forceBlockScan / blockSizeOverride / ciMode 是测试钩子（强制路径、分块粒度、CI 形态），
    /// 生产调用一律不传。
    /// </summary>
    internal static unsafe int[] MsgFilterCore(LogDocument doc, int[] cand, Models.FilterSpec spec,
        IProgress<(double, string)>? progress, CancellationToken ct,
        bool? forceBlockScan = null, int? blockSizeOverride = null,
        BlockScanCiMode ciMode = BlockScanCiMode.Auto)
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

        int total = cand.Length;

        // 零拷贝读取会话：复用文档当前映射视图（容量随 Append 重映射），
        // 一次 AcquirePointer/ReleasePointer 配对，替换「每行 new byte[] + ReadArray 拷贝」
        using var session = doc.OpenMessageSession();

        // 块扫描路径：分派条件满足且拿得到映射基指针时接管；否则原样走逐条匹配
        if ((forceBlockScan ?? BlockScanEligible(doc, cand, spec))
            && session.TryAcquireRaw(out byte* raw, out long cap))
        {
            var viaBlocks = MsgFilterBlockScan(doc, session, raw, cap, cand, keys, keyAscii,
                needFold, opAnd, exclude, progress, ct,
                blockSizeOverride ?? BLOCK_SCAN_BLOCK, ciMode);
            if (viaBlocks != null) return viaBlocks;
        }

        var result = new List<int>();

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

    // ── 块扫描（全量 Message 过滤的大文档路径）──
    // 形态出自探索笔记《UTF8-BYTE-SIMD-SEARCH》§7.2 探针：Parallel 分块 + 大小写不敏感
    // SIMD 扫描原始 mmap 字节，命中点按行索引（Offs 二分 + Moff/Lens 段边界复核）归属到行，
    // 只对命中行做必要复核。判定标准（笔记 §8）：同一输入同一过滤条件，
    // 块扫描与逐条匹配路径的行号集合完全一致。

    /// <summary>块扫描的 CI 形态：测试用它比对 SIMD 与标量回退的结论；生产用 Auto（有 AVX2 走 SIMD）。</summary>
    internal enum BlockScanCiMode
    {
        /// <summary>生产默认：<see cref="Vector256.IsHardwareAccelerated"/> 为真走 SIMD，否则标量回退。</summary>
        Auto,
        /// <summary>强制 SIMD 候选提取（无 AVX2 硬件时由 JIT 软件模拟，结论与标量一致）。</summary>
        Simd,
        /// <summary>强制标量候选提取（无 SIMD 硬件回退形态）。</summary>
        Scalar,
    }

    /// <summary>
    /// 分派条件（design D1）：行数、候选占比、关键词可用性三条件<b>同时</b>满足才走块扫描。
    /// 关键词可用性 = 全部为 ASCII 字节且非空词数 ≤ <see cref="BLOCK_SCAN_MAX_KEYS"/>；
    /// 任一关键词含非 ASCII 字节时整体回退逐条匹配（design D3 决策 2=①：不做「块扫描 + 逐行补齐」，
    /// 因为 and 语义下补齐等于对全部候选行再跑一遍逐行，收益归零却仍付并行调度与复核开销）。
    /// </summary>
    internal static bool BlockScanEligible(LogDocument doc, int[] cand, Models.FilterSpec spec)
        => BlockScanScaleOk(doc.RowCount, cand.Length) && BlockScanKeysUsable(spec.Msg);

    /// <summary>分派规模条件：行数达标且候选占比 &gt; 1/<see cref="BLOCK_SCAN_RATIO"/>。</summary>
    internal static bool BlockScanScaleOk(int rowCount, int candCount)
        => rowCount >= BLOCK_SCAN_MIN_ROWS
           && (long)candCount * BLOCK_SCAN_RATIO > rowCount;

    /// <summary>分派关键词条件：非空词全为 ASCII 且个数 ≤ <see cref="BLOCK_SCAN_MAX_KEYS"/>。</summary>
    internal static bool BlockScanKeysUsable(string[] terms)
    {
        int used = 0;
        foreach (var term in terms)
        {
            if (string.IsNullOrEmpty(term)) continue;          // 空词短路在合并层处理，不占扫描轮次
            if (++used > BLOCK_SCAN_MAX_KEYS) return false;
            if (!Ascii.IsValid(Encoding.UTF8.GetBytes(term))) return false;
        }
        return true;
    }

    /// <summary>
    /// 块扫描主流程（design D2–D4）：每个关键词一轮并行块扫描，命中点归属到行后只记「该行命中该词」；
    /// 忽略大小写时另做一轮 ≥0x80 字节定位，把这些行交回既有折叠路径判定（D3 完备性红线）；
    /// 最后按候选行序逐行合并 and/or/exclude（行序与逐条匹配路径一致）。
    /// 返回 null 表示无法块扫描（空映射 / 无内容），调用方回退逐条匹配。
    /// </summary>
    static unsafe int[]? MsgFilterBlockScan(LogDocument doc, LogDocument.MessageSession session,
        byte* raw, long cap, int[] cand, byte[][] keys, bool[] keyAscii,
        bool needFold, bool opAnd, bool exclude,
        IProgress<(double, string)>? progress, CancellationToken ct,
        int blockSize, BlockScanCiMode ciMode)
    {
        int n = doc.RowCount;
        if (n <= 0 || cap <= 0 || blockSize <= 0) return null;

        // D4 空 needle 短路：or 模式整体立即成立（exclude 下整体不成立），不进扫描。
        // 逐条路径 MatchAny / MatchFolded 对 or + 空词同样无条件命中，语义一致。
        if (!opAnd)
            for (int i = 0; i < keys.Length; i++)
                if (keys[i].Length == 0)
                    return exclude ? Array.Empty<int>() : cand;

        int scannable = 0, ndMax = 0;
        bool andBlocked = false;      // CI 下存在非空但非 ASCII 的词 → 纯 ASCII 行的 and 恒不成立
        for (int i = 0; i < keys.Length; i++)
        {
            if (keys[i].Length == 0) continue;                 // and 模式跳过、不构成否决
            if (needFold && !keyAscii[i])
            {
                if (opAnd) andBlocked = true;
                continue;
            }
            scannable++;
            if (keys[i].Length > ndMax) ndMax = keys[i].Length;
        }
        // and 且无任何可扫词（全为空词）：MatchAny 的 and 分支返回 opAnd=true，不进扫描。
        // or 无空词时不成立（关键词都非空但都不可扫 = CI 非 ASCII 词），必须走下方折叠判定。
        if (opAnd && scannable == 0 && !andBlocked) return exclude ? Array.Empty<int>() : cand;

        // 扫描上界取「最后一行末尾」与映射容量的较小值：索引区之后的尾巴不是记录，
        // 扫它只会产出必然被段边界复核剔除的命中点。
        long dataEnd = Math.Min(cap, doc.Offs[n - 1] + doc.Lens[n - 1]);
        if (dataEnd <= 0) return null;

        long overlap = ndMax > 0 ? ndMax - 1 : 0;              // D2：重叠防跨块关键词漏配
        int nBlocks = checked((int)((dataEnd + blockSize - 1) / blockSize));
        int rounds = scannable + (needFold ? 1 : 0);
        var prog = new BlockScanProgress(progress, doc, cand, dataEnd, rounds);
        var counts = new byte[n];                              // 每行累计命中关键词数
        byte[]? nonAscii = needFold ? new byte[n] : null;      // 含 ≥0x80 字节的行（回退折叠路径）
        bool ciSimd = ciMode switch
        {
            BlockScanCiMode.Simd => true,
            BlockScanCiMode.Scalar => false,
            _ => Vector256.IsHardwareAccelerated,
        };
        var options = new ParallelOptions
        {
            CancellationToken = ct,                            // D6：取消检查逐块生效
            MaxDegreeOfParallelism = Math.Max(1,
                Math.Min(Environment.ProcessorCount, BLOCK_SCAN_MAX_DOP)),
        };

        for (int k = 0; k < keys.Length; k++)
        {
            if (keys[k].Length == 0 || (needFold && !keyAscii[k])) continue;
            var needle = keys[k];
            bool ci = needFold;
            Parallel.For(0, nBlocks, options, b =>
            {
                var offs = doc.Offs; var lens = doc.Lens; var moff = doc.Moff;
                long rawStart = (long)b * blockSize;
                long rawEnd = Math.Min(dataEnd, rawStart + blockSize);
                long scanEnd = BlockScanEnd(offs, lens, rawStart, rawEnd, overlap, cap);
                int len = checked((int)(scanEnd - rawStart));
                if (len >= needle.Length)
                {
                    // 每块一份归属状态（游标与去重标记都是块内局部的）
                    var sink = new BlockHitSink
                    {
                        Counts = counts, RawStart = rawStart, RawEnd = rawEnd,
                        Cap = cap, N = n, NdLen = needle.Length,
                        Cursor = -1, Counted = -1,
                    };
                    if (ci)
                        ScanCiBlock(raw + rawStart, len, needle, ciSimd, ref sink, offs, lens, moff);
                    else
                        ScanCsBlock(raw + rawStart, len, needle, ref sink, offs, lens, moff);
                }
                prog.BlockDone(scanEnd - rawStart);
            });
            ct.ThrowIfCancellationRequested();
        }

        if (needFold)
        {
            Parallel.For(0, nBlocks, options, b =>
            {
                var offs = doc.Offs; var lens = doc.Lens; var moff = doc.Moff;
                long rawStart = (long)b * blockSize;
                long rawEnd = Math.Min(dataEnd, rawStart + blockSize);
                long scanEnd = BlockScanEnd(offs, lens, rawStart, rawEnd, overlap, cap);
                ScanNonAsciiBlock(raw, rawStart, scanEnd, rawEnd, nonAscii!, cap, n, offs, lens, moff);
                prog.BlockDone(scanEnd - rawStart);
            });
        }

        return VerdictPass(session, cand, counts, nonAscii, keys, keyAscii,
            needFold, opAnd, exclude, scannable, andBlocked);
    }

    /// <summary>
    /// 块的扫描右界：本块归属行 = 行首落在 <c>[rawStart, rawEnd)</c> 的记录；末行正文可能越过
    /// <paramref name="rawEnd"/>，扫描必须延伸到该行末尾 —— 否则长行后半段的命中点会被邻块
    /// 按「行首不在本块」丢弃，造成漏报。无归属行的块返回 <paramref name="rawStart"/>（不扫）。
    /// </summary>
    static long BlockScanEnd(ReadOnlySpan<long> offs, ReadOnlySpan<int> lens,
        long rawStart, long rawEnd, long overlap, long cap)
    {
        int last = FirstRowAtOrAfter(offs, rawEnd) - 1;
        if (last < 0 || offs[last] < rawStart) return rawStart;
        long end = rawEnd;
        long rowEnd = offs[last] + lens[last];
        if (rowEnd > end) end = rowEnd;
        return Math.Min(cap, end + overlap);
    }

    /// <summary>
    /// 块内命中归属状态（每块一份）：命中位置 → 行号按 Offs 单调推进（本块首个命中二分定位，
    /// 其后按升序推进 —— 短关键词命中可达千万级，逐点二分不可接受），只计入行首落在本块的记录
    /// （重叠区不重复计行），并复核命中完整落在 message 段内。行区间按块互斥，故可直接写共享计数数组。
    /// </summary>
    struct BlockHitSink
    {
        public byte[] Counts;
        public long RawStart, RawEnd, Cap;
        public int N, NdLen;
        public int Cursor;    // 已定位到的行号（-1 = 尚未定位）
        public int Counted;   // 本块已计入的最后一行（-1 = 无）
    }

    /// <summary>命中点归属 + 段边界复核（design D2/D4）：通过则把该行在本关键词上的命中计数 +1。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void AcceptHit(long p, ref BlockHitSink s,
        ReadOnlySpan<long> offs, ReadOnlySpan<int> lens, ReadOnlySpan<int> moff)
    {
        if (p < 0 || p >= s.Cap) return;
        int row = NextRow(ref s.Cursor, offs, s.N, p);
        if (row < 0) return;
        long rowStart = offs[row];
        if (rowStart < s.RawStart || rowStart >= s.RawEnd) return;   // 归属去重：行首不在本块
        if (row == s.Counted) return;                                // 同一行的多次命中只计一次
        int mo = moff[row];
        int mlen = lens[row] - mo;
        if (mlen <= 0) return;                                       // 无正文记录恒不命中（D4）
        long start = rowStart + mo;
        if (start + mlen > s.Cap) return;                            // 容量护栏，同 MessageSpan 的空片段分支
        if (p < start || p + s.NdLen > start + mlen) return;         // 命中必须完整落在 message 段
        s.Counted = row;
        s.Counts[row]++;
    }

    /// <summary>命中位置 → 行号：先在首个命中处二分，其后随命中位置升序推进（摊还 O(1)）。</summary>
    static int NextRow(ref int cursor, ReadOnlySpan<long> offs, int n, long p)
    {
        if (cursor < 0)
        {
            cursor = RowOfOffset(offs, p);
            return cursor;
        }
        while (cursor + 1 < n && offs[cursor + 1] <= p) cursor++;
        return cursor;
    }

    /// <summary>命中起点 → 行号：Offs 升序，取「offs[i] ≤ p 的最大 i」；p 在首行之前返回 -1。</summary>
    static int RowOfOffset(ReadOnlySpan<long> offs, long p)
    {
        if (offs.Length == 0 || p < offs[0]) return -1;
        int lo = 0, hi = offs.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (offs[mid] <= p) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>首个 <c>offs[i] ≥ pos</c> 的行号（可作为「已完成行数」估计）。</summary>
    static int FirstRowAtOrAfter(ReadOnlySpan<long> offs, long pos)
    {
        int lo = 0, hi = offs.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (offs[mid] < pos) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>升序数组里首个 ≥ value 的下标（进度：行估计 → 候选序号）。</summary>
    static int LowerBound(int[] sorted, int value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (sorted[mid] < value) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>
    /// 区分大小写块扫描：原始字节 <see cref="MemoryExtensions.IndexOf(ReadOnlySpan{Byte}, ReadOnlySpan{Byte})"/>
    /// （向量化）逐次定位出现点，与逐条路径的 <see cref="MatchAny"/> 逐位等价（无需大小写处理）。
    /// </summary>
    static unsafe void ScanCsBlock(byte* p, int len, ReadOnlySpan<byte> needle,
        ref BlockHitSink sink, ReadOnlySpan<long> offs, ReadOnlySpan<int> lens, ReadOnlySpan<int> moff)
    {
        var hay = new ReadOnlySpan<byte>(p, len);
        int from = 0;
        while (from <= len - needle.Length)
        {
            // 窗口取到块尾：按「needle 长度」截短会把跨右界的 needle 尾字节切掉
            int idx = hay.Slice(from).IndexOf(needle);
            if (idx < 0) return;
            AcceptHit(sink.RawStart + from + idx, ref sink, offs, lens, moff);
            from += idx + 1;
        }
    }

    /// <summary>
    /// 忽略大小写块扫描（design D3）：算法口径与 <see cref="ContainsAsciiInsensitive"/> 一致 ——
    /// needle 首字节的小写/大写两态定位候选，再逐字节验证；SIMD 形态按探针 CI-Simd
    /// （<c>Vector256</c> 两态等值 + <c>ExtractMostSignificantBits</c> 提取候选位图），
    /// 无 AVX2 硬件回退标量候选定位。两形态结论逐位一致（测试钉住）。
    /// needle 须全 ASCII 且已按 ToLowerInvariant 预编码（调用方经 keyAscii 保证）。
    /// </summary>
    static unsafe void ScanCiBlock(byte* p, int len, ReadOnlySpan<byte> needle, bool useSimd,
        ref BlockHitSink sink, ReadOnlySpan<long> offs, ReadOnlySpan<int> lens, ReadOnlySpan<int> moff)
    {
        byte lo = needle[0];
        byte hi = (byte)(lo >= (byte)'a' && lo <= (byte)'z' ? lo - 32 : lo);
        var hay = new ReadOnlySpan<byte>(p, len);
        int last = len - needle.Length;      // 候选起点右界（含）
        int i = 0;
        if (useSimd)
        {
            var vlo = Vector256.Create(lo);
            var vhi = Vector256.Create(hi);
            int vcnt = Vector256<byte>.Count;
            int vecEnd = last - vcnt + 1;    // i ≤ vecEnd 时可整窗口加载
            while (i <= vecEnd)
            {
                var v = Unsafe.ReadUnaligned<Vector256<byte>>(p + i);
                uint mask = Vector256.ExtractMostSignificantBits(
                    Vector256.Equals(v, vlo) | Vector256.Equals(v, vhi));
                while (mask != 0)
                {
                    int at = i + BitOperations.TrailingZeroCount(mask);
                    if (VerifyAsciiInsensitive(hay, at, needle))
                        AcceptHit(sink.RawStart + at, ref sink, offs, lens, moff);
                    mask &= mask - 1;
                }
                i += vcnt;
            }
        }
        // 标量回退 / SIMD 窗口外的尾部
        for (; i <= last; i++)
        {
            byte b = p[i];
            if ((b == lo || b == hi) && VerifyAsciiInsensitive(hay, i, needle))
                AcceptHit(sink.RawStart + i, ref sink, offs, lens, moff);
        }
    }

    /// <summary>
    /// 非 ASCII 行识别（CI 路径专用，design D3）：对 <c>≥0x80</c> 单字节做一轮定位
    /// （UTF-8 多字节序列的首/续字节均 ≥0x80），把命中落在 message 段的行标记出来，
    /// 交回既有「整行 Unicode 折叠」路径判定 —— <c>İ</c> 小写化会产出 ASCII <c>i</c>、
    /// <c>ẞ/ß</c> 宽度变化等规则只在折叠路径下成立，原始字节扫描找不到这类命中。
    /// 一行只标记一次：命中整行后直接跳到行尾继续扫（中文重日志里 ≥0x80 出现点可达数亿）。
    /// </summary>
    static unsafe void ScanNonAsciiBlock(byte* p, long rawStart, long scanEnd, long rawEndOwn,
        byte[] flags, long cap, int n,
        ReadOnlySpan<long> offs, ReadOnlySpan<int> lens, ReadOnlySpan<int> moff)
    {
        long pos = rawStart;
        int cursor = -1;
        while (pos < scanEnd)
        {
            var hay = new ReadOnlySpan<byte>(p + pos, checked((int)(scanEnd - pos)));
            int idx = hay.IndexOfAnyExceptInRange((byte)0x00, (byte)0x7F);
            if (idx < 0) return;
            long hit = pos + idx;
            int row = NextRow(ref cursor, offs, n, hit);
            if (row < 0) { pos = hit + 1; continue; }

            long rowStart = offs[row];
            long rowEnd = rowStart + lens[row];
            int mo = moff[row];
            int mlen = lens[row] - mo;
            long start = rowStart + mo;
            bool owned = rowStart >= rawStart && rowStart < rawEndOwn;
            bool inMessage = mlen > 0 && start + mlen <= cap && hit >= start;
            if (owned && inMessage)
            {
                flags[row] = 1;
                pos = Math.Max(hit + 1, Math.Min(scanEnd, rowEnd));
            }
            else if (owned)
            {
                // 命中在头部（时间戳/tag）：正文可能还有非 ASCII，本行继续扫，不整行跳过
                pos = hit + 1;
            }
            else
            {
                // 非本块归属：该行由归属块覆盖到行尾，这里跳过整行
                pos = Math.Max(hit + 1, Math.Min(scanEnd, rowEnd));
            }
        }
    }

    /// <summary>
    /// 结果合并层（design D4）：按候选行序逐行给结论，口径与 <see cref="MatchAny"/> /
    /// <see cref="MatchFolded"/> 逐位一致 —— and 要求该行命中全部可扫词，or 要求命中任一；
    /// exclude 取反；含非 ASCII 字节的行整行交回折叠路径（D3），其余行只用字节扫描结论。
    /// </summary>
    static int[] VerdictPass(LogDocument.MessageSession session, int[] cand,
        byte[] counts, byte[]? nonAscii, byte[][] keys, bool[] keyAscii,
        bool needFold, bool opAnd, bool exclude, int scannable, bool andBlocked)
    {
        var result = new List<int>(cand.Length);
        for (int i = 0; i < cand.Length; i++)
        {
            int r = cand[i];
            bool ok = needFold && nonAscii![r] != 0
                ? MatchFolded(session.MessageSpan(r), keys, keyAscii, opAnd)
                : opAnd
                    ? !andBlocked && counts[r] == scannable
                    : counts[r] > 0;
            if (ok != exclude) result.Add(r);
        }
        return result.ToArray();
    }

    /// <summary>
    /// 块扫描进度（design D6）：内部按已完成字节累计，对外仍报「已完成行数估计」，
    /// 沿用现有键 <c>匹配 message {0}/{1}</c>（不新增本地化键）。
    /// x = 已扫字节 → <see cref="FirstRowAtOrAfter"/> 二分位置 → 候选序号，y = 候选行总数，
    /// 故 x/y 单调不减且不超过 1。锁内「累计 + 上报」成对执行，并行完成顺序不影响上报单调性。
    /// </summary>
    sealed class BlockScanProgress
    {
        readonly IProgress<(double, string)>? _progress;
        readonly LogDocument _doc;
        readonly int[] _cand;
        readonly long _dataEnd;
        readonly long _totalBytes;
        long _doneBytes;

        public BlockScanProgress(IProgress<(double, string)>? progress, LogDocument doc,
            int[] cand, long dataEnd, int rounds)
        {
            _progress = progress;
            _doc = doc;
            _cand = cand;
            _dataEnd = dataEnd;
            _totalBytes = Math.Max(1, dataEnd * Math.Max(1, rounds));
        }

        public void BlockDone(long bytes)
        {
            if (_progress == null) return;
            lock (this)
            {
                _doneBytes += bytes;
                double frac = Math.Min(1.0, (double)_doneBytes / _totalBytes);
                int rows = FirstRowAtOrAfter(_doc.Offs, (long)(frac * _dataEnd));
                int done = LowerBound(_cand, rows);
                _progress.Report((Math.Min(1.0, (double)done / _cand.Length),
                    Loc.F("匹配 message {0}/{1}", done, _cand.Length)));
            }
        }
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

        // Message 过滤（增量固定走逐条匹配：行数少，逐条已是最优，块扫描的整文档语义不适配增量）
        if (spec.Msg.Length > 0)
            return MsgFilterCore(doc, result.ToArray(), spec, null, CancellationToken.None,
                forceBlockScan: false);

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
