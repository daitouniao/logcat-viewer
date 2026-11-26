namespace Next.App.Storage;

/// <summary>
/// 诊断日志（design D7）：追加写、共享读写给多实例、20 MB 轮转保留一份旧档。
/// 任何写入失败都被吞掉——日志不可用不得影响功能。
/// </summary>
public static class StartupLog
{
    public const long DefaultLimitBytes = 20L * 1024 * 1024;

    static readonly object Gate = new();
    static string? _path;
    static long _limitBytes = DefaultLimitBytes;

    /// <summary>path 为 null（仅会话内）时所有写入静默丢弃；limitBytes 供测试注入。</summary>
    public static void Initialize(string? path, long limitBytes = DefaultLimitBytes)
    {
        lock (Gate)
        {
            _path = path;
            _limitBytes = limitBytes;
        }
    }

    public static void Write(string level, string message, Exception? error = null)
    {
        try
        {
            lock (Gate)
            {
                if (_path is null)
                {
                    return;
                }

                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                RotateIfNeeded();

                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} " +
                           $"[{Environment.ProcessId}:{Environment.CurrentManagedThreadId}] " +
                           $"{level} {message}{Environment.NewLine}";
                var text = error is null ? line : line + error + Environment.NewLine;

                using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream);
                writer.Write(text);
            }
        }
        catch (Exception)
        {
            // 日志失败不致命（spec next/app-storage：写入失败不得影响启动与功能）
        }
    }

    static void RotateIfNeeded()
    {
        var info = new FileInfo(_path!);
        if (!info.Exists || info.Length < _limitBytes)
        {
            return;
        }

        File.Move(_path!, _path + ".1", overwrite: true);
    }
}
