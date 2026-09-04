using System.Text;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using Microsoft.Extensions.Logging;

namespace logcat.Services;

/// <summary>
/// 实时 logcat 采集：后台 Task 持续读取设备 logcat 输出，写入临时文件，
/// 批量通知 UI。行边界 flush，保证文件末尾停在完整行。
/// </summary>
public sealed class LogcatStream
{
    const int BATCH_LINES = 500;
    const double BATCH_INTERVAL = 0.15; // 秒

    readonly AdbManager _manager;
    readonly string _serial;
    readonly string _outputPath;
    readonly ILogger _logger;

    CancellationTokenSource? _cts;
    Task? _task;

    public event Action<List<string>>? LinesReceived;
    public event Action<string>? ErrorOccurred;
    public event Action? Stopped;

    public bool IsRunning => _task != null && !_task.IsCompleted;
    public string OutputPath => _outputPath;

    public LogcatStream(AdbManager manager, string serial, string outputPath, ILogger logger)
    {
        _manager = manager;
        _serial = serial;
        _outputPath = outputPath;
        _logger = logger;
    }

    public void Start(bool clearFirst = true)
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _task = Task.Run(() => RunAsync(clearFirst, _cts.Token));
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_task != null)
        {
            try { await _task; } catch { }
        }
        _cts?.Dispose();
        _cts = null;
        _task = null;
    }

    async Task RunAsync(bool clearFirst, CancellationToken ct)
    {
        _logger.LogInformation("LogcatStream 启动: serial={0}, clearFirst={1}", _serial, clearFirst);

        // 可选：清空缓冲区
        if (clearFirst)
        {
            try { await _manager.ShellAsync(_serial, "logcat -c", 5); }
            catch { /* 忽略 */ }
        }

        string cmd = "logcat -v threadtime";
        var lineCount = 0;

        try
        {
            var client = new AdbClient();
            var devices = client.GetDevices();
            var device = devices.FirstOrDefault(d => d.Serial == _serial);
            if (device == null)
            {
                ErrorOccurred?.Invoke($"设备 {_serial} 未连接");
                return;
            }

            // 打开输出文件
            using var fs = new FileStream(_outputPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(fs, Encoding.UTF8);

            var pending = new List<string>();
            var nextEmit = DateTimeOffset.UtcNow.AddSeconds(BATCH_INTERVAL);

            // 使用 ExecuteRemoteEnumerableAsync 逐行读取
            await foreach (var line in client.ExecuteRemoteEnumerableAsync(cmd, device, Encoding.UTF8, ct))
            {
                if (ct.IsCancellationRequested) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.StartsWith("---------") || line.StartsWith("beginning of") || line.StartsWith("switch to"))
                    continue;

                pending.Add(line);
                lineCount++;

                var now = DateTimeOffset.UtcNow;
                if (pending.Count >= BATCH_LINES || now >= nextEmit)
                {
                    await FlushAsync(pending, writer, fs);
                    LinesReceived?.Invoke(new List<string>(pending));
                    pending.Clear();
                    nextEmit = now.AddSeconds(BATCH_INTERVAL);
                }
            }

            // 最后 flush
            if (pending.Count > 0)
            {
                await FlushAsync(pending, writer, fs);
                LinesReceived?.Invoke(new List<string>(pending));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "采集过程中发生异常");
            if (!ct.IsCancellationRequested)
                ErrorOccurred?.Invoke($"采集异常：{ex.Message}");
        }
        finally
        {
            _logger.LogInformation("LogcatStream 结束: lines={0}", lineCount);
            Stopped?.Invoke();
        }
    }

    static async Task FlushAsync(List<string> lines, StreamWriter writer, FileStream fs)
    {
        if (lines.Count == 0) return;
        foreach (var line in lines)
            await writer.WriteLineAsync(line);
        await writer.FlushAsync();
        await fs.FlushAsync();
    }
}
