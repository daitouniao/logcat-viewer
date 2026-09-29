using System.IO.MemoryMappedFiles;
using System.Text;
using Microsoft.Extensions.Logging;

namespace logcat.Services;

/// <summary>
/// 日志文档：基于 MemoryMappedFile 的列式索引。
/// 文本本体始终留在磁盘（mmap），只有定长列进入内存。
/// </summary>
public sealed class LogDocument : IDisposable
{
    const int BLOCK = 1 << 23;          // 8 MB per scan chunk
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

    public byte[] HeaderBytes(int row)
    {
        if (_view == null) return Array.Empty<byte>();
        int mo = _moff[row];
        if (mo <= 1) return Array.Empty<byte>();
        int len = Math.Min(mo - 1, 23);
        byte[] buf = new byte[len];
        _view.ReadArray(_offs[row], buf, 0, len);
        return buf;
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
        progress?.Report((0.0, "扫描行…"));
        var part = ScanRange(Path, 0, Size, join, false, progress, ct);
        ct.ThrowIfCancellationRequested();
        Assemble(new[] { part }, baseYear, progress);
        IndexedSize = Size;
    }

    // ── 扫描 ──
    struct ScanPart
    {
        public long[] Offs; public int[] Lens; public int[] Moff;
        public long[] Within; public int[] Years;
        public int[] Pids; public int[] Tids;
        public byte[] Lvls; public int[] TagIds; public byte[] Flags;
        public List<byte[]> LocalTags;
        /// <summary>本批开头若干行属于「上一批末尾那条记录」的续行（跨批次合并）。</summary>
        public int CarryLines;
        /// <summary>这些续行结束处的绝对文件偏移。</summary>
        public long CarryEndOff;
    }

    static ScanPart ScanRange(string path, long start, long end, bool join, bool prevHeadDoc = false,
        IProgress<(double, string)>? progress = null, CancellationToken ct = default)
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

