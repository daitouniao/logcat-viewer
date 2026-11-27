namespace Next.App.Selection;

/// <summary>
/// 画布需要的「选中判定」能力（design D3）：画布只读，不感知选择集合的具体实现。
/// </summary>
public interface IRowSelection
{
    /// <summary>选择集合原地变化（增删 / 清空 / 整体替换）后触发。</summary>
    event Action? Changed;

    /// <summary>给定行序是否选中。</summary>
    bool Contains(long index);
}

/// <summary>闭区间 [Start, End]，行序 0 基。</summary>
internal readonly record struct RowRange(long Start, long End)
{
    internal long Length => End - Start + 1;
}

/// <summary>
/// 有序不重叠区间集（design D1）：Shift 选中百万级行只存边界，成员判定二分查找。
/// 区间始终按起点升序、互不重叠且互不相邻（相邻即合并）。
/// </summary>
internal sealed class RowSelection : IRowSelection
{
    readonly List<RowRange> _ranges = [];

    public event Action? Changed;

    /// <summary>选中记录数（合并记录按一条计）。</summary>
    public long Count { get; private set; }

    public bool IsEmpty => Count == 0;

    /// <summary>按行序升序的区间快照（复制按此顺序取行）。</summary>
    public IReadOnlyList<RowRange> OrderedRanges => _ranges;

    public bool Contains(long row)
    {
        int lo = 0, hi = _ranges.Count - 1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            var range = _ranges[mid];
            if (row < range.Start)
            {
                hi = mid - 1;
            }
            else if (row > range.End)
            {
                lo = mid + 1;
            }
            else
            {
                return true;
            }
        }
        return false;
    }

    public void Clear()
    {
        if (_ranges.Count == 0)
        {
            return;
        }
        _ranges.Clear();
        Count = 0;
        Changed?.Invoke();
    }

    public void ReplaceWith(long row) => ReplaceWithRange(row, row);

    public void ReplaceWithRange(long first, long last)
    {
        var (start, end) = Normalize(first, last);
        _ranges.Clear();
        _ranges.Add(new RowRange(start, end));
        Count = end - start + 1;
        Changed?.Invoke();
    }

    /// <summary>并入区间；与既有区间重叠或相邻时合并。</summary>
    public void AddRange(long first, long last)
    {
        var (start, end) = Normalize(first, last);
        InsertRange(start, end);
        RecomputeCount();
        Changed?.Invoke();
    }

    /// <summary>切换单行：未选中则加入，已选中则移除（移除中间行会拆分区间）。</summary>
    public void Toggle(long row)
    {
        var index = IndexOfContaining(row);
        if (index >= 0)
        {
            var range = _ranges[index];
            if (range.Start == range.End)
            {
                _ranges.RemoveAt(index);
            }
            else if (row == range.Start)
            {
                _ranges[index] = range with { Start = row + 1 };
            }
            else if (row == range.End)
            {
                _ranges[index] = range with { End = row - 1 };
            }
            else
            {
                _ranges[index] = new RowRange(range.Start, row - 1);
                _ranges.Insert(index + 1, new RowRange(row + 1, range.End));
            }
        }
        else
        {
            InsertRange(row, row);
        }
        RecomputeCount();
        Changed?.Invoke();
    }

    /// <summary>把 [start, end] 并入有序区间集，必要时合并相邻 / 重叠区间。</summary>
    void InsertRange(long start, long end)
    {
        var insertAt = 0;
        while (insertAt < _ranges.Count && _ranges[insertAt].End < start - 1)
        {
            insertAt++;
        }
        var mergedStart = start;
        var mergedEnd = end;
        var removeEnd = insertAt;
        while (removeEnd < _ranges.Count && _ranges[removeEnd].Start <= mergedEnd + 1)
        {
            mergedStart = Math.Min(mergedStart, _ranges[removeEnd].Start);
            mergedEnd = Math.Max(mergedEnd, _ranges[removeEnd].End);
            removeEnd++;
        }
        if (removeEnd > insertAt)
        {
            _ranges.RemoveRange(insertAt, removeEnd - insertAt);
        }
        _ranges.Insert(insertAt, new RowRange(mergedStart, mergedEnd));
    }

    int IndexOfContaining(long row)
    {
        int lo = 0, hi = _ranges.Count - 1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            var range = _ranges[mid];
            if (row < range.Start)
            {
                hi = mid - 1;
            }
            else if (row > range.End)
            {
                lo = mid + 1;
            }
            else
            {
                return mid;
            }
        }
        return -1;
    }

    void RecomputeCount()
    {
        long count = 0;
        foreach (var range in _ranges)
        {
            count += range.Length;
        }
        Count = count;
    }

    static (long Start, long End) Normalize(long first, long last) =>
        first <= last ? (first, last) : (last, first);
}
