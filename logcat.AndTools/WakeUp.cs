using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace logcat.AndTools;

/// <summary>
/// 二次启动唤醒（design D8 降级版协议）的前置手段：TrySetForegroundWindow 失败退
/// FlashWindow（任务栏闪烁，不加提示文案）。原生调用以委托注入（internal 可换），
/// 「前置失败 → 闪烁」的判定可单测；真实前置效果列手工验收清单。
/// </summary>
internal static class WakeUp
{
    /// <summary>原生 SetForegroundWindow。internal 可注入点，测试换假件。</summary>
    internal static Func<IntPtr, bool> TrySetForegroundWindow = User32.SetForegroundWindow;

    /// <summary>原生 FlashWindowEx。internal 可注入点，测试换假件。</summary>
    internal static Action<IntPtr> FlashWindow = User32.FlashWindow;

    /// <summary>唤醒既有主窗体：前置失败则任务栏闪烁兜底。</summary>
    internal static void BringToFront(IntPtr handle)
    {
        if (!TrySetForegroundWindow(handle))
            FlashWindow(handle);
    }

    static class User32
    {
        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        internal static void FlashWindow(IntPtr hWnd)
        {
            var info = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd = hWnd,
                dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
                uCount = uint.MaxValue,
                dwTimeout = 0,
            };
            _ = FlashWindowEx(ref info);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    const uint FLASHW_ALL = 3;
    const uint FLASHW_TIMERNOFG = 12;
}
