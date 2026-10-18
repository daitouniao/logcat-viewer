using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using logcat.Services;
using Xunit;

// 有意同步阻塞（GetAwaiter().GetResult()）：仓库的 tstrun 反射 runner 不 await 返回
// Task 的测试方法——写成 async Task 会被静默跳过、断言根本不执行（假绿）。
// xUnit1031 的「防死锁」建议在这里不适用：测试进程无 SynchronizationContext，
// 且被测的是命名管道 IO，阻塞点不会回砌 UI 线程。
#pragma warning disable xUnit1031
namespace logcat.Tests;

/// <summary>
/// 文件代理的回环测试：测试进程内起模拟代理（<see cref="FakeProxy"/>，真实命名管道），
/// 客户端走完整协议。覆盖 open/read/close/spool 落盘逐字节相等、EOF、坏句柄、
/// 分块完整性、双连接隔离、长度截断、off≥2^31 大偏移、proto 不匹配、坏请求不断线。
///
/// 注意：测试方法一律<strong>同步</strong>（内部 GetAwaiter().GetResult() 阻塞）——
/// tstrun 反射 runner 不 await 返回 Task 的测试方法，写成 async Task 会被静默跳过。
/// </summary>
// 追逐态文案断言锁中文（SetLangGuard），须与其它改 Loc 全局语言的类串行
[Collection(LangCollection.Name)]
public class FileProxyLoopbackTests
{
    static readonly int Chunk = 8 * 1024 * 1024;   // 与 spool 约定一致

    // ── 小工具 ──

    sealed class TempBinary : IDisposable
    {
        public string Path { get; }

        public TempBinary(byte[] content)
        {
            Directory.CreateDirectory(TempRoot.Dir);
            Path = System.IO.Path.Combine(TempRoot.Dir, Guid.NewGuid().ToString("N") + ".bin");
            File.WriteAllBytes(Path, content);
        }

        /// <summary>稀疏大文件：逻辑大小 size，仅在 off 处写 pattern（NTFS 空洞不占盘）。</summary>
        public static TempBinary Sparse(long size, long off, byte[] pattern)
        {
            Directory.CreateDirectory(TempRoot.Dir);
            var p = System.IO.Path.Combine(TempRoot.Dir, Guid.NewGuid().ToString("N") + ".bin");
            using (var fs = new FileStream(p, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.SetLength(size);
                fs.Position = off;
                fs.Write(pattern, 0, pattern.Length);
            }
            return new TempBinary(p);
        }

        TempBinary(string path) => Path = path;

        public byte[] ReadAll() => File.ReadAllBytes(Path);

        public void Dispose()
        {
            try { File.Delete(Path); } catch { }
        }
    }

    static byte[] Pattern(int n, int seed = 7)
    {
        var b = new byte[n];
        var rnd = new Random(seed);
        rnd.NextBytes(b);
        return b;
    }

    static FileProxyClient Connect(FakeProxy proxy, int timeoutMs = 5000)
        => FileProxyClient.ConnectAsync(proxy.PipeName, timeoutMs).GetAwaiter().GetResult();

    static T Trap<T>(Action act, out Exception? ex) where T : Exception
    {
        try { act(); ex = null; return null!; }
        catch (Exception e) { ex = e; return (T)e; }
    }

    // ── 用例 ──

