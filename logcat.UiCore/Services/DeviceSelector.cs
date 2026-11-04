using logcat.Models;

namespace logcat.Services;

/// <summary>
/// 设备选择逻辑（design D9）：持有 <see cref="AdbManager"/>，承载「自愈拉起 adb server →
/// 刷新设备列表 → 当前设备选择」的单份逻辑；两个 exe 各画各的下拉 UI，
/// 订阅 <see cref="DevicesChanged"/> 回填控件。沿 <see cref="DeviceOpsDialog"/> 的注入范式，
/// 不跨窗口类型抽象控件。
/// </summary>
public class DeviceSelector
{
    readonly AdbManager _manager;
    readonly List<DeviceInfo> _devices = new();

    int _selectedIndex = -1;

    /// <summary>设备列表变化（监控事件或刷新结果）。UI 据此回填下拉框；事件可能在后台线程触发。</summary>
    public event Action<IReadOnlyList<DeviceInfo>>? DevicesChanged;

    /// <summary>自愈失败：携带可直接展示的提示串（运行时数据消息，勿经 Loc.F 再格式化）。</summary>
    public event Action<string>? HealFailed;

    public DeviceSelector(AdbManager manager)
    {
        _manager = manager;
        _manager.DevicesChanged += (_, devices) => ApplyDevices(devices);
    }

    /// <summary>启动设备监控（透传 <see cref="AdbManager.StartMonitor"/>）。</summary>
    public void StartMonitor() => _manager.StartMonitor();

    public IReadOnlyList<DeviceInfo> Devices => _devices;

    /// <summary>当前选中项下标（-1 = 无选择）。UI 下拉联动时回写。</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set => _selectedIndex = value;
    }

    /// <summary>当前设备序列号；无选择或下标越界返回 null。</summary>
    public string? CurrentSerial =>
        _selectedIndex >= 0 && _selectedIndex < _devices.Count
            ? _devices[_selectedIndex].Serial
            : null;

    /// <summary>当前设备的 RootMode；无选择或找不到设备返回 <see cref="RootMode.None"/>。</summary>
    public RootMode CurrentRootMode
    {
        get
        {
            var serial = CurrentSerial;
            if (serial == null) return RootMode.None;
            return _devices.FirstOrDefault(d => d.Serial == serial)?.RootMode ?? RootMode.None;
        }
    }

    /// <summary>
    /// 刷新设备列表（含自愈）：adb server 未运行时自动拉起（与日志查看器同一自愈语义）；
    /// 拉不起触发 <see cref="HealFailed"/> 并清空列表，不误报「无设备」。
    /// </summary>
    public async Task RefreshAsync()
    {
        var heal = await _manager.EnsureServerRunningAsync();
        if (heal.Prompt is { } prompt)
        {
            Clear();
            HealFailed?.Invoke(prompt);
            return;
        }
        // 实证：server 被杀后存量监控是僵尸态（不抛异常也不再收事件），自愈拉起成功后无条件重建
        if (heal.Restarted) _manager.RestartMonitor();

        ApplyDevices(await _manager.ListDevicesAsync());
    }

    /// <summary>清空设备列表与选择（自愈失败与空列表分支共用）。</summary>
    public void Clear()
    {
        _devices.Clear();
        _selectedIndex = -1;
    }

    void ApplyDevices(List<DeviceInfo> devices)
    {
        _devices.Clear();
        _devices.AddRange(devices);
        // 列表重建后重选第一台；无设备保持无选择
        _selectedIndex = devices.Count > 0 ? 0 : -1;
        DevicesChanged?.Invoke(_devices);
    }
}
