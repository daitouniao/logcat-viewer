using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace logcat.Services;

/// <summary>
/// 文件代理协议 v1 客户端（spec「协议消息帧格式」）。
///
/// 传输 = Windows 命名管道字节流。控制消息单行 JSON + '\n'；读响应 = JSON 头行
/// （含负载字节数 n）+ 紧跟的 n 字节裸块，无分隔符；n=0 即 EOF 无负载。
///
/// **实现关键坑：管道上不能套文本读取器**（StreamReader 的缓冲会把二进制帧吃掉），
/// 必须自写「行缓冲读 JSON + 按 n 收裸块」的双模读取器（<see cref="FrameReader"/>，
/// leftover buffer 模式，与 examples/ 两个代理例子同构）。
/// </summary>
public sealed class FileProxyClient : IAsyncDisposable, IDisposable
{
    /// <summary>协议版本。缺失或不等于 1 一律判连接失败，不做兼容降级。</summary>
    public const int ProtoVersion = 1;

    /// <summary>单次 read 请求的长度上限（16MB，代理侧同样 clamp）。</summary>
    public const int MaxReadLen = 16 * 1024 * 1024;

    /// <summary>控制消息单行上限（64KB）。路径超长直接报错，不分帧。</summary>
    public const int MaxLineBytes = 64 * 1024;

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    readonly NamedPipeClientStream _pipe;
    readonly FrameReader _reader;
    int _nextId = 1;
    bool _disposed;

    FileProxyClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = new FrameReader(pipe);
    }

    /// <summary>代理在 hello 响应里给出的自描述（连接成功后非空）。</summary>
    public string? ProxyName { get; private set; }

    /// <summary>连接命名管道并完成 hello 握手。握手失败（连不上 / proto 不匹配）抛 <see cref="FileProxyException"/>。</summary>
    /// <param name="pipeName">管道名（不含 <c>\\.\pipe\</c> 前缀）。</param>
    /// <param name="timeoutMs">连接 + 握手总超时。连不上按 <see cref="FileProxyErrorCode.NoProxyNotRunning"/> 报。</param>
    public static async Task<FileProxyClient> ConnectAsync(string pipeName, int timeoutMs, CancellationToken ct = default)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        try
        {
            await pipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new FileProxyException(FileProxyErrorCode.NoProxyNotRunning,
                Loc.F("连接文件代理超时（{0} ms）：管道 {1} 无响应", timeoutMs, pipeName));
        }
        catch (Exception ex)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            // 管道不存在 / 拒绝访问等都按「代理未在运行」报——拉起与否由编排层按配置决定
            throw new FileProxyException(FileProxyErrorCode.NoProxyNotRunning,
                Loc.F("连接文件代理失败：管道 {0}（{1}）", pipeName, ex.Message), ex);
        }

        var client = new FileProxyClient(pipe);
        try
        {
            var proxy = await client.HelloAsync(ct).ConfigureAwait(false);
            client.ProxyName = proxy;
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // ── 协议方法 ──

    /// <summary>hello 握手：校验 proto == 1，返回代理自描述。</summary>
    public async Task<string> HelloAsync(CancellationToken ct = default)
    {
        var resp = await SendAsync<HelloResp>(ct, "hello").ConfigureAwait(false);
        // proto 缺失或不等于 1 一律判连接失败（安全侧默认：连的是什么都不确定的代理，
        // 不给它读文件的机会）；先于 ok 判定，代理自描述附在错误消息里
        if (resp.Proto != ProtoVersion)
            throw new FileProxyException(FileProxyErrorCode.ProtoMismatch,
                Loc.F("文件代理协议版本不匹配：代理自描述 {0}，协议版本 {1}，应用仅支持 {2}",
                    resp.Proxy ?? "（未提供）", resp.Proto?.ToString() ?? "（缺失）", ProtoVersion));
        if (!resp.Ok)
            throw ProxyError(resp.Err);
        return resp.Proxy ?? "";
    }

    /// <summary>打开文件，返回（句柄, 大小）。句柄仅当前连接内有效。</summary>
    public async Task<(long Handle, long Size)> OpenAsync(string path, CancellationToken ct = default)
    {
        var resp = await SendAsync<OpenResp>(ct, "open", ("path", path)).ConfigureAwait(false);
        if (!resp.Ok || resp.H is null || resp.Size is null)
            throw ProxyError(resp.Err);
        return (resp.H.Value, resp.Size.Value);
    }

    /// <summary>
    /// 读一段：发送 read 请求，按响应头声明的 n 精确收取裸块。
    /// n=0 表示到达文件末尾，返回空数组。len 超上限时按 <see cref="MaxReadLen"/> 截断。
    /// </summary>
    public async Task<byte[]> ReadAsync(long handle, long offset, int len, CancellationToken ct = default)
    {
        len = ClampLen(len);
        var resp = await SendAsync<ReadResp>(ct, "read", ("h", handle), ("off", offset), ("len", len)).ConfigureAwait(false);        if (!resp.Ok || resp.N is null)
            throw ProxyError(resp.Err);

        long n = resp.N.Value;
        if (n < 0 || n > MaxReadLen)
            throw new FileProxyException(FileProxyErrorCode.Io,
                Loc.F("文件代理返回的负载长度越界：{0}", n));
        var payload = new byte[n];
        if (n > 0)
            await _reader.ReadExactAsync(payload, ct).ConfigureAwait(false);
        return payload;
    }

    /// <summary>关闭句柄。代理对坏句柄也回 ok；本方法的失败一律吞掉（关不死也不阻断主流程）。</summary>
    public async Task CloseAsync(long handle, CancellationToken ct = default)
    {
        try
        {
            var resp = await SendAsync<CloseResp>(ct, "close", ("h", handle)).ConfigureAwait(false);
            _ = resp;
        }
        catch { /* 关闭路径不抛 */ }
    }

    // ── 发送 / 接收 ──

    /// <summary>组帧并发出一项控制消息，读回响应。字段顺序即线格式（id 在前，代理例子同构）。</summary>
    async Task<T> SendAsync<T>(CancellationToken ct, string op,
        (string Key, object Value)? field1 = null, (string Key, object Value)? field2 = null,
        (string Key, object Value)? field3 = null) where T : Resp
    {
        int id = _nextId++;
        string json = BuildRequestJson(id, op, field1, field2, field3);
        // 单行上限：超长路径在发送前就报错，不分帧（代理侧按 64KB 行上限解析）
        int bytes = Encoding.UTF8.GetByteCount(json);
        if (bytes > MaxLineBytes)
            throw new FileProxyException(FileProxyErrorCode.Io,
                Loc.F("文件代理控制消息超长（{0} 字节，上限 {1}）：路径可能过长", bytes, MaxLineBytes));

        await _pipe.WriteAsync(Encoding.UTF8.GetBytes(json + "\n"), ct).ConfigureAwait(false);
        await _pipe.FlushAsync(ct).ConfigureAwait(false);

        var resp = await ReadJsonAsync<T>(ct).ConfigureAwait(false);
        if (resp.Id != id)
            throw new FileProxyException(FileProxyErrorCode.Io,
                Loc.F("文件代理响应 ID 不匹配：期望 {0}，实际 {1}", id, resp.Id));
        return resp;
    }

    /// <summary>internal：组出请求行的 JSON 文本（单测直接断言线格式，不走管道）。</summary>
    internal static string BuildRequestJson(int id, string op,
        (string Key, object Value)? field1 = null, (string Key, object Value)? field2 = null,
        (string Key, object Value)? field3 = null)
    {
        var obj = new JsonObject { ["id"] = id, ["op"] = op };
        if (field1.HasValue) obj[field1.Value.Key] = JsonValue.Create(field1.Value.Value);
        if (field2.HasValue) obj[field2.Value.Key] = JsonValue.Create(field2.Value.Value);
        if (field3.HasValue) obj[field3.Value.Key] = JsonValue.Create(field3.Value.Value);
        return obj.ToJsonString();
    }

    /// <summary>internal：读取长度按 16MB 上限截断（单测直接覆盖边界）。</summary>
    internal static int ClampLen(int len) => Math.Clamp(len, 0, MaxReadLen);

    async Task<T> ReadJsonAsync<T>(CancellationToken ct) where T : Resp
    {
        var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<T>(line, JsonOpts)
                ?? throw new FileProxyException(FileProxyErrorCode.Io,
                    Loc.F("文件代理返回了空响应：{0}", Truncate(line, 200)));
        }
        catch (JsonException ex)
        {
            throw new FileProxyException(FileProxyErrorCode.Io,
                Loc.F("文件代理响应不是合法 JSON：{0}", Truncate(line, 200)), ex);
        }
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    static FileProxyException ProxyError(string? err) => new(FileProxyErrorCode.ProxyError,
        Loc.F("文件代理返回错误：{0}", err ?? "（未给出原因）")) { ProxyError = err };

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await _pipe.DisposeAsync().ConfigureAwait(false); }
        catch { /* 关闭路径不抛 */ }
    }

    /// <summary>同步收尾（同步调用方 / 测试用；测试进程无 SynchronizationContext，不会死锁）。</summary>
    void IDisposable.Dispose() => DisposeAsync().GetAwaiter().GetResult();

    // ── 响应 DTO ──
    // ok 缺省 false：代理回的怪串按失败处理，不误放行。
    // internal：单测直接反序列化样本行，覆盖缺字段 / 大小写 / 怪值（见 FileProxyProtocolTests）。

    internal abstract record Resp([property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("err")] string? Err);

    internal sealed record HelloResp(long Id, bool Ok, string? Err,
        [property: JsonPropertyName("proxy")] string? Proxy,
        [property: JsonPropertyName("proto")] int? Proto) : Resp(Id, Ok, Err);

    internal sealed record OpenResp(long Id, bool Ok, string? Err,
        [property: JsonPropertyName("h")] long? H,
        [property: JsonPropertyName("size")] long? Size) : Resp(Id, Ok, Err);

    internal sealed record ReadResp(long Id, bool Ok, string? Err,
        [property: JsonPropertyName("n")] long? N) : Resp(Id, Ok, Err);

    internal sealed record CloseResp(long Id, bool Ok, string? Err) : Resp(Id, Ok, Err);
}

