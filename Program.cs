namespace logcat
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            logcat.Services.StartupLog.SessionStart("logcat");
            logcat.Services.StartupLog.Write("[启动] Program.Main 开始");

            // 中文界面统一用「微软雅黑 UI」：默认的 Segoe UI 没有中文字形，高 DPI 缩放下
            // GDI 回退字体「测量宽度 ≠ 绘制宽度」，按钮/页签的中文会被裁成残缺字形
            // （实测 125% 缩放下「新建」只画出「新」）。改用原生中文字体后测量与绘制一致。
            ApplicationConfiguration.Initialize();
            //字体随界面语言切换（中文必须用雅黑：Segoe UI 无中文字形，高 DPI 下会被裁字）
            Application.SetDefaultFont(logcat.Services.Loc.UiFont);
            logcat.Services.Loc.SetDefaultFont(logcat.Services.Loc.UiFont);
            logcat.Services.StartupLog.Write("[启动] 初始化完成，准备启动主窗口");

            // 单实例守卫（决策 D13，BREAKING 行为变更）：获取失败 → 提示「已在运行」后退出。
            // 守卫全程持有（using 挂在 Main 生命周期上，禁提前 Dispose），进程退出时系统回收。
            using var singleInstance = new logcat.Services.SingleInstance();
            if (!singleInstance.TryAcquire())
            {
                logcat.Services.StartupLog.Write("[启动] 已有实例在运行，退出");
                MessageBox.Show(logcat.Services.Loc.T("logcat 已在运行。"), logcat.Services.AppInfo.Title,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 启动后台清理（决策 D13：单实例前提下暂存根目录的残留必为死亡目录，
            // 无时间阈值全删；主界面启动前起线程，不阻塞 UI）
            var spoolCleanup = new System.Threading.Thread(
                () => logcat.Services.FileProxyService.CleanupStaleSpoolDirs())
            {
                IsBackground = true,
                Name = "spool-startup-cleanup",
            };
            spoolCleanup.Start();

            // 退出双保险之二（决策 D10）：进程退出事件杀拉起的代理进程树
            // （主窗关闭流程 OnFormClosing 是第一保险；此处兜异常退出路径）
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                logcat.Services.FileProxyService.KillLaunchedProcesses();

            Application.Run(new frmMain());

            logcat.Services.StartupLog.Write("[启动] Application.Run 退出");
        }
    }
}