        int prev = -1;
        // 续行状态跨批次传递：本批第一行如果属于上一批末尾那条记录，要并回去，
        // 否则多行日志（崩溃堆栈）会在批次边界被切成年两条记录。
        bool prevHead = prevHeadDoc;
        int joined = 0;
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
            f.Position = end;
            int b = f.ReadByte();
            while (b >= 0 && b != '\n') b = f.ReadByte();
            end = b < 0 ? fileSize : f.Position;
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
                // 空行：不索引，中断合并
                prev = -1;
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
                }
                else
                {
                    // prev < 0：并回「上一批末尾那条记录」（由 Append 写回列存储）
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

            prev = offs.Count - 1;
            prevHead = pr.WithinMs >= 0 || pr.Level > 0;
            joined = 0;

            pos = nl >= 0 ? nl + 1 : data.Length;

            // 进度报告
            lineInChunk++;
            if (progress != null && pos >= nextReport)
            {
                nextReport = pos + (1 << 22);
                progress.Report(((double)pos / data.Length, "扫描行…"));
            }
            if (lineInChunk % 5000 == 0)
            {
                ct.ThrowIfCancellationRequested();
            }
        }

        return new ScanPart
        {
            Offs = offs.ToArray(), Lens = lens.ToArray(), Moff = moff.ToArray(),
            Within = within.ToArray(), Years = years.ToArray(),
            Pids = pids.ToArray(), Tids = tids.ToArray(),
            Lvls = lvls.ToArray(), TagIds = tagIds.ToArray(), Flags = flags.ToArray(),
            LocalTags = tagList,
            CarryLines = carryLines, CarryEndOff = carryEndOff
        };
    }

    static ScanPart EmptyPart() => new()
    {
        Offs = Array.Empty<long>(), Lens = Array.Empty<int>(), Moff = Array.Empty<int>(),
        Within = Array.Empty<long>(), Years = Array.Empty<int>(),
        Pids = Array.Empty<int>(), Tids = Array.Empty<int>(),
        Lvls = Array.Empty<byte>(), TagIds = Array.Empty<int>(), Flags = Array.Empty<byte>(),
        LocalTags = new List<byte[]>()
    };

    // ── 组装 ──
    void Assemble(ScanPart[] parts, int? baseYear, IProgress<(double, string)>? progress)
    {
        // 过滤空分区
        var valid = parts.Where(p => p.Offs.Length > 0).ToArray();
        if (valid.Length == 0)
        {
            SetColumns(Array.Empty<long>(), Array.Empty<int>(), Array.Empty<int>(),
                       Array.Empty<long>(), Array.Empty<int>(), Array.Empty<int>(),
                       Array.Empty<byte>(), Array.Empty<int>(), Array.Empty<byte>());
            Tags = new(); TagIndex = new();
            return;
        }

        progress?.Report((0.95, "合并索引…"));

        // 合并 tag 字典
        var globalTagIndex = new Dictionary<string, int>();
        var globalTags = new List<string>();
        var remaps = new int[valid.Length][];

        for (int pi = 0; pi < valid.Length; pi++)
        {
            var local = valid[pi].LocalTags;
            var remap = new int[local.Count + 1];
            remap[0] = -1;
            for (int i = 0; i < local.Count; i++)
            {
                string name = Encoding.UTF8.GetString(local[i]);
                if (!globalTagIndex.TryGetValue(name, out int g))
                {
                    g = globalTags.Count;
                    globalTagIndex[name] = g;
                    globalTags.Add(name);
                }
                remap[i + 1] = g;
            }
            remaps[pi] = remap;
        }

        // 拼接数组
        long[] offs = Concat(valid.Select(p => p.Offs).ToArray());
        int[] lens = Concat(valid.Select(p => p.Lens).ToArray());
        int[] moff = Concat(valid.Select(p => p.Moff).ToArray());
        long[] withinArr = Concat(valid.Select(p => p.Within).ToArray());
        int[] yearsArr = Concat(valid.Select(p => p.Years).ToArray());
        int[] pidArr = Concat(valid.Select(p => p.Pids).ToArray());
        int[] tidArr = Concat(valid.Select(p => p.Tids).ToArray());
        byte[] lvlArr = ConcatByte(valid.Select(p => p.Lvls).ToArray());
        byte[] flagsArr = ConcatByte(valid.Select(p => p.Flags).ToArray());

        // 映射 tag id
        int totalLen = offs.Length;
        int[] tagId = new int[totalLen];
        int offset = 0;
        for (int pi = 0; pi < valid.Length; pi++)
        {
            var localTagIds = valid[pi].TagIds;
            var remap = remaps[pi];
            for (int i = 0; i < localTagIds.Length; i++)
            {
                int lid = localTagIds[i];
                tagId[offset + i] = lid >= 0 ? remap[lid + 1] : -1;
            }
            offset += localTagIds.Length;
        }

        Tags = globalTags;
        TagIndex = globalTagIndex;

        // 时间戳终算
        HasYear = yearsArr.Any(y => y >= 0);
        var (ts, by, yl) = FinalizeTs(withinArr, yearsArr, baseYear);
        BaseYear = by;
        _yearLast = yl;
        var validWithin = withinArr.Where(w => w >= 0).ToArray();
        _withinLast = validWithin.Length > 0 ? validWithin[^1] : null;

        SetColumns(offs, lens, moff, ts, pidArr, tidArr, lvlArr, tagId, flagsArr);
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
        // 否则非闰年的 3 月之后整体差一天，"今天"的日志会被判成去年。
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

    /// <summary>文档末行是否是一条「还能继续吸收续行」的记录（有时间戳或级别）。</summary>
    bool PrevRowJoinable() => _n > 0 && (_ts[_n - 1] >= 0 || _lvl[_n - 1] > 0);

    string Append(long newSize, IProgress<(double, string)>? progress, CancellationToken ct)
    {
        progress?.Report((0.0, "追加新内容…"));

        // 裁掉末尾不完整行（流式写入的 flush 边界可能切在行中间），
        // 未完部分留待下次追加，避免同一行被拆成两条记录
        newSize = TrimPartial(newSize, IndexedSize);
        if (newSize <= IndexedSize)
            return "unchanged";

        var part = ScanRange(Path, IndexedSize, newSize, Join, PrevRowJoinable(), progress, ct);
        if (ct.IsCancellationRequested) return "unchanged";
        LastAppendCarried = part.CarryLines > 0;

        // 跨批次续行：本批开头的续行并回上一批末尾那条记录，
        // 否则多行日志会在批次边界被切成两条（只影响列存储，文本本体不动）
        if (part.CarryLines > 0 && _n > 0)
        {
            _lens[_n - 1] = (int)(part.CarryEndOff - _offs[_n - 1]);
            _flags[_n - 1] |= FLAG_MULTILINE;
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

    // ── 工具 ──
    static T[] Concat<T>(T[][] arrays)
    {
        if (arrays.Length == 1) return arrays[0];
        int total = arrays.Sum(a => a.Length);
        var result = new T[total];
        int off = 0;
        foreach (var a in arrays) { Array.Copy(a, 0, result, off, a.Length); off += a.Length; }
        return result;
    }

    static byte[] ConcatByte(byte[][] arrays)
    {
        if (arrays.Length == 1) return arrays[0];
        int total = arrays.Sum(a => a.Length);
        var result = new byte[total];
        int off = 0;
        foreach (var a in arrays) { Array.Copy(a, 0, result, off, a.Length); off += a.Length; }
        return result;
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
