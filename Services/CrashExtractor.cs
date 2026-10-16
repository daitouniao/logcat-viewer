using logcat.Models;

namespace logcat.Services;

/// <summary>提取结果：崩溃类别 + 异常类型（或信号名）+ 包名。字段可为空串（未提取到）。</summary>
public readonly record struct CrashMatch(byte Kind, string ExcType, string Pkg);

/// <summary>
/// 崩溃信息提取：从崩溃消息文本中识别类别（Java / Native / ANR / tombstone）、
/// 异常类型与包名。纯函数、无状态，供索引扫描（LogDocument.ScanRange）与
/// 摘要行的类型回退共用。
///
/// <para><b>判据设计（重误报防护，用例口径每类 1 正 + 2 反）</b>：</para>
/// <list type="bullet">
/// <item>Java：行含 <c>FATAL EXCEPTION:</c>，且其後 3 行内存在 <c>Process: &lt;pkg&gt;, PID:</c> 行
///       —— 没有 Process 行不算（业务日志打印「FATAL EXCEPTION」字样的 D 级日志不误报）。</item>
/// <item>异常类型：Process 行之后第一个非空行首 token，须以 java./javax./android./androidx./kotlin.
///       开头（自定义包名的异常提不出来 → 类型留空，但崩溃事件仍记录）。</item>
/// <item>Native/tombstone：<c>signal N (SIGXXX)</c> 且 SIG 名在白名单内（URL/聊天文本里的
///       「signal」字样不误报）；有 <c>*** *** ***</c> tombstone 头的归 tombstone。</item>
/// <item>ANR：行以 <c>ANR in </c> 开头且首 token 是包名形态（含点分段）——「ANR」缩写词
///       出现在业务消息里不误报。</item>
/// </list>
/// </summary>
public static class CrashExtractor
{
    public const byte KindJava = CrashKind.Java;
    public const byte KindNative = CrashKind.Native;
    public const byte KindAnr = CrashKind.Anr;
    public const byte KindTombstone = CrashKind.Tombstone;

    /// <summary>Android 常见致命信号白名单（信号名误报的最后防线）。</summary>
    static readonly string[] Signals =
    {
        "SIGSEGV", "SIGABRT", "SIGBUS", "SIGILL", "SIGFPE", "SIGTRAP", "SIGSYS", "SIGSTKFLT",
    };

    static readonly string[] ExcPrefixes = { "java.", "javax.", "android.", "androidx.", "kotlin." };

    /// <summary>索引扫描的字节级快筛关键字：任一命中才值得解码文本做完整提取。
    /// 取的是「真实崩溃必有、普通日志罕见」的片段，宁滥勿缺（漏了就丢事件）。</summary>
    internal static readonly string[] FastKeywords = { "FATAL EXCEPTION", "SIG", "ANR in ", "*** ***" };

    static readonly byte[][] _fastKeywordBytes =
        FastKeywords.Select(k => System.Text.Encoding.UTF8.GetBytes(k)).ToArray();

    /// <summary>字节级快筛：行内任一崩溃关键字命中返回 true（千万行扫描的零分配快速路径）。</summary>
    internal static bool ContainsCrashKeyword(ReadOnlySpan<byte> line)
    {
        foreach (var kw in _fastKeywordBytes)
            if (line.IndexOf(kw) >= 0) return true;
        return false;
    }

    /// <summary>
    /// 从崩溃消息文本提取崩溃信息。文本为「首行 msg + 续行」的合并视图（msg 部分），
    /// 逐行判定；不是崩溃返回 null。
    /// </summary>
    public static CrashMatch? TryExtract(string message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var lines = message.Split('\n');
        return TryExtractLines(lines);
    }

    static CrashMatch? TryExtractLines(string[] lines)
    {
        var java = TryJava(lines);
        if (java.HasValue) return java;
        var native = TryNative(lines);
        if (native.HasValue) return native;
        return TryAnr(lines);
    }

