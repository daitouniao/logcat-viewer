using System.Runtime.InteropServices;
using System.Text;
using logcat.Models;

namespace logcat.Services;

/// <summary>
/// sidecar 索引缓存的落盘载荷（design D3）：8 段 SoA 列 + 三字典 + 崩溃事件表。
/// 字典 id 即列表下标——与 <see cref="LogDocument"/> 的内部表示一致，装载时零重映射。
/// </summary>
internal sealed class SidecarPayload
{
    public long[] Offs = Array.Empty<long>();
    public int[] Lens = Array.Empty<int>();
    public int[] Moff = Array.Empty<int>();
    public byte[] Minutes = Array.Empty<byte>();
    public int[] Pids = Array.Empty<int>();
    public int[] Tids = Array.Empty<int>();
    public byte[] Lvls = Array.Empty<byte>();
    public int[] TagIds = Array.Empty<int>();

    public List<string> Tags = new();
    public List<string> ExcTypes = new();
    public List<string> Pkgs = new();
    public List<CrashEvent> Crashes = new();

    public int RowCount => Offs.Length;
}

/// <summary>
/// 源文件身份摘要（design D2）：<c>size</c> + <c>mtime</c> + 首尾各 64 KB 内容指纹。
/// 任一字段不等即视为「内容已不是同一份」，缓存失效重建。
/// <para>
/// 为什么还要内容指纹：FAT32/exFAT 的 mtime 精度只有 2 s 级，就地改写可能既不动 size
/// 也不动 mtime；首尾指纹覆盖最常见的「追加」与「头部改写」，代价恒定 128 KB 读取。
/// 全文件哈希覆面更宽，但 GB 级文件重开时自身就是秒级开销，与亚秒目标冲突，否决。
/// </para>
/// </summary>
internal readonly struct SidecarIdentity : IEquatable<SidecarIdentity>
{
    /// <summary>单侧指纹取样字节数。</summary>
    internal const int FingerprintBytes = 64 * 1024;

    internal long Size { get; }
    internal long MtimeTicks { get; }
    internal ulong HeadHash { get; }
    internal ulong TailHash { get; }

    internal SidecarIdentity(long size, long mtimeTicks, ulong headHash, ulong tailHash)
    {
        Size = size;
        MtimeTicks = mtimeTicks;
        HeadHash = headHash;
        TailHash = tailHash;
    }

    /// <summary>取样当前文件身份。文件不可读时抛异常，调用方按「无法校验」处理。</summary>
    internal static SidecarIdentity Capture(string path)
    {
        var info = new FileInfo(path);
        long size = info.Length;
        long ticks = info.LastWriteTimeUtc.Ticks;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        int len = (int)Math.Min(FingerprintBytes, size);
        var head = new byte[len];
        if (len > 0) fs.ReadExactly(head, 0, len);
        ulong headHash = Fnv1a(head);
        ulong tailHash = headHash;             // 小文件（≤ 64 KB）首尾是同一段
        if (size > len)
        {
            fs.Position = size - len;
            var tail = new byte[len];
            fs.ReadExactly(tail, 0, len);
            tailHash = Fnv1a(tail);
        }
        return new SidecarIdentity(size, ticks, headHash, tailHash);
    }

    /// <summary>取不到身份（文件临时不可读）返回 null——调用方据此放弃缓存，不影响正常打开。</summary>
    internal static SidecarIdentity? TryCapture(string path)
    {
        try { return Capture(path); }
        catch { return null; }
    }

    internal bool Matches(in SidecarIdentity other) => Equals(other);

    public bool Equals(SidecarIdentity other) =>
        Size == other.Size && MtimeTicks == other.MtimeTicks &&
        HeadHash == other.HeadHash && TailHash == other.TailHash;

    public override bool Equals(object? obj) => obj is SidecarIdentity other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Size, MtimeTicks, HeadHash, TailHash);

    /// <summary>
    /// FNV-1a 64：指纹只用于「同一文件内容变了没有」的偶然碰撞防护，
    /// 不承担加密强度，选它是因为逐字节无分支、对 128 KB 取样足够快（design D2 的成本取舍）。
    /// </summary>
    internal static ulong Fnv1a(ReadOnlySpan<byte> data)
    {
        ulong h = 14695981039346656037UL;
        foreach (byte b in data)
        {
            h ^= b;
            h *= 1099511628211UL;
        }
        return h;
    }
}

