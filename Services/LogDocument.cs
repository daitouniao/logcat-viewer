using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Text;
using logcat.Models;
using Microsoft.Extensions.Logging;

namespace logcat.Services;

/// <summary>
/// 正文读取回调：<see cref="ReadOnlySpan{T}"/> 只在回调执行期间有效，
/// 调用方不得将其带出回调作用域（形态上由 span 参数保证，无法存入字段或长期引用）。
/// </summary>
public delegate void MessageBodyAction(ReadOnlySpan<byte> body);

/// <summary>
/// 日志文档：基于 MemoryMappedFile 的列式索引。
/// 文本本体始终留在磁盘（mmap），只有定长列进入内存。
/// </summary>
public sealed class LogDocument : IDisposable
{
    /// <summary>分钟列哨兵：行首无有效时间戳的记录存 255（分钟集合只含 0-59，天然不命中）。</summary>
    public const byte MINUTE_NONE = 255;

    static readonly ILogger _logger = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance.CreateLogger("LogDocument");

    /// <summary>
    /// 显示路径（用户原路径）：标题栏、MRU、导出默认文件名等 UI 面统一用它。
    /// 非代理文档与源路径相同；代理文档指向用户给的原始路径（spec「文档路径语义」）。
    /// </summary>
    public string Path { get; }

    /// <summary>源路径（实际读取的文件）：内部触及文件的唯一锚。代理文档时为暂存文件。</summary>
    public string SourcePath { get; }

    /// <summary>是否经文件代理装载（决策 D6：显式布尔标记，不做运行时路径比较）。</summary>
    public bool IsProxied { get; internal set; }

    /// <summary>
    /// spool 暂存目录租约（代理文档非 null）：目录路径 + 活性锁句柄。
    /// Close 在既有释放序列之后<b>先删暂存目录、后释放活性锁</b>（不变式：目录存在 ⇒ 锁仍持有），
    /// 删不掉只留痕不抛；非代理文档恒为 null，Close 不做任何目录操作（零行为变化）。
    /// </summary>
    public SpoolDirLease? SpoolDir { get; internal set; }

    public string EncodingName { get; } = "utf-8";
    public long Size { get; private set; }
    public long IndexedSize { get; private set; }

    // ── 列存储 ──
    // 自然行口径：除空行与横幅行外，每一物理行各成一条记录，不合并续行。
    long[] _offs = Array.Empty<long>();
    int[] _lens = Array.Empty<int>();
    int[] _moff = Array.Empty<int>();
    byte[] _min = Array.Empty<byte>();
    int[] _pid = Array.Empty<int>();
    int[] _tid = Array.Empty<int>();
    byte[] _lvl = Array.Empty<byte>();
    int[] _tagId = Array.Empty<int>();
    int _n;      // 有效行数
    int _cap;    // 已分配容量

    // ── Tag 字典 ──
    public List<string> Tags { get; private set; } = new();
    public Dictionary<string, int> TagIndex { get; private set; } = new();

    // ── 崩溃事件（稀疏）──
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

    public LogDocument(string path, string encoding = "utf-8", string? displayPath = null)
    {
        SourcePath = System.IO.Path.GetFullPath(path);
        // 显示路径默认等于源路径；代理文档传入用户原路径（可能是打不开的受限路径，仅作展示，不触发文件访问）
        Path = displayPath is null ? SourcePath : System.IO.Path.GetFullPath(displayPath);
        EncodingName = encoding;
    }

    // ── 列只读视图 ──
    public ReadOnlySpan<long> Offs => _offs.AsSpan(0, _n);
    public ReadOnlySpan<int> Lens => _lens.AsSpan(0, _n);
    public ReadOnlySpan<int> Moff => _moff.AsSpan(0, _n);
    /// <summary>分钟列：行首时间戳的原始分钟 0-59；无有效时间戳为 <see cref="MINUTE_NONE"/>。</summary>
    public ReadOnlySpan<byte> Minute => _min.AsSpan(0, _n);
    public ReadOnlySpan<int> Pid => _pid.AsSpan(0, _n);
    public ReadOnlySpan<int> Tid => _tid.AsSpan(0, _n);
    public ReadOnlySpan<byte> Lvl => _lvl.AsSpan(0, _n);
    public ReadOnlySpan<int> TagId => _tagId.AsSpan(0, _n);
    public int RowCount => _n;

