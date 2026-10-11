using logcat.Models;

namespace logcat.Services;

/// <summary>崩溃聚合组（F-05）：同「类别 × 异常类型 × 包名」的崩溃事件归为一组。</summary>
/// <param name="Kind">类别（CrashKind 常量）。</param>
/// <param name="ExcType">异常类型 / 信号名；提取不到为空串（显示时回退 "-"）。</param>
/// <param name="Pkg">包名；Native 崩溃提不到（显示 "-"）。</param>
/// <param name="Count">该组崩溃次数。</param>
/// <param name="FirstTs">首次发生时间戳（毫秒，未知为 -1）。</param>
/// <param name="LastTs">末次发生时间戳。</param>
/// <param name="FirstRow">首次发生的文档行号（点击跳转落点）。</param>
public readonly record struct CrashGroup(
    byte Kind, string ExcType, string Pkg,
    int Count, long FirstTs, long LastTs, int FirstRow);

/// <summary>
/// 崩溃事件聚合（F-05）：纯函数，输入 <see cref="LogDocument"/> 的稀疏崩溃清单与两个字典，
/// 输出汇总行。不设组数上限——聚合结果全量列出（产品文档 §3.2 拍板）。
/// </summary>
public static class CrashAggregator
{
    /// <summary>
    /// 按 (Kind, ExcId, PkgId) 分组。排序：次数降序，同次数按首见时间升序
    /// （LINQ OrderBy 稳定，同键组保持首次出现顺序，结果确定可测）。
    /// </summary>
    public static List<CrashGroup> Aggregate(
        IReadOnlyList<CrashEvent> crashes,
        IReadOnlyList<string> excTypes,
        IReadOnlyList<string> pkgs)
    {
        var order = new List<CrashGroup>();
        var index = new Dictionary<(byte Kind, int ExcId, int PkgId), int>();

        foreach (var ce in crashes)
        {
            var key = (ce.Kind, ce.ExcId, ce.PkgId);
            if (index.TryGetValue(key, out int i))
            {
                var g = order[i];
                order[i] = g with { Count = g.Count + 1, LastTs = Math.Max(g.LastTs, ce.Ts) };
            }
            else
            {
                index[key] = order.Count;
                order.Add(new CrashGroup(
                    ce.Kind,
                    At(excTypes, ce.ExcId),
                    At(pkgs, ce.PkgId),
                    1, ce.Ts, ce.Ts, ce.Row));
            }
        }

        return order.OrderByDescending(g => g.Count).ThenBy(g => g.FirstTs).ToList();
    }

    static string At(IReadOnlyList<string> list, int id)
        => id >= 0 && id < list.Count ? list[id] : "";
}
