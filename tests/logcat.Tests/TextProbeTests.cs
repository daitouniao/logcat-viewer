using System;
using System.IO;
using System.Text;
using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// 文本嗅探判据（spec「文本嗅探判据」）：8KB 窗口、>10% 控制字符/NUL 判非文本、
/// 空文件判文本、UTF-16 判非文本（预期内）、句柄及时释放。
/// </summary>
public class TextProbeTests
{
    sealed class TempBytes : IDisposable
    {
        public string Path { get; }
        public TempBytes(byte[] content)
        {
            Directory.CreateDirectory(TempRoot.Dir);
            Path = System.IO.Path.Combine(TempRoot.Dir, Guid.NewGuid().ToString("N") + ".bin");
            File.WriteAllBytes(Path, content);
        }
        public void Dispose() { try { File.Delete(Path); } catch { } }
    }

    static byte[] Repeat(byte[] pattern, int total)
    {
        var b = new byte[total];
        for (int i = 0; i < total; i++) b[i] = pattern[i % pattern.Length];
        return b;
    }

    [Fact]
    public void logcat样本判文本()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 200; i++)
            sb.Append($"10-17 19:4{i % 10}:02.123  1234  5678 I ActivityManager: Displayed com.example/.MainActivity: +1s234ms\n");
        using var f = new TempBytes(Encoding.UTF8.GetBytes(sb.ToString()));
        Assert.True(TextProbe.LooksLikeText(f.Path));
    }

    [Fact]
    public void 中文UTF8日志判文本()
    {
        // 护栏：C1 区字节（UTF-8 多字节后续字节）不得算控制字符，否则中文日志全被误判二进制
        var sb = new StringBuilder();
        for (int i = 0; i < 300; i++)
            sb.Append($"10-17 19:48:0{i % 10}.000  1234  5678 D 中文标签: 这是一条中文日志内容，含标点，。！\n");
        using var f = new TempBytes(Encoding.UTF8.GetBytes(sb.ToString()));
        Assert.True(TextProbe.LooksLikeText(f.Path));
    }

    [Fact]
    public void 随机字节判非文本()
    {
        var b = new byte[8192];
        new Random(1).NextBytes(b);
        using var f = new TempBytes(b);
        Assert.False(TextProbe.LooksLikeText(f.Path));
    }

    [Fact]
    public void UTF16判非文本()
    {
        var text = string.Concat(System.Linq.Enumerable.Repeat("Hello 世界 ", 800));
        using var le = new TempBytes(Encoding.Unicode.GetBytes(text));       // UTF-16LE
        Assert.False(TextProbe.LooksLikeText(le.Path));
        using var be = new TempBytes(Encoding.BigEndianUnicode.GetBytes(text)); // UTF-16BE
        Assert.False(TextProbe.LooksLikeText(be.Path));
    }

    [Fact]
    public void 空文件判文本()
    {
        using var f = new TempBytes(Array.Empty<byte>());
        Assert.True(TextProbe.LooksLikeText(f.Path));
    }

    [Fact]
    public void 窗口只取头部8KB_尾部二进制不影响判定()
    {
        var head = Repeat(Encoding.ASCII.GetBytes("10-17 19:48:02.123  1  2 I Tag: message line\n"), TextProbe.WindowBytes);
        var tail = new byte[100 * 1024];
        new Random(2).NextBytes(tail);
        var all = new byte[head.Length + tail.Length];
        head.CopyTo(all, 0);
        tail.CopyTo(all, head.Length);
        using var f = new TempBytes(all);
        Assert.True(TextProbe.LooksLikeText(f.Path));
    }

    [Fact]
    public void 边界占比_严格大于10才判非文本()
    {
        // 8192 样本：819 个控制字符 = 9.99% → 文本；820 = 10.01% → 非文本
        var ok = new byte[8192];
        var bad = new byte[8192];
        for (int i = 0; i < ok.Length; i++) ok[i] = (byte)'a';
        for (int i = 0; i < bad.Length; i++) bad[i] = (byte)'a';
        for (int i = 0; i < 819; i++) ok[i] = 0x01;
        for (int i = 0; i < 820; i++) bad[i] = 0x01;

        Assert.True(TextProbe.DecideText(ok));
        Assert.False(TextProbe.DecideText(bad));
    }

    [Fact]
    public void 允许的空白字符不算非文本()
    {
        // \t \n \r \f 占比再高也仍是文本（制表符日志 / 空行多的日志）
        var b = Repeat(new byte[] { (byte)'\t', (byte)'\n', (byte)'\r', (byte)'\f' }, 4096);
        Assert.True(TextProbe.DecideText(b));
    }

    [Fact]
    public void 嗅探句柄用后即释_可被独占打开()
    {
        // 转代理时源文件还要被代理侧打开：探测完必须放句柄（FileShare.None 能开成功即证明）
        var content = new byte[4096];
        new Random(3).NextBytes(content);   // 非文本 → 会走转代理分支
        using var f = new TempBytes(content);
        Assert.False(TextProbe.LooksLikeText(f.Path));

        using var exclusive = new FileStream(f.Path, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.True(exclusive.CanRead);
    }
}