/// <summary>
/// 缓存条目名派生（design D2）：可读前缀 + 源路径哈希。
/// <b>不落源路径明文</b>——条目名会出现在用户磁盘与故障截图里，公开仓库红线要求
/// 不暴露用户目录结构与用户名；可读前缀只取文件名主干供人工辨认。
/// </summary>
internal static class SidecarKey
{
    internal const string Extension = ".lcs";
    const int MaxPrefixLength = 24;

    /// <summary>源路径 → 条目文件名（同文件不同写法落到同一条目：Windows 路径大小写不敏感）。</summary>
    internal static string DeriveName(string sourcePath)
    {
        var full = System.IO.Path.GetFullPath(sourcePath);
        var prefix = Sanitize(System.IO.Path.GetFileNameWithoutExtension(full));
        var hash = SidecarIdentity.Fnv1a(Encoding.UTF8.GetBytes(full.ToUpperInvariant()));
        return prefix + "_" + hash.ToString("x16") + Extension;
    }

    static string Sanitize(string stem)
    {
        var sb = new StringBuilder(MaxPrefixLength);
        foreach (var ch in stem)
        {
            if (sb.Length >= MaxPrefixLength) break;
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }
        return sb.Length == 0 ? "log" : sb.ToString();
    }
}

/// <summary>
/// sidecar 落盘格式（design D3）：头部（magic + 版本 + 8 段元素宽度 + 行数 + 各段长度 + 总长 +
/// 身份摘要 + 三字典 + 崩溃事件表）+ 8 段定宽原始字节顺序排布，不压缩。
/// <para>
/// 读侧严格校验 magic / 版本 / 段长 / 总长，任一不符即整份判无效（design D6「长度/版本不符即丢弃重建」），
/// 由调用方走全量解析并重建缓存；不做向后兼容读取，也不做部分修复。
/// </para>
/// <para>格式固定小端（Windows 桌面应用）；非小端平台直接拒绝，避免静默读错。</para>
/// </summary>
internal static class SidecarFile
{
    internal const int FormatVersion = 1;
    internal const int SegmentCount = 8;

    /// <summary>「各段期望长度」字段在头部中的起始偏移：magic 4 + 版本 4 + 行数 4 + 宽度 8。</summary>
    internal const int SegmentLengthsOffset = 20;
    internal const int SegmentLengthSize = 8;

    /// <summary>8 段元素宽度（字节）：offs / lens / moff / min / pid / tid / lvl / tagId。</summary>
    static readonly int[] Widths = { 8, 4, 4, 1, 4, 4, 1, 4 };
    /// <summary>单条崩溃事件落盘字节：Row/ExcId/PkgId/Pid/Tid 各 4 + Kind 1。</summary>
    const int CrashEntrySize = 21;
    /// <summary>字典 / 事件表的数量字段上限：防损坏文件诱导巨型分配。</summary>
    const int MaxTableCount = 1 << 24;
    const int MaxStringBytes = 1 << 20;

    static readonly byte[] Magic = { (byte)'L', (byte)'C', (byte)'S', (byte)'C' };

    internal static long SegmentsSize(SidecarPayload p) =>
        (long)p.Offs.Length * Widths[0] + (long)p.Lens.Length * Widths[1] +
        (long)p.Moff.Length * Widths[2] + (long)p.Minutes.Length * Widths[3] +
        (long)p.Pids.Length * Widths[4] + (long)p.Tids.Length * Widths[5] +
        (long)p.Lvls.Length * Widths[6] + (long)p.TagIds.Length * Widths[7];

    /// <summary>头部字节数（不含数据区）。写侧据此算总长，读侧据此校验总长。</summary>
    internal static long HeaderSize(SidecarPayload p) =>
        HeaderSize(p.Tags, p.ExcTypes, p.Pkgs, p.Crashes.Count);

