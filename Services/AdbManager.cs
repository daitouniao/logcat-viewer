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

    /// <summary>adb server 状态探测（生产实现走 <see cref="AdbServer.Instance"/> 的 GetStatus；测试可注入假实现）。</summary>
    readonly Func<bool> _statusProbe;

    /// <summary>adb server 拉起（生产实现走 <see cref="RunAdbAsync"/> 的 start-server；测试可注入假实现）。</summary>
    readonly Func<Task<int>> _startServer;

    /// <summary>设备监控的 socket 工厂（生产实现 new <see cref="AdbSocket"/>——构造即急切连接 5037，
    /// server 不在时抛 <see cref="System.Net.Sockets.SocketException"/>；测试可注入假 socket）。</summary>
    readonly Func<IAdbSocket> _socketFactory;

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
        : this(logger, ProbeAdbServerStatus, StartAdbServerAsync, StartupStartServer, DefaultSocketFactory)
    {
    }

    /// <summary>
    /// 测试注入构造：以假实现替换四个生产 seam（状态探测 / 自愈拉起 / 构造时拉起 / 监控 socket），
    /// 单测不触真实 <see cref="AdbServer"/>、不派生 adb 进程、零 5037 连接。
    /// 公共构造签名不变，生产行为与原实现逐字一致。
    /// </summary>
    internal AdbManager(
        ILogger logger,
        Func<bool> statusProbe,
        Func<Task<int>> startServer,
        Action startupStart,
        Func<IAdbSocket> socketFactory)
    {
        _logger = logger;
        _client = new AdbClient();
        _statusProbe = statusProbe;
        _startServer = startServer;
        _socketFactory = socketFactory;
        try
        {
            startupStart();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("启动 ADB server 失败: {0}", ex.Message);
        }
    }

    /// <summary>生产状态探测：<see cref="AdbServer.Instance"/> 的 GetStatus 双态均不抛（停止态 IsRunning=false）。</summary>
    static bool ProbeAdbServerStatus() => AdbServer.Instance.GetStatus().IsRunning;

    /// <summary>生产自愈拉起：<c>adb start-server</c> 子进程；退出码 ≠ 0 或抛异常都算失败
    /// （比库 StartServer 可靠：有退出码可判，异常自带 PATH 指引）。</summary>
    static Task<int> StartAdbServerAsync() => RunAdbAsync("start-server", null, CancellationToken.None);

    /// <summary>生产构造时拉起：沿用库原语（幂等，server 在跑时直接返回 OK），失败仅日志。</summary>
    static void StartupStartServer() => AdbServer.Instance.StartServer("adb", false);

    /// <summary>生产监控 socket：构造即急切连接 5037，server 不在时抛 SocketException（由 StartMonitor 的 catch 接住置空）。</summary>
    static IAdbSocket DefaultSocketFactory() => new AdbSocket();

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

    // ── adb server 自愈 ──

    /// <summary>
    /// 刷新设备前的 adb server 自愈：先探测，未运行则自动拉起，复核仍不可达返回手动处理提示。
    /// </summary>
    /// <remarks>
    /// 本方法 SHALL NOT 触及设备监控——是否重建监控由调用方按
    /// <see cref="AdbServerSelfHealResult.Restarted"/> 决定（解耦保证单测零触网）。
    /// <para>
    /// 提示串<b>引导语走本地化</b>（<see cref="Loc.F"/>），英文界面下不能显示中文；
    /// 尾部的<b>原因</b>（异常原文 / 退出码）按仓库约定保持原文不翻译——
    /// 那是 adb 自己抛的话，翻译它等于伪造上游信息。
    /// </para>
    /// <para>
    /// 原因里可能含花括号（如路径 <c>{0}</c>）：它是 <c>string.Format</c> 的<b>实参</b>而非格式串，
    /// 实参不做格式解析，故不会炸 <see cref="FormatException"/>。
    /// 调用方仍须走 <c>ShowStatusRaw</c> 而非 <c>ShowStatus</c>：后者会把整条提示当格式串再格式化一次。
    /// </para>
    /// </remarks>
    public async Task<AdbServerSelfHealResult> EnsureServerRunningAsync()
    {
        if (ProbeServerRunning())
            return new AdbServerSelfHealResult(Prompt: null, Restarted: false);

        string? reason = null;
        try
        {
            var exit = await _startServer();
            if (exit != 0) reason = $"adb start-server 退出码 {exit}";
        }
        catch (Exception ex)
        {
            reason = ex.Message;
        }

        if (reason == null)
        {
            // 拉起后复核：进程退出码 0 不等于 server 真的在听 5037
            if (ProbeServerRunning())
                return new AdbServerSelfHealResult(Prompt: null, Restarted: true);
            reason = "拉起后 adb server 仍不可达";
        }

        return new AdbServerSelfHealResult(
            Prompt: Loc.F("未能启动 adb server（请确认 adb 已加入 PATH，或手动执行 adb start-server）：{0}", reason),
            Restarted: false);
    }

    /// <summary>探测 server 是否在运行；探测本身抛异常按未运行处理（如 adb 不在 PATH 时 GetStatus 失败）。</summary>
    bool ProbeServerRunning()
    {
        try
        {
            return _statusProbe();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("探测 adb server 状态失败: {0}", ex.Message);
            return false;
        }
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

    /// <summary>
    /// 把同步通道自带的字节回调映射成项目惯用的进度形状（design D1/D5）：
    /// pct = 已传字节/总字节之比（经 <see cref="TransferProgress.PercentOf"/> 归一，总字节不可得时为 NaN 表示不确定态）；
    /// 不把库的回调类型外泄到上层。
    /// </summary>
    static Action<SyncProgressChangedEventArgs>? ByteCallback(IProgress<(double pct, string msg)>? progress) =>
        progress == null ? null : args =>
            progress.Report((TransferProgress.PercentOf((long)args.ReceivedBytesSize, (long)args.TotalBytesToReceive) ?? double.NaN, ""));

    public void Pull(string serial, string remotePath, Stream stream, IProgress<(double pct, string msg)>? progress = null)
    {
        var device = FindDevice(serial);
        using var sync = new SyncService(new AdbSocket(), device);
        var callback = ByteCallback(progress);
        if (callback == null)
        {
            sync.Pull(remotePath, stream);
            return;
        }
        bool isCancelled = false;
        sync.Pull(remotePath, stream, callback, false, ref isCancelled);
    }

    public Task PullAsync(string serial, string remotePath, Stream stream, IProgress<(double pct, string msg)>? progress = null) =>
        Task.Run(() => Pull(serial, remotePath, stream, progress));

    /// <summary>
    /// 上传时赋予设备端文件的权限模式：普通文件 + 0644。
    /// 必须显式指定——传 default(0) 会让文件权限变成 000，导致文件无法读取、也无法再下载回来。
    /// </summary>
    static readonly UnixFileStatus PushFileMode =
        UnixFileStatus.Regular
        | UnixFileStatus.UserRead | UnixFileStatus.UserWrite
        | UnixFileStatus.GroupRead
        | UnixFileStatus.OtherRead;

    public void Push(string serial, string localPath, string remotePath, IProgress<(double pct, string msg)>? progress = null)
    {
        var device = FindDevice(serial);
        using var sync = new SyncService(new AdbSocket(), device);
        // 共享模式放宽为 ReadWrite：外部进程（编辑器、下载中等）以写方式持有文件时也能上传，
        // 读到的可能只是写入中途的快照，对上传场景可接受。
        using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var callback = ByteCallback(progress);
        if (callback == null)
        {
            sync.Push(fs, remotePath, PushFileMode, DateTimeOffset.Now);
            return;
        }
        bool isCancelled = false;
        sync.Push(fs, remotePath, PushFileMode, DateTimeOffset.Now, callback, false, ref isCancelled);
    }

    public Task PushAsync(string serial, string localPath, string remotePath, IProgress<(double pct, string msg)>? progress = null) =>
        Task.Run(() => Push(serial, localPath, remotePath, progress));

    // ── 设备监控 ──

    public void StartMonitor()
    {
        if (_monitor != null) return;
        try
        {
            _monitorCts = new CancellationTokenSource();
            var monitor = new DeviceMonitor(_socketFactory());
            _monitor = monitor;
            monitor.DeviceConnected += OnDeviceEvent;
            monitor.DeviceDisconnected += OnDeviceEvent;
            monitor.DeviceChanged += OnDeviceEvent;
            var ct = _monitorCts.Token;
            // fire-and-forget 包 Task.Run：StartAsync 及监控循环的异常进不了上面的同步 catch
            // （实证：故障要等 Dispose 才抛），不接住就是未观察异常。OCE（StopMonitor 取消）静默；
            // 其余仅记日志——此处不置空 _monitor：僵尸态（server 被杀）下监控无异常也不再收事件，
            // 重建只走 RestartMonitor，且 OCE 冒泡时置空会误杀刚重建的新监控
            _ = Task.Run(async () =>
            {
                try { await monitor.StartAsync(ct); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _logger.LogWarning("设备监控运行失败: {0}", ex.Message); }
            });
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
            try
            {
                // 监控循环已故障时 DeviceMonitor.Dispose 会把故障异常再抛一遍（实证），
                // 不接住的话 RestartMonitor 将把异常抛进刷新流程
                _monitor.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("停止设备监控失败: {0}", ex.Message);
            }
            _monitor = null;
        }
    }

    /// <summary>
    /// 无条件重建设备监控（Stop + Start）。server 曾被杀后存量监控进入僵尸态——
    /// 不抛异常也不再收事件（实证），只能整体重建，不能以异常作为重建触发器。
    /// </summary>
    public void RestartMonitor()
    {
        StopMonitor();
        StartMonitor();
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
    /// 是否重测按 <see cref="ShouldRedetect"/> 判定：
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

    /// <summary>重测范围：无缓存需首测；Adbd 型重测成本低（一条裸 id，无授权框）自动重测；
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