    // ── 容量管理 ──
    void SetColumns(long[] offs, int[] lens, int[] moff, byte[] min, int[] pid, int[] tid,
                    byte[] lvl, int[] tagId)
    {
        int n = offs.Length;
        _n = n;
        _cap = n + (n >> 2) + 1024;
        _offs = new long[_cap]; _lens = new int[_cap]; _moff = new int[_cap];
        _min = new byte[_cap]; _pid = new int[_cap]; _tid = new int[_cap];
        _lvl = new byte[_cap]; _tagId = new int[_cap];
        if (n > 0)
        {
            Array.Copy(offs, _offs, n); Array.Copy(lens, _lens, n);
            Array.Copy(moff, _moff, n); Array.Copy(min, _min, n);
            Array.Copy(pid, _pid, n); Array.Copy(tid, _tid, n);
            Array.Copy(lvl, _lvl, n); Array.Copy(tagId, _tagId, n);
        }
    }

    void EnsureCap(int need)
    {
        if (need <= _cap) return;
        int cap = Math.Max(need, _cap + Math.Max(_cap >> 2, 1 << 16));
        Array.Resize(ref _offs, cap); Array.Resize(ref _lens, cap); Array.Resize(ref _moff, cap);
        Array.Resize(ref _min, cap); Array.Resize(ref _pid, cap); Array.Resize(ref _tid, cap);
        Array.Resize(ref _lvl, cap); Array.Resize(ref _tagId, cap);
        _cap = cap;
    }

