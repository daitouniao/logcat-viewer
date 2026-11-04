using logcat.Models;
using logcat.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

// 有意同步阻塞（GetAwaiter().GetResult()）：tstrun 反射 runner 不 await 返回 Task 的
// 测试方法——写成 async Task 会被静默跳过、断言根本不执行（假绿）。
#pragma warning disable xUnit1031
namespace logcat.Tests;

/// <summary>
/// 设备选择逻辑 <see cref="DeviceSelector"/>（design D9）：刷新含自愈、自愈重启后重建设备监控、
/// 自愈失败清空不误报、监控事件驱动列表变化、选择态与序列号/RootMode 联动。
/// 经 <see cref="FakeAdbManager"/> 注入假实现：零触网、不派生 adb 进程。
/// </summary>
public class DeviceSelectorTests
{
    /// <summary>假 AdbManager：重写四个虚成员；监控事件由用例经 OnDevicesChanged 手动触发。</summary>
    class FakeAdbManager : AdbManager
    {
        public List<DeviceInfo> NextDevices { get; } = new();
        public AdbServerSelfHealResult NextHeal { get; set; } =
            new(Prompt: null, Restarted: false);

        public int ListCalls { get; private set; }
        public int StartMonitorCalls { get; private set; }
        public int RestartMonitorCalls { get; private set; }

        public FakeAdbManager() : base(NullLogger<AdbManager>.Instance) { }

        public override Task<List<DeviceInfo>> ListDevicesAsync()
        {
            ListCalls++;
            return Task.FromResult(NextDevices.ToList());
        }

        public override Task<AdbServerSelfHealResult> EnsureServerRunningAsync()
            => Task.FromResult(NextHeal);

        public override void StartMonitor() => StartMonitorCalls++;

        public override void RestartMonitor() => RestartMonitorCalls++;

        /// <summary>模拟监控线程收到设备列表（走受保护的 OnDevicesChanged 触发真实事件链）。</summary>
        public void RaiseDevicesChanged(params DeviceInfo[] devices)
            => OnDevicesChanged(devices.ToList());
    }

    static DeviceInfo Dev(string serial, RootMode mode = RootMode.None) => new()
    {
        Serial = serial,
        RootMode = mode,
    };

    // ── 刷新（含自愈）──

    [Fact]
    public void 刷新拉起监控_枚举设备_选中第一台()
    {
        using var mgr = new FakeAdbManager
        {
            NextDevices = { Dev("emu-5554"), Dev("SERIAL2") },
        };
        var sel = new DeviceSelector(mgr);

        sel.StartMonitor();
        sel.RefreshAsync().GetAwaiter().GetResult();

        Assert.Equal(1, mgr.StartMonitorCalls);
        Assert.Equal(1, mgr.ListCalls);
        Assert.Equal(0, mgr.RestartMonitorCalls);   // 未重启不重建
        Assert.Equal(2, sel.Devices.Count);
        Assert.Equal("emu-5554", sel.CurrentSerial);
        Assert.Equal(0, sel.SelectedIndex);
    }

    [Fact]
    public void 自愈重启后无条件重建设备监控()
    {
        using var mgr = new FakeAdbManager
        {
            NextHeal = new AdbServerSelfHealResult(Prompt: null, Restarted: true),
            NextDevices = { Dev("d1") },
        };
        var sel = new DeviceSelector(mgr);

        sel.RefreshAsync().GetAwaiter().GetResult();

        Assert.Equal(1, mgr.RestartMonitorCalls);
        Assert.Equal(0, mgr.StartMonitorCalls);     // StartMonitor 只由外部启动期调，Refresh 不代调
        Assert.Equal("d1", sel.CurrentSerial);
    }

    [Fact]
    public void 自愈失败触发HealFailed并清空列表_不误报无设备()
    {
        using var mgr = new FakeAdbManager
        {
            NextHeal = new AdbServerSelfHealResult(Prompt: "adb server 拉起失败", Restarted: false),
            NextDevices = { Dev("d1") },   // 不应被消费
        };
        var sel = new DeviceSelector(mgr);
        string? failedPrompt = null;
        sel.HealFailed += p => failedPrompt = p;

        sel.RefreshAsync().GetAwaiter().GetResult();

        Assert.Equal("adb server 拉起失败", failedPrompt);
        Assert.Empty(sel.Devices);                  // 清空，但不是「枚举到 0 台」
        Assert.Null(sel.CurrentSerial);
        Assert.Equal(0, mgr.ListCalls);             // 拉不起 server 直接短路，不做设备枚举
    }

    // ── 监控事件驱动 ──

    [Fact]
    public void 监控事件驱动列表变化_重选第一台并通知()
    {
        using var mgr = new FakeAdbManager();
        var sel = new DeviceSelector(mgr);
        int changes = 0;
        sel.DevicesChanged += _ => changes++;

        mgr.RaiseDevicesChanged(Dev("a"), Dev("b"));
        Assert.Equal(1, changes);
        Assert.Equal("a", sel.CurrentSerial);

        sel.SelectedIndex = 1;
        Assert.Equal("b", sel.CurrentSerial);

        mgr.RaiseDevicesChanged(Dev("c"));          // 列表重建后重选第一台
        Assert.Equal(2, changes);
        Assert.Equal(1, sel.Devices.Count);
        Assert.Equal("c", sel.CurrentSerial);
        Assert.Equal(0, sel.SelectedIndex);
    }

    // ── 选择态联动 ──

    [Fact]
    public void UI改选下标后_CurrentSerial与RootMode联动()
    {
        using var mgr = new FakeAdbManager
        {
            NextDevices = { Dev("plain"), Dev("rooted", RootMode.Su) },
        };
        var sel = new DeviceSelector(mgr);
        sel.RefreshAsync().GetAwaiter().GetResult();

        Assert.Equal("plain", sel.CurrentSerial);
        Assert.Equal(RootMode.None, sel.CurrentRootMode);

        sel.SelectedIndex = 1;
        Assert.Equal("rooted", sel.CurrentSerial);
        Assert.Equal(RootMode.Su, sel.CurrentRootMode);

        sel.SelectedIndex = 99;                     // 越界 → 无选择
        Assert.Null(sel.CurrentSerial);
        Assert.Equal(RootMode.None, sel.CurrentRootMode);
    }
}
