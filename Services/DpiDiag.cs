namespace logcat.Services;

/// <summary>
/// 高 DPI 布局诊断：把窗口/控件的 DPI、字体与尺寸快照写入 startup.log，
/// 用于多屏不同缩放（如主屏 150%）下「控件显示不下/被裁切」的排查。
/// 只读不写 UI 状态，任何异常吞掉，不影响主流程。
/// </summary>
static class DpiDiag
{
    public static void Log(string tag, Control c)
    {
        try
        {
            StartupLog.Write($"[DPI] {tag}: dpi={c.DeviceDpi} font={c.Font.FontFamily.Name} {c.Font.SizeInPoints:F1}pt " +
                             $"bounds={c.Bounds} client={c.ClientSize}");
        }
        catch { /* 诊断不影响主流程 */ }
    }

    /// <summary>把一行文本追加进 [DPI] 诊断流（已含时间戳）。</summary>
    public static void Write(string message) => StartupLog.Write($"[DPI] {message}");
}