    [Fact]
    public void 全流程_hello_open_read_close_请求线格式()
    {
        var content = Pattern(1000);
        using var file = new TempBinary(content);
        using var proxy = FakeProxy.StartAsync().GetAwaiter().GetResult();
        using var client = Connect(proxy);

        Assert.Equal("test-fileproxy/1.0", client.ProxyName);

        var (h, size) = client.OpenAsync(file.Path).GetAwaiter().GetResult();
        Assert.Equal(1000, size);
        Assert.Equal(1, proxy.OpenCount);

        var part1 = client.ReadAsync(h, 0, 400).GetAwaiter().GetResult();
        Assert.Equal(content.AsSpan(0, 400).ToArray(), part1);
        var part2 = client.ReadAsync(h, 400, 600).GetAwaiter().GetResult();
        Assert.Equal(content.AsSpan(400).ToArray(), part2);

        // EOF：读到末尾后 n=0、无负载
        var eof = client.ReadAsync(h, 1000, 400).GetAwaiter().GetResult();
        Assert.Empty(eof);

        client.CloseAsync(h).GetAwaiter().GetResult();
        Assert.Equal(new long[] { 1 }, proxy.ClosedHandles);

        // 线格式：首行 hello 无多余字段；open/read/close 依次出现且 path 原样（JSON 字符串）
        var lines = proxy.RequestLines;
        Assert.Equal("{\"id\":1,\"op\":\"hello\"}", lines[0]);
        Assert.Contains("\"op\":\"open\"", lines[1]);
        Assert.Contains(file.Path.Replace("\\", "\\\\"), lines[1]);   // JSON 串里反斜杠是转义形态
        Assert.Contains("\"op\":\"read\"", lines[2]);
        Assert.Contains("\"op\":\"close\"", lines[^1]);
        Assert.Contains("\"h\":1", lines[^1]);
    }

