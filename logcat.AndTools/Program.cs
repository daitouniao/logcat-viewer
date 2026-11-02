using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using logcat.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace logcat.AndTools;

internal static class Program
{
    /// <summary>
    /// 安卓工具箱独立入口（design D3/D6/D8/D10）：与 logcat.exe 同目录分发、共享
    /// logcat.UiCore.dll 与三份配置文件；互斥量与唤醒事件均用自有名，与日志查看器并存。
    /// </summary>
    [STAThread]
    static void Main()
    {
        StartupLog.SessionStart("logcat-andtools", "startup-andtools.log");
        StartupLog.Write("[启动] Program.Main 开始");

        // 中文界面统一「微软雅黑 UI」，与 logcat 同口径（Segoe UI 无中文字形，高 DPI 下裁字）
        ApplicationConfiguration.Initialize();
        Application.SetDefaultFont(Loc.UiFont);
        Loc.SetDefaultFont(Loc.UiFont);

        // andtools 侧落盘分支（design D4）：旧单文件 settings.json 拆出 andtools.json 与
        // language.json（复制不移动，settings.json 本体归 logcat 分支清理）；已是新布局则幂等跳过
        var baseDir = AppContext.BaseDirectory;
        AndToolsMigration.EnsureSplit(
            Path.Combine(baseDir, "settings.json"),
            Path.Combine(baseDir, "andtools.json"),
            Path.Combine(baseDir, "language.json"));
        StartupLog.Write("[启动] 配置拆分检查完成");

        // 诊断日志走 Debug 输出由 logcat 侧接；工具箱先用空实现（AdbManager 只在告警/错误路径打日志）
        var manager = new AdbManager(NullLogger<AdbManager>.Instance);
        var selector = new DeviceSelector(manager);
        var form = new AndToolsMainForm(manager, selector);

        // 唤醒事件（design D8）：首实例创建并监听；二次启动拿互斥量失败后 Set 它再退出
        using var wakeEvent = new EventWaitHandle(
            initialState: false, mode: EventResetMode.AutoReset,
            name: SingleInstance.AndToolsWakeEventName);

        // 单实例守卫（design D6）：独立互斥量名，获取失败 = 工具箱已在运行
        using var singleInstance = new SingleInstance(SingleInstance.AndToolsMutexName);
        if (!singleInstance.TryAcquire())
        {
            StartupLog.Write("[启动] 已有工具箱实例在运行，发唤醒事件后退出");
            wakeEvent.Set();
            MessageBox.Show(Loc.T("安卓工具箱已在运行。"), AppInfo.Title,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // 首实例唤醒监听（design D8）：收到事件 → UI 线程上前置主窗体。
        // 后台线程常驻阻塞等待；句柄未就绪时丢弃该次唤醒（启动早期自唤属罕见竞态，不做排队）。
        var wakeListener = new Thread(() =>
        {
            while (wakeEvent.WaitOne())
            {
                if (form.IsHandleCreated && !form.IsDisposed)
                    form.BeginInvoke(() => form.WakeFromSecondInstance());
            }
        })
        {
            IsBackground = true,
            Name = "andtools-wake-listener",
        };
        wakeListener.Start();
        StartupLog.Write("[启动] 唤醒监听已就绪，准备启动主窗口");

        Application.Run(form);

        StartupLog.Write("[启动] Application.Run 退出");
    }
}
