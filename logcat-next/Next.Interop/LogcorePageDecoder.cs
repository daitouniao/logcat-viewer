using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Next.Interop;

/// <summary>
/// 页缓冲布局 v1 解码，并保证原生缓冲「无论成败都恰好释放一次」。
/// 头 36 字节：rows/tagCount/excCount/pkgCount/textBytes；行 52 字节定宽；其后
/// 三个长度前缀字符串表与 UTF-8 正文 blob。释放责任经 <see cref="FreePage"/> 委托
/// 注入，便于测试断言恰好释放一次（design D6：解码后立即释放，不跨帧持有）。
/// </summary>
public static class LogcorePageDecoder
{
    const int HeaderBytes = 36;
    const int RowBytes = 52;
    const uint NoRef = uint.MaxValue;

    /// <summary>解码并释放。解析抛异常时缓冲仍被释放（finally 保证）。释放委托与导出签名同型。</summary>
    public static LogcorePageRow[] DecodeAndRelease(IntPtr buffer, UIntPtr length, LogcoreLibrary.FreePageFn free)
    {
        try
        {
            return Decode(buffer, length);
        }
        finally
        {
            free(buffer, length);
        }
    }

    /// <summary>纯解码，不释放缓冲。</summary>
    public static LogcorePageRow[] Decode(IntPtr buffer, UIntPtr length)
    {
        var data = new byte[checked((int)(ulong)length)];
        Marshal.Copy(buffer, data, 0, data.Length);
        return Decode(data);
    }

    /// <summary>纯解码托管副本（测试直接喂字节用）。</summary>
    public static LogcorePageRow[] Decode(ReadOnlySpan<byte> span)
    {
        if (span.Length < HeaderBytes)
        {
            throw new ArgumentException("页缓冲不足 36 字节头", nameof(span));
        }
        var rows = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
        var tagCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[16..]);
        var excCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[20..]);
        var pkgCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[24..]);
        var textBytes = (int)BinaryPrimitives.ReadUInt64LittleEndian(span[28..]);
        var at = HeaderBytes + checked(rows * RowBytes);
        if (span.Length < at + textBytes)
        {
            throw new ArgumentException("页缓冲长度与头字段不一致", nameof(span));
        }
        var tags = ReadTable(span, ref at, tagCount);
        ReadTable(span, ref at, excCount);   // 偏移必须推进，异常表 / 包名表本壳不展示
        ReadTable(span, ref at, pkgCount);
        var blob = span.Slice(at, textBytes);

        var result = new LogcorePageRow[rows];
        for (var i = 0; i < rows; i++)
        {
            var rec = span.Slice(HeaderBytes + i * RowBytes, RowBytes);
            var textOff = (int)BinaryPrimitives.ReadUInt64LittleEndian(rec[28..]);
            var textLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(rec[36..]);
            var raw = blob.Slice(textOff, textLen).ToArray();
            result[i] = new LogcorePageRow
            {
                Ord = BinaryPrimitives.ReadUInt32LittleEndian(rec),
                Level = rec[4],
                Pid = BinaryPrimitives.ReadInt32LittleEndian(rec[8..]),
                Tid = BinaryPrimitives.ReadInt32LittleEndian(rec[12..]),
                Tag = Ref(tags, BinaryPrimitives.ReadUInt32LittleEndian(rec[40..])),
                Raw = raw,
                Time = Slice(raw,
                    (int)BinaryPrimitives.ReadUInt32LittleEndian(rec[20..]),
                    (int)BinaryPrimitives.ReadUInt32LittleEndian(rec[24..])),
                Message = Slice(raw,
                    (int)BinaryPrimitives.ReadUInt32LittleEndian(rec[16..]),
                    raw.Length),
            };
        }
        return result;
    }

    static string[] ReadTable(ReadOnlySpan<byte> data, ref int at, int count)
    {
        var values = new string[count];
        for (var i = 0; i < count; i++)
        {
            var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
            at += 4;
            values[i] = Encoding.UTF8.GetString(data.Slice(at, len));
            at += len;
        }
        return values;
    }

    static string? Ref(string[] table, uint index) =>
        index == NoRef ? null : table[index];

    static string Slice(ReadOnlySpan<byte> raw, int start, int len)
    {
        start = Math.Clamp(start, 0, raw.Length);
        len = Math.Clamp(len, 0, raw.Length - start);
        return len == 0 ? "" : Encoding.UTF8.GetString(raw.Slice(start, len));
    }
}