    [Fact]
    public void spool落盘_逐字节相等()
    {
        // 测试侧复刻 spool 循环：8MB 块顺序读、写到落盘文件、n=0 收尾
        var content = Pattern(Chunk + 12345);
        using var file = new TempBinary(content);
        using var proxy = FakeProxy.StartAsync().GetAwaiter().GetResult();
        using var client = Connect(proxy);

        var (h, size) = client.OpenAsync(file.Path).GetAwaiter().GetResult();
        Assert.Equal(content.Length, size);

        string spool = System.IO.Path.Combine(TempRoot.Dir, Guid.NewGuid().ToString("N") + ".spool");
        try
        {
            long received = 0;
            using (var outFs = new FileStream(spool, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                while (true)
                {
                    var chunk = client.ReadAsync(h, received, Chunk).GetAwaiter().GetResult();
                    if (chunk.Length == 0) break;
                    outFs.Write(chunk, 0, chunk.Length);
                    received += chunk.Length;
                }
            }
            Assert.Equal(content.Length, received);            // 完整性：实收 == open.size
            Assert.Equal(content, File.ReadAllBytes(spool));   // 逐字节一致
        }
        finally { try { File.Delete(spool); } catch { } }
    }

    [Fact]
    public void 坏句柄返回代理错误()
    {
        using var file = new TempBinary(Pattern(64));
        using var proxy = FakeProxy.StartAsync().GetAwaiter().GetResult();
        using var client = Connect(proxy);

        var ex = Trap<FileProxyException>(
            () => client.ReadAsync(999, 0, 16).GetAwaiter().GetResult(), out _);
        Assert.Equal(FileProxyErrorCode.ProxyError, ex.Code);
        Assert.Contains("bad handle", ex.Message);
    }

    [Fact]
    public void 分块完整性_10MB()
    {
        // 10MB+：跨 8MB 块界，第二块是「半个多」块，验证拼接不错位
        var content = Pattern(10 * 1024 * 1024 + 777);
        using var file = new TempBinary(content);
        using var proxy = FakeProxy.StartAsync().GetAwaiter().GetResult();
        using var client = Connect(proxy);

        var (h, _) = client.OpenAsync(file.Path).GetAwaiter().GetResult();
        var ms = new MemoryStream();
        long off = 0;
        while (true)
        {
            var chunk = client.ReadAsync(h, off, Chunk).GetAwaiter().GetResult();
            if (chunk.Length == 0) break;
            ms.Write(chunk, 0, chunk.Length);
            off += chunk.Length;
        }
        Assert.Equal(content, ms.ToArray());
        Assert.True(proxy.ReadCount >= 2);
    }

    [Fact]
    public void 双连接隔离_句柄按连接生效()
    {
        var content = Pattern(5000);
        using var file = new TempBinary(content);
        using var proxy = FakeProxy.StartAsync().GetAwaiter().GetResult();
        using var c1 = Connect(proxy);
        using var c2 = Connect(proxy);

        var (h1, _) = c1.OpenAsync(file.Path).GetAwaiter().GetResult();
        var (h2a, _) = c2.OpenAsync(file.Path).GetAwaiter().GetResult();
        var (h2b, _) = c2.OpenAsync(file.Path).GetAwaiter().GetResult();
        Assert.Equal(2, proxy.ConnectionCount);
        // 句柄编号按连接各自从 1 起：c2 的第二个句柄（2）在 c1 上不存在
        Assert.Equal(1, h1);
        Assert.Equal(1, h2a);
        Assert.Equal(2, h2b);

        // 交错读：两条连接互不串台
        var a = c1.ReadAsync(h1, 1000, 500).GetAwaiter().GetResult();
        var b = c2.ReadAsync(h2a, 2000, 500).GetAwaiter().GetResult();
        var a2 = c1.ReadAsync(h1, 1000, 500).GetAwaiter().GetResult();
        Assert.Equal(content.AsSpan(1000, 500).ToArray(), a);
        Assert.Equal(content.AsSpan(2000, 500).ToArray(), b);
        Assert.Equal(a, a2);

        // c1 使用 c2  connection 内的句柄 2：坏句柄（证明句柄表按连接隔离）
        var ex = Trap<FileProxyException>(
            () => c1.ReadAsync(h2b, 0, 16).GetAwaiter().GetResult(), out _);
        Assert.Equal(FileProxyErrorCode.ProxyError, ex.Code);
    }

    [Fact]
    public void 读取长度超限时按16MB截断()
    {
        using var file = new TempBinary(Pattern(1024));
        using var proxy = FakeProxy.StartAsync().GetAwaiter().GetResult();
        using var client = Connect(proxy);

        var (h, _) = client.OpenAsync(file.Path).GetAwaiter().GetResult();
        var all = client.ReadAsync(h, 0, 32 * 1024 * 1024).GetAwaiter().GetResult();
        Assert.Equal(1024, all.Length);
        Assert.Equal(16 * 1024 * 1024, proxy.LastReadLen);   // 代理侧收到的 len 已被截断
    }

    [Fact]
    public void 大偏移读取_off超过2GB位置正确()
    {
        // 稀疏文件：逻辑 2GB+，数据只写在 off=2^31 处（NTFS 空洞不占盘）。
        // 守护 JS 例子里 `|0` 把 off 截成 32 位有符号的坑——C# 侧 long 透传，这里端到端验位置
        const long BigOff = (long)int.MaxValue + 1;   // 2^31
        var pattern = Pattern(8192, seed: 99);
        using var file = TempBinary.Sparse(BigOff + 8192, BigOff, pattern);
        using var proxy = FakeProxy.StartAsync().GetAwaiter().GetResult();
        using var client = Connect(proxy);

        var (h, size) = client.OpenAsync(file.Path).GetAwaiter().GetResult();
        Assert.Equal(BigOff + 8192, size);

        var got = client.ReadAsync(h, BigOff, 8192).GetAwaiter().GetResult();
        Assert.Equal(pattern, got);
        Assert.Equal(BigOff, proxy.LastReadOffset);

        // 边界前一个字节仍归「空洞」：n=0（读请求越过末尾）与越界前的位置都正确
        var before = client.ReadAsync(h, BigOff - 8, 8).GetAwaiter().GetResult();
        Assert.Equal(new byte[8], before);
    }

    [Fact]
    public void 协议版本不匹配判连接失败并附代理自描述()
    {
        using var bad = new FakeProxy { Proto = 2, ProxyName = "other-proxy/9.9" };
        bad.WaitListeningAsync().GetAwaiter().GetResult();
        var ex = Trap<FileProxyException>(() => Connect(bad), out _);
        Assert.Equal(FileProxyErrorCode.ProtoMismatch, ex.Code);
        Assert.Contains("other-proxy/9.9", ex.Message);   // 附代理自描述
    }

    [Fact]
    public void 代理未监听时判未连接()
    {
        var ex = Trap<FileProxyException>(
            () => FileProxyClient.ConnectAsync("logcat-test-nobody-" + Guid.NewGuid().ToString("N"), 500).GetAwaiter().GetResult(),
            out _);
        Assert.Equal(FileProxyErrorCode.NoProxyNotRunning, ex.Code);
    }

    [Fact]
    public void 坏JSON与未知op不断线_连接可继续完成open与read()
    {
        using var file = new TempBinary(Pattern(64));
        using var proxy = FakeProxy.StartAsync().GetAwaiter().GetResult();

        using var raw = new NamedPipeClientStream(".", proxy.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        raw.Connect(3000);
        var session = new RawSession(raw);

        var hello = session.Request("{\"id\":1,\"op\":\"hello\"}");
        Assert.True(hello.Ok);
        Assert.Equal(1, hello.Proto);

        // 坏 JSON：回错误、不断线
        var badJson = session.Request("not json at all");
        Assert.False(badJson.Ok);

        // 未知 op：回错误、不断线
        var unknown = session.Request("{\"id\":5,\"op\":\"frobnicate\"}");
        Assert.False(unknown.Ok);
        Assert.Contains("unknown op", unknown.Err);

        // 坏句柄：回错误、不断线
        var badHandle = session.Request("{\"id\":6,\"op\":\"read\",\"h\":999,\"off\":0,\"len\":8}");
        Assert.False(badHandle.Ok);

        // 连接仍可用：open + 带负载的 read 照常完成
        var open = session.Request($"{{\"id\":7,\"op\":\"open\",\"path\":{JsonSerializer.Serialize(file.Path)}}}");
        Assert.True(open.Ok);
        Assert.NotNull(open.H);
        var read = session.Request($"{{\"id\":8,\"op\":\"read\",\"h\":{open.H},\"off\":0,\"len\":16}}");
        Assert.True(read.Ok);
        Assert.Equal(16, read.N);
        Assert.Equal(file.ReadAll().AsSpan(0, 16).ToArray(), read.Payload);

        // 该 raw 会话发的坏请求都留了痕，供排障
        Assert.Contains("not json at all", string.Join("\n", proxy.RequestLines));
    }

    // ── spool 全流程（FileProxyService，任务 4.2/4.3）──

    /// <summary>同步进度收集器：SpoolCoreAsync 的报告在 await 延续上同步发生，返回后必然收全。</summary>
    sealed class SyncProgress : IProgress<(double Pct, string Msg)>
    {
        readonly object _lock = new();
        readonly List<(double Pct, string Msg)> _items = new();
        readonly CancellationTokenSource? _cancelAfter;
        readonly int _cancelAfterCount;

        public SyncProgress(CancellationTokenSource? cancelAfter = null, int cancelAfterCount = 0)
        { _cancelAfter = cancelAfter; _cancelAfterCount = cancelAfterCount; }

        public void Report((double Pct, string Msg) value)
        {
            lock (_lock)
            {
                _items.Add(value);
                if (_cancelAfter != null && _items.Count >= _cancelAfterCount)
                    _cancelAfter.Cancel();
            }
        }

        public List<(double Pct, string Msg)> Items { get { lock (_lock) return new(_items); } }
    }

    [Fact]
    public void SpoolAsync_分块落盘逐字节相等_进度序列()
    {
        var content = Pattern(Chunk + 12345);
        using var file = new TempBinary(content);
        using var proxy = FakeProxy.StartAsync().GetAwaiter().GetResult();
        using var client = Connect(proxy);

        var sessionDir = System.IO.Path.Combine(TempRoot.Dir, "spool-" + Guid.NewGuid().ToString("N"));
        var prog = new SyncProgress();
        var result = FileProxyService.SpoolAsync(client, file.Path, sessionDir, prog).GetAwaiter().GetResult();

        // 完整性：实收 == open.size，落盘逐字节相等；成功路径目录与文件保留
        Assert.Equal(content, File.ReadAllBytes(result.SpoolFilePath));
        Assert.Equal(content.Length, result.BytesWritten);
        Assert.True(File.Exists(result.SpoolFilePath));
        Assert.StartsWith(sessionDir, result.SpoolFilePath, StringComparison.Ordinal);

        // 进度序列：8MB 跨两块 → 至少两次报告，末次 50%（实收 == 期望），全程单调不回退
        var items = prog.Items;
        Assert.True(items.Count >= 2, "进度报告次数不足");
        Assert.Equal(0.5, items[^1].Pct, 10);
        for (int i = 1; i < items.Count; i++)
            Assert.True(items[i].Pct >= items[i - 1].Pct, $"进度回退于第 {i} 块");
    }

    [Fact]
    public void SpoolAsync_短读_删目录报ShortRead含实收期望()
    {
        var content = Pattern(Chunk * 2);   // 16MB，跨两块
        using var file = new TempBinary(content);
        // FakeProxy 每次 read 精确读满（不做短读模拟）：第一块 8MB 送完即暴力断线，
        // 第二块 read 时流已结束 → 客户端实收 8MB != open.size 16MB → 短读
        using var proxy = new FakeProxy { AbortAfterBytes = Chunk };
        proxy.WaitListeningAsync().GetAwaiter().GetResult();
        using var client = Connect(proxy);

        var sessionDir = System.IO.Path.Combine(TempRoot.Dir, "spool-" + Guid.NewGuid().ToString("N"));
        var ex = Trap<FileProxyException>(
            () => FileProxyService.SpoolAsync(client, file.Path, sessionDir).GetAwaiter().GetResult(), out _);

        Assert.Equal(FileProxyErrorCode.ShortRead, ex.Code);
        Assert.True(ex.Received < ex.Expected, "短读应带实收/期望且实收小于期望");
        Assert.Equal(content.Length, ex.Expected);
        Assert.True(ex.Received > 0);
        Assert.False(Directory.Exists(sessionDir));   // 不符即删，无残留
    }

    [Fact]
    public void SpoolAsync_代理干净收尾但实收不足_仍报ShortRead删目录()
    {
        // 短读的另一半路径：不走异常（断线），而是代理优雅收尾（n=0 EOF）但实收 < open.size。
        // 完整性判据是「实收 == open.size」，干净关闭不豁免（「不符即删」同一口径）
        var content = Pattern(1024);
        using var file = new TempBinary(content);
        using var proxy = new FakeProxy { ServeMaxBytes = 512 };
        proxy.WaitListeningAsync().GetAwaiter().GetResult();
        using var client = Connect(proxy);

        var sessionDir = System.IO.Path.Combine(TempRoot.Dir, "spool-" + Guid.NewGuid().ToString("N"));
        var ex = Trap<FileProxyException>(
            () => FileProxyService.SpoolAsync(client, file.Path, sessionDir).GetAwaiter().GetResult(), out _);

        Assert.Equal(FileProxyErrorCode.ShortRead, ex.Code);
        Assert.Equal(512, ex.Received);
        Assert.Equal(content.Length, ex.Expected);
        Assert.False(Directory.Exists(sessionDir));   // 干净收尾的短读同样删目录，无残留
    }

    [Fact]
    public void 读响应负载超上限或为负_收负载前即报越界()
    {
        // FakeProxy 只回异常的 n 头、不发负载：越界拒绝必须发生在收取负载之前。
        // 异常消息走 Loc.F 本地化（zh「越界」/ en "out-of-range"），而 CI runner 是
        // en-US 区域——2026-10-18 CI 实挂在硬编码中文断言上，详见
        // docs/bugfixes/CI-LOCALE-ASSERT-FILEPROXY.md。
        // 修法：两种界面语言各「生成 + 断言」一遍。生成必须发生在语言作用域内——
        // 消息是抛出那一刻按当时语言算出来的，事后切语言拿到的是旧语言的串；
        // 且只测中文的话，被测代码退回硬编码中文仍然全绿，抓不住回归。
        void Reject(long fakeN, string keyword)
        {
            using var file = new TempBinary(Pattern(64));
            using var proxy = new FakeProxy { FakeReadN = fakeN };
            proxy.WaitListeningAsync().GetAwaiter().GetResult();
            using var client = Connect(proxy);
            var (h, _) = client.OpenAsync(file.Path).GetAwaiter().GetResult();

            var ex = Trap<FileProxyException>(() => client.ReadAsync(h, 0, 16).GetAwaiter().GetResult(), out _);
            Assert.Equal(FileProxyErrorCode.Io, ex.Code);
            Assert.Contains(keyword, ex.Message);
            Assert.Contains(fakeN.ToString(), ex.Message);   // 越界值回显（不随语言变）
        }

        LangScope.InZh(() => Reject(FileProxyClient.MaxReadLen + 1, "越界"));
        LangScope.InEn(() => Reject(FileProxyClient.MaxReadLen + 1, "out-of-range"));

        // 负值同样拒绝（服务端回异常 n 即断开，另起一条连接）
        LangScope.InZh(() => Reject(-5, "越界"));
        LangScope.InEn(() => Reject(-5, "out-of-range"));
    }

    [Fact]
    public void 响应ID与请求不匹配_报Io拒绝()
    {
        using var proxy = new FakeProxy { IdOffset = 100 };
        proxy.WaitListeningAsync().GetAwaiter().GetResult();
        var ex = Trap<FileProxyException>(() => Connect(proxy), out _);
        Assert.NotNull(ex);
        Assert.Equal(FileProxyErrorCode.Io, ex.Code);
        Assert.Contains("101", ex.Message);   // 期望 1，实际 1+100
    }

    [Fact]
    public void SpoolAsync_中途取消_目录已删()
    {
        var content = Pattern(Chunk * 3);   // 24MB，至少 3 块，留取消窗口
        using var file = new TempBinary(content);
        using var proxy = FakeProxy.StartAsync().GetAwaiter().GetResult();
        using var client = Connect(proxy);

        var sessionDir = System.IO.Path.Combine(TempRoot.Dir, "spool-" + Guid.NewGuid().ToString("N"));
        using var cts = new CancellationTokenSource();
        var prog = new SyncProgress(cts, cancelAfterCount: 2);   // 第 2 次报告后取消
        var ex = Trap<OperationCanceledException>(
            () => FileProxyService.SpoolAsync(client, file.Path, sessionDir, prog, cts.Token).GetAwaiter().GetResult(), out _);
        Assert.NotNull(ex);
        Assert.False(Directory.Exists(sessionDir));   // 取消即删，无残留
    }

    [Fact]
    public void SpoolProgress_追逐态_pct驻留50_msg含MB且随块刷新()
    {
        var zh = Loc.SetLangGuard(UiLang.ZhCn);
        try
        {
            long expected = 4 * 1024 * 1024;
            // 正常态：pct 随实收线性升（1MB/4MB → 12.5%），50% 封顶
            var (p1, m1) = FileProxyService.SpoolProgress(1 * 1024 * 1024, expected);
            Assert.Equal(0.125, p1, 6);
            Assert.Contains("12%", m1);
            var (p50, _) = FileProxyService.SpoolProgress(expected, expected);
            Assert.Equal(0.5, p50, 6);

            // 边界：实收恰等于期望必须落在正常态文案（「50%」），不得误入追逐态（MB 文案）——
            // 两条路径 pct 同为 0.5，只有文案能区分
            var (pe, me) = FileProxyService.SpoolProgress(expected, expected);
            Assert.Contains("%", me);
            Assert.DoesNotContain("源文件持续增长", me);
            // 期望 0：无分母，直接 50%
            var (p0, _) = FileProxyService.SpoolProgress(0, 0);
            Assert.Equal(0.5, p0);

            // 追逐态（实收 > open.size，分母失效）：pct 驻留 50%，文案含已接收 MB 且逐块变化
            var (c1, cm1) = FileProxyService.SpoolProgress(expected + 8L * 1024 * 1024, expected);
            var (c2, cm2) = FileProxyService.SpoolProgress(expected + 16L * 1024 * 1024, expected);
            Assert.Equal(0.5, c1);
            Assert.Equal(0.5, c2);
            Assert.Contains("源文件持续增长", cm1);
            Assert.Contains("源文件持续增长", cm2);
            Assert.Contains(((expected + 8L * 1024 * 1024) / (1024 * 1024)).ToString(), cm1);   // 含字节数
            Assert.NotEqual(cm1, cm2);   // 随块刷新（禁静态文案 + 静止进度条组合）
        }
        finally { zh(); }
    }

    [Fact]
    public void 超大文件阈值_1GB以上需确认_取消即中止语义()
    {
        // 阈值写死不进设置（任务 4.3）：>1GB → 需确认，≤1GB → 不弹
        Assert.False(FileProxyService.NeedsHugeFileConfirm(0));
        Assert.False(FileProxyService.NeedsHugeFileConfirm(1024));
        Assert.False(FileProxyService.NeedsHugeFileConfirm(FileProxyService.HugeFileThreshold));
        Assert.True(FileProxyService.NeedsHugeFileConfirm(FileProxyService.HugeFileThreshold + 1));
    }

    // ── 裸管道会话（发任意帧、读任意响应，含负载）──

    sealed class RawResponse
    {
        public bool Ok { get; init; }
        public string? Err { get; init; }
        public int? Proto { get; init; }
        public long? H { get; init; }
        public long? N { get; init; }
        public byte[]? Payload { get; init; }
    }

    sealed class RawSession : IDisposable
    {
        readonly NamedPipeClientStream _s;
        readonly byte[] _buf = new byte[8192];
        int _start, _end;

        public RawSession(NamedPipeClientStream s) => _s = s;

        public RawResponse Request(string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            _s.Write(bytes, 0, bytes.Length);
            _s.Flush();

            var line = ReadLine();
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            long? n = root.TryGetProperty("n", out var nEl) && nEl.ValueKind == JsonValueKind.Number
                ? nEl.GetInt64() : null;
            byte[]? payload = null;
            if (n is > 0)
            {
                payload = new byte[n.Value];
                int filled = 0;
                while (filled < payload.Length)
                {
                    int read = ReadInto(payload, filled);
                    Assert.True(read > 0, "裸会话收取负载时流提前结束");
                    filled += read;
                }
            }
            return new RawResponse
            {
                Ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean(),
                Err = root.TryGetProperty("err", out var errEl) ? errEl.GetString() : null,
                Proto = root.TryGetProperty("proto", out var pEl) ? pEl.GetInt32() : (int?)null,
                H = root.TryGetProperty("h", out var hEl) ? hEl.GetInt64() : (long?)null,
                N = n,
                Payload = payload,
            };
        }

        string ReadLine()
        {
            var sb = new StringBuilder();
            while (true)
            {
                EnsureBuffered();
                int nl = Array.IndexOf(_buf, (byte)'\n', _start, _end - _start);
                if (nl < 0)
                {
                    sb.Append(Encoding.UTF8.GetString(_buf, _start, _end - _start));
                    _start = _end;
                    continue;
                }
                sb.Append(Encoding.UTF8.GetString(_buf, _start, nl - _start));
                _start = nl + 1;
                return sb.ToString();
            }
        }

        int ReadInto(byte[] dst, int at)
        {
            EnsureBuffered();
            int take = Math.Min(dst.Length - at, _end - _start);
            Array.Copy(_buf, _start, dst, at, take);
            _start += take;
            return take;
        }

        void EnsureBuffered()
        {
            if (_start < _end) return;
            _start = 0;
            _end = _s.Read(_buf, 0, _buf.Length);
            Assert.True(_end > 0, "裸会话读响应时流提前结束");
        }

        public void Dispose()
        {
            try { _s.Dispose(); } catch { }
        }
    }
}
