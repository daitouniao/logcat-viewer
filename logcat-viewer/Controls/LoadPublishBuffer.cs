namespace logcat.Controls;

/// <summary>一次发布上屏操作：ReplaceAll=true 表示整表替换（加载期过滤条件变更后的重过滤结果），
/// 否则为尾部追加。</summary>
public readonly record struct LoadPublishOp(bool ReplaceAll, int[] Rows);

/// <summary>
/// 加载期发布节流缓冲（design 决策 8）：分块加载的 worker 逐块产出视图行，
/// 按 ~100 ms 时间片聚合一批上屏，避免逐块刷新的 UI 抖动；加载完成时无条件
/// 取走全部累积操作做最终整批发布；取消加载时整队丢弃（spec「取消加载整份清空」）。
/// 纯逻辑类（时钟可注入），线程安全：Add/ReplaceAll 供 worker 调用，
/// TakeIfDue/TakeAll 供 UI 线程调用。操作按入队顺序生效，替换与追加混排安全。
/// </summary>
public sealed class LoadPublishBuffer
{
    readonly int _windowMs;
    readonly object _gate = new();
    readonly Queue<LoadPublishOp> _ops = new();
    long _dueAt;                 // 当前节流窗口到期时刻（Environment.TickCount64 毫秒）；0 = 无待发批次
    readonly Func<long> _now;

    public LoadPublishBuffer(int windowMs = 100, Func<long>? now = null)
    {
        _windowMs = windowMs;
        _now = now ?? (() => Environment.TickCount64);
    }

    /// <summary>worker 线程：追加新发布的行。首个待发批次开启节流窗口，后续追加并入同一批。</summary>
    public void Add(int[] rows)
    {
        if (rows.Length == 0) return;
        lock (_gate)
        {
            _ops.Enqueue(new LoadPublishOp(false, rows));
            if (_dueAt == 0)
                _dueAt = _now() + _windowMs;
        }
    }

    /// <summary>worker 线程：整表替换（加载期过滤条件变更后的已发布前缀重过滤结果）。</summary>
    public void ReplaceAll(int[] rows)
    {
        lock (_gate)
        {
            _ops.Enqueue(new LoadPublishOp(true, rows));
            if (_dueAt == 0)
                _dueAt = _now() + _windowMs;
        }
    }

    /// <summary>
    /// UI 线程：窗口到期则一次性取走全部累积操作（触发一次视图刷新）；
    /// 窗口未到期或无待发时返回 null（不刷新）。
    /// </summary>
    public List<LoadPublishOp>? TakeIfDue()
    {
        lock (_gate)
        {
            if (_ops.Count == 0 || _now() < _dueAt) return null;
            return Take();
        }
    }

    /// <summary>UI 线程：无条件取走全部累积操作（加载完成的最终整批发布）。</summary>
    public List<LoadPublishOp> TakeAll()
    {
        lock (_gate)
        {
            return Take();
        }
    }

    List<LoadPublishOp> Take()
    {
        var list = new List<LoadPublishOp>(_ops);
        _ops.Clear();
        _dueAt = 0;
        return list;
    }
}
