using System;
using System.IO;

namespace logcat.Services;

/// <summary>
/// 文本嗅探（spec「文本嗅探判据」）：取目标文件头部至多 8KB 样本，
/// NUL 或控制字符（不含 \t \r \n \f）占比 &gt;10% 判非文本；空文件判文本。
///
/// 判据口径的两个要点：
/// - **只数 ASCII 控制字符**（&lt;0x20 排除 09/0A/0C/0D，外加 7F）。C1 区（0x80-0x9F）
///   **不数**——UTF-8 多字节序列的后续字节正落在该区，数了会把中文日志误判成二进制。
/// - **UTF-16 文本判非文本**（当前版本可接受）：其 ASCII 字符带 NUL 间字节，占比远超 10%。
/// </summary>
public static class TextProbe
{
    /// <summary>嗅探窗口大小（头部 8KB）。</summary>
    public const int WindowBytes = 8 * 1024;

    /// <summary>非文本判定阈值：非文本字节占比严格大于 10%。</summary>
    public const int ThresholdPercent = 10;

    /// <summary>
    /// 探测文件是否像文本。句柄用后即释（转代理时不得占用源文件妨碍代理侧打开）。
    /// 文件打不开时抛异常（由调用方按「直接打开失败」分支处理）。
    /// </summary>
    public static bool LooksLikeText(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buf = new byte[WindowBytes];
        int n = 0;
        while (n < buf.Length)
        {
            int read = fs.Read(buf, n, buf.Length - n);
            if (read <= 0) break;
            n += read;
        }
        return DecideText(buf.AsSpan(0, n));
    }

    /// <summary>internal：纯判据（单测直接喂字节，不走文件）。</summary>
    internal static bool DecideText(ReadOnlySpan<byte> sample)
    {
        if (sample.Length == 0) return true;   // 空文件判文本
        int bad = 0;
        foreach (byte b in sample)
        {
            // NUL 与其余 C0 控制字符（\t=09 \n=0A \f=0C \r=0D 除外）+ DEL(7F)
            if (b < 0x20 && b is not (9 or 10 or 12 or 13)) bad++;
            else if (b == 0x7F) bad++;
        }
        // 严格大于 10%：整数比较避免浮点误差（bad*10 > total ⇔ bad/total > 0.1）
        return bad * 100 <= ThresholdPercent * sample.Length;
    }
}
