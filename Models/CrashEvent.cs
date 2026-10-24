using logcat.Services;

namespace logcat.Models;

/// <summary>崩溃类别。提取期由判据算出，非读 Sig。</summary>
public static class CrashKind
{
    public const byte Java = 0;
    public const byte Native = 1;
    public const byte Anr = 2;
    public const byte Tombstone = 3;

    /// <summary>类别显示名（进 Loc 表，随界面语言变化）。</summary>
    public static string Name(byte kind) => kind switch
    {
        Java => Loc.T("Java 崩溃"),
        Native => Loc.T("Native 崩溃"),
        Anr => Loc.T("ANR 应用无响应"),
        Tombstone => Loc.T("Native 崩溃（tombstone）"),
        _ => Loc.T("未知"),
    };
}

/// <summary>
/// 崩溃事件（稀疏存储，每条崩溃一行事件，约 40 B）。
/// 摘要行渲染与崩溃汇总窗口的统计都来自它。
/// </summary>
public struct CrashEvent
{
    /// <summary>崩溃父行行号（Join=true 下就是唯一行号）。文档行号，升序。</summary>
    public int Row;
    /// <summary>异常类型字典 id，-1 = 未提取到（如批次边界前瞻不全）。</summary>
    public int ExcId;
    /// <summary>包名字典 id，-1 = 未提取到。</summary>
    public int PkgId;
    /// <summary>崩溃父行自带的 PID / TID（logcat 头部，-1 = 未知）。</summary>
    public int Pid;
    public int Tid;
    /// <summary>崩溃类别，见 <see cref="CrashKind"/>。</summary>
    public byte Kind;
    /// <summary>合并行数 = joined 计数 + 1；「+N 行」展示 LineCount-1。</summary>
    public int LineCount;
}
