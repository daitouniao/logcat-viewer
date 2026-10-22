using System.Windows.Forms;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// 「清空日志」按钮（change adb-buffer-manual-clear）与 adb server 自愈失败分支的
/// 设备列表清理（change fix-adb-server-self-heal）的纯逻辑覆盖。
///
/// <para>
/// <b>为什么单独成文件</b>：这两处此前<b>零自动化覆盖</b>——
///
/// <list type="bullet">
/// <item>spec「清除入口的可用性状态机」明确列了三个场景（无设备时禁用 / 采集中禁用 /
/// 停止后恢复），但全在 <c>frmMain</c> 的窗体胶水里，只能靠手工验收。</item>
/// <item>spec「拉起失败时 SHALL 清空设备列表」要求清空下拉框，而
/// <c>ComboBox.Items.Clear()</c> <b>不清 Text</b>（实测残留旧序列号）——
/// 这正是当初写 <c>_comboDevice.Text = string.Empty</c> 那行的原因，
/// 但没有用例钉住它：谁把那一行删了，测试不会红，UI 上会出现幽灵设备。</item>
/// </list>
///
/// <para>
/// 判据沿用仓库既有做法（见 <see cref="frmMainHelperTests"/>）：把窗体里的纯逻辑
/// 提取为 <c>internal static</c> 后直接单测，不构造窗体、不碰 STA 消息泵。
/// 控件（<see cref="ComboBox"/>）可在测试进程内构造并观察其 WinForms 行为，故直接用真控件。
/// </para>
/// </summary>
public class AdbClearLogButtonTests
{
    // ── 可用性状态机 ──

    /// <summary>
    /// 无设备连接时「清空日志」必须禁用——没有设备就没有缓冲区可清。
    /// </summary>
    [Theory]
    [InlineData(false, false)]   // 无设备、未采集
    [InlineData(false, true)]    // 无设备、采集中
    public void 无设备时清空日志按钮禁用(bool hasDevice, bool capturing)
    {
        Assert.False(frmMain.CanClearLog(hasDevice, capturing));
    }

    /// <summary>
    /// 采集中必须禁用：清空缓冲区会与采集流抢同一缓冲区（与「开始采集」同一条状态机）。
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    public void 采集中清空日志按钮禁用(bool hasDevice, bool capturing)
    {
        Assert.False(frmMain.CanClearLog(hasDevice, capturing));
    }

    /// <summary>有设备且未在采集时可用。</summary>
    [Fact]
    public void 有设备且未采集时清空日志按钮可用()
    {
        Assert.True(frmMain.CanClearLog(hasDevice: true, capturing: false));
    }

    /// <summary>
    /// 「清空日志」与「开始采集」必须遵循<b>同一条</b>状态机。
    /// </summary>
    /// <remarks>
    /// 两者都是对设备缓冲区的独占操作。spec「清除入口的可用性状态机」写的是
    /// 「与『开始采集』按钮遵循同一状态机」——若将来有人只改其中一个，
    /// 会出现「能开始采集但不能清空」这类不对称行为，本用例直接咬住。
    /// </remarks>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void 清空日志与开始采集同一条状态机(bool hasDevice, bool capturing)
    {
        Assert.Equal(frmMain.CanStartCapture(hasDevice, capturing), frmMain.CanClearLog(hasDevice, capturing));
    }

    /// <summary>
    /// 停止采集后按钮恢复可用（spec 场景「采集停止后恢复可用」）。
    /// </summary>
    [Fact]
    public void 停止采集后清空日志按钮恢复可用()
    {
        Assert.False(frmMain.CanClearLog(hasDevice: true, capturing: true));
        Assert.True(frmMain.CanClearLog(hasDevice: true, capturing: false));
    }

    // ── 设备列表清理 ──

    /// <summary>
    /// 清空下拉框后 <c>Text</c> 不得残留旧设备序列号。
    /// </summary>
    /// <remarks>
    /// <c>ComboBox.Items.Clear()</c> 只清条目、<c>SelectedIndex</c> 归 -1，
    /// 但 <c>Text</c> 会留着旧值（实测：Items.Clear 后 Text 仍是 "SERIAL123"）。
    /// 自愈失败时用它：若不显式清 Text，状态栏提示「adb 不可用、请手动处理」的同时，
    /// 设备框里还挂着旧序列号——用户会以为设备还连着。
    /// </remarks>
    [Fact]
    public void 清空设备下拉框后文本不残留旧序列号()
    {
        using var combo = new ToolStripComboBox();
        combo.Items.Add("SERIAL123");
        combo.SelectedIndex = 0;
        Assert.Equal("SERIAL123", combo.Text);   // 前置：确认确实选中了

        frmMain.ClearDeviceCombo(combo);

        Assert.Empty(combo.Items);
        Assert.Equal(string.Empty, combo.Text);
    }

    /// <summary>空下拉框上调用不抛异常（幂等）。</summary>
    [Fact]
    public void 清空空下拉框不抛异常()
    {
        using var combo = new ToolStripComboBox();
        frmMain.ClearDeviceCombo(combo);
        Assert.Empty(combo.Items);
        Assert.Equal(string.Empty, combo.Text);
    }

    /// <summary>
    /// 只清 Items <b>不</b>清 Text 时会残留——本用例记录 WinForms 的这个行为，
    /// 说明 <see cref="frmMain.ClearDeviceCombo"/> 里那行显式赋空为何不可省。
    /// </summary>
    /// <remarks>
    /// 这是一条「钉住反例」的用例：它断言的是 WinForms 控件自身的既有行为，
    /// 不是本项目的逻辑。若哪天 WinForms 改了行为（Items.Clear 顺带清 Text），
    /// 本用例会红——那时 <c>ClearDeviceCombo</c> 里的显式赋空就成了冗余，可一并清理。
    /// </remarks>
    [Fact]
    public void 仅清条目不清文本会残留_说明显式赋空不可省()
    {
        using var combo = new ToolStripComboBox();
        combo.Items.Add("SERIAL123");
        combo.SelectedIndex = 0;

        combo.Items.Clear();   // 只做这一步

        Assert.NotEqual(string.Empty, combo.Text);   // 残留旧序列号
    }
}
