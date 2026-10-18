using System;
using System.IO;
using System.Text;
using System.Text.Json;
using logcat.Services;
using Xunit;

// 有意同步阻塞（GetAwaiter().GetResult()）：仓库的 tstrun 反射 runner 不 await 返回
// Task 的测试方法——写成 async Task 会被静默跳过、断言根本不执行（假绿）。
// xUnit1031 的「防死锁」建议在这里不适用：测试进程无 SynchronizationContext。
#pragma warning disable xUnit1031
namespace logcat.Tests;

/// <summary>
/// 管道协议客户端的帧层测试（不走真实管道，直接喂字节流）：
/// 双模读取器的行/块边界、leftover 语义、长度截断、线格式与响应反序列化。
/// 真实管道行为（坏句柄 / 不断线 / 大偏移 / 分块完整性）见 <see cref="FileProxyLoopbackTests"/>。
///
/// 注意：测试方法一律<strong>同步</strong>（内部 GetAwaiter().GetResult() 阻塞）。
/// tstrun 反射 runner 不 await 返回 Task 的测试方法——写成 async Task 会被静默跳过。
/// </summary>
public class FileProxyProtocolTests
{
    static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    static Stream StreamOf(params string[] parts)
    {
        var ms = new MemoryStream();
        foreach (var p in parts) ms.Write(Bytes(p));
        ms.Position = 0;
        return ms;
    }

    static Exception? Catch(Action act)
    {
        try { act(); return null; }
        catch (Exception ex) { return ex; }
    }

    [Fact]
    public void 读响应头与裸块精确拼接_行与块之间无多余字节()
    {
        using var s = StreamOf(
            "{\"id\":1,\"ok\":true,\"n\":5}\nHELLO",
            "{\"id\":2,\"ok\":true,\"n\":0}\n",
            "{\"id\":3,\"ok\":true}\n");
        var r = new FrameReader(s);

        Assert.Equal("{\"id\":1,\"ok\":true,\"n\":5}", r.ReadLineAsync(default).GetAwaiter().GetResult());
        var payload = new byte[5];
        r.ReadExactAsync(payload, default).GetAwaiter().GetResult();
        Assert.Equal("HELLO", Encoding.UTF8.GetString(payload));
        // n=0 的 EOF 响应无负载：下一行必须立即可读，一个字节都不许多吃
        Assert.Equal("{\"id\":2,\"ok\":true,\"n\":0}", r.ReadLineAsync(default).GetAwaiter().GetResult());
        Assert.Equal("{\"id\":3,\"ok\":true}", r.ReadLineAsync(default).GetAwaiter().GetResult());
    }

    [Fact]
    public void 跨缓冲块的大负载_leftover与直读衔接正确()
    {
        // 头行 + 20000 字节负载 + 下一个头行：总量远超 FrameReader 的 8KB 缓冲，
        // 必须走「先吃 leftover、再大块直读」的路径，且读完块后下一行不错位
        var payload = new byte[20000];
        new Random(42).NextBytes(payload);
        using var ms = new MemoryStream();
        ms.Write(Bytes("{\"id\":1,\"ok\":true,\"n\":20000}\n"));
        ms.Write(payload);
        ms.Write(Bytes("{\"id\":2,\"ok\":true,\"n\":0}\n"));
        ms.Position = 0;
        var r = new FrameReader(ms);

        Assert.Equal("{\"id\":1,\"ok\":true,\"n\":20000}", r.ReadLineAsync(default).GetAwaiter().GetResult());
        var got = new byte[20000];
        r.ReadExactAsync(got, default).GetAwaiter().GetResult();
        Assert.Equal(payload, got);
        Assert.Equal("{\"id\":2,\"ok\":true,\"n\":0}", r.ReadLineAsync(default).GetAwaiter().GetResult());
    }

    [Fact]
    public void 控制消息超行上限抛错()
    {
        using var s = StreamOf(new string('x', 70 * 1024));   // 无换行，超 64KB
        var r = new FrameReader(s);
        var ex = Catch(() => r.ReadLineAsync(default).GetAwaiter().GetResult());
        var fp = Assert.IsType<FileProxyException>(ex);
        Assert.Equal(FileProxyErrorCode.Io, fp.Code);
    }

    [Fact]
    public void 读行时流结束抛错()
    {
        using var s = StreamOf("{\"id\":1");   // 半个头行，流就结束了
        var r = new FrameReader(s);
        var ex = Catch(() => r.ReadLineAsync(default).GetAwaiter().GetResult());
        Assert.IsType<FileProxyException>(ex);
    }

