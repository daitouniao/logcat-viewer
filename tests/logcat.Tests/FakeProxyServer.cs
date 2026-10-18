using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// 进程内模拟文件代理：在测试进程里起命名管道服务端，按协议 v1 应答，
/// 不起任何外部进程（真实代理例子见 openspec change 的 examples/）。
///
/// 支持注入的行为（init 属性）：
/// - <see cref="Proto"/> / <see cref="ProxyName"/>：hello 自描述与协议版本（不匹配测试用）
/// - <see cref="OpenError"/>：open 恒失败并回该错误（代理侧错误透出测试用）
/// - <see cref="AbortAfterBytes"/>：累计送出这么多字节后暴力断开（模拟中途断线）
///
/// 观测面（字段/属性，供断言）：
/// - <see cref="ConnectionCount"/> / <see cref="OpenCount"/> / <see cref="ReadCount"/>
/// - <see cref="RequestLines"/>：收到的原始请求行（线格式断言用）
/// - <see cref="LastReadOffset"/> / <see cref="LastReadLen"/>：最近一次 read 的 off/len
/// - <see cref="OpenedPaths"/> / <see cref="ClosedHandles"/>：open 的路径与 close 的句柄
/// </summary>
sealed class FakeProxy : IAsyncDisposable, IDisposable
{
    // ── 注入行为 ──
    public int Proto { get; init; } = FileProxyClient.ProtoVersion;
    public string ProxyName { get; init; } = "test-fileproxy/1.0";
    public string? OpenError { get; init; }
    public long AbortAfterBytes { get; init; } = -1;   // <0 = 不断线

    // ── 观测面 ──
    int _connections;
    public int ConnectionCount => _connections;
    int _opens;
    public int OpenCount => _opens;
    int _reads;
    public int ReadCount => _reads;
    long _lastReadOffset = -1;
    public long LastReadOffset => _lastReadOffset;
    long _lastReadLen = -1;
    public long LastReadLen => _lastReadLen;
    readonly List<string> _requestLines = new();
    public IReadOnlyList<string> RequestLines { get { lock (_requestLines) return _requestLines.ToArray(); } }
    readonly List<string> _openedPaths = new();
    public IReadOnlyList<string> OpenedPaths { get { lock (_openedPaths) return _openedPaths.ToArray(); } }
    readonly List<long> _closedHandles = new();
    public IReadOnlyList<long> ClosedHandles { get { lock (_closedHandles) return _closedHandles.ToArray(); } }

    readonly string _pipeName;
    readonly CancellationTokenSource _cts = new();
    readonly List<Task> _connTasks = new();
    readonly Task _acceptLoop;

    public FakeProxy(string? pipeName = null)
    {
        _pipeName = pipeName ?? "logcat-test-" + Guid.NewGuid().ToString("N");
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public string PipeName => _pipeName;

    public static async Task<FakeProxy> StartAsync(string? pipeName = null)
    {
        var p = new FakeProxy(pipeName);
        await p.WaitListeningAsync().ConfigureAwait(false);
        return p;
    }

    /// <summary>等到管道实例就绪（首个 server stream 已创建并开始等待连接）。</summary>
    public async Task WaitListeningAsync()
    {
        // 首实例在 AcceptLoopAsync 入口同步创建；给它一个调度窗口
        for (int i = 0; i < 100 && !_firstServerCreated; i++)
            await Task.Delay(10).ConfigureAwait(false);
        Assert.True(_firstServerCreated, "模拟代理未能在 1s 内进入监听状态");
    }

    volatile bool _firstServerCreated;
    long _totalSent;

    async Task AcceptLoopAsync()
    {
        var server = NewServer();
        _firstServerCreated = true;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (IOException) { break; }   // 客户端暴力断开时等待被惊扰

            var conn = server;
            server = NewServer();             // 立刻补一个监听实例，避免第二个客户端撞上「无实例」窗口
            _firstServerCreated = true;
            var task = Task.Run(() => HandleConnAsync(conn));
            lock (_connTasks) _connTasks.Add(task);
        }
        try { server.Dispose(); } catch { }
    }

