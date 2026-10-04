using System;
using System.IO;

namespace logcat.Services;

/// <summary>
/// 轻量启动诊断日志，用于验证窗口几何（多屏）的保存/恢复是否生效。
/// 写到 exe 同目录的 Log\startup.log，每次启动追加一段，
/// 可直接打开文件对比「存了什么 / 读了什么 / 实际落到哪块屏」。
/// 任何异常都被吞掉，绝不能影响主流程。
/// </summary>
static class StartupLog
{
    static readonly string FilePath = Path.Combine(
        AppContext.BaseDirectory, "Log", "startup.log");

    public static void Write(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            File.AppendAllText(FilePath, $"[{ts}] {message}{Environment.NewLine}");
        }
        catch { /* 诊断日志决不能影响主流程 */ }
    }

    public static void SessionStart(string tag)
    {
        Write(new string('=', 64));
        Write($"=== {tag} 启动会话 @ {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
    }
}
