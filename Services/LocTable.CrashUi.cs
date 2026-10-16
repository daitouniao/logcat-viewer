namespace logcat.Services;

/// <summary>英文译文表：崩溃汇总窗口与崩溃摘要行。</summary>
static partial class LocTable
{
    public static Dictionary<string, string> CrashUi() => new Dictionary<string, string>()
    {
        // ── 窗口 ──
        ["崩溃汇总"] = "Crash Summary",
        ["崩溃汇总…"] = "Crash Summary…",
        ["无崩溃"] = "No crashes",

        // ── 列头（「包名」= Package /「次数」= Count 已在 ApkUi / CommandUiMain，勿重复定义）──
        ["类别"] = "Category",
        ["异常类型"] = "Exception Type",
        ["首次"] = "First Seen",
        ["末次"] = "Last Seen",

        // ── 类别名（CrashKind.Name）──
        ["Java 崩溃"] = "Java Crash",
        ["Native 崩溃"] = "Native Crash",
        ["ANR 应用无响应"] = "ANR (App Not Responding)",
        ["Native 崩溃（tombstone）"] = "Native Crash (tombstone)",
        ["未知"] = "Unknown",

        // ── 摘要行（☠ 是符号不是文案，可硬编码；文案走整串键）──
        ["☠ {0} · {1} · {2}"] = "☠ {0} · {1} · {2}",
        ["☠ {0} · {1} · {2} · +{3} 行"] = "☠ {0} · {1} · {2} · +{3} lines",

        // ── 跳转 ──
        ["该行不在当前过滤结果中"] = "Row not in current filter results",
    };
}