    // ── Java 崩溃 ──
    static CrashMatch? TryJava(string[] lines)
    {
        int head = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("FATAL EXCEPTION:")) { head = i; break; }
        }
        if (head < 0) return null;

        // FATAL EXCEPTION 行之后 3 行内必须有 Process: 行 —— 防业务日志打印「FATAL EXCEPTION」字样的误报
        for (int j = head + 1; j <= head + 3 && j < lines.Length; j++)
        {
            var t = lines[j].TrimStart('\r', '\t', ' ');
            if (!t.StartsWith("Process: ", StringComparison.Ordinal)) continue;
            if (!t.Contains(", PID")) continue;

            string pkg = ExtractPkg(t);
            string exc = FindExceptionType(lines, j);
            return new CrashMatch(KindJava, exc, pkg);
        }
        return null;
    }

    /// <summary>「Process: com.example.app, PID: 869」→「com.example.app」。</summary>
    static string ExtractPkg(string processLine)
    {
        int start = processLine.IndexOf("Process: ", StringComparison.Ordinal);
        if (start < 0) return "";
        start += "Process: ".Length;
        int comma = processLine.IndexOf(',', start);
        string pkg = (comma >= 0 ? processLine[start..comma] : processLine[start..]).Trim();
        // 宽容校验：必须是包名形态（含点分段），否则视为未提取到
        return IsPackageName(pkg) ? pkg : "";
    }

    /// <summary>Process 行之后第一个非空行的首 token，须以已知命名空间开头，截到第一个 ':'。</summary>
    static string FindExceptionType(string[] lines, int processIdx)
    {
        for (int k = processIdx + 1; k < lines.Length && k <= processIdx + 4; k++)
        {
            var t = lines[k].TrimStart('\r', '\t', ' ');
            if (t.Length == 0) continue;
            int sp = IndexOfWhitespace(t);
            string token = sp >= 0 ? t[..sp] : t;
            foreach (var p in ExcPrefixes)
            {
                if (token.StartsWith(p, StringComparison.Ordinal))
                {
                    int colon = token.IndexOf(':');
                    return colon >= 0 ? token[..colon] : token;
                }
            }
            return "";   // 第一个非空行不是已知命名空间的异常类型 → 提取不到，别再往后凑
        }
        return "";
    }

    // ── Native / tombstone ──
    static CrashMatch? TryNative(string[] lines)
    {
        bool tombstoneHead = false;
        for (int i = 0; i < lines.Length; i++)
        {
            var t = lines[i].TrimStart('\r', '\t', ' ');
            if (!tombstoneHead && t.StartsWith("*** *** ***", StringComparison.Ordinal))
                tombstoneHead = true;

            string? sig = FindSignal(t);
            if (sig != null)
                return new CrashMatch(tombstoneHead ? KindTombstone : KindNative, sig, "");
        }
        return null;
    }

    /// <summary>在单行内找「signal N (SIGXXX)」，SIG 名须在白名单内。未命中返回 null。</summary>
    static string? FindSignal(string line)
    {
        int pos = 0;
        while ((pos = line.IndexOf("signal", pos, StringComparison.Ordinal)) >= 0)
        {
            // signal 之后：空白 + 数字 + 空白 + '(' + SIG 名 + ')'
            int p = pos + 6;
            while (p < line.Length && (line[p] == ' ' || line[p] == '\t')) p++;
            int digits = 0;
            while (p + digits < line.Length && line[p + digits] >= '0' && line[p + digits] <= '9') digits++;
            if (digits == 0) { pos += 6; continue; }
            p += digits;
            while (p < line.Length && (line[p] == ' ' || line[p] == '\t')) p++;
            if (p >= line.Length || line[p] != '(') { pos += 6; continue; }
            p++;
            int e = p;
            while (e < line.Length && line[e] != ')') e++;
            if (e >= line.Length) { pos += 6; continue; }
            string name = line[p..e];
            foreach (var s in Signals)
                if (string.Equals(name, s, StringComparison.Ordinal)) return name;
            pos += 6;
        }
        return null;
    }

    // ── ANR ──
    static CrashMatch? TryAnr(string[] lines)
    {
        foreach (var raw in lines)
        {
            var t = raw.TrimStart('\r', '\t', ' ');
            if (!t.StartsWith("ANR in ", StringComparison.Ordinal)) continue;
            string rest = t["ANR in ".Length..];
            int sp = IndexOfWhitespace(rest);
            string token = (sp >= 0 ? rest[..sp] : rest).Trim();
            if (IsPackageName(token))
                return new CrashMatch(KindAnr, "", token);
        }
        return null;
    }

    // ── 辅助 ──

    /// <summary>包名形态：字母开头、点分多段、每段字母数字下划线。</summary>
    static bool IsPackageName(string s)
    {
        if (s.Length < 3) return false;
        if (s[0] < 'a' || s[0] > 'z') return false;
        bool hasDot = false, segAlpha = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '.')
            {
                if (!segAlpha) return false;    // 点前必须有内容
                hasDot = true;
                segAlpha = false;
            }
            else if (char.IsLetterOrDigit(c) || c == '_')
            {
                segAlpha = true;
            }
            else return false;
        }
        return hasDot && segAlpha;
    }

    static int IndexOfWhitespace(string s)
    {
        for (int i = 0; i < s.Length; i++)
            if (s[i] == ' ' || s[i] == '\t' || s[i] == '\r') return i;
        return -1;
    }
}
