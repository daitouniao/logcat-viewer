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

    /// <summary>root 检测结果缓存（序列号 → 检测条目）。</summary>
    readonly Dictionary<string, RootDetectEntry> _rootCache = new();

    /// <summary>
    /// root 探测单条命令的超时秒数。AdvancedSharpAdbClient 的通道没有任何超时机制，
    /// 设备端 su 弹授权框等待用户操作时调用会无限阻塞，靠它兜底取消；
    /// 取 8 秒，覆盖 Magisk 默认授权等待，又不至于让设备枚举久等。
    /// </summary>
    const int RootProbeTimeoutSec = 8;

    public event EventHandler<List<DeviceInfo>>? DevicesChanged;

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

    /// <summary>
    /// 枚举设备并逐台检测 root。root 探测走带超时的异步通道（见 <see cref="TryProbeAsync"/>），
    /// 因此本方法天然异步——同步版本曾因 su 授权框挂起而卡死设备监控事件循环。
    /// </summary>
    public async Task<List<DeviceInfo>> ListDevicesAsync()
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
                    RootMode = (await DetectRootCachedAsync(d)).Mode,
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

    /// <summary>以 root 身份执行设备端命令：Su 模式包 su -c；Adbd 模式 shell 已是 uid=0，不包裹。</summary>
    public string ShellRoot(string serial, string cmd, int timeoutSec = 30) =>
        Shell(serial, BuildRootShellCmd(GetRootMode(serial), cmd), timeoutSec);

    /// <summary>
    /// 按设备 root 形态生成提权命令（纯函数，单测覆盖）：
    /// 仅 Su 模式需要 su -c 包裹；Adbd 模式 shell 天生 root，无 su 二进制的设备包了反而失败；
    /// None 模式没有 root 能力，只能原样执行。
    /// </summary>
    internal static string BuildRootShellCmd(RootMode mode, string cmd) =>
        mode == RootMode.Su ? $"su -c {ShellQuote(cmd)}" : cmd;

    /// <summary>查询设备的 root 形态；未检测过返回 <see cref="RootMode.None"/>。</summary>
    public RootMode GetRootMode(string serial) =>
        GetRootDetectEntry(serial)?.Mode ?? RootMode.None;

    /// <summary>查询设备的 root 检测缓存条目；未检测过返回 null。</summary>
    public RootDetectEntry? GetRootDetectEntry(string serial)
    {
        lock (_rootCache) return _rootCache.TryGetValue(serial ?? "", out var e) ? e : null;
    }

    /// <summary>清除指定设备的 root 检测缓存，下次枚举设备时重测。</summary>
    public void InvalidateRootCache(string serial)
    {
        lock (_rootCache) _rootCache.Remove(serial ?? "");
    }

    /// <summary>清除全部 root 检测缓存（手动刷新强制重测时使用）。</summary>
    public void ClearRootCache()
    {
        lock (_rootCache) _rootCache.Clear();
    }

    /// <summary>
    /// 失效指定设备的 root 缓存并重新枚举设备，结果经 <see cref="DevicesChanged"/> 通知订阅方
    /// （设备操作窗口的「重试 root 检测」入口走这里，主窗口的设备下拉与 [root] 标记随之刷新）。
    /// </summary>
    public async Task RedetectRootAsync(string serial)
    {
        InvalidateRootCache(serial);
        var devices = await ListDevicesAsync();
        DevicesChanged?.Invoke(this, devices);
    }

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
            }) ?? throw new InvalidOperationException("adb 启动失败");   // 异常消息按约定保持中文，不本地化
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
        // 共享模式放宽为 ReadWrite：外部进程（编辑器、下载中等）以写方式持有文件时也能上传，
        // 读到的可能只是写入中途的快照，对上传场景可接受。
        using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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

    async void OnDeviceEvent(object? sender, DeviceDataEventArgs e)
    {
        try
        {
            // root 探测是带超时的异步命令，必须 await 而非同步等待：
            // 在监控事件线程上同步执行时，设备端 su 授权框一挂起整个事件循环就停摆，
            // 后续所有设备增删事件全部无人处理
            var devices = await ListDevicesAsync();
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
    /// root 检测结果按序列号缓存。su -c 每执行一次手机上就会弹一次 root 授权，
    /// 是否重测按 Q5 修订范围决定（见 <see cref="ShouldRedetect"/>）：
    /// Su（已授权）与 None（su 阶段未确认，重测会重弹授权框）复用缓存，
    /// Adbd 型重测仅一条裸 id、无授权框，自动重测以跟随 adb root/unroot 的状态变化。
    /// </summary>
    async Task<RootDetectEntry> DetectRootCachedAsync(DeviceData device)
    {
        var serial = device.Serial ?? "";
        RootDetectEntry? cached;
        lock (_rootCache) _rootCache.TryGetValue(serial, out cached);
        if (!ShouldRedetect(cached)) return cached!;
        var entry = await DetectRootAsync(device, CancellationToken.None);
        lock (_rootCache) _rootCache[serial] = entry;
        return entry;
    }

    /// <summary>Q5 修订版重测范围：无缓存需首测；Adbd 型重测成本低（一条裸 id，无授权框）自动重测；
    /// Su 与 None 复用缓存，强制重测只走手动刷新确认 / 重试入口的逃生门。</summary>
    internal static bool ShouldRedetect(RootDetectEntry? cached) =>
        cached is null || cached.Mode == RootMode.Adbd;

    /// <summary>
    /// root 探测：先跑不带 su 的裸 id，shell 自身已是 uid=0 即 adbd root 模式（短路返回，
    /// 不对这类设备发出注定失败或多余的 su）；未探到再跑 su -c id 判断 su 提权。
    /// </summary>
    async Task<RootDetectEntry> DetectRootAsync(DeviceData device, CancellationToken ct)
    {
        var plain = await TryProbeAsync(device, "id", ct);
        if (plain?.Contains("uid=0") == true)
            return ClassifyRootOutput(plain, null);
        var su = await TryProbeAsync(device, "su -c id", ct);
        return ClassifyRootOutput(plain, su);
    }

    /// <summary>把裸 id 与 su -c id 的输出分类为检测条目（纯函数，单测覆盖）。</summary>
    internal static RootDetectEntry ClassifyRootOutput(string? plainIdOut, string? suIdOut)
    {
        // 裸 id 探得 uid=0：adbd 以 root 运行，无需 su
        if (plainIdOut?.Contains("uid=0") == true)
            return new RootDetectEntry(RootMode.Adbd, SuFailed: false);
        // su 输出 uid=0：su 提权可用
        if (suIdOut?.Contains("uid=0") == true)
            return new RootDetectEntry(RootMode.Su, SuFailed: false);
        // su 阶段没拿到任何输出：异常/超时（如授权框挂起后被取消），结果未确认
        if (suIdOut == null)
            return new RootDetectEntry(RootMode.None, SuFailed: true);
        // su 有输出但非 uid=0（su 不存在或被拒绝）：明确无 su 提权
        return new RootDetectEntry(RootMode.None, SuFailed: false);
    }

    /// <summary>
    /// 执行探测命令并返回完整输出；超时/取消/异常一律返回 null（对应「结果未确认」）。
    /// 必须用带 CancellationToken 的异步重载：通道无超时机制，同步调用在 su 授权框
    /// 挂起期间会无限阻塞，这里用 CTS 兜底取消。
    /// </summary>
    async Task<string?> TryProbeAsync(DeviceData device, string cmd, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(RootProbeTimeoutSec));
            var receiver = new ConsoleOutputReceiver();
            await _client.ExecuteRemoteCommandAsync(cmd, device, receiver, Encoding.UTF8, cts.Token);
            return receiver.ToString();
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("root 探测命令超时或被取消: {0} @ {1}", cmd, device.Serial);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogInformation("root 探测命令执行失败: {0} @ {1}: {2}", cmd, device.Serial, ex.Message);
            return null;
        }
    }

    public void Dispose()
    {
        StopMonitor();
    }
}
