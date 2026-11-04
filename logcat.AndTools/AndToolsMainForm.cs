using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using logcat.Forms;
using logcat.Models;
using logcat.Services;

namespace logcat.AndTools;

/// <summary>
/// 工具箱主窗体（design D7/D9）：DeviceOpsDialog 容器形态升格为独立应用主窗体——
/// 页签构建逻辑全部复用基类，窗体级差异（标题、自有设备选择 UI、窗口几何持久化）在本工程侧改。
/// 设备下拉/刷新走 UiCore 的 <see cref="DeviceSelector"/>（自愈语义与日志查看器同一份逻辑）；
/// 几何三键存 andtools.json（N11），与日志查看器 settings.json 的几何互不干扰。
/// 图标不显式设置：WinForms 未设 Form.Icon 时使用 exe 的应用图标（csproj ApplicationIcon）。
/// </summary>
public class AndToolsMainForm : DeviceOpsDialog
{
    readonly DeviceSelector _selector;
    readonly ToolStripComboBox _comboDevice = new() { Enabled = false, Width = 230, DropDownWidth = 320 };
    readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };

    public AndToolsMainForm(AdbManager manager, DeviceSelector selector)
        : base(manager, () => selector.CurrentSerial, () => selector.CurrentRootMode)
    {
        _selector = selector;

        Text = Loc.T("安卓工具箱");
        Loc.Bind(this, "安卓工具箱");

        // 自有设备选择 UI（design D9：两 exe 各画各的下拉）：刷新按钮 + 设备下拉，
        // 位于页签上方；自愈失败提示落在底部状态条
        var combo = _comboDevice;
        combo.SelectedIndexChanged += (_, _) =>
        {
            _selector.SelectedIndex = combo.SelectedIndex;
            OnDeviceChanged();   // 换设备时重建绑定设备的页签
        };
        var btnRefresh = Loc.Bind(new ToolStripButton("刷新设备", null,
            (_, _) => _ = RefreshDevicesAsync()), "刷新设备");
        var strip = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        strip.Items.AddRange(new ToolStripItem[]
        {
            btnRefresh,
            Loc.Bind(new ToolStripLabel("设备:"), "设备:"),
            combo,
        });
        Controls.Add(strip);
        Controls.Add(new StatusStrip { Items = { _status } });

        _selector.DevicesChanged += devices => SafeInvoke(() => OnDevicesChanged(devices));
        _selector.HealFailed += prompt => SafeInvoke(() =>
        {
            _selector.Clear();
            ClearDeviceCombo();
            ShowStatusRaw(prompt);
        });
    }

    /// <summary>启动设备监控并做首次刷新（Program 在 Show 前 await，自愈失败提示先于交互出现）。</summary>
    public async Task InitDevicesAsync()
    {
        _selector.StartMonitor();
        await RefreshDevicesAsync();
    }

    async Task RefreshDevicesAsync()
    {
        ShowStatusRaw(Loc.T("刷新中…"));
        try
        {
            await _selector.RefreshAsync();
            if (_selector.Devices.Count == 0)
                ShowStatusRaw(Loc.T("无设备"));
        }
        catch (Exception ex)
        {
            // 运行时数据消息走原样展示（Loc.F 会被异常消息里的花括号炸 FormatException）
            ShowStatusRaw(Loc.T("刷新失败: {0}").Replace("{0}", ex.Message));
        }
    }

    void OnDevicesChanged(IReadOnlyList<DeviceInfo> devices)
    {
        _comboDevice.Items.Clear();
        foreach (var d in devices)
            _comboDevice.Items.Add(d.ToString());
        if (devices.Count > 0)
        {
            // Items.Clear() 后 SelectedIndex 必为 -1，重选第一台（selector 已同步 SelectedIndex=0）
            _comboDevice.SelectedIndex = 0;
            ShowStatusRaw(string.Empty);
        }
        else
        {
            // Items.Clear() 后 SelectedIndex 归 -1，但 Text 会残留旧设备序列号，需显式清空
            _comboDevice.Text = string.Empty;
        }
        _comboDevice.Enabled = true;
    }

    /// <summary>清空设备下拉框：Items 之外必须显式清 Text（ComboBox.Items.Clear 后 Text 残留旧序列号）。</summary>
    void ClearDeviceCombo()
    {
        _comboDevice.Items.Clear();
        _comboDevice.Text = string.Empty;
    }

    void ShowStatusRaw(string message) => _status.Text = message;

    /// <summary>事件可能在后台线程触发（监控推送）；未建句柄时丢弃，启动早期竞态无 UI 可更新。</summary>
    void SafeInvoke(Action action)
    {
        if (!IsHandleCreated || IsDisposed) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        AndToolsWindowGeometry.Restore(this, AndToolsSettings.Default);
    }

    // 句柄就绪后再首次刷新：DevicesChanged 回填下拉要过 SafeInvoke（无句柄会被丢弃）
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _ = InitDevicesAsync();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        AndToolsWindowGeometry.Save(this, AndToolsSettings.Default);
        AndToolsSettings.Default.Save();
    }

    /// <summary>二次启动唤醒（design D8）：最小化先还原，再尽力前置，失败闪任务栏。</summary>
    internal void WakeFromSecondInstance()
    {
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Activate();
        if (IsHandleCreated)
            WakeUp.BringToFront(Handle);
    }
}
