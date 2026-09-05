using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;
using logcat.Models;
using Microsoft.Extensions.Logging;

namespace logcat.Services;

/// <summary>
/// ADB 设备管理：枚举、Shell、截图、文件传输。
/// 基于 AdvancedSharpAdbClient 封装。
/// </summary>
public sealed class AdbManager : IDisposable
{
    readonly AdbClient _client;
    readonly ILogger _logger;
    DeviceMonitor? _monitor;
    CancellationTokenSource? _monitorCts;

    /// <summary>root 支持检测结果缓存（序列号 → 是否支持）。</summary>
    readonly Dictionary<string, bool> _rootCache = new();

    public event EventHandler<List<DeviceInfo>>? DevicesChanged;
    public event EventHandler<string>? Error;

    public AdbManager(ILogger logger)
    {
        _logger = logger;
        _client = new AdbClient();
        try
        {
            AdbServer.Instance.StartServer("adb", false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("启动 ADB server 失败: {0}", ex.Message);
        }
    }

    // ── 设备枚举 ──

    public List<DeviceInfo> ListDevices()
    {
        var result = new List<DeviceInfo>();
        try
        {
            var devices = _client.GetDevices();
            foreach (var d in devices)
            {
                if (d.State != DeviceState.Online) continue;
                var info = new DeviceInfo
                {
                    Serial = d.Serial ?? "",
                    Model = GetModel(d),
                    State = d.State.ToString(),
                    IsRoot = DetectRootCached(d),
                };
                result.Add(info);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("枚举设备失败: {0}", ex.Message);
        }
        return result;
    }

    public Task<List<DeviceInfo>> ListDevicesAsync() =>
        Task.Run(ListDevices);

    // ── Shell ──

    public string Shell(string serial, string cmd, int timeoutSec = 30)
    {
        var device = FindDevice(serial);
        var receiver = new ConsoleOutputReceiver();
        _client.ExecuteRemoteCommand(cmd, device, receiver, Encoding.UTF8);
        return receiver.ToString();
    }

    public Task<string> ShellAsync(string serial, string cmd, int timeoutSec = 30) =>
        Task.Run(() => Shell(serial, cmd, timeoutSec));

    /// <summary>
    /// 用单引号包裹，内部的单引号按 '\'' 规则转义。
    /// 内层命令自身常带单引号（路径引用），不转义的话 su -c 只会收到被截断的前半段，
    /// 路径含空格时直接失效。
    /// </summary>
    public static string ShellQuote(string value) => "'" + (value ?? "").Replace("'", @"'\''") + "'";

    public string ShellRoot(string serial, string cmd, int timeoutSec = 30) =>
        Shell(serial, $"su -c {ShellQuote(cmd)}", timeoutSec);

    /// <summary>
    /// 执行设备端命令并逐行回传输出。logcat、top 这类一直不退出的命令可以边出边看，也能中途取消；
    /// onLine 请由调用方在 UI 线程构造 Progress&lt;string&gt;，回传会自动回到该线程。
    /// </summary>
    public Task<int> ShellLinesAsync(string serial, string cmd, IProgress<string>? onLine, CancellationToken ct) =>
        Task.Run(async () =>
        {
            var device = FindDevice(serial);
            int count = 0;
            await foreach (var line in _client.ExecuteRemoteEnumerableAsync(cmd, device, Encoding.UTF8, ct))
            {
                ct.ThrowIfCancellationRequested();
                onLine?.Report((line ?? "").TrimEnd('\r'));
                count++;
            }
            return count;
        }, ct);

    // ── 本机 adb 进程 ──

    /// <summary>与具体设备无关、不能带 -s 的 adb 子命令。</summary>
    static readonly HashSet<string> AdbHostCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "devices", "version", "help", "start-server", "kill-server", "connect", "disconnect", "pair",
    };

    /// <summary>
    /// 拼 adb 参数：需要设备的子命令自动补 -s，否则多设备时 adb 会报 “more than one device”。
    /// </summary>
    public static string AdbArgs(string command, string? serial)
    {
        var cmd = (command ?? "").Trim();
        if (cmd.Length == 0) return cmd;
        var head = cmd.Split([' ', '\t'], 2)[0];
        return !string.IsNullOrEmpty(serial) && !AdbHostCommands.Contains(head)
            ? $"-s {serial} {cmd}"
            : cmd;
    }

    /// <summary>
    /// 调用本机 adb 执行 adb 子命令：install / reboot / forward 这类走不了 shell 通道的命令用这里。
    /// stdout、stderr 逐行回传，取消（含超时）时结束整个进程树。
    /// </summary>
    public static async Task<int> RunAdbAsync(string arguments, IProgress<string>? onLine, CancellationToken ct)
    {
        Process p;
        try
        {
            p = Process.Start(new ProcessStartInfo("adb", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            }) ?? throw new InvalidOperationException("adb 启动失败");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"未能启动 adb 可执行文件（请确认 adb 已加入 PATH）：{ex.Message}");
        }

        using (p)
        using (ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } }))
        {
            p.OutputDataReceived += (_, e) => { if (e.Data != null) onLine?.Report(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine?.Report("[stderr] " + e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            try
            {
                await p.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // 进程已被结束，再等一次把缓冲中的输出读完，避免最后几行丢失
                try { await p.WaitForExitAsync(CancellationToken.None); } catch { }
                throw;
            }
            return p.ExitCode;
        }
    }

    // ── 截图 ──

    public Image? Screenshot(string serial)
    {
        var device = FindDevice(serial);
        var fb = _client.GetFrameBuffer(device);
        fb.Refresh();
        return FramebufferToImage(fb);
    }

    public Task<Image?> ScreenshotAsync(string serial) =>
        Task.Run(() => Screenshot(serial));

    static Bitmap? FramebufferToImage(Framebuffer fb)
    {
        var header = fb.Header;
        var data = fb.Data;
        if (data == null || data.Length == 0) return null;

        int w = (int)(int)header.Width;
        int h = (int)(int)header.Height;
        if (w <= 0 || h <= 0) return null;

        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var bmpData = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int srcStride = w * 4;
            int dstStride = bmpData.Stride;
            int copyLen = Math.Min(srcStride, dstStride);

            // Android framebuffer 为 RGBA 字节序，GDI+ 32bppArgb 内存布局为 BGRA，
            // 需交换每像素的 R/B 字节，否则红蓝通道互换（黄色会显示成偏蓝的青色）
            for (int y = 0; y < h; y++)
            {
                int rowOff = y * srcStride;
                for (int x = 0; x < w && rowOff + x * 4 + 3 < data.Length; x++)
                {
                    int i = rowOff + x * 4;
                    (data[i], data[i + 2]) = (data[i + 2], data[i]);
                }
            }

            for (int y = 0; y < h; y++)
            {
                int srcOff = y * srcStride;
                long dstOff = bmpData.Scan0.ToInt64() + (long)y * dstStride;
                if (srcOff + copyLen <= data.Length)
                    System.Runtime.InteropServices.Marshal.Copy(data, srcOff, new IntPtr(dstOff), copyLen);
            }
        }
        finally
        {
            bmp.UnlockBits(bmpData);
        }
        return bmp;
    }

    // ── 文件传输 ──

    public void Pull(string serial, string remotePath, Stream stream)
    {
        var device = FindDevice(serial);
        using var sync = new SyncService(new AdbSocket(), device);
        sync.Pull(remotePath, stream);
    }

    public Task PullAsync(string serial, string remotePath, Stream stream) =>
        Task.Run(() => Pull(serial, remotePath, stream));

    /// <summary>
    /// 上传时赋予设备端文件的权限模式：普通文件 + 0644。
    /// 必须显式指定——传 default(0) 会让文件权限变成 000，导致文件无法读取、也无法再下载回来。
    /// </summary>
    static readonly UnixFileStatus PushFileMode =
        UnixFileStatus.Regular
        | UnixFileStatus.UserRead | UnixFileStatus.UserWrite
        | UnixFileStatus.GroupRead
        | UnixFileStatus.OtherRead;

    public void Push(string serial, string localPath, string remotePath)
    {
        var device = FindDevice(serial);
        using var sync = new SyncService(new AdbSocket(), device);
        using var fs = File.OpenRead(localPath);
        sync.Push(fs, remotePath, PushFileMode, DateTimeOffset.Now);
    }

    public Task PushAsync(string serial, string localPath, string remotePath) =>
        Task.Run(() => Push(serial, localPath, remotePath));

    // ── 设备监控 ──

    public void StartMonitor()
    {
        if (_monitor != null) return;
        try
        {
            _monitorCts = new CancellationTokenSource();
            _monitor = new DeviceMonitor(new AdbSocket());
            _monitor.DeviceConnected += OnDeviceEvent;
            _monitor.DeviceDisconnected += OnDeviceEvent;
            _monitor.DeviceChanged += OnDeviceEvent;
            _ = _monitor.StartAsync(_monitorCts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("启动设备监控失败: {0}", ex.Message);
            _monitor = null;
        }
    }

    public void StopMonitor()
    {
        _monitorCts?.Cancel();
        if (_monitor != null)
        {
            _monitor.DeviceConnected -= OnDeviceEvent;
            _monitor.DeviceDisconnected -= OnDeviceEvent;
            _monitor.DeviceChanged -= OnDeviceEvent;
            _monitor.Dispose();
            _monitor = null;
        }
    }

    void OnDeviceEvent(object? sender, DeviceDataEventArgs e)
    {
        try
        {
            var devices = ListDevices();
            DevicesChanged?.Invoke(this, devices);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("设备变更通知处理失败: {0}", ex.Message);
        }
    }

    // ── 辅助 ──

    DeviceData FindDevice(string serial)
    {
        var devices = _client.GetDevices();
        var device = devices.FirstOrDefault(d => d.Serial == serial);
        if (device == null)
            throw new InvalidOperationException($"设备 {serial} 未连接");
        return device;
    }

    static string GetModel(DeviceData d)
    {
        if (!string.IsNullOrEmpty(d.Model))
            return d.Model.Replace("_", " ");
        return d.Serial ?? "";
    }

    /// <summary>
    /// root 支持检测结果按序列号缓存。
    /// su -c 每执行一次手机上就会弹一次 root 授权，设备列表每次刷新都重测
    /// 会让授权请求反复出现，因此每台设备只在首次遇到时检测一次。
    /// </summary>
    bool DetectRootCached(DeviceData device)
    {
        var serial = device.Serial ?? "";
        lock (_rootCache)
        {
            if (_rootCache.TryGetValue(serial, out var cached)) return cached;
        }
        var supported = DetectRoot(device);
        lock (_rootCache) _rootCache[serial] = supported;
        return supported;
    }

    bool DetectRoot(DeviceData device)
    {
        try
        {
            var receiver = new ConsoleOutputReceiver();
            _client.ExecuteRemoteCommand("su -c id", device, receiver, Encoding.UTF8);
            return receiver.ToString().Contains("uid=0");
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        StopMonitor();
    }
}
