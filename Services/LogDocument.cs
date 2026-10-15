using System.IO.MemoryMappedFiles;
using System.Text;
using logcat.Models;
using Microsoft.Extensions.Logging;

namespace logcat.Services;

/// <summary>
/// 日志文档：基于 MemoryMappedFile 的列式索引。
/// 文本本体始终留在磁盘（mmap），只有定长列进入内存。
/// </summary>
public sealed class LogDocument : IDisposable
{
    const int MAX_JOIN = 200;           // 单条记录最多合并续行数
    const byte FLAG_MULTILINE = 0x01;
    const int YEAR_LO = -5, YEAR_HI = 55;
    const long HALF_DAY = LogParser.DAY_MS / 2;

    static readonly ILogger _logger = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance.CreateLogger("LogDocument");

    public string Path { get; }
    public string EncodingName { get; } = "utf-8";
    public long Size { get; private set; }
    public long IndexedSize { get; private set; }
    public int BaseYear { get; private set; } = DateTime.Now.Year;
    public bool HasYear { get; private set; }
    public bool Join { get; set; } = true;

    // ── 列存储 ──
    long[] _offs = Array.Empty<long>();
    int[] _lens = Array.Empty<int>();
    int[] _moff = Array.Empty<int>();
    long[] _ts = Array.Empty<long>();
    int[] _pid = Array.Empty<int>();
    int[] _tid = Array.Empty<int>();
    byte[] _lvl = Array.Empty<byte>();
    int[] _tagId = Array.Empty<int>();
    byte[] _flags = Array.Empty<byte>();
    int _n;      // 有效行数
    int _cap;    // 已分配容量

    // ── Tag 字典 ──
    public List<string> Tags { get; private set; } = new();
    public Dictionary<string, int> TagIndex { get; private set; } = new();

    // ── 崩溃事件（稀疏，F-02）──
    // 后台扫描写 / UI 读：引用整体替换（不原位改），读侧持有旧快照也自洽
    List<CrashEvent> _crashes = new();
    /// <summary>崩溃事件清单，按 Row 升序；无崩溃时为空。摘要行与汇总窗口的统一数据源。</summary>
    public IReadOnlyList<CrashEvent> Crashes => _crashes;

    // 异常类型 / 包名字典（照 Tag 字典模式；种类数远小于 Tag）
    public List<string> ExcTypes { get; private set; } = new();
    public Dictionary<string, int> ExcTypeIndex { get; private set; } = new();
    public List<string> Pkgs { get; private set; } = new();
    public Dictionary<string, int> PkgIndex { get; private set; } = new();

    // ── mmap ──
    MemoryMappedFile? _mmf;
    MemoryMappedViewAccessor? _view;
    FileStream? _fh;
    (MemoryMappedViewAccessor? view, MemoryMappedFile? mmf, FileStream? fh) _retired;
    int? _yearLast;
    long? _withinLast;

    public LogDocument(string path, string encoding = "utf-8")
    {
        Path = System.IO.Path.GetFullPath(path);
        EncodingName = encoding;
    }

    // ── 列只读视图 ──
    public ReadOnlySpan<long> Offs => _offs.AsSpan(0, _n);
    public ReadOnlySpan<int> Lens => _lens.AsSpan(0, _n);
    public ReadOnlySpan<int> Moff => _moff.AsSpan(0, _n);
    public ReadOnlySpan<long> Ts => _ts.AsSpan(0, _n);
    public ReadOnlySpan<int> Pid => _pid.AsSpan(0, _n);
    public ReadOnlySpan<int> Tid => _tid.AsSpan(0, _n);
    public ReadOnlySpan<byte> Lvl => _lvl.AsSpan(0, _n);
    public ReadOnlySpan<int> TagId => _tagId.AsSpan(0, _n);
    public ReadOnlySpan<byte> Flags => _flags.AsSpan(0, _n);
    public int RowCount => _n;

    public bool IsMultiline(int row) => (_flags[row] & FLAG_MULTILINE) != 0;

    /// <summary>
    /// 最近一次 Append 是否发生了跨批次续行合并（末记录被拉长、message 内容变化）。
    /// 实时采集的增量过滤据此决定是否回退全量：carry 意味着末行的 Msg 匹配结果可能翻转。
    /// </summary>
    public bool LastAppendCarried { get; private set; }

    // ── 容量管理 ──
    void SetColumns(long[] offs, int[] lens, int[] moff, long[] ts, int[] pid, int[] tid,
                    byte[] lvl, int[] tagId, byte[] flags)
    {
        int n = offs.Length;
        _n = n;
        _cap = n + (n >> 2) + 1024;
        _offs = new long[_cap]; _lens = new int[_cap]; _moff = new int[_cap];
        _ts = new long[_cap]; _pid = new int[_cap]; _tid = new int[_cap];
        _lvl = new byte[_cap]; _tagId = new int[_cap]; _flags = new byte[_cap];
        if (n > 0)
        {
            Array.Copy(offs, _offs, n); Array.Copy(lens, _lens, n);
            Array.Copy(moff, _moff, n); Array.Copy(ts, _ts, n);
            Array.Copy(pid, _pid, n); Array.Copy(tid, _tid, n);
            Array.Copy(lvl, _lvl, n); Array.Copy(tagId, _tagId, n);
            Array.Copy(flags, _flags, n);
        }
    }

    void EnsureCap(int need)
    {
        if (need <= _cap) return;
        int cap = Math.Max(need, _cap + Math.Max(_cap >> 2, 1 << 16));
        Array.Resize(ref _offs, cap); Array.Resize(ref _lens, cap); Array.Resize(ref _moff, cap);
        Array.Resize(ref _ts, cap); Array.Resize(ref _pid, cap); Array.Resize(ref _tid, cap);
        Array.Resize(ref _lvl, cap); Array.Resize(ref _tagId, cap); Array.Resize(ref _flags, cap);
        _cap = cap;
    }

