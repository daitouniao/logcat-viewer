using System;
using logcat.AndTools;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// 二次启动唤醒兜底判定（tasks 2.5 / design D8）：TrySetForegroundWindow 失败退 FlashWindow。
/// 原生调用以委托注入验证判定分支；真实前置/闪烁效果属 UI 前置行为，列手工验收清单。
/// </summary>
public class WakeUpTests : IDisposable
{
    readonly System.Collections.Generic.List<string> _calls = new();

    public WakeUpTests()
    {
        WakeUp.TrySetForegroundWindow = _ => { _calls.Add("set"); return _setResult; };
        WakeUp.FlashWindow = _ => _calls.Add("flash");
    }

    bool _setResult;

    readonly Func<IntPtr, bool> _origSet = WakeUp.TrySetForegroundWindow;
    readonly Action<IntPtr> _origFlash = WakeUp.FlashWindow;

    public void Dispose()
    {
        WakeUp.TrySetForegroundWindow = _origSet;
        WakeUp.FlashWindow = _origFlash;
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void 前置成功_不闪烁()
    {
        _setResult = true;

        WakeUp.BringToFront(IntPtr.Zero);

        Assert.Equal(new[] { "set" }, _calls);
    }

    [Fact]
    public void 前置失败_退任务栏闪烁兜底()
    {
        _setResult = false;

        WakeUp.BringToFront(IntPtr.Zero);

        Assert.Equal(new[] { "set", "flash" }, _calls);
    }
}