    // ── mmap 管理 ──
    void OpenMap()
    {
        ReleaseRetired();
        _retired = (_view, _mmf, _fh);
        _view = null; _mmf = null; _fh = null;

        _fh = new FileStream(SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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
        // 代理文档：既有释放序列之后先删暂存目录、后释放活性锁（决策 D4，删不掉只留痕不抛）。
        // 非代理文档 SpoolDir 为 null，零行为变化。
        if (SpoolDir != null)
        {
            FileProxyService.DeleteDirBestEffort(SpoolDir.Dir);
            SpoolDir.Release();    // 不变式：目录存在 ⇒ 锁仍持有，故删目录在前、释放锁在后
            SpoolDir = null;
        }
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
    /// 方括号 long 格式（-v long）行首是 "[ "，跳过 2 字节再按日期形态判定；
    /// 行首不是时间戳（如 brief 的 E/Tag 形式）返回空。
    /// </summary>
    public byte[] HeaderBytes(int row)
    {
        if (_view == null) return Array.Empty<byte>();
        int mo = _moff[row];
        int len = Math.Min(mo, 40);   // yyyy-MM-dd HH:mm:ss.fffffffff 最长 33 字节
        if (len <= 0) return Array.Empty<byte>();
        byte[] buf = new byte[len];
        _view.ReadArray(_offs[row], buf, 0, len);

        // 方括号 long 格式行首为 "[ "（91 32）：跳过前缀再按既有两种日期形态判定
        int s = len >= 2 && buf[0] == 91 && buf[1] == 32 ? 2 : 0;

        int dot = len - s > 14 && buf[s + 2] == 45 && buf[s + 5] == 32 && buf[s + 14] == 46 ? s + 14
                : len - s > 19 && buf[s + 4] == 45 && buf[s + 7] == 45 && buf[s + 19] == 46 ? s + 19 : -1;
        if (dot < 0) return Array.Empty<byte>();
        int end = dot + 1;
        while (end < len && buf[end] >= 48 && buf[end] <= 57) end++;
        if (end == dot + 1) return Array.Empty<byte>();
        if (end == len && s == 0) return buf;
        var ts = new byte[end - s];
        Array.Copy(buf, s, ts, 0, end - s);
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

    // ── 零拷贝正文读取会话 ──

    /// <summary>
    /// 打开只读正文读取会话：一次 AcquirePointer/ReleasePointer 配对的生命周期内，
    /// 以零拷贝方式读取各行正文（字节序列与 <see cref="MessageBytes"/> 逐字节一致）。
    /// </summary>
    /// <remarks>
    /// <para>会话<b>复用文档当前的映射视图</b>（容量随 <c>Append</c> 重映射刷新），
    /// 不自持独立映射——独立映射的容量冻结于创建时刻，文件增长后读新追加行即越界，
    /// 而裸指针越界读是进程段错误（exit 139）而非托管异常。</para>
    /// <para>正文经回调交付：<see cref="ReadOnlySpan{T}"/> 只在回调执行期间有效，
    /// 调用方不得把它带出回调作用域（会话释放后 span 即悬垂，且不抛异常、只产乱码）。
    /// 回调形态让「span 不得逸出」成为编译期可检查的结构约束。</para>
    /// <para>会话应在 using 作用域内使用、尽早释放；不可跨 <see cref="Close"/> 使用
    /// （Close 释放映射后继续使用会话属误用）。</para>
    /// </remarks>
    public MessageSession OpenMessageSession() => new(this);

    /// <summary>
    /// 只读正文读取会话（见 <see cref="LogDocument.OpenMessageSession"/>）。
    /// 指针生命周期由 AcquirePointer/ReleasePointer 配对控制：映射由 Acquire 的引用计数托住，
    /// 与文档视图是否被换掉无关；但每次过滤都应现取新会话，避免跨调用持有指针读到旧映射内容。
    /// </summary>
    public sealed unsafe class MessageSession : IDisposable
    {
        readonly LogDocument _doc;
        readonly MemoryMappedViewAccessor? _view;
        readonly long _capacity;
        byte* _ptr;
        bool _released;

        internal MessageSession(LogDocument doc)
        {
            _doc = doc;
            _view = doc._view;
            _capacity = _view?.Capacity ?? 0;
            if (_view != null)
                _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _ptr);
        }

        /// <summary>建会话时刻的映射容量（字节）。「偏移+长度」超出它的读取被护栏拦截为空片段。</summary>
        public long Capacity => _capacity;

        /// <summary>
        /// 回调式读取第 <paramref name="row"/> 行的正文片段，边界与 <see cref="MessageBytes"/> 一致：
        /// 起于「行首偏移 + 消息偏移」、长为「行长度 - 消息偏移」。
        /// 行号为负、超出行数、消息偏移大于等于行长（无正文）、或「偏移+长度」超会话容量时，
        /// 以空片段调用回调（裸指针无越界保护，必须构造 span 前校验）。
        /// 会话释放后调用同样返回空片段（误用不抛，但不保证内容）。
        /// </summary>
        public void ReadMessage(int row, MessageBodyAction body)
        {
            ArgumentNullException.ThrowIfNull(body);
            body(MessageSpan(row));
        }

        /// <summary>
        /// 与 <see cref="ReadMessage"/> 同一套边界与护栏（行号范围 / 无正文 / 映射容量），
        /// 但直接返回正文 span 而不经回调。
        /// <para>
        /// <b>internal：仅供同程序集的过滤引擎热循环使用</b>——1582 万行的逐行过滤里，
        /// 每行一次「方法调用 + 委托间接调用 + 闭包字段回写」合计可达数百 ms，
        /// 故引擎热循环走直连接口；span 只在调用语句内消费，不得存入任何位置。
        /// 对外契约仍是回调式 <see cref="ReadMessage"/>：span 逸出即悬垂读
        /// （不抛异常、只产乱码），回调形态让逸出成为编译期错误。
        /// </para>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ReadOnlySpan<byte> MessageSpan(int row)
        {
            var doc = _doc;
            if (_released || _ptr == null || row < 0 || row >= doc._n)
                return ReadOnlySpan<byte>.Empty;
            long off = doc._offs[row];
            int mo = doc._moff[row];
            int mlen = doc._lens[row] - mo;
            if (mlen <= 0)
                return ReadOnlySpan<byte>.Empty;
            long start = off + mo;
            // 容量护栏：文档视图可能已被 Append 重映射（本会话指针指向旧映射），
            // 经旧指针读新行 = 越界 = 段错误，必须先校验「偏移 + 长度 ≤ 建会话时的容量」
            if (start < 0 || start + mlen > _capacity)
                return ReadOnlySpan<byte>.Empty;
            return new ReadOnlySpan<byte>(_ptr + start, mlen);
        }

        /// <summary>
        /// internal：块扫描用的原始基指针（<see cref="FilterEngine"/> 的并行分块扫描）。
        /// 指针为映射视图数据起点（已含 <c>PointerOffset</c>），只在会话 Dispose 前有效；
        /// 容量是建会话时的快照，与 <see cref="MessageSpan"/> 的护栏同源 ——
        /// 调用方须按「偏移 + 长度 ≤ 容量」自检后再构造 span。
        /// 返回 false 表示会话已释放或无映射（空文件），调用方回退逐条匹配。
        /// </summary>
        internal bool TryAcquireRaw(out byte* ptr, out long capacity)
        {
            if (_released || _ptr == null)
            {
                ptr = null;
                capacity = 0;
                return false;
            }
            ptr = _ptr;
            capacity = _capacity;
            return true;
        }

        /// <summary>释放指针。幂等：重复调用不抛。</summary>
        public void Dispose()
        {
            if (_released) return;
            _released = true;
            if (_ptr != null)
            {
                _view!.SafeMemoryMappedViewHandle.ReleasePointer();
                _ptr = null;
            }
        }
    }

    // ── 构建 ──
    public static async Task<LogDocument> BuildAsync(string path,
        IProgress<(double pct, string msg)>? progress = null, CancellationToken ct = default,
        string? displayPath = null)
    {
        return await Task.Run(() => Build(path, progress, ct, displayPath), ct);
    }

    public static LogDocument Build(string path,
        IProgress<(double pct, string msg)>? progress = null, CancellationToken ct = default,
        string? displayPath = null)
    {
        var doc = new LogDocument(path, displayPath: displayPath);
        doc.DoBuild(progress, ct);
        return doc;
    }

    void DoBuild(IProgress<(double, string)>? progress, CancellationToken ct)
    {
        Size = new FileInfo(SourcePath).Length;
        OpenMap();
        progress?.Report((0.0, Loc.T("扫描行…")));
        var part = ScanRange(SourcePath, 0, Size, progress, ct);
        ct.ThrowIfCancellationRequested();
        Assemble(part, progress);
        IndexedSize = Size;
    }

    // ── 扫描 ──
    /// <summary>internal：单元测试直接验证区间扫描的行边界对齐（见 InternalsVisibleTo）。</summary>
    internal struct ScanPart
    {
        public long[] Offs; public int[] Lens; public int[] Moff;
        public byte[] Minutes;
        public int[] Pids; public int[] Tids;
        public byte[] Lvls; public int[] TagIds;
        public List<byte[]> LocalTags;
        /// <summary>本批提取到的崩溃事件（稀疏方案）。Row 为批次内相对行号，Append 时 +n 偏移。</summary>
        public List<CrashEvent> Crashes;
        /// <summary>本批出现的异常类型 / 包名局部字典（id 即 CrashEvent.ExcId/PkgId）。</summary>
        public List<string> LocalExcTypes;
        public List<string> LocalPkgs;
    }

    /// <summary>
    /// 扫描 [start, end) 区间并按行切分。两端都对齐到行边界：start 落在行中间时跳到下一行行首
    /// （前半段已由上一批索引），end 落在行中间时回退到该行行首（不完整行留待下次追加）。
    /// internal：单元测试直接验证对齐语义（见 InternalsVisibleTo）。
    /// </summary>
    internal static ScanPart ScanRange(string path, long start, long end,
        IProgress<(double, string)>? progress = null, CancellationToken ct = default)
    {
        var offs = new List<long>();
        var lens = new List<int>();
        var moff = new List<int>();
        var minutes = new List<byte>();
        var pids = new List<int>();
        var tids = new List<int>();
        var lvls = new List<byte>();
        var tagIds = new List<int>();

        var tagMap = new Dictionary<string, int>();
        var tagList = new List<byte[]>();

        // ── 崩溃提取 ──
        var crashes = new List<CrashEvent>();
        var excMap = new Dictionary<string, int>();
        var excList = new List<string>();
        var pkgMap = new Dictionary<string, int>();
        var pkgList = new List<string>();

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
                // 空行：不是日志记录，不进索引（自然行口径下无状态可重置，仅跳过）。
                // 此分支是行为契约（空行不索引），变异测试有专用锚点守护，勿并入通用跳过。
                pos = nl >= 0 ? nl + 1 : data.Length;
                continue;
            }

            ReadOnlySpan<byte> line = data.AsSpan(lineOff, lineLen);

            // 横幅行（--------- beginning of main / ----- timezone:Asia/Kuala_Lumpur 等）
            // 不是日志记录：不进索引，与实时采集的过滤规则保持一致。
            // 此分支同为行为契约（横幅不索引），变异测试有专用锚点守护，勿并入通用跳过。
            if (LogParser.IsBanner(line))
            {
                pos = nl >= 0 ? nl + 1 : data.Length;
                continue;
            }

            LogParser.ParseResult pr;
            try { pr = LogParser.ParseLine(line); }
            catch { pr = new LogParser.ParseResult(-1, -1, -1, -1, -1, 0, "", 0); }

            offs.Add(currentOff);
            lens.Add(lineLen);
            moff.Add(pr.MsgOffset >= 0 && pr.MsgOffset <= lineLen ? pr.MsgOffset : 0);
            minutes.Add(pr.Minute < 0 ? MINUTE_NONE : (byte)pr.Minute);
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

            // ── 崩溃头识别（a 轨）──
            // 字节级快筛零成本放行绝大多数行；命中才解码 msg + 前瞻后续行拼文本做完整提取。
            // 自然行口径下 Process/异常类型行本就是独立记录，前瞻是拿到它们的唯一途径。
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
                        Kind = match.Value.Kind,
                        LineCount = 1,        // 过渡期占位（4.x 删 LineCount 字段时一并清理）
                    });
                }
            }

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

        // a 轨已按行序产出事件（offs 本就升序），无需再排序
        return new ScanPart
        {
            Offs = offs.ToArray(), Lens = lens.ToArray(), Moff = moff.ToArray(),
            Minutes = minutes.ToArray(),
            Pids = pids.ToArray(), Tids = tids.ToArray(),
            Lvls = lvls.ToArray(), TagIds = tagIds.ToArray(),
            LocalTags = tagList,
            Crashes = crashes, LocalExcTypes = excList, LocalPkgs = pkgList,
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
        Minutes = Array.Empty<byte>(),
        Pids = Array.Empty<int>(), Tids = Array.Empty<int>(),
        Lvls = Array.Empty<byte>(), TagIds = Array.Empty<int>(),
        LocalTags = new List<byte[]>(),
        Crashes = new List<CrashEvent>(), LocalExcTypes = new List<string>(), LocalPkgs = new List<string>()
    };

    // ── 组装 ──
    // 只有「单分片」一种形态：DoBuild 一次 ScanRange(0, Size) 扫完整个文件。
    // 曾经的多分片合并（按 BLOCK 分块扫描 + 拼接数组 + 全局 tag 重映射）已随分块方案一起删除。
    void Assemble(ScanPart part, IProgress<(double, string)>? progress)
    {
        if (part.Offs.Length == 0)
        {
            SetColumns(Array.Empty<long>(), Array.Empty<int>(), Array.Empty<int>(),
                       Array.Empty<byte>(), Array.Empty<int>(), Array.Empty<int>(),
                       Array.Empty<byte>(), Array.Empty<int>());
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

        SetColumns(part.Offs, part.Lens, part.Moff, part.Minutes, part.Pids, part.Tids, part.Lvls, part.TagIds);

        // ── 崩溃事件落表（单分片：局部 id 即全局 id，无需重映射）──
        // 事件只存行号，时间文本由展示侧经 HeaderBytes(行号) 现取（行序 + 原文，见 CrashTime）
        _crashes = new List<CrashEvent>(part.Crashes);
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

    // ── 增量 ──
    public string Reload(IProgress<(double, string)>? progress = null, CancellationToken ct = default)
    {
        long newSize = new FileInfo(SourcePath).Length;
        if (newSize == IndexedSize && _view != null)
            return "unchanged";
        if (newSize < IndexedSize || IndexedSize == 0 || _view == null)
        {
            DoBuild(progress, ct);
            return "rebuilt";
        }

        // 检查头部是否变化
        int head = (int)Math.Min(IndexedSize, 1 << 16);
        byte[] headBuf = new byte[head];
        using (var f = new FileStream(SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            f.ReadExactly(headBuf);

        byte[] oldHead = new byte[head];
        _view!.ReadArray(0, oldHead, 0, head);

        if (!headBuf.AsSpan().SequenceEqual(oldHead))
        {
            DoBuild(progress, ct);
            return "rebuilt";
        }

        return Append(newSize, progress, ct);
    }

    string Append(long newSize, IProgress<(double, string)>? progress, CancellationToken ct)
    {
        progress?.Report((0.0, Loc.T("追加新内容…")));

        // 裁掉末尾不完整行（流式写入的 flush 边界可能切在行中间），
        // 未完部分留待下次追加，避免同一行被拆成两条记录
        newSize = TrimPartial(newSize, IndexedSize);
        if (newSize <= IndexedSize)
            return "unchanged";

        var part = ScanRange(SourcePath, IndexedSize, newSize, progress, ct);
        if (ct.IsCancellationRequested) return "unchanged";

        if (part.Offs.Length == 0)
        {
            // 本批只有空行/横幅行：无新记录，仅推进索引口径（文本本体不必重映射读取）
            IndexedSize = newSize; Size = newSize;
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

        int k = part.Offs.Length;
        int n = _n;
        EnsureCap(n + k);

        Array.Copy(part.Offs, 0, _offs, n, k);
        Array.Copy(part.Lens, 0, _lens, n, k);
        Array.Copy(part.Moff, 0, _moff, n, k);
        Array.Copy(part.Minutes, 0, _min, n, k);
        Array.Copy(part.Pids, 0, _pid, n, k);
        Array.Copy(part.Tids, 0, _tid, n, k);
        Array.Copy(part.Lvls, 0, _lvl, n, k);
        Array.Copy(newTagId, 0, _tagId, n, k);

        // ── 崩溃事件并入 ──
        // 局部 ExcType/Pkg id 重映射到全局、Row +n 偏移；
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
            using var f = new FileStream(SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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

}
