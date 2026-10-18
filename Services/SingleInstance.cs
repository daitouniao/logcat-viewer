namespace logcat.Services;

/// <summary>
/// 单实例守卫（决策 D13，BREAKING 行为变更）：`Local\` 前缀命名互斥量，全程持有禁提前 Dispose。
/// 获取失败 = 已有实例在运行，入口程序提示「已在运行」后退出。
/// 仅在一个进程内使用（不跨会话/跨用户），`Local\` 前缀即此语义。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>互斥量名。应用标识命名，`Local\` 前缀 = 本会话范围。</summary>
    public const string MutexName = @"Local\logcat-viewer-single-instance";

    Mutex? _mutex;

    /// <summary>是否已持有（TryAcquire 成功后为 true，Dispose 后回 false）。</summary>
    public bool Owned { get; private set; }

    /// <summary>
    /// 尝试获取单实例。已有实例持有时（另一进程在运行，或本守卫已持有）返回 false，
    /// 调用方提示后退出。其它进程异常退出时互斥量由系统回收，可正常获取。
    /// </summary>
    public bool TryAcquire()
    {
        if (Owned) return false;   // 已持有：再取必失败（真值表判据）
        var m = new Mutex(initiallyOwned: true, MutexName, out bool created);
        if (!created)
        {
            m.Dispose();
            return false;
        }
        _mutex = m;
        Owned = true;
        return true;
    }

    /// <summary>
    /// 释放互斥量。入口程序用 using 挂在 Main 生命周期上，正常路径只在进程退出时触发；
    /// 全程持有禁提前 Dispose（提前释放会让第二实例起来，违反单实例承诺）。
    /// </summary>
    public void Dispose()
    {
        if (_mutex != null)
        {
            try { if (Owned) _mutex.ReleaseMutex(); }
            catch (ApplicationException) { /* 持有线程异常退出等边缘态：忽略，互斥量随进程回收 */ }
            _mutex.Dispose();
            _mutex = null;
        }
        Owned = false;
    }
}