    NamedPipeServerStream NewServer() =>
        new(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    async Task HandleConnAsync(NamedPipeServerStream conn)
    {
        Interlocked.Increment(ref _connections);
        var reader = new FrameReader(conn);
        var handles = new Dictionary<long, FileStream>();
        long nextH = 1;
        try
        {
            while (!_cts.IsCancellationRequested && conn.IsConnected)
            {
                string line;
                try { line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false); }
                catch (Exception) { break; }   // 客户端断开 / 取消：连接结束
                lock (_requestLines) _requestLines.Add(line);
                if (line.Trim().Length == 0) continue;

                long id = 0;
                string? op = null;
                JsonNode? root = null;
                try
                {
                    root = JsonNode.Parse(line);
                    id = root?["id"]?.GetValue<long>() ?? 0;
                    op = root?["op"]?.GetValue<string>();
                }
                catch (JsonException)
                {
                    await SendJson(conn, new JsonObject { ["id"] = 0, ["ok"] = false, ["err"] = "bad json" }).ConfigureAwait(false);
                    continue;
                }

                switch (op)
                {
                    case "hello":
                        await SendJson(conn, new JsonObject
                        {
                            ["id"] = id, ["ok"] = true,
                            ["proxy"] = ProxyName, ["proto"] = Proto,
                        }).ConfigureAwait(false);
                        break;

                    case "open":
                    {
                        Interlocked.Increment(ref _opens);
                        string? path = root?["path"]?.GetValue<string>();
                        lock (_openedPaths) _openedPaths.Add(path ?? "");
                        if (OpenError != null)
                        {
                            await SendJson(conn, new JsonObject { ["id"] = id, ["ok"] = false, ["err"] = OpenError }).ConfigureAwait(false);
                            break;
                        }
                        try
                        {
                            var fs = File.OpenRead(path!);
                            long h = nextH++;
                            handles[h] = fs;
                            await SendJson(conn, new JsonObject { ["id"] = id, ["ok"] = true, ["h"] = h, ["size"] = fs.Length }).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            await SendJson(conn, new JsonObject { ["id"] = id, ["ok"] = false, ["err"] = ex.Message }).ConfigureAwait(false);
                        }
                        break;
                    }

                    case "read":
                    {
                        Interlocked.Increment(ref _reads);
                        long h = root?["h"]?.GetValue<long>() ?? -1;
                        long off = root?["off"]?.GetValue<long>() ?? 0;
                        long len = root?["len"]?.GetValue<long>() ?? 0;
                        _lastReadOffset = off;
                        _lastReadLen = len;

                        if (!handles.TryGetValue(h, out var fs))
                        {
                            await SendJson(conn, new JsonObject { ["id"] = id, ["ok"] = false, ["err"] = "bad handle" }).ConfigureAwait(false);
                            break;
                        }
                        // 与 examples/ 一致：len 按 16MB clamp，off 不得为负
                        long clamped = Math.Clamp(len, 0, FileProxyClient.MaxReadLen);
                        long want = Math.Min(clamped, Math.Max(0, fs.Length - off));
                        var buf = new byte[want];
                        if (want > 0)
                        {
                            fs.Position = off;
                            fs.ReadExactly(buf, 0, (int)want);   // 本地文件：精确读满，不做短读模拟
                        }
                        await SendJson(conn, new JsonObject { ["id"] = id, ["ok"] = true, ["n"] = (int)want }).ConfigureAwait(false);
                        if (want > 0)
                        {
                            await conn.WriteAsync(buf.AsMemory(0, (int)want), _cts.Token).ConfigureAwait(false);
                            await conn.FlushAsync(_cts.Token).ConfigureAwait(false);
                            Interlocked.Add(ref _totalSent, want);
                        }
                        // 断线注入：累计送出达到阈值后暴力断开（客户端下一次读会拿到流结束）
                        if (AbortAfterBytes >= 0 && Interlocked.Read(ref _totalSent) >= AbortAfterBytes)
                        {
                            conn.Dispose();     // 不发 close、不走优雅关闭
                            return;
                        }
                        break;
                    }

                    case "close":
                    {
                        long h = root?["h"]?.GetValue<long>() ?? -1;
                        if (handles.Remove(h, out var fs))
                        {
                            try { fs.Dispose(); } catch { }
                            lock (_closedHandles) _closedHandles.Add(h);
                        }
                        await SendJson(conn, new JsonObject { ["id"] = id, ["ok"] = true }).ConfigureAwait(false);
                        break;
                    }

                    default:
                        await SendJson(conn, new JsonObject { ["id"] = id, ["ok"] = false, ["err"] = "unknown op: " + op }).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (Exception) { /* 单连接异常不惊扰其他连接 */ }
        finally
        {
            foreach (var fs in handles.Values) { try { fs.Dispose(); } catch { } }
            try { conn.Dispose(); } catch { }
        }
    }

    static async Task SendJson(Stream s, JsonObject obj)
    {
        var bytes = Encoding.UTF8.GetBytes(obj.ToJsonString() + "\n");
        await s.WriteAsync(bytes).ConfigureAwait(false);
        await s.FlushAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _acceptLoop.ConfigureAwait(false); } catch { }
        Task[] tasks;
        lock (_connTasks) tasks = _connTasks.ToArray();
        try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }

    /// <summary>同步收尾（tstrun 反射调用测试方法，不便 await；测试进程无 SynchronizationContext，不会死锁）。</summary>
    void IDisposable.Dispose() => DisposeAsync().GetAwaiter().GetResult();
}
