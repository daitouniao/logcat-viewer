using System;
using System.IO;

namespace logcat.Services;

/// <summary>
/// 轻量启动诊断日志，用于验证窗口几何（多屏）的保存/恢复是否生效。
/// 写到 exe 同目录的 Log\startup.log；<see cref="SessionStart"/> 可指定本会话的日志文件
/// （如 andtools 的 startup-andtools.log），此后本进程的 Write 不传文件名也落该会话文件，
/// 调用方无需每处传参。每次启动追加一段，
/// 可直接打开文件对比「存了什么 / 读了什么 / 实际落到哪块屏」。
/// 任何异常都被吞掉，绝不能影响主流程。
/// </summary>
public static class StartupLog
{
    /// <summary>本会话的默认日志文件名（SessionStart 设定；未设定时 startup.log）。</summary>
    static string _sessionFile = "startup.log";

    static string FilePathFor(string? fileName) => Path.Combine(
        AppContext.BaseDirectory, "Log", fileName ?? _sessionFile);

    public static void Write(string message, string? fileName = null)
    {
        try
        {
            var filePath = FilePathFor(fileName);
            var dir = Path.GetDirectoryName(filePath)!;
            Directory.CreateDirectory(dir);
            var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            File.AppendAllText(filePath, $"[{ts}] {message}{Environment.NewLine}");
        }
        catch { /* 诊断日志决不能影响主流程 */ }
    }

    public static void SessionStart(string tag, string? fileName = null)
    {
        _sessionFile = fileName ?? "startup.log";
        Write(new string('=', 64));
        Write($"=== {tag} 启动会话 @ {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
    }
}
