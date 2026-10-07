using System.IO;
using System.Text;

namespace logcat.Tests;

/// <summary>临时文件的公共根目录：tests/logcat.Tests/tmp，由各 Temp* 帮助类共用。</summary>
static class TempRoot
{
    // 使用项目目录下的临时文件夹，避免 %TEMP% 在某些环境下被测试运行器沙盒限制
    static readonly string s_tempRoot = System.IO.Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..",  // 从 bin 回溯到项目根
        "tests", "logcat.Tests", "tmp");

    public static readonly string Dir = System.IO.Path.GetFullPath(s_tempRoot);
}

/// <summary>
/// 一次性临时日志文件。Dispose 时删除（必须在被测的 LogDocument 释放之后）。
/// </summary>
sealed class TempLogFile : IDisposable
{
    public string Path { get; }

    public TempLogFile(string content = "", bool withBom = false)
    {
        Directory.CreateDirectory(TempRoot.Dir);
        Path = System.IO.Path.Combine(TempRoot.Dir, Guid.NewGuid().ToString("N") + ".log");
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

/// <summary>
/// 收集进度回调，用于断言进度确实上报过。
///
/// <para>
/// <b>必须用线程安全的容器</b>：过滤/索引的进度回报走<code>Task.Run</code> 的工作线程
/// （<c>FilterEngine</c> / <c>LogDocument</c> 里多线程分段推进度），
/// 无锁 <c>List.Add</c> 会在并发写时抛「Collection was modified」。
/// 该异常<b>偶发</b>——只在恰好两个线程同时 Add 时出现，表现为测试"时好时坏"。
/// </para>
/// </summary>
sealed class ProgressRecorder : IProgress<(double pct, string msg)>
{
    readonly List<(double pct, string msg)> _items = new();

    public IReadOnlyList<(double pct, string msg)> Items
    {
        get { lock (_items) return _items.ToArray(); }
    }

    public void Report((double pct, string msg) value)
    {
        lock (_items) _items.Add(value);
    }
}

/// <summary>首次收到进度上报时取消令牌，用于验证长任务能中途取消（而不是跑完整份才抛）。</summary>
sealed class CancelOnFirstReport : IProgress<(double pct, string msg)>
{
    readonly CancellationTokenSource _cts;

    public CancelOnFirstReport(CancellationTokenSource cts) => _cts = cts;

    public int Count { get; private set; }

    public void Report((double pct, string msg) value)
    {
        Count++;
        if (Count == 1) _cts.Cancel();
    }
}

/// <summary>
/// 同 <see cref="CancelOnFirstReport"/>，但等到**第一次 pct &gt; 0** 的上报才取消，
/// 并记下那一刻的 pct。
/// 用来区分异常是「扫描途中抛出」还是「扫描完了在收尾处抛出」——
/// 后者 pct 已接近 1，前者必然远小于 1。
/// 注意不能用「第一次上报」：DoBuild 在扫描开始前就先Report((0.0, "扫描行…"))，
/// 那一刀取消掉的话异常由 DoBuild 收尾处的 ThrowIfCancellationRequested 抛出，
/// 跟扫描期检查点稀不稀疏无关，抓不住 % 5000 → % 1000000 的变异。
/// </summary>
sealed class CancelOnFirstMidScanReport : IProgress<(double pct, string msg)>
{
    readonly CancellationTokenSource _cts;

    public CancelOnFirstMidScanReport(CancellationTokenSource cts) => _cts = cts;

    /// <summary>触发取消的那次上报的 pct。</summary>
    public double CancelPct { get; private set; } = -1;

    /// <summary>收到的全部 pct，用于断言「没有跑到收尾阶段」。</summary>
    public List<double> Log { get; } = new();

    public int Count { get; private set; }

    public void Report((double pct, string msg) value)
    {
        Count++;
        Log.Add(value.pct);
        if (Count == 1) return;              // pct=0 那次是「扫描开始」信号，不 cancel
        if (CancelPct >= 0) return;         // 已取消过
        if (value.pct <= 0) return;

        CancelPct = value.pct;
        _cts.Cancel();
    }
}

static class TestData
{
    /// <summary>threadtime 假日志行（单字符宽度字段，偏移量好数）。</summary>
    public static string Threadtime(string time, int pid, int tid, char level, string tag, string msg)
        => $"{time}  {pid}  {tid} {level} {tag}: {msg}\n";
}

/// <summary>
/// 一次性临时文件，供存储类（CommandStore / FavoritesStore / AppSettings）的落盘测试使用，
/// 避免触碰 %LOCALAPPDATA% 下的真实数据文件。
/// content 传 null 表示「只给路径、不创建文件」，用于测「文件不存在」分支。
/// </summary>
sealed class TempStoreFile : IDisposable
{
    public string Path { get; }

    public TempStoreFile(string? content = "")
    {
        Directory.CreateDirectory(TempRoot.Dir);
        Path = System.IO.Path.Combine(TempRoot.Dir, Guid.NewGuid().ToString("N") + ".json");
        if (content != null)
            File.WriteAllText(Path, content, new UTF8Encoding(false));
    }

    public bool Exists => File.Exists(Path);

    public string Read() => File.ReadAllText(Path);

    public void Dispose()
    {
        try { File.Delete(Path); } catch { /* 句柄未及时释放时留给系统清理 */ }
    }
}
