using System.Runtime.InteropServices;

namespace logcat.Services;

/// <summary>
/// 带重试的剪贴板写入。
/// Windows 剪贴板是共享资源，其他进程（剪贴板管理器、远程桌面、输入法等）短暂持有它会
/// 导致 OpenClipboard 失败，WinForms 抛 ExternalException(0x800401D0)。
/// Clipboard.SetText 默认只重试有限次数，占用时间稍长就崩，这里加长重试窗口并吞掉最终失败。
/// </summary>
static class ClipboardHelper
{
    /// <summary>写文本到剪贴板。总重试窗口约 2 秒，全部失败返回 false（不抛异常）。</summary>
    public static bool SetText(string text)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                // SetDataObject 内部再对 OpenClipboard 做 5 次 x 50ms 的重试，外层最多补 8 次 x 200ms
                Clipboard.SetDataObject(text, true, 5, 50);
                return true;
            }
            catch (ExternalException) when (attempt < 8)
            {
                Thread.Sleep(200);
            }
            catch (ExternalException)
            {
                return false;
            }
        }
    }
}