/// <summary>
/// 管道双模读取器：行缓冲读 JSON + 按 n 收裸块，leftover buffer 模式。
///
/// 为什么不能套 <see cref="StreamReader"/>：二进制帧会被读取器的缓冲吃掉——
/// 头行之后紧跟的裸块与下一头行之间没有任何分隔符，读取器会按自己的缓冲边界
/// 任意切分，行与块的边界全部错位。
/// </summary>
internal sealed class FrameReader
{
    const int BufSize = 8192;

    readonly Stream _stream;
    readonly byte[] _buf = new byte[BufSize];
    int _start;   // 有效数据起点
    int _end;     // 有效数据终点（不含）

    public FrameReader(Stream stream) => _stream = stream;

    /// <summary>读一行（不含 '\n'）。行上限 64KB，超限抛 <see cref="FileProxyException"/>。</summary>
    public async Task<string> ReadLineAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        while (true)
        {
            if (_start >= _end && !await FillAsync(ct).ConfigureAwait(false))
                throw new FileProxyException(FileProxyErrorCode.Io,
                    Loc.F("文件代理连接中断：读取控制消息时流已结束（已收 {0} 字节）", sb.Length));

            int nl = Array.IndexOf(_buf, (byte)'\n', _start, _end - _start);
            if (nl < 0)
            {
                sb.Append(Encoding.UTF8.GetString(_buf, _start, _end - _start));
                _start = _end;
                if (sb.Length > FileProxyClient.MaxLineBytes)
                    throw new FileProxyException(FileProxyErrorCode.Io,
                        Loc.F("文件代理控制消息超过单行上限 {0} 字节", FileProxyClient.MaxLineBytes));
                continue;
            }

            int take = nl - _start;
            sb.Append(Encoding.UTF8.GetString(_buf, _start, take));
            _start = nl + 1;   // 跳过 '\n'，剩余字节留给下一次读取（leftover）
            return sb.ToString();
        }
    }

    /// <summary>精确收取 <paramref name="dst"/> 全长的字节。流中途结束抛 <see cref="FileProxyException"/>。</summary>
    public async Task ReadExactAsync(byte[] dst, CancellationToken ct)
    {
        int n = dst.Length;
        int filled = 0;

        // 先吃 leftover（上一次读行剩下的、属于裸块的字节）
        if (_start < _end)
        {
            int take = Math.Min(n, _end - _start);
            Array.Copy(_buf, _start, dst, 0, take);
            _start += take;
            filled = take;
        }

        // 大块直读：不再过小缓冲（8MB 负载过 8KB 缓冲 = 上千次多余拷贝）
        while (filled < n)
        {
            int read = await _stream.ReadAsync(dst.AsMemory(filled, n - filled), ct).ConfigureAwait(false);
            if (read <= 0)
                throw new FileProxyException(FileProxyErrorCode.Io,
                    Loc.F("文件代理连接中断：收取裸块时流已结束（应收 {0} 字节，实收 {1}）", n, filled));
            filled += read;
        }
    }

    /// <summary>从流补充数据到缓冲。返回 false 表示流已结束。</summary>
    async Task<bool> FillAsync(CancellationToken ct)
    {
        if (_start >= _end)
        {
            _start = 0;
            _end = 0;
        }
        else if (_start > 0)
        {
            // 压缩：把剩余字节移到缓冲头部（行没读满、后续还有数据的场景）
            Array.Copy(_buf, _start, _buf, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }

        if (_end == _buf.Length) return true;   // 缓冲已满还没有换行 → 由调用方按行上限报错
        int read = await _stream.ReadAsync(_buf.AsMemory(_end, _buf.Length - _end), ct).ConfigureAwait(false);
        if (read <= 0) return false;
        _end += read;
        return true;
    }
}
