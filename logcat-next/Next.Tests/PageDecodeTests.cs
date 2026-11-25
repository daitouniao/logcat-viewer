using Xunit;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Next.Interop;

namespace Next.Tests;

/// <summary>页布局 v1 解码与释放契约（tasks 2.2）。</summary>
public class PageDecodeTests
{
    /// <summary>解码后释放恰好一次，释放参数与传入一致（design D6：不跨帧持有）。</summary>
    [Fact]
    public void ReleasesNativeBufferAfterDecode()
    {
        var data = BuildPage(rowCount: 2);
        var buffer = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, buffer, data.Length);
        var calls = new List<(IntPtr Buffer, int Length)>();
        try
        {
            var rows = LogcorePageDecoder.DecodeAndRelease(
                buffer, (UIntPtr)data.Length, (ptr, len) => calls.Add((ptr, (int)(ulong)len)));
            Assert.Equal(2, rows.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        var call = Assert.Single(calls);
        Assert.Equal(buffer, call.Buffer);
        Assert.Equal(data.Length, call.Length);
    }

    /// <summary>解析失败时缓冲仍被释放恰好一次（finally 保证）。</summary>
    [Fact]
    public void ReleasesBufferEvenWhenParseFails()
    {
        var calls = 0;
        var buffer = Marshal.AllocHGlobal(8);
        try
        {
            Assert.ThrowsAny<Exception>(() =>
            {
                LogcorePageDecoder.DecodeAndRelease(buffer, (UIntPtr)8, (_, _) => { calls++; });
            });
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        Assert.Equal(1, calls);
    }

    /// <summary>行字段解码：ord / level / pid / tid / tag / time / message 切片正确。</summary>
    [Fact]
    public void DecodesRowFields()
    {
        var rows = LogcorePageDecoder.Decode(BuildPage(rowCount: 2));
        Assert.Equal([10u, 11u], rows.Select(r => r.Ord));
        Assert.Equal([3, 5], rows.Select(r => r.Level));
        Assert.Equal([1000, 1001], rows.Select(r => r.Pid));
        Assert.Equal([2000, 2001], rows.Select(r => r.Tid));
        Assert.Equal(["Alpha", "Beta"], rows.Select(r => r.Tag));
        Assert.Equal("12:00:01.123", rows[0].Time);
        Assert.Equal("msg-0", rows[0].Message);
        Assert.Equal("msg-1", rows[1].Message);
    }

    /// <summary>无 tag 行（NoRef）Tag 为 null，正文不受影响。</summary>
    [Fact]
    public void HandlesNoRef()
    {
        var rows = LogcorePageDecoder.Decode(BuildPage(rowCount: 1, noTag: true));
        Assert.Null(rows[0].Tag);
        Assert.Equal("msg-0", rows[0].Message);
        Assert.Equal("12:00:01.123", rows[0].Time);
    }

    /// <summary>长度不足 36 字节头的缓冲抛明确异常。</summary>
    [Fact]
    public void RejectsTruncatedHeader()
    {
        Assert.ThrowsAny<Exception>(() => LogcorePageDecoder.Decode(new byte[8]));
    }

    /// <summary>
    /// 手工构造布局 v1 页缓冲（手写期望，不用实现回填）：头 36 + 行 52×n +
    /// tag 表（可选）+ 正文 blob。行文本 = 时间 + 空格 + 消息。
    /// </summary>
    public static byte[] BuildPage(int rowCount, bool noTag = false)
    {
        string[] tags = ["Alpha", "Beta"];
        var lines = new string[rowCount];
        for (var i = 0; i < rowCount; i++)
        {
            lines[i] = $"12:00:{(i + 1):D2}.{123 + i:D3} msg-{i}";
        }
        var tagBytes = noTag ? [] : tags.Select(Encoding.UTF8.GetBytes).ToArray();
        var lineBytes = lines.Select(Encoding.UTF8.GetBytes).ToArray();
        var textBytes = lineBytes.Sum(l => l.Length);

        var header = 36;
        var rowsArea = rowCount * 52;
        var tableArea = tagBytes.Sum(b => 4 + b.Length);
        var data = new byte[header + rowsArea + tableArea + textBytes];

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), (uint)rowCount);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), (uint)tagBytes.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(28), (ulong)textBytes);

        var tableAt = header + rowsArea;
        foreach (var tag in tagBytes)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(tableAt), (uint)tag.Length);
            tableAt += 4;
            tag.CopyTo(data, tableAt);
            tableAt += tag.Length;
        }
        var blobStart = tableAt;

        for (var i = 0; i < rowCount; i++)
        {
            var rec = data.AsSpan(header + i * 52, 52);
            var line = lineBytes[i];
            const int timeLen = 12;   // "12:00:01.123" 定长
            var msgOff = timeLen + 1; // 空格之后

            BinaryPrimitives.WriteUInt32LittleEndian(rec, (uint)(10 + i));          // ord
            rec[4] = (byte)(3 + i * 2);                                             // level
            rec[5] = 0; rec[6] = 0;                                                 // flags / crash kind
            BinaryPrimitives.WriteInt32LittleEndian(rec[8..], 1000 + i);            // pid
            BinaryPrimitives.WriteInt32LittleEndian(rec[12..], 2000 + i);           // tid
            BinaryPrimitives.WriteUInt32LittleEndian(rec[16..], (uint)msgOff);      // msgOff
            BinaryPrimitives.WriteUInt32LittleEndian(rec[20..], 0);                 // timeOff
            BinaryPrimitives.WriteUInt32LittleEndian(rec[24..], timeLen);           // timeLen
            BinaryPrimitives.WriteUInt64LittleEndian(rec[28..],                     // textOff
                (ulong)(lineBytes.Take(i).Sum(l => l.Length)));
            BinaryPrimitives.WriteUInt32LittleEndian(rec[36..], (uint)line.Length); // textLen
            BinaryPrimitives.WriteUInt32LittleEndian(rec[40..],                     // tagRef
                noTag ? uint.MaxValue : (uint)(i % tags.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(rec[44..], uint.MaxValue);     // excRef
            BinaryPrimitives.WriteUInt32LittleEndian(rec[48..], uint.MaxValue);     // pkgRef
            line.CopyTo(data, blobStart + lineBytes.Take(i).Sum(l => l.Length));
        }
        return data;
    }
}
