using System.Text;
using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// sidecar 缓存（spec `sidecar-index-cache`）：落盘格式读写与校验、缓存键与身份摘要、
/// 原子替换、静态文件边界、容量治理与 LRU 淘汰、静默降级。
/// 断言口径为「行为可观察」：文件是否出现、条目内容是否仍是旧的、读取返回成功与否。
/// </summary>
public class SidecarCacheTests
{
    // ── 样本 ──

    /// <summary>threadtime 行样本：含崩溃块（保证三字典与崩溃事件表非空）与多种级别。</summary>
    internal static string Sample(int rows = 12)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < rows; i++)
        {
            sb.AppendLine(LogDocumentTests.Line($"09-23 18:00:{i:00}.000",
                i % 3 == 0 ? 'E' : 'I', i % 2 == 0 ? "TagA" : "TagB", $"message body {i}"));
            if (i == 4)
            {
                sb.AppendLine(LogDocumentTests.Line("09-23 18:00:04.100", 'E', "AndroidRuntime", "FATAL EXCEPTION: main"));
                sb.AppendLine(LogDocumentTests.Line("09-23 18:00:04.101", 'E', "AndroidRuntime", "Process: com.example.app, PID: 869"));
                sb.AppendLine(LogDocumentTests.Line("09-23 18:00:04.102", 'E', "AndroidRuntime", "java.lang.IllegalStateException: boom"));
            }
        }
        return sb.ToString();
    }

    /// <summary>同形态文件（相同字典与行数）——条目体积可预期，便于容量断言。</summary>
    static string Uniform(string tag) => Sample(10).Replace("message body", tag + " body");

    static SidecarPayload PayloadOf(string path)
    {
        using var doc = LogDocument.Build(path);
        return doc.SnapshotIndex();
    }

    static byte[] Serialize(SidecarIdentity identity, SidecarPayload payload,
        int version = SidecarFile.FormatVersion)
    {
        using var ms = new MemoryStream();
        SidecarFile.Write(ms, identity, payload, version);
        return ms.ToArray();
    }

    static SidecarPayload? Deserialize(byte[] bytes, SidecarIdentity identity, out string reason)
    {
        using var ms = new MemoryStream(bytes);
        return SidecarFile.Read(ms, identity, out reason);
    }

    // ── 1.2 读写器 ──

    /// <summary>写→读往返：8 段逐元素一致，三字典与崩溃事件表一致。</summary>
    [Fact]
    public void WriteRead_RoundTrip_PreservesAllColumns()
    {
        using var tmp = new TempLogFile(Sample());
        var payload = PayloadOf(tmp.Path);
        var identity = SidecarIdentity.Capture(tmp.Path);
        Assert.True(payload.RowCount > 0);
        Assert.NotEmpty(payload.Crashes);

        var bytes = Serialize(identity, payload);
        var read = Deserialize(bytes, identity, out var reason);

        Assert.NotNull(read);
        Assert.Equal("", reason);
        Assert.Equal(payload.RowCount, read!.RowCount);
        Assert.Equal(payload.Offs, read.Offs);
        Assert.Equal(payload.Lens, read.Lens);
        Assert.Equal(payload.Moff, read.Moff);
        Assert.Equal(payload.Minutes, read.Minutes);
        Assert.Equal(payload.Pids, read.Pids);
        Assert.Equal(payload.Tids, read.Tids);
        Assert.Equal(payload.Lvls, read.Lvls);
        Assert.Equal(payload.TagIds, read.TagIds);
        Assert.Equal(payload.Tags, read.Tags);
        Assert.Equal(payload.ExcTypes, read.ExcTypes);
        Assert.Equal(payload.Pkgs, read.Pkgs);
        Assert.Equal(payload.Crashes, read.Crashes);
    }

    /// <summary>magic 被改写或格式版本更高：整份判无效（不猜、不部分修复）。</summary>
    [Fact]
    public void Header_MagicOrVersionMismatch_Rejected()
    {
        using var tmp = new TempLogFile(Sample());
        var payload = PayloadOf(tmp.Path);
        var identity = SidecarIdentity.Capture(tmp.Path);

        var corrupted = Serialize(identity, payload);
        corrupted[0] = (byte)'X';
        Assert.Null(Deserialize(corrupted, identity, out var magicReason));
        Assert.Contains("magic", magicReason);

        var future = Serialize(identity, payload, version: SidecarFile.FormatVersion + 1);
        Assert.Null(Deserialize(future, identity, out var versionReason));
        Assert.Contains("版本", versionReason);
    }

    /// <summary>段长度与「行数 × 段宽」不符（截断/篡改）：整份判无效。</summary>
    [Fact]
    public void Segment_LengthMismatch_Rejected()
    {
        using var tmp = new TempLogFile(Sample());
        var payload = PayloadOf(tmp.Path);
        var identity = SidecarIdentity.Capture(tmp.Path);

        var bytes = Serialize(identity, payload);
        long declared = BitConverter.ToInt64(bytes, SidecarFile.SegmentLengthsOffset);
        BitConverter.GetBytes(declared + 1).CopyTo(bytes, SidecarFile.SegmentLengthsOffset);

        Assert.Null(Deserialize(bytes, identity, out var reason));
        Assert.Contains("段长度", reason);
    }

    // ── 2.1 缓存键与身份 ──

    /// <summary>条目名不得出现源路径明文（目录结构 / 用户名 / 分隔符）——公开仓库红线。</summary>
    [Fact]
    public void Key_DoesNotContainSourcePathText()
    {
        using var tmp = new TempLogFile(Sample());

        var name = SidecarKey.DeriveName(tmp.Path);

        Assert.EndsWith(SidecarKey.Extension, name);
        Assert.DoesNotContain(TempRoot.Dir, name);
        Assert.DoesNotContain("\\", name);
        Assert.DoesNotContain("/", name);
        Assert.DoesNotContain(":", name);
    }

    /// <summary>文件被追加：size 与尾指纹变化即判失效。</summary>
    [Fact]
    public void Identity_AppendDetected()
    {
        using var tmp = new TempLogFile(Sample(3));
        var before = SidecarIdentity.Capture(tmp.Path);

        tmp.Append(LogDocumentTests.Line("09-23 18:00:09.000", 'I', "TagA", "appended"));

        var after = SidecarIdentity.Capture(tmp.Path);
        Assert.False(before.Matches(after));
    }

    /// <summary>size 与 mtime 都不变、首部字节被就地改写：内容指纹仍能发现。</summary>
    [Fact]
    public void Identity_HeadOrTailRewriteDetected()
    {
        using var tmp = new TempLogFile(Sample(3));
        var before = SidecarIdentity.Capture(tmp.Path);

        var original = File.ReadAllText(tmp.Path);
        // 等长改写（只换首行内容），不改文件长度
        var rewritten = original.Replace("message body 0", "message body X");
        Assert.Equal(original.Length, rewritten.Length);
        tmp.WriteInPlace(rewritten);
        File.SetLastWriteTimeUtc(tmp.Path, new DateTime(before.MtimeTicks, DateTimeKind.Utc));

        var after = SidecarIdentity.Capture(tmp.Path);
        Assert.Equal(before.Size, after.Size);
        Assert.Equal(before.MtimeTicks, after.MtimeTicks);
        Assert.False(before.Matches(after));
    }

    /// <summary>同内容副本按路径各自独立建条目（不做内容寻址去重）。</summary>
    [Fact]
    public void Identity_CopyAtPath_IndependentEntry()
    {
        using var tmp = new TempLogFile(Sample(3));
        using var dir = new TempDir();
        var copyPath = System.IO.Path.Combine(dir.Path, "copy.log");
        File.Copy(tmp.Path, copyPath);

        var sourceName = SidecarKey.DeriveName(tmp.Path);
        var copyName = SidecarKey.DeriveName(copyPath);
        Assert.NotEqual(sourceName, copyName);

        using var store = new TempStore();
        var identity = SidecarIdentity.Capture(tmp.Path);
        Assert.True(store.Store.Save(tmp.Path, identity, identity, PayloadOf(tmp.Path)));

        var copyIdentity = SidecarIdentity.Capture(copyPath);
        Assert.True(store.Store.Save(copyPath, copyIdentity, copyIdentity, PayloadOf(copyPath)));

        Assert.Equal(2, store.EntryFiles().Length);
        Assert.True(store.Store.TryLoad(tmp.Path, out _));
        Assert.True(store.Store.TryLoad(copyPath, out _));
    }

    // ── 4.1 原子落盘 ──

    /// <summary>写正式名之前先写临时文件：目录里不残留半截文件，正式条目可读。</summary>
    [Fact]
    public void Write_TempThenAtomicRename_NoPartialFileVisible()
    {
        using var tmp = new TempLogFile(Sample());
        using var store = new TempStore();
        var identity = SidecarIdentity.Capture(tmp.Path);

        Assert.True(store.Store.Save(tmp.Path, identity, identity, PayloadOf(tmp.Path)));

        var files = store.AllFiles();
        Assert.Single(files);
        Assert.EndsWith(SidecarKey.Extension, files[0]);
        Assert.True(store.Store.TryLoad(tmp.Path, out var payload));
        Assert.NotNull(payload);
    }

    /// <summary>写失败静默：返回 false、不抛异常，且既有缓存不被半截写入污染。</summary>
    [Fact]
    public void Write_Failure_SilentAndOriginalCacheIntact()
    {
        using var tmp = new TempLogFile(Sample(3));
        using var store = new TempStore();
        var identity = SidecarIdentity.Capture(tmp.Path);
        Assert.True(store.Store.Save(tmp.Path, identity, identity, PayloadOf(tmp.Path)));
        int rowsBefore = PayloadOf(tmp.Path).RowCount;

        // 独占打开正式条目：替换必然失败（模拟杀软占用 / 磁盘错误）
        var entry = store.Store.EntryPath(tmp.Path);
        using (new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var bigger = PayloadOf(tmp.Path);
            Assert.False(store.Store.Save(tmp.Path, identity, identity, bigger));
        }

        Assert.True(store.Store.TryLoad(tmp.Path, out var payload));
        Assert.Equal(rowsBefore, payload!.RowCount);
    }

    // ── 4.2 静态文件边界 ──

    /// <summary>解析前后身份不一致（文件在构建期间被写入）：不落盘。</summary>
    [Fact]
    public void StaticBoundary_FileChangedDuringBuild_NoCacheWritten()
    {
        using var tmp = new TempLogFile(Sample(3));
        using var store = new TempStore();
        var before = SidecarIdentity.Capture(tmp.Path);
        var payload = PayloadOf(tmp.Path);          // 构建期间文件被追加
        tmp.Append(LogDocumentTests.Line("09-23 18:00:09.000", 'I', "TagA", "late"));

        var after = SidecarIdentity.Capture(tmp.Path);
        Assert.False(store.Store.Save(tmp.Path, before, after, payload));
        Assert.Empty(store.AllFiles());
    }

    // ── 4.3 容量治理 ──

    /// <summary>单份超上限：不落盘，且不因它触发对既有条目的淘汰。</summary>
    [Fact]
    public void Capacity_OversizedIndex_NotWritten_NoEvictionTriggered()
    {
        using var small = new TempLogFile(Sample(2));
        using var big = new TempLogFile(Sample(200));
        using var store = new TempStore(maxEntryBytes: 1000, dirLimitBytes: 1 << 20);

        var smallId = SidecarIdentity.Capture(small.Path);
        Assert.True(store.Store.Save(small.Path, smallId, smallId, PayloadOf(small.Path)));

        var bigId = SidecarIdentity.Capture(big.Path);
        var bigPayload = PayloadOf(big.Path);
        Assert.True(bigPayload.RowCount * SidecarCacheStore.EstimatedBytesPerRow > 1000);
        Assert.False(store.Store.Save(big.Path, bigId, bigId, bigPayload));

        Assert.Single(store.EntryFiles());                       // 既有条目仍在
        Assert.True(File.Exists(store.Store.EntryPath(small.Path)));
        Assert.False(File.Exists(store.Store.EntryPath(big.Path)));
    }

    /// <summary>目录超限：按最近使用时间从旧到新淘汰，直到容纳新条目。</summary>
    [Fact]
    public void Capacity_DirectoryOverLimit_EvictOldestFirst()
    {
        using var a = new TempLogFile(Uniform("a"));
        using var b = new TempLogFile(Uniform("b"));
        using var c = new TempLogFile(Uniform("c"));
        using var dir = new TempDir();
        // 先按标准上限落两条，量出条目体积，再按「装得下 2 条、装不下 3 条」收紧触发线
        long entrySize;
        {
            var probe = new SidecarCacheStore(dir.Path);
            var id = SidecarIdentity.Capture(a.Path);
            Assert.True(probe.Save(a.Path, id, id, PayloadOf(a.Path)));
            entrySize = new FileInfo(probe.EntryPath(a.Path)).Length;
        }
        File.Delete(System.IO.Path.Combine(dir.Path, SidecarKey.DeriveName(a.Path)));

        var store = new SidecarCacheStore(dir.Path,
            maxEntryBytes: SidecarCacheStore.MaxEntryBytes,
            dirLimitBytes: entrySize * 2 + entrySize / 2);

        var idA = SidecarIdentity.Capture(a.Path);
        Assert.True(store.Save(a.Path, idA, idA, PayloadOf(a.Path)));
        var idB = SidecarIdentity.Capture(b.Path);
        Assert.True(store.Save(b.Path, idB, idB, PayloadOf(b.Path)));

        var old = DateTime.UtcNow.AddHours(-2);
        MarkUsed(store.EntryPath(a.Path), old);
        MarkUsed(store.EntryPath(b.Path), old.AddHours(1));

        var idC = SidecarIdentity.Capture(c.Path);
        Assert.True(store.Save(c.Path, idC, idC, PayloadOf(c.Path)));

        Assert.False(File.Exists(store.EntryPath(a.Path)));        // 最旧的被淘汰
        Assert.True(File.Exists(store.EntryPath(b.Path)));
        Assert.True(File.Exists(store.EntryPath(c.Path)));         // 新条目必留
    }

    /// <summary>当前会话使用中的条目跳过淘汰，改淘汰次旧条目。</summary>
    [Fact]
    public void Capacity_InUseEntry_NotEvicted()
    {
        using var a = new TempLogFile(Uniform("a"));
        using var b = new TempLogFile(Uniform("b"));
        using var c = new TempLogFile(Uniform("c"));
        using var dir = new TempDir();
        long entrySize;
        {
            var probe = new SidecarCacheStore(dir.Path);
            var id = SidecarIdentity.Capture(a.Path);
            Assert.True(probe.Save(a.Path, id, id, PayloadOf(a.Path)));
            entrySize = new FileInfo(probe.EntryPath(a.Path)).Length;
        }
        File.Delete(System.IO.Path.Combine(dir.Path, SidecarKey.DeriveName(a.Path)));

        var store = new SidecarCacheStore(dir.Path,
            maxEntryBytes: SidecarCacheStore.MaxEntryBytes,
            dirLimitBytes: entrySize * 2 + entrySize / 2);

        var idA = SidecarIdentity.Capture(a.Path);
        Assert.True(store.Save(a.Path, idA, idA, PayloadOf(a.Path)));
        var idB = SidecarIdentity.Capture(b.Path);
        Assert.True(store.Save(b.Path, idB, idB, PayloadOf(b.Path)));

        var old = DateTime.UtcNow.AddHours(-2);
        MarkUsed(store.EntryPath(a.Path), old);
        MarkUsed(store.EntryPath(b.Path), old.AddHours(1));
        store.MarkInUse(a.Path);                                  // a 正在使用

        var idC = SidecarIdentity.Capture(c.Path);
        Assert.True(store.Save(c.Path, idC, idC, PayloadOf(c.Path)));

        Assert.True(File.Exists(store.EntryPath(a.Path)));         // 使用中：不淘汰
        Assert.False(File.Exists(store.EntryPath(b.Path)));        // 次旧条目被淘汰
        Assert.True(File.Exists(store.EntryPath(c.Path)));
    }

    static void MarkUsed(string entryPath, DateTime utc)
    {
        File.SetLastAccessTimeUtc(entryPath, utc);
        File.SetLastWriteTimeUtc(entryPath, utc);
    }

    // ── 5.2 静默降级 ──

    /// <summary>缓存被截断/损坏：不命中，但文件打开（全量解析）照常成功。</summary>
    [Fact]
    public void Degrade_CorruptCache_FallsBackToFullLoad()
    {
        using var tmp = new TempLogFile(Sample());
        using var store = new TempStore();
        var identity = SidecarIdentity.Capture(tmp.Path);
        Assert.True(store.Store.Save(tmp.Path, identity, identity, PayloadOf(tmp.Path)));

        File.WriteAllBytes(store.Store.EntryPath(tmp.Path), Encoding.UTF8.GetBytes("not a sidecar file"));

        Assert.False(store.Store.TryLoad(tmp.Path, out var payload));
        Assert.Null(payload);

        using var doc = LogDocument.Build(tmp.Path);              // 既有全量路径不受影响
        Assert.True(doc.RowCount > 0);
    }

    /// <summary>更高格式版本：忽略旧缓存并重建（不做跨版本迁移）。</summary>
    [Fact]
    public void Degrade_HigherVersion_RebuildsCache()
    {
        using var tmp = new TempLogFile(Sample());
        using var store = new TempStore();
        var identity = SidecarIdentity.Capture(tmp.Path);
        var payload = PayloadOf(tmp.Path);
        Directory.CreateDirectory(store.Dir);
        File.WriteAllBytes(store.Store.EntryPath(tmp.Path),
            Serialize(identity, payload, version: SidecarFile.FormatVersion + 1));

        Assert.False(store.Store.TryLoad(tmp.Path, out _));

        Assert.True(store.Store.Save(tmp.Path, identity, identity, payload));
        Assert.True(store.Store.TryLoad(tmp.Path, out var rebuilt));
        Assert.Equal(payload.RowCount, rebuilt!.RowCount);
    }

    /// <summary>缓存目录不可写（此处以一个同名文件占位，创建目录必然失败）：静默停用，加载照常。</summary>
    [Fact]
    public void Degrade_DirectoryNotWritable_LoadSucceedsSilently()
    {
        using var tmp = new TempLogFile(Sample());
        using var blocker = new TempLogFile("occupied");           // 同名文件占住「目录」路径
        var store = new SidecarCacheStore(blocker.Path);
        var identity = SidecarIdentity.Capture(tmp.Path);

        Assert.False(store.TryLoad(tmp.Path, out _));
        Assert.False(store.Save(tmp.Path, identity, identity, PayloadOf(tmp.Path)));

        using var doc = LogDocument.Build(tmp.Path);
        Assert.True(doc.RowCount > 0);
    }

    // ── 测试脚手架 ──

    /// <summary>一次性缓存目录 + 可调上限的 store。</summary>
    sealed class TempStore : IDisposable
    {
        readonly TempDir _dir = new();

        internal TempStore(long maxEntryBytes = SidecarCacheStore.MaxEntryBytes,
            long dirLimitBytes = SidecarCacheStore.DirectoryLimitBytes)
        {
            Store = new SidecarCacheStore(_dir.Path, maxEntryBytes, dirLimitBytes);
        }

        internal SidecarCacheStore Store { get; }
        internal string Dir => _dir.Path;

        internal string[] AllFiles() => Directory.Exists(Dir)
            ? Directory.GetFiles(Dir)
            : Array.Empty<string>();

        internal string[] EntryFiles() => Directory.Exists(Dir)
            ? Directory.GetFiles(Dir, "*" + SidecarKey.Extension)
            : Array.Empty<string>();

        public void Dispose() => _dir.Dispose();
    }
}