    internal static long HeaderSize(List<string> tags, List<string> excTypes,
        List<string> pkgs, int crashCount) =>
        4                                // magic
        + 4                              // 版本
        + 4                              // 行数
        + SegmentCount                   // 段宽
        + SegmentCount * SegmentLengthSize
        + 8                              // 总长
        + 8 + 8 + 8 + 8                  // size / mtime / 首指纹 / 尾指纹
        + DictSize(tags) + DictSize(excTypes) + DictSize(pkgs)
        + 4 + (long)CrashEntrySize * crashCount;

    static long DictSize(List<string> items)
    {
        long n = 4;
        foreach (var s in items) n += 4 + Encoding.UTF8.GetByteCount(s);
        return n;
    }

    /// <summary>写出完整 sidecar。<paramref name="version"/> 仅测试用于构造「更高版本」的缓存。</summary>
    internal static void Write(Stream output, SidecarIdentity identity, SidecarPayload payload,
        int version = FormatVersion)
    {
        if (!BitConverter.IsLittleEndian)
            throw new PlatformNotSupportedException("sidecar 缓存格式固定小端");

        long[] segLengths =
        {
            (long)payload.Offs.Length * Widths[0], (long)payload.Lens.Length * Widths[1],
            (long)payload.Moff.Length * Widths[2], (long)payload.Minutes.Length * Widths[3],
            (long)payload.Pids.Length * Widths[4], (long)payload.Tids.Length * Widths[5],
            (long)payload.Lvls.Length * Widths[6], (long)payload.TagIds.Length * Widths[7],
        };
        long header = HeaderSize(payload);
        long total = header + SegmentsSize(payload);

        using (var w = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            w.Write(version);
            w.Write(payload.RowCount);
            foreach (var width in Widths) w.Write((byte)width);
            foreach (var len in segLengths) w.Write(len);
            w.Write(total);
            w.Write(identity.Size);
            w.Write(identity.MtimeTicks);
            w.Write(identity.HeadHash);
            w.Write(identity.TailHash);
            WriteDict(w, payload.Tags);
            WriteDict(w, payload.ExcTypes);
            WriteDict(w, payload.Pkgs);
            w.Write(payload.Crashes.Count);
            foreach (var e in payload.Crashes)
            {
                w.Write(e.Row); w.Write(e.ExcId); w.Write(e.PkgId);
                w.Write(e.Pid); w.Write(e.Tid); w.Write(e.Kind);
            }
            w.Flush();
        }

        output.Write(MemoryMarshal.AsBytes(payload.Offs.AsSpan()));
        output.Write(MemoryMarshal.AsBytes(payload.Lens.AsSpan()));
        output.Write(MemoryMarshal.AsBytes(payload.Moff.AsSpan()));
        output.Write(MemoryMarshal.AsBytes(payload.Minutes.AsSpan()));
        output.Write(MemoryMarshal.AsBytes(payload.Pids.AsSpan()));
        output.Write(MemoryMarshal.AsBytes(payload.Tids.AsSpan()));
        output.Write(MemoryMarshal.AsBytes(payload.Lvls.AsSpan()));
        output.Write(MemoryMarshal.AsBytes(payload.TagIds.AsSpan()));
    }

