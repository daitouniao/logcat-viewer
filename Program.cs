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
            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();
            // 中文界面统一用「微软雅黑 UI」：默认的 Segoe UI 没有中文字形，高 DPI 缩放下
            // GDI 回退字体「测量宽度 ≠ 绘制宽度」，按钮/页签的中文会被裁成残缺字形
            // （实测 125% 缩放下「新建」只画出「新」）。改用原生中文字体后测量与绘制一致。
            Application.SetDefaultFont(new Font("Microsoft YaHei UI", 9F));
            Application.Run(new frmMain());
        }
    }
}