    // ── mmap 管理 ──
    void OpenMap()
    {
        ReleaseRetired();
        _retired = (_view, _mmf, _fh);
        _view = null; _mmf = null; _fh = null;

        _fh = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            if (_fh.Length == 0)
            {
                _mmf = null; _view = null;
                return;
            }
            _mmf = MemoryMappedFile.CreateFromFile(_fh, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, false);
            _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        }
        catch
        {
            _view?.Dispose(); _mmf?.Dispose();
            _fh.Dispose();
            _view = null; _mmf = null; _fh = null;
            throw;
        }
    }

    void ReleaseRetired()
    {
        _retired.view?.Dispose();
        _retired.mmf?.Dispose();
        _retired.fh?.Dispose();
        _retired = (null, null, null);
    }

    public void Close()
    {
        _view?.Dispose(); _mmf?.Dispose(); _fh?.Dispose();
        _view = null; _mmf = null; _fh = null;
        ReleaseRetired();
    }

    public void Dispose() => Close();

    // ── 读取 ──
    public byte[] LineBytes(int row)
    {
        if (_view == null) return Array.Empty<byte>();
        long off = _offs[row]; int len = _lens[row];
        byte[] buf = new byte[len];
        _view.ReadArray(off, buf, 0, len);
        return buf;
    }

    public byte[] MessageBytes(int row)
    {
        if (_view == null) return Array.Empty<byte>();
        long off = _offs[row]; int mo = _moff[row]; int len = _lens[row];
        int mlen = len - mo;
        if (mlen <= 0) return Array.Empty<byte>();
        byte[] buf = new byte[mlen];
        _view.ReadArray(off + mo, buf, 0, mlen);
        return buf;
    }

    /// <summary>
    /// 行首时间戳的原始字节（保留 1~9 位小数秒精度）。
    /// threadtime 行的头部还含 pid/tid/级别/tag，这里只截到小数秒末位；
    /// 行首不是时间戳（如 brief 的 E/Tag 形式）返回空，由调用方回退 TsToText。
    /// </summary>
    public byte[] HeaderBytes(int row)
    {
        if (_view == null) return Array.Empty<byte>();
        int mo = _moff[row];
        int len = Math.Min(mo, 40);   // yyyy-MM-dd HH:mm:ss.fffffffff 最长 33 字节
        if (len <= 0) return Array.Empty<byte>();
        byte[] buf = new byte[len];
        _view.ReadArray(_offs[row], buf, 0, len);

        int dot = len > 14 && buf[2] == 45 && buf[5] == 32 && buf[14] == 46 ? 14
                : len > 19 && buf[4] == 45 && buf[7] == 45 && buf[19] == 46 ? 19 : -1;
        if (dot < 0) return Array.Empty<byte>();
        int end = dot + 1;
        while (end < len && buf[end] >= 48 && buf[end] <= 57) end++;
        if (end == dot + 1) return Array.Empty<byte>();
        if (end == len) return buf;
        var ts = new byte[end];
        Array.Copy(buf, ts, end);
        return ts;
    }

    public string Decode(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    public string TagOf(int row)
    {
        if (row < 0 || row >= _n) return "";
        int i = _tagId[row];
        // 防御性检查：后台 Reload 可能导致 Tags 瞬态不一致
        var tags = Tags;
        return i >= 0 && i < tags.Count ? tags[i] : "";
    }

    // ── 构建 ──
    public static async Task<LogDocument> BuildAsync(string path, bool join = true,
        IProgress<(double pct, string msg)>? progress = null, CancellationToken ct = default)
    {
        return await Task.Run(() => Build(path, join, progress, ct), ct);
    }

    public static LogDocument Build(string path, bool join = true,
        IProgress<(double pct, string msg)>? progress = null, CancellationToken ct = default)
    {
        var doc = new LogDocument(path) { Join = join };
        doc.DoBuild(null, join, progress, ct);
        return doc;
    }

    void DoBuild(int? baseYear, bool join, IProgress<(double, string)>? progress, CancellationToken ct)
    {
        Size = new FileInfo(Path).Length;
        OpenMap();
        progress?.Report((0.0, Loc.T("扫描行…")));
        var part = ScanRange(Path, 0, Size, join, false, 0, progress, ct);
        ct.ThrowIfCancellationRequested();
        // 整体重建：批末合并状态缓存一并刷新（Reload 检测到头变化走重建时，旧缓存态作废）
        _prevHeadAtEnd = part.EndPrevHead;
        _joinedAtEnd = part.EndJoined;
        Assemble(part, baseYear, progress);
        IndexedSize = Size;
    }

    // ── 扫描 ──
    /// <summary>internal：单元测试直接验证区间扫描的行边界对齐（见 InternalsVisibleTo）。</summary>
    internal struct ScanPart
    {
        public long[] Offs; public int[] Lens; public int[] Moff;
        public long[] Within; public int[] Years;
        public int[] Pids; public int[] Tids;
        public byte[] Lvls; public int[] TagIds; public byte[] Flags;
        public List<byte[]> LocalTags;
        /// <summary>本批提取到的崩溃事件（F-02 稀疏方案）。Row 为批次内相对行号，Append 时 +n 偏移。</summary>
        public List<CrashEvent> Crashes;
        /// <summary>本批出现的异常类型 / 包名局部字典（id 即 CrashEvent.ExcId/PkgId）。</summary>
        public List<string> LocalExcTypes;
        public List<string> LocalPkgs;
        /// <summary>本批开头若干行属于「上一批末尾那条记录」的续行（跨批次合并）。</summary>
        public int CarryLines;
        /// <summary>这些续行结束处的绝对文件偏移。</summary>
        public long CarryEndOff;
        /// <summary>批末真实合并状态（空行已重置、横幅不重置，语义与批内一致）。跨批缓存用，替代按已索引行反推。</summary>
        public bool EndPrevHead;
        /// <summary>批末真实 joined：自末条记录头起的续行总数（跨批 carry 行也计入）。</summary>
        public int EndJoined;
    }

    /// <summary>
    /// 扫描 [start, end) 区间并按行切分。两端都对齐到行边界：start 落在行中间时跳到下一行行首
    /// （前半段已由上一批索引），end 落在行中间时回退到该行行首（不完整行留待下次追加）。
    /// internal：单元测试直接验证对齐语义（见 InternalsVisibleTo）。
    /// </summary>
    internal static ScanPart ScanRange(string path, long start, long end, bool join, bool prevHeadDoc = false,
        int joinedDoc = 0, IProgress<(double, string)>? progress = null, CancellationToken ct = default)
    {
        var offs = new List<long>();
        var lens = new List<int>();
        var moff = new List<int>();
        var within = new List<long>();
        var years = new List<int>();
        var pids = new List<int>();
        var tids = new List<int>();
        var lvls = new List<byte>();
        var tagIds = new List<int>();
        var flags = new List<byte>();

        var tagMap = new Dictionary<string, int>();
        var tagList = new List<byte[]>();

        // ── 崩溃提取（F-02）──
        var crashes = new List<CrashEvent>();
        var crashRows = new HashSet<int>();       // a 轨已建事件的行号（c 轨批末补扫去重）
        var excMap = new Dictionary<string, int>();
        var excList = new List<string>();
        var pkgMap = new Dictionary<string, int>();
        var pkgList = new List<string>();
        var joinCounts = new List<int>();         // 与 offs 平行：每条记录已合并的续行数（LineCount 来源）

        int prev = -1;
        // 续行状态跨批次传递：本批第一行如果属于上一批末尾那条记录，要并回去，
        // 否则多行日志（崩溃堆栈）会在批次边界被切成年两条记录。
        bool prevHead = prevHeadDoc;
        // joined 同样跨批传递：MAX_JOIN 是「单记录」全局上限，不是每批预算（X-A8 fix-6，2026-10-15 定案）
        int joined = joinedDoc;
        int carryLines = 0;
        long carryEndOff = 0;

        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long fileSize = f.Length;
        if (fileSize == 0 || start >= fileSize)
            return EmptyPart();

        if (start > 0)
        {
            // 对齐到整行：start 应当是某个 '\n' 之后的第一字节。
            // 判断依据是 start 前一个字节是不是换行；用 start 自身的首字节判断会错——
            // 那正好是下一行的行首字符，于是被误判为「切在行中间」，
            // 白白跳掉一整行（增量追加时每次都会吞掉新内容的第一行）。
            f.Position = start - 1;
            int prevByte = f.ReadByte();
            if (prevByte != '\n')
            {
                // 确实切在行中间：向后找到下一个换行，从下一行行首开始
                f.Position = start;
                int b = f.ReadByte();
                while (b >= 0 && b != '\n') b = f.ReadByte();
                if (b < 0) return EmptyPart();
                start = f.Position;
            }
        }

        if (end < fileSize)
        {
            // 对齐到整行：end 应当是「某个 '\n' 之后的第一字节」。
            // 若 end 落在行中间，说明尾段是没写完的不完整行 —— 回退到该行行首，留待下次追加。
            // 不能向后推进到下一个换行：那会把 Append 里 TrimPartial 刚裁掉的残行又读回来，
            // 使「完整行 + 末尾残行」与「只写残行」两种写入形态得出不同结果。
            f.Position = end - 1;
            if (f.ReadByte() != '\n')
            {
                long q = -1;
                for (long p = end - 1; p >= start; p--)
                {
                    f.Position = p;
                    if (f.ReadByte() == '\n') { q = p; break; }
                }
                end = q >= 0 ? q + 1 : start;
            }
        }

        if (start >= end) return EmptyPart();

        // 读取区间数据
        long rangeSize = end - start;
        byte[] data = new byte[rangeSize];
        f.Position = start;
        long totalRead = 0;
        while (totalRead < rangeSize)
        {
            int read = f.Read(data, (int)totalRead, (int)(rangeSize - totalRead));
            if (read <= 0) break;
            totalRead += read;
        }

        // 逐行解析
        int pos = 0;
        int lineInChunk = 0;
        long nextReport = 1 << 22;

        while (pos < data.Length)
        {
            int nl = Array.IndexOf(data, (byte)'\n', pos);
            int lineEnd = nl >= 0 ? nl : data.Length;
            int rawLen = lineEnd - pos;

            // 去 \r
            int lineLen = rawLen;
            if (lineLen > 0 && data[pos + lineLen - 1] == 13) lineLen--;

            // 文件首行的 UTF-8 BOM：剥掉再解析。
            // 否则首行（含「----- timezone:…」这类头部）会被整行当成「未知」记录。
            int lineOff = pos;
            if (pos == 0 && lineLen >= 3 &&
                data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            {
                lineOff = 3;
                lineLen -= 3;
            }

            long currentOff = start + lineOff;

            if (lineLen == 0)
            {
                // 空行：不索引，中断合并。
                // prevHead/joined 必须一起重置——否则空行后的缩进行仍满足合并判据，
                // 全量路径（Assemble 不读 CarryLines）会把它蒸发，增量路径（Append 消费 carry）
                // 却把它吸收进上一条记录，同一字节两条路径结论相反（X-A8，2026-10-14 实测）。
                // 对照：横幅行分支刻意不重置（崩溃堆栈中间的横幅不切断堆栈），两者语义相反，勿「统一」。
                prev = -1;
                prevHead = false;
                joined = 0;
                pos = nl >= 0 ? nl + 1 : data.Length;
                continue;
            }

            ReadOnlySpan<byte> line = data.AsSpan(lineOff, lineLen);

            // 横幅行（--------- beginning of main / ----- timezone:Asia/Kuala_Lumpur 等）
            // 不是日志记录：不进索引，也不打断续行合并，与实时采集的过滤规则保持一致
            if (LogParser.IsBanner(line))
            {
                pos = nl >= 0 ? nl + 1 : data.Length;
                continue;
            }

            // 续行合并
            if (join && prevHead && joined < MAX_JOIN && !LogParser.LooksLikeNewRecord(line))
            {
                if (prev >= 0)
                {
                    lens[prev] = (int)((currentOff + lineLen) - offs[prev]);
                    flags[prev] = (byte)(flags[prev] | FLAG_MULTILINE);
                    joinCounts[prev]++;
                }
                else
                {
                    // prev < 0：并回「上一批末尾那条记录」（由 Append 写回列存储）。
                    // 该记录若是崩溃，其 LineCount 由 Append 按 CarryLines 补增。
                    carryLines++;
                    carryEndOff = currentOff + lineLen;
                }
                joined++;
                pos = nl >= 0 ? nl + 1 : data.Length;
                continue;
            }

            LogParser.ParseResult pr;
            try { pr = LogParser.ParseLine(line); }
            catch { pr = new LogParser.ParseResult(-1, -1, -1, -1, 0, "", 0); }

            offs.Add(currentOff);
            lens.Add(lineLen);
            moff.Add(pr.MsgOffset >= 0 && pr.MsgOffset <= lineLen ? pr.MsgOffset : 0);
            within.Add(pr.WithinMs);
            years.Add(pr.Year);
            pids.Add(pr.Pid);
            tids.Add(pr.Tid);
            lvls.Add((byte)pr.Level);
            joinCounts.Add(0);

            int gid = -1;
            if (!string.IsNullOrEmpty(pr.Tag))
            {
                byte[] tagBytes = Encoding.UTF8.GetBytes(pr.Tag.Trim());
                if (tagBytes.Length > 0)
                {
                    string tagKey = Encoding.UTF8.GetString(tagBytes);
                    if (!tagMap.TryGetValue(tagKey, out gid))
                    {
                        gid = tagList.Count;
                        tagMap[tagKey] = gid;
                        tagList.Add(tagBytes);
                    }
                }
            }
            tagIds.Add(gid);
            flags.Add(0);

            // ── 崩溃头识别（a 轨，与 Join 无关）──
            // 字节级快筛零成本放行绝大多数行；命中才解码 msg + 前瞻后续行拼文本做完整提取
            //（Join=false 时 Process/异常类型行是独立记录，前瞻是拿到它们的唯一途径，见 ANALYSIS-2.1 §1.3 v3）
            if (CrashExtractor.ContainsCrashKeyword(line))
            {
                var match = ExtractCrashAt(data, lineOff, lineLen, moff[^1], pos);
                if (match != null)
                {
                    crashes.Add(new CrashEvent
                    {
                        Row = offs.Count - 1,
                        ExcId = Intern(match.Value.ExcType, excMap, excList),
                        PkgId = Intern(match.Value.Pkg, pkgMap, pkgList),
                        Pid = pr.Pid,
                        Tid = pr.Tid,
                        Ts = 0,               // Assemble / Append 终算后回填（扫描期时间戳未定）
                        Kind = match.Value.Kind,
                        LineCount = 1,        // 批末按 joinCounts 终算
                    });
                    crashRows.Add(offs.Count - 1);
                }
            }

            prev = offs.Count - 1;
            prevHead = pr.WithinMs >= 0 || pr.Level > 0;
            joined = 0;

            pos = nl >= 0 ? nl + 1 : data.Length;

            // 进度报告
            lineInChunk++;
            if (progress != null && pos >= nextReport)
            {
                nextReport = pos + (1 << 22);
                progress.Report(((double)pos / data.Length, Loc.T("扫描行…")));
            }
            if (lineInChunk % 5000 == 0)
            {
                ct.ThrowIfCancellationRequested();
            }
        }

        // ── 崩溃事件收尾 ──
        // 1) LineCount 终算：合并行数 = 批内 joinCounts + 1（跨批 carry 由 Append 补增）
        for (int i = 0; i < crashes.Count; i++)
        {
            var ce = crashes[i];
            ce.LineCount = joinCounts[ce.Row] + 1;
            crashes[i] = ce;
        }

        // 2) c 轨批末补扫：崩溃头缩进并入父记录的合并形态（Join=true），头行不经过 a 轨
        //    的「新记录行」分支 —— 对 multiline 记录的合并文本统一再提一次（Row 已建的跳过）
        for (int i = 0; i < offs.Count; i++)
        {
            if ((flags[i] & FLAG_MULTILINE) == 0 || crashRows.Contains(i)) continue;
            int mo = moff[i];
            int len = lens[i] - mo;
            if (len <= 0) continue;
            var span = data.AsSpan((int)(offs[i] - start) + mo, len);
            if (!CrashExtractor.ContainsCrashKeyword(span)) continue;
            var match = CrashExtractor.TryExtract(Encoding.UTF8.GetString(span));
            if (match == null) continue;
            crashes.Add(new CrashEvent
            {
                Row = i,
                ExcId = Intern(match.Value.ExcType, excMap, excList),
                PkgId = Intern(match.Value.Pkg, pkgMap, pkgList),
                Pid = pids[i],
                Tid = tids[i],
                Ts = 0,
                Kind = match.Value.Kind,
                LineCount = joinCounts[i] + 1,
            });
            crashRows.Add(i);
        }
        crashes.Sort((a, b) => a.Row.CompareTo(b.Row));

        return new ScanPart
        {
            Offs = offs.ToArray(), Lens = lens.ToArray(), Moff = moff.ToArray(),
            Within = within.ToArray(), Years = years.ToArray(),
            Pids = pids.ToArray(), Tids = tids.ToArray(),
            Lvls = lvls.ToArray(), TagIds = tagIds.ToArray(), Flags = flags.ToArray(),
            LocalTags = tagList,
            Crashes = crashes, LocalExcTypes = excList, LocalPkgs = pkgList,
            CarryLines = carryLines, CarryEndOff = carryEndOff,
            EndPrevHead = prevHead, EndJoined = joined
        };
    }

    // ── 崩溃提取辅助（ScanRange 用）──

    /// <summary>
    /// 在 data 的 [lineOff, lineOff+lineLen) 行识别崩溃头并提取：本行 msg + 最多 3 行前瞻
    /// （各剥日志头部前缀）拼成多行文本交给 <see cref="CrashExtractor.TryExtract"/>。
    /// 不是崩溃头返回 null。
    /// </summary>
    static CrashMatch? ExtractCrashAt(byte[] data, int lineOff, int lineLen, int msgOff, int lookFrom)
    {
        var sb = new StringBuilder(256);
        sb.Append(MsgTextOfLine(data, lineOff, lineOff + lineLen, msgOff));
        int q = lookFrom;
        for (int look = 0; look < 3 && q < data.Length; look++)
        {
            int nl2 = Array.IndexOf(data, (byte)'\n', q);
            int le2 = nl2 >= 0 ? nl2 : data.Length;
            if (le2 > q)
            {
                // 前瞻行同样剥日志头部前缀（时间戳/PID/TID/Tag），否则 threadtime 形态的
                // 「Process: …」「java.lang.…」行匹配不上判据 → 独立行形态的 Java 崩溃会漏提；
                // 解析失败回退整行（缩进续行 ParseLine 得 MsgOffset=0，本来就是整行）
                int plen = le2 - q;
                if (plen > 0 && data[le2 - 1] == 13) plen--;
                int mo = 0;
                if (plen > 0)
                {
                    try
                    {
                        var pr = LogParser.ParseLine(data.AsSpan(q, plen));
                        if (pr.MsgOffset > 0 && pr.MsgOffset < plen) mo = pr.MsgOffset;
                    }
                    catch { }
                }
                sb.Append('\n').Append(MsgTextOfLine(data, q, le2, mo));
            }
            q = nl2 >= 0 ? nl2 + 1 : data.Length;
        }
        return CrashExtractor.TryExtract(sb.ToString());
    }

    /// <summary>取 [start,end) 行的 msg 文本：MsgOffset 之后的字节（解析失败取整行），去尾部 \r。</summary>
    static string MsgTextOfLine(byte[] data, int start, int end, int msgOff)
    {
        int len = end - start;
        if (len > 0 && data[end - 1] == 13) len--;
        if (len <= 0) return "";
        var span = data.AsSpan(start, len);
        if (msgOff > 0 && msgOff < len) span = span[msgOff..];
        else if (msgOff >= len) return "";
        return Encoding.UTF8.GetString(span);
    }

    /// <summary>字符串入局部字典：已存在返回旧 id，否则追加。空串返回 -1（不进字典）。</summary>
    static int Intern(string s, Dictionary<string, int> map, List<string> list)
    {
        if (string.IsNullOrEmpty(s)) return -1;
        if (map.TryGetValue(s, out int id)) return id;
        id = list.Count;
        map[s] = id;
        list.Add(s);
        return id;
    }

    static ScanPart EmptyPart() => new()
    {
        Offs = Array.Empty<long>(), Lens = Array.Empty<int>(), Moff = Array.Empty<int>(),
        Within = Array.Empty<long>(), Years = Array.Empty<int>(),
        Pids = Array.Empty<int>(), Tids = Array.Empty<int>(),
        Lvls = Array.Empty<byte>(), TagIds = Array.Empty<int>(), Flags = Array.Empty<byte>(),
        LocalTags = new List<byte[]>(),
        Crashes = new List<CrashEvent>(), LocalExcTypes = new List<string>(), LocalPkgs = new List<string>()
    };

    // ── 组装 ──
    // 只有「单分片」一种形态：DoBuild 一次 ScanRange(0, Size) 扫完整个文件。
    // 曾经的多分片合并（按 BLOCK 分块扫描 + 拼接数组 + 全局 tag 重映射）已随分块方案一起删除。
    void Assemble(ScanPart part, int? baseYear, IProgress<(double, string)>? progress)
    {
        if (part.Offs.Length == 0)
        {
            SetColumns(Array.Empty<long>(), Array.Empty<int>(), Array.Empty<int>(),
                       Array.Empty<long>(), Array.Empty<int>(), Array.Empty<int>(),
                       Array.Empty<byte>(), Array.Empty<int>(), Array.Empty<byte>());
            Tags = new(); TagIndex = new();
            _crashes = new(); ExcTypes = new(); ExcTypeIndex = new();
            Pkgs = new(); PkgIndex = new();
            return;
        }

        progress?.Report((0.95, Loc.T("合并索引…")));

        // tag 字典：单分片下分区内的局部 id 就是全局 id（ScanRange 按出现顺序从 0 编号），无需重映射
        var tagIndex = new Dictionary<string, int>();
        var tags = new List<string>(part.LocalTags.Count);
        for (int i = 0; i < part.LocalTags.Count; i++)
        {
            string name = Encoding.UTF8.GetString(part.LocalTags[i]);
            if (tagIndex.TryAdd(name, tags.Count)) tags.Add(name);
        }
        Tags = tags;
        TagIndex = tagIndex;

        // 时间戳终算
        HasYear = part.Years.Any(y => y >= 0);
        var (ts, by, yl) = FinalizeTs(part.Within, part.Years, baseYear);
        BaseYear = by;
        _yearLast = yl;
        var validWithin = part.Within.Where(w => w >= 0).ToArray();
        _withinLast = validWithin.Length > 0 ? validWithin[^1] : null;

        SetColumns(part.Offs, part.Lens, part.Moff, ts, part.Pids, part.Tids, part.Lvls, part.TagIds, part.Flags);

        // ── 崩溃事件落表（单分片：局部 id 即全局 id，无需重映射）──
        // 时间戳在列终算后回填（扫描期 ts 未定）；Row 为绝对行号（全量扫描从 0 起）
        _crashes = new List<CrashEvent>(part.Crashes.Count);
        foreach (var ce in part.Crashes)
        {
            var e = ce;
            e.Ts = e.Row < ts.Length ? ts[e.Row] : -1;
            _crashes.Add(e);
        }
        var excIndex = new Dictionary<string, int>();
        var excTypes = new List<string>(part.LocalExcTypes.Count);
        for (int i = 0; i < part.LocalExcTypes.Count; i++)
            if (excIndex.TryAdd(part.LocalExcTypes[i], excTypes.Count)) excTypes.Add(part.LocalExcTypes[i]);
        ExcTypes = excTypes; ExcTypeIndex = excIndex;
        var pkgIndex = new Dictionary<string, int>();
        var pkgs = new List<string>(part.LocalPkgs.Count);
        for (int i = 0; i < part.LocalPkgs.Count; i++)
            if (pkgIndex.TryAdd(part.LocalPkgs[i], pkgs.Count)) pkgs.Add(part.LocalPkgs[i]);
        Pkgs = pkgs; PkgIndex = pkgIndex;
    }

    // ── 时间戳终算 ──
    static (long[] ts, int baseYear, int lastYear) FinalizeTs(long[] within, int[] years, int? baseYear,
        int yearIdxStart = 0, int? prevYear = null, long? prevWithin = null)
    {
        int n = within.Length;
        if (n == 0)
        {
            int y = baseYear ?? DateTime.Now.Year;
            return (Array.Empty<long>(), y, y);
        }

        // 跨年检测
        long[] w = new long[n];
        int[] run = new int[n];
        int lastValid = -1;
        for (int i = 0; i < n; i++)
        {
            w[i] = within[i] >= 0 ? within[i] : -1;
            if (within[i] >= 0) lastValid = i;
            run[i] = lastValid;
        }

        long[] refArr = new long[n];
        refArr[0] = prevWithin ?? -1;
        for (int i = 1; i < n; i++)
            refArr[i] = run[i - 1] >= 0 ? w[run[i - 1]] : -1;

        bool[] fb = new bool[n];
        for (int i = 0; i < n; i++)
            fb[i] = within[i] >= 0 && refArr[i] >= 0 && within[i] < refArr[i] - HALF_DAY;

        int[] roll = new int[n];
        int cum = 0;
        for (int i = 0; i < n; i++)
        {
            if (fb[i]) cum++;
            roll[i] = cum;
        }

        // 每行的绝对年份
        bool anyKnown = years.Any(y => y >= 0);
        int[] yAbs;
        int baseY;

        if (anyKnown)
        {
            int first = Array.FindIndex(years, y => y >= 0);
            baseY = baseYear ?? years[first];
            yAbs = new int[n];

            // 第一个已知年份之前的行
            int anchorYear = prevYear ?? years[first];
            for (int i = 0; i < first; i++)
                yAbs[i] = anchorYear;

            // 从 first 开始传播
            int kidx = 0;
            for (int i = first; i < n; i++)
            {
                if (years[i] >= 0) kidx = i;
                yAbs[i] = years[i] >= 0 ? years[i] : years[kidx] + (roll[i] - roll[kidx]);
            }
            // fix: for indices before first, use roll[0]
            for (int i = 0; i < first; i++)
                yAbs[i] = anchorYear + roll[i];
        }
        else
        {
            baseY = baseYear ?? GuessYear(within);
            yAbs = new int[n];
            int startY = prevYear ?? (baseY + yearIdxStart);
            for (int i = 0; i < n; i++)
                yAbs[i] = startY + roll[i];
        }

        // 换算绝对毫秒
        var (offs, adj) = YearTables(baseY);
        long[] ts = new long[n];
        for (int i = 0; i < n; i++)
        {
            int yi = Math.Clamp(yAbs[i] - baseY - YEAR_LO, 0, YEAR_HI - YEAR_LO - 1);
            ts[i] = within[i] + offs[yi];
            if (within[i] >= LogParser.LEAP_BOUNDARY)
                ts[i] -= adj[yi];
            if (within[i] < 0) ts[i] = -1;
        }

        return (ts, baseY, yAbs[^1]);
    }

    static int GuessYear(long[] within)
    {
        var now = DateTime.Now;
        int validIdx = Array.FindIndex(within, w => w >= 0);
        if (validIdx < 0) return now.Year;
        // within 用「闰年日序」编码（见 MDAYS），比较基准必须换算到同一编码，
        // 否则非闰年的 3 月之后整体差一天，Loc.T("今天")的日志会被判成去年。
        long todayWithin = (now.DayOfYear - 1) * LogParser.DAY_MS;
        if (!LogParser.IsLeap(now.Year) && now.DayOfYear > 59) todayWithin += LogParser.DAY_MS;
        long first = within[validIdx];
        return first - todayWithin > LogParser.DAY_MS ? now.Year - 1 : now.Year;
    }

    static (long[] offs, long[] adj) _yearTablesCache;
    static int _yearTablesBaseYear;

    static (long[] offs, long[] adj) YearTables(int baseYear)
    {
        if (_yearTablesBaseYear == baseYear && _yearTablesCache.offs != null)
            return _yearTablesCache;

        int span = YEAR_HI - YEAR_LO;
        long[] offs = new long[span];
        long[] adj = new long[span];
        var refDate = new DateTime(baseYear, 1, 1);
        for (int i = 0; i < span; i++)
        {
            int y = YEAR_LO + i;
            var target = new DateTime(baseYear + y, 1, 1);
            offs[i] = (long)(target - refDate).TotalMilliseconds;
            adj[i] = LogParser.IsLeap(baseYear + y) ? 0 : LogParser.DAY_MS;
        }
        _yearTablesCache = (offs, adj);
        _yearTablesBaseYear = baseYear;
        return (offs, adj);
    }

    // ── 增量 ──
    public string Reload(bool? join = null, IProgress<(double, string)>? progress = null, CancellationToken ct = default)
    {
        if (join.HasValue) Join = join.Value;
        long newSize = new FileInfo(Path).Length;
        if (newSize == IndexedSize && _view != null)
            return "unchanged";
        if (newSize < IndexedSize || IndexedSize == 0 || _view == null)
        {
            DoBuild(BaseYear, Join, progress, ct);
            return "rebuilt";
        }

        // 检查头部是否变化
        int head = (int)Math.Min(IndexedSize, 1 << 16);
        byte[] headBuf = new byte[head];
        using (var f = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            f.ReadExactly(headBuf);

        byte[] oldHead = new byte[head];
        _view!.ReadArray(0, oldHead, 0, head);

        if (!headBuf.AsSpan().SequenceEqual(oldHead))
        {
            DoBuild(BaseYear, Join, progress, ct);
            return "rebuilt";
        }

        return Append(newSize, progress, ct);
    }

    // ── 批末合并状态跨批缓存（X-A8 批界配套 fix-5 + fix-6）──
    // ScanRange 透传「批末真实状态」（空行已重置、横幅不重置），替代按已索引行反推——
    // 后者看不见批尾空行，会让全量/增量两路径在批界处结论相反。
    bool _prevHeadAtEnd;
    // 批末 joined：自末条记录头起的续行总数，保证 MAX_JOIN 是单记录全局上限而非每批预算
    int _joinedAtEnd;

    string Append(long newSize, IProgress<(double, string)>? progress, CancellationToken ct)
    {
        progress?.Report((0.0, Loc.T("追加新内容…")));

        // 裁掉末尾不完整行（流式写入的 flush 边界可能切在行中间），
        // 未完部分留待下次追加，避免同一行被拆成两条记录
        newSize = TrimPartial(newSize, IndexedSize);
        if (newSize <= IndexedSize)
            return "unchanged";

        var part = ScanRange(Path, IndexedSize, newSize, Join, _prevHeadAtEnd, _joinedAtEnd, progress, ct);
        if (ct.IsCancellationRequested) return "unchanged";
        // 批末状态必须在任何早退分支之前入缓存：整批空行走 "unchanged" 但合并状态已变
        _prevHeadAtEnd = part.EndPrevHead;
        _joinedAtEnd = part.EndJoined;
        LastAppendCarried = part.CarryLines > 0;

        // 跨批次续行：本批开头的续行并回上一批末尾那条记录，
        // 否则多行日志会在批次边界被切成两条（只影响列存储，文本本体不动）
        if (part.CarryLines > 0 && _n > 0)
        {
            _lens[_n - 1] = (int)(part.CarryEndOff - _offs[_n - 1]);
            _flags[_n - 1] |= FLAG_MULTILINE;

            // 末记录若是崩溃事件且被本批续行拉长：copy-on-write 补增 LineCount。
            // 此处先行更新，覆盖「整批都是续行、part.Offs 为空」的早退分支（那里不做崩溃并入）
            var oldCrashes = _crashes;
            if (oldCrashes.Count > 0 && oldCrashes[^1].Row == _n - 1)
            {
                var last = oldCrashes[^1];
                last.LineCount += part.CarryLines;
                _crashes = new List<CrashEvent>(oldCrashes) { [oldCrashes.Count - 1] = last };
            }
        }

        if (part.Offs.Length == 0)
        {
            IndexedSize = newSize; Size = newSize;
            if (part.CarryLines > 0)
            {
                // 整批都是续行：末行被拉长了，必须重映射才能读到新增字节
                OpenMap();
                return "appended";
            }
            return "unchanged";
        }

        // 重映射 tag
        var remap = new int[part.LocalTags.Count + 1];
        remap[0] = -1;
        for (int i = 0; i < part.LocalTags.Count; i++)
        {
            string name = Encoding.UTF8.GetString(part.LocalTags[i]);
            if (!TagIndex.TryGetValue(name, out int g))
            {
                g = Tags.Count;
                TagIndex[name] = g;
                Tags.Add(name);
            }
            remap[i + 1] = g;
        }
        int[] newTagId = new int[part.TagIds.Length];
        for (int i = 0; i < part.TagIds.Length; i++)
        {
            int lid = part.TagIds[i];
            newTagId[i] = lid >= 0 ? remap[lid + 1] : -1;
        }

        // 时间戳
        var (ts, _, lastYear) = FinalizeTs(part.Within, part.Years, BaseYear, 0, _yearLast, _withinLast);
        if (part.Years.Any(y => y >= 0)) HasYear = true;
        if (ts.Length > 0) _yearLast = lastYear;
        var validW = part.Within.Where(w => w >= 0).ToArray();
        if (validW.Length > 0) _withinLast = validW[^1];

        int k = part.Offs.Length;
        int n = _n;
        EnsureCap(n + k);

        Array.Copy(part.Offs, 0, _offs, n, k);
        Array.Copy(part.Lens, 0, _lens, n, k);
        Array.Copy(part.Moff, 0, _moff, n, k);
        Array.Copy(ts, 0, _ts, n, k);
        Array.Copy(part.Pids, 0, _pid, n, k);
        Array.Copy(part.Tids, 0, _tid, n, k);
        Array.Copy(part.Lvls, 0, _lvl, n, k);
        Array.Copy(newTagId, 0, _tagId, n, k);
        Array.Copy(part.Flags, 0, _flags, n, k);

        // ── 崩溃事件并入（ANALYSIS-2.1 §2.2 第 3 条）──
        // 局部 ExcType/Pkg id 重映射到全局、Row +n 偏移、Ts 从本批 ts 列回填；
        // 整体引用替换（copy-on-write），后台写与 UI 读互不干扰
        if (part.Crashes.Count > 0)
        {
            var excRemap = new int[part.LocalExcTypes.Count + 1];
            excRemap[0] = -1;
            for (int i = 0; i < part.LocalExcTypes.Count; i++)
            {
                if (!ExcTypeIndex.TryGetValue(part.LocalExcTypes[i], out int g))
                {
                    g = ExcTypes.Count;
                    ExcTypeIndex[part.LocalExcTypes[i]] = g;
                    ExcTypes.Add(part.LocalExcTypes[i]);
                }
                excRemap[i + 1] = g;
            }
            var pkgRemap = new int[part.LocalPkgs.Count + 1];
            pkgRemap[0] = -1;
            for (int i = 0; i < part.LocalPkgs.Count; i++)
            {
                if (!PkgIndex.TryGetValue(part.LocalPkgs[i], out int g))
                {
                    g = Pkgs.Count;
                    PkgIndex[part.LocalPkgs[i]] = g;
                    Pkgs.Add(part.LocalPkgs[i]);
                }
                pkgRemap[i + 1] = g;
            }

            var merged = new List<CrashEvent>(_crashes.Count + part.Crashes.Count);
            merged.AddRange(_crashes);
            foreach (var ce in part.Crashes)
            {
                var e = ce;
                e.Row += n;
                e.ExcId = e.ExcId >= 0 ? excRemap[e.ExcId + 1] : -1;
                e.PkgId = e.PkgId >= 0 ? pkgRemap[e.PkgId + 1] : -1;
                e.Ts = ts[e.Row - n];   // Row 已偏移，减回 n 得本批列下标
                merged.Add(e);
            }
            _crashes = merged;
        }

        IndexedSize = newSize; Size = newSize;
        OpenMap();
        _n = n + k;
        return "appended";
    }

    /// <summary>把 size 回退到最后一个换行之后；若无完整行则返回 floor。</summary>
    long TrimPartial(long size, long floor)
    {
        try
        {
            using var f = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long pos = size - 1;
            while (pos >= floor)
            {
                f.Position = pos;
                if (f.ReadByte() == '\n')
                    return pos + 1;
                pos--;
            }
        }
        catch { }
        return floor;
    }

    // ── 定位 ──
    public int NearestPos(ReadOnlySpan<int> rows, long ts)
    {
        int n = rows.Length;
        if (n == 0) return 0;
        // 二分查找
        int lo = 0, hi = n - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_ts[rows[mid]] < ts) lo = mid + 1;
            else hi = mid;
        }
        if (lo <= 0) return 0;
        if (lo >= n) return n - 1;
        long diffLo = Math.Abs(_ts[rows[lo]] - ts);
        long diffPrev = Math.Abs(_ts[rows[lo - 1]] - ts);
        return diffLo < diffPrev ? lo : lo - 1;
    }

    // ── 时间戳文本 ──
    public static string TsToText(long ts, int baseYear, bool withMs = true)
    {
        if (ts < 0) return "";
        try
        {
            var dt = new DateTime(baseYear, 1, 1).AddMilliseconds(ts);
            return withMs
                ? dt.ToString("MM-dd HH:mm:ss.") + $"{dt.Millisecond:D3}"
                : dt.ToString("MM-dd HH:mm:ss");
        }
        catch { return ""; }
    }

    public static long TsFromParts(int year, int month, int day, int hour, int minute, int second, int ms,
        int baseYear, long? refTs = null)
    {
        if (hour < 0 || hour > 23 || minute < 0 || minute > 59 || second < 0 || second > 60 || ms < 0 || ms > 999)
            throw new ArgumentException("时间字段越界");
        long hms = hour * 3_600_000L + minute * 60_000L + second * 1000L + ms;
        if (month == -1)
        {
            if (refTs == null || refTs < 0)
                throw new ArgumentException("缺少参考时间");
            return (refTs.Value / LogParser.DAY_MS) * LogParser.DAY_MS + hms;
        }
        if (month < 1 || month > 12 || day < 1 || day > 31)
            throw new ArgumentException("日期字段越界");
        int[] mdays = { 0, 0, 31, 60, 91, 121, 152, 182, 213, 244, 274, 305, 335 };
        long within = ((long)mdays[month] + day - 1) * LogParser.DAY_MS + hms;
        int y = year > 0 ? year : baseYear;
        var (offs, adj) = YearTables(baseYear);
        int span = YEAR_HI - YEAR_LO;
        int yi = Math.Clamp(y - baseYear - YEAR_LO, 0, span - 1);
        long result = within + offs[yi];
        if (within >= LogParser.LEAP_BOUNDARY) result -= adj[yi];
        return result;
    }
}