    static void WriteDict(BinaryWriter w, List<string> items)
    {
        w.Write(items.Count);
        foreach (var s in items)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            w.Write(bytes.Length);
            w.Write(bytes);
        }
    }

    /// <summary>
    /// 读出并校验一份 sidecar。任何不符（magic / 版本 / 段长 / 总长 / 身份摘要）返回 null，
    /// 原因经 <paramref name="reason"/> 交给调用方写启动日志；不抛异常给上层（静默降级）。
    /// </summary>
    internal static SidecarPayload? Read(Stream input, SidecarIdentity expected, out string reason)
    {
        reason = "";
        try
        {
            if (!BitConverter.IsLittleEndian)
            {
                reason = "平台非小端";
                return null;
            }

            using var r = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
            var magic = r.ReadBytes(Magic.Length);
            if (magic.Length != Magic.Length || !magic.AsSpan().SequenceEqual(Magic))
            {
                reason = "magic 不符";
                return null;
            }

            int version = r.ReadInt32();
            if (version != FormatVersion)
            {
                reason = $"格式版本不识别（{version}）";
                return null;
            }

            int rows = r.ReadInt32();
            if (rows < 0)
            {
                reason = "行数为负";
                return null;
            }
            for (int i = 0; i < SegmentCount; i++)
            {
                if (r.ReadByte() != Widths[i])
                {
                    reason = "段元素宽度不符";
                    return null;
                }
            }

            var segLengths = new long[SegmentCount];
            long dataLength = 0;
            for (int i = 0; i < SegmentCount; i++)
            {
                segLengths[i] = r.ReadInt64();
                // 每段长度必须恰好等于「行数 × 段宽」——段长被截断/篡改即整体判无效
                if (segLengths[i] != (long)rows * Widths[i])
                {
                    reason = $"段长度不符（第 {i} 段）";
                    return null;
                }
                dataLength += segLengths[i];
            }

            long total = r.ReadInt64();
            long size = r.ReadInt64();
            long mtime = r.ReadInt64();
            ulong headHash = r.ReadUInt64();
            ulong tailHash = r.ReadUInt64();

            var tags = ReadDict(r, out reason);
            if (tags == null) return null;
            var excTypes = ReadDict(r, out reason);
            if (excTypes == null) return null;
            var pkgs = ReadDict(r, out reason);
            if (pkgs == null) return null;

            int crashCount = r.ReadInt32();
            if (crashCount < 0 || crashCount > MaxTableCount || crashCount > rows)
            {
                reason = "崩溃事件数不符";
                return null;
            }
            var crashes = new List<CrashEvent>(crashCount);
            for (int i = 0; i < crashCount; i++)
            {
                crashes.Add(new CrashEvent
                {
                    Row = r.ReadInt32(), ExcId = r.ReadInt32(), PkgId = r.ReadInt32(),
                    Pid = r.ReadInt32(), Tid = r.ReadInt32(), Kind = r.ReadByte(),
                });
            }

            long headerLength = HeaderSize(tags, excTypes, pkgs, crashCount);
            if (total != headerLength + dataLength)
            {
                reason = "总长与头部声明不符";
                return null;
            }

            var identity = new SidecarIdentity(size, mtime, headHash, tailHash);
            if (!identity.Matches(expected))
            {
                reason = "源文件身份已变化";
                return null;
            }

            var payload = new SidecarPayload
            {
                Offs = new long[rows], Lens = new int[rows], Moff = new int[rows],
                Minutes = new byte[rows], Pids = new int[rows], Tids = new int[rows],
                Lvls = new byte[rows], TagIds = new int[rows],
                Tags = tags, ExcTypes = excTypes, Pkgs = pkgs, Crashes = crashes,
            };
            ReadInto(input, payload.Offs);
            ReadInto(input, payload.Lens);
            ReadInto(input, payload.Moff);
            ReadInto(input, payload.Minutes);
            ReadInto(input, payload.Pids);
            ReadInto(input, payload.Tids);
            ReadInto(input, payload.Lvls);
            ReadInto(input, payload.TagIds);
            return payload;
        }
        catch (Exception ex)
        {
            reason = "读取异常：" + ex.Message;
            return null;
        }
    }

    static List<string>? ReadDict(BinaryReader r, out string reason)
    {
        reason = "";
        int count = r.ReadInt32();
        if (count < 0 || count > MaxTableCount)
        {
            reason = "字典条目数不符";
            return null;
        }
        var list = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            int len = r.ReadInt32();
            if (len < 0 || len > MaxStringBytes)
            {
                reason = "字典条目长度不符";
                return null;
            }
            list.Add(Encoding.UTF8.GetString(r.ReadBytes(len)));
        }
        return list;
    }

    static void ReadInto<T>(Stream input, T[] target) where T : struct =>
        input.ReadExactly(MemoryMarshal.AsBytes(target.AsSpan()));
}
