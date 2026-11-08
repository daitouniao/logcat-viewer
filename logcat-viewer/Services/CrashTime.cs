using logcat.Services;

namespace logcat.Services;

/// <summary>
/// 崩溃行的时间文本：取自行首原始时间戳字节（与 Time 列同源，spec「崩溃事件的时间呈现与排序」）。
/// 汇总行显示到秒、列表摘要只显示时分秒；行首无时间戳返回 null（调用方落 "-"）。
/// </summary>
public static class CrashTime
{
    /// <summary>汇总行时间文本：行首原文截到秒（"MM-dd HH:mm:ss"，跨天日志必须带日期）；无时间戳返回 null。</summary>
    public static string? GroupText(LogDocument doc, int row) => ToSecond(doc, row);

    /// <summary>列表摘要时间文本：只取时分秒（"HH:mm:ss"）；无时间戳返回 null。</summary>
    public static string? SummaryText(LogDocument doc, int row)
    {
        var s = ToSecond(doc, row);
        if (s == null) return null;
        int sp = s.LastIndexOf(' ');
        return sp >= 0 ? s[(sp + 1)..] : s;
    }

    /// <summary>行首时间戳原文截到秒（小数秒丢弃）："09-23 18:06:48.123" → "09-23 18:06:48"。</summary>
    static string? ToSecond(LogDocument doc, int row)
    {
        var hb = doc.HeaderBytes(row);
        if (hb.Length == 0) return null;
        var s = doc.Decode(hb);
        int dot = s.IndexOf('.');
        return dot > 0 ? s[..dot] : s;
    }
}