    [Fact]
    public void 收取裸块时流结束抛错_消息含应收与实收()
    {
        using var s = StreamOf("{\"id\":1,\"ok\":true,\"n\":10}\nabc");   // 声明 10 只给 3
        var r = new FrameReader(s);
        Assert.Equal("{\"id\":1,\"ok\":true,\"n\":10}", r.ReadLineAsync(default).GetAwaiter().GetResult());
        var ex = Catch(() => r.ReadExactAsync(new byte[10], default).GetAwaiter().GetResult());
        var fp = Assert.IsType<FileProxyException>(ex);
        Assert.Contains("10", fp.Message);
        Assert.Contains("3", fp.Message);
    }

    [Theory]
    [InlineData(32 * 1024 * 1024, 16 * 1024 * 1024)]   // 超上限截到 16MB
    [InlineData(16 * 1024 * 1024, 16 * 1024 * 1024)]   // 等于上限不动
    [InlineData(1024, 1024)]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]                                 // 负值归零
    public void 读取长度按16MB截断(int req, int expect)
        => Assert.Equal(expect, FileProxyClient.ClampLen(req));

    [Fact]
    public void 请求线格式_四种消息()
    {
        Assert.Equal("{\"id\":1,\"op\":\"hello\"}",
            FileProxyClient.BuildRequestJson(1, "hello"));
        Assert.Equal("{\"id\":2,\"op\":\"open\",\"path\":\"C:\\\\logs\\\\app.log\"}",
            FileProxyClient.BuildRequestJson(2, "open", ("path", "C:\\logs\\app.log")));
        Assert.Equal("{\"id\":3,\"op\":\"read\",\"h\":7,\"off\":0,\"len\":8388608}",
            FileProxyClient.BuildRequestJson(3, "read", ("h", 7L), ("off", 0L), ("len", 8 * 1024 * 1024)));
        Assert.Equal("{\"id\":4,\"op\":\"close\",\"h\":7}",
            FileProxyClient.BuildRequestJson(4, "close", ("h", 7L)));
    }

    [Fact]
    public void 大偏移与特殊路径在线格式中保留()
    {
        // off ≥ 2^31：JSON number 必须完整保留（JS 侧 `|0` 截断坑的 C# 侧护栏）
        var json = FileProxyClient.BuildRequestJson(9, "read", ("h", 1L), ("off", 3_000_000_000L), ("len", 4096));
        Assert.Contains("\"off\":3000000000", json);

        // UNC / 中文 / 空格路径：反斜杠按 JSON 规则转义，原文可还原
        var unicode = FileProxyClient.BuildRequestJson(9, "open", ("path", "\\\\server\\共享\\日志.log"));
        Assert.Contains("path", unicode);
        var back = JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(unicode)!["path"]!.GetValue<string>();
        Assert.Equal("\\\\server\\共享\\日志.log", back);
    }

    [Fact]
    public void 响应反序列化_缺字段与大小写不敏感()
    {
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        var hello = JsonSerializer.Deserialize<FileProxyClient.HelloResp>(
            "{\"Id\":7,\"OK\":true,\"proxy\":\"node-fileproxy/1.0\",\"proto\":1}", opts)!;
        Assert.Equal(7, hello.Id);
        Assert.True(hello.Ok);
        Assert.Equal("node-fileproxy/1.0", hello.Proxy);
        Assert.Equal(1, hello.Proto);

        // proto 缺失 → null（调用方据此判 ProtoMismatch）
        var noProto = JsonSerializer.Deserialize<FileProxyClient.HelloResp>(
            "{\"id\":1,\"ok\":true,\"proxy\":\"x\"}", opts)!;
        Assert.Null(noProto.Proto);

        // ok 缺失 → false：代理回的怪串按失败处理，不误放行
        var noOk = JsonSerializer.Deserialize<FileProxyClient.OpenResp>("{\"id\":1,\"h\":3,\"size\":10}", opts)!;
        Assert.False(noOk.Ok);

        // 未知字段不影响解析
        var extra = JsonSerializer.Deserialize<FileProxyClient.ReadResp>(
            "{\"id\":2,\"ok\":true,\"n\":5,\"mtime\":123,\"future\":[1,2]}", opts)!;
        Assert.Equal(5, extra.N);

        var close = JsonSerializer.Deserialize<FileProxyClient.CloseResp>("{\"id\":3,\"ok\":true}", opts)!;
        Assert.True(close.Ok);
        Assert.Null(close.Err);

        var err = JsonSerializer.Deserialize<FileProxyClient.CloseResp>("{\"id\":4,\"ok\":false,\"err\":\"boom\"}", opts)!;
        Assert.False(err.Ok);
        Assert.Equal("boom", err.Err);
    }
}
