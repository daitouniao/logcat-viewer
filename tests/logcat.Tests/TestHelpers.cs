using System.Text;

namespace logcat.Tests;

/// <summary>
/// 一次性临时日志文件。Dispose 时删除（必须在被测的 LogDocument 释放之后）。
/// </summary>
sealed class TempLogFile : IDisposable
{
    // 注意：这里的 Path 属性会遮蔽 System.IO.Path，静态初始化必须写全名
    static readonly string Root = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "logcat-tests");

    public string Path { get; }

    public TempLogFile(string content = "", bool withBom = false)
    {
        Directory.CreateDirectory(Root);
        Path = System.IO.Path.Combine(Root, Guid.NewGuid().ToString("N") + ".log");
        var bytes = new List<byte>();
        if (withBom) bytes.AddRange(new byte[] { 0xEF, 0xBB, 0xBF });
        bytes.AddRange(new UTF8Encoding(false).GetBytes(content));
        File.WriteAllBytes(Path, bytes.ToArray());
    }

    /// <summary>以 UTF-8 无 BOM 追加内容，模拟 logcat 流式写入。</summary>
    public void Append(string text) =>
        File.AppendAllText(Path, text, new UTF8Encoding(false));

    /// <summary>
    /// 就地覆写（不截断文件）。文件正被 mmap 打开时，FileMode.Create 会在 Windows 上
    /// 报「请求的操作无法在使用用户映射区域打开的文件上执行」，必须走这条路径。
    /// 写到末尾时若新内容更短，尾部会残留旧字节——用于「头部变化」这类等长场景。
    /// </summary>
    public void WriteInPlace(string text)
    {
        var bytes = new UTF8Encoding(false).GetBytes(text);
        using var fs = new FileStream(Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        fs.Write(bytes, 0, bytes.Length);
        fs.Flush();
    }

    /// <summary>整体重写并截断。调用前必须确保被测的 LogDocument 已 Close。</summary>
    public void Overwrite(string text) =>
        File.WriteAllBytes(Path, new UTF8Encoding(false).GetBytes(text));

    public long Length => new FileInfo(Path).Length;

    public void Dispose()
    {
        try { File.Delete(Path); } catch { /* 句柄未及时释放时留给系统清理 */ }
    }
}

/// <summary>收集进度回调，用于断言进度确实上报过。</summary>
sealed class ProgressRecorder : IProgress<(double pct, string msg)>
{
    public List<(double pct, string msg)> Items { get; } = new();

    public void Report((double pct, string msg) value) => Items.Add(value);
}

static class TestData
{
    /// <summary>threadtime 假日志行（单字符宽度字段，偏移量好数）。</summary>
    public static string Threadtime(string time, int pid, int tid, char level, string tag, string msg)
        => $"{time}  {pid}  {tid} {level} {tag}: {msg}\n";
}
