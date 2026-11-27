using Next.App.Selection;
using Xunit;

namespace Next.Tests;

/// <summary>区间选择集合契约（design D1）：单行替换、切换、区间合并、二分成员判定、升序输出。</summary>
public class SelectionRangeTests
{
    [Fact]
    public void ReplaceWithSingleRow()
    {
        var selection = new RowSelection();
        selection.ReplaceWith(5);
        Assert.Equal(1, selection.Count);
        Assert.True(selection.Contains(5));

        selection.ReplaceWith(9);
        Assert.Equal(1, selection.Count);
        Assert.False(selection.Contains(5));
        Assert.True(selection.Contains(9));
        var range = Assert.Single(selection.OrderedRanges);
        Assert.Equal((9L, 9L), (range.Start, range.End));
    }

    [Fact]
    public void ToggleRowAddsAndRemoves()
    {
        var selection = new RowSelection();
        selection.AddRange(10, 20);

        // 移除中间行 → 拆成两段
        selection.Toggle(15);
        Assert.Equal(10, selection.Count);
        Assert.Equal(2, selection.OrderedRanges.Count);
        Assert.False(selection.Contains(15));

        // 加回 → 合并回一段
        selection.Toggle(15);
        Assert.Equal(11, selection.Count);
        var range = Assert.Single(selection.OrderedRanges);
        Assert.Equal((10L, 20L), (range.Start, range.End));

        // 移除边界与单行区间
        selection.Toggle(10);
        Assert.Equal((11L, 20L), (selection.OrderedRanges[0].Start, selection.OrderedRanges[0].End));
        selection.Toggle(12);
        Assert.False(selection.Contains(12));
        Assert.Equal(9, selection.Count);
    }

    [Fact]
    public void AddRangeMergesOverlappingIntervals()
    {
        var selection = new RowSelection();
        selection.AddRange(10, 20);
        selection.AddRange(15, 25);
        var merged = Assert.Single(selection.OrderedRanges);
        Assert.Equal((10L, 25L), (merged.Start, merged.End));

        // 不相邻保持独立，相邻则合并（26 与 25 相邻）
        selection.AddRange(30, 40);
        Assert.Equal(2, selection.OrderedRanges.Count);
        selection.AddRange(26, 29);
        var whole = Assert.Single(selection.OrderedRanges);
        Assert.Equal((10L, 40L), (whole.Start, whole.End));
        Assert.Equal(31, selection.Count);
    }

    [Fact]
    public void ContainsUsesBinarySearchOnSortedIntervals()
    {
        var selection = new RowSelection();
        const int intervals = 1000;
        for (var i = 0; i < intervals; i++)
        {
            selection.AddRange(i * 10, i * 10);
        }
        Assert.Equal(intervals, selection.OrderedRanges.Count);
        for (var i = 0; i < intervals; i++)
        {
            Assert.True(selection.Contains(i * 10), $"应包含 {i * 10}");
            Assert.False(selection.Contains(i * 10 + 5), $"不应包含 {i * 10 + 5}");
        }
    }

    [Fact]
    public void OrderedRangesExposeAscendingRows()
    {
        var selection = new RowSelection();
        selection.AddRange(100, 110);
        selection.AddRange(5, 6);
        selection.AddRange(50, 51);

        var ranges = selection.OrderedRanges;
        Assert.Equal(new long[] { 5, 50, 100 }, ranges.Select(r => r.Start).ToArray());
        for (var i = 1; i < ranges.Count; i++)
        {
            Assert.True(ranges[i - 1].End < ranges[i].Start, "区间必须按行序升序且互不重叠");
        }
    }

    /// <summary>Shift 选中 200 万行只产生 1 个区间（不逐行分配）。</summary>
    [Fact]
    public void LargeShiftRangeStaysSingleInterval()
    {
        var selection = new RowSelection();
        selection.ReplaceWithRange(0, 2_000_000);
        var range = Assert.Single(selection.OrderedRanges);
        Assert.Equal((0L, 2_000_000L), (range.Start, range.End));
        Assert.Equal(2_000_001, selection.Count);
    }
}

/// <summary>选择手势契约（design D3）：锚点只由普通 / Ctrl 单击更新，Shift 始终以锚点重算。</summary>
public class SelectionGestureTests
{
    static (RowSelection Selection, SelectionGesture Gesture) Make()
    {
        var selection = new RowSelection();
        return (selection, new SelectionGesture(selection));
    }

    [Fact]
    public void PlainClickReplacesSelectionAndSetsAnchor()
    {
        var (selection, gesture) = Make();
        selection.AddRange(1, 3);

        Assert.True(gesture.Click(10, SelectionClickKind.Plain));
        Assert.Equal(10, gesture.Anchor);
        Assert.Equal(1, selection.Count);
        Assert.True(selection.Contains(10));
        Assert.False(selection.Contains(2));
    }

    [Fact]
    public void CtrlClickTogglesAndMovesAnchor()
    {
        var (selection, gesture) = Make();
        gesture.Click(4, SelectionClickKind.Plain);
        gesture.Click(7, SelectionClickKind.Toggle);
        Assert.Equal(7, gesture.Anchor);
        Assert.True(selection.Contains(4));
        Assert.True(selection.Contains(7));

        // 再 Ctrl 单击同一行 → 移除，锚点仍指向该行
        gesture.Click(7, SelectionClickKind.Toggle);
        Assert.Equal(7, gesture.Anchor);
        Assert.False(selection.Contains(7));
        Assert.True(selection.Contains(4));
    }

    [Fact]
    public void ShiftClickExtendsFromAnchorBothDirections()
    {
        var (selection, gesture) = Make();
        gesture.Click(10, SelectionClickKind.Plain);

        gesture.Click(5, SelectionClickKind.Extend);
        Assert.Equal(10, gesture.Anchor);
        Assert.Equal(6, selection.Count);
        Assert.True(selection.Contains(5) && selection.Contains(10));

        gesture.Click(15, SelectionClickKind.Extend);
        Assert.Equal(10, gesture.Anchor);
        Assert.Equal(6, selection.Count);
        Assert.True(selection.Contains(10) && selection.Contains(15));
        Assert.False(selection.Contains(5));
    }

    [Fact]
    public void RepeatedShiftClickKeepsOriginalAnchor()
    {
        var (selection, gesture) = Make();
        gesture.Click(10, SelectionClickKind.Plain);
        gesture.Click(5, SelectionClickKind.Extend);
        gesture.Click(8, SelectionClickKind.Extend);

        Assert.Equal(10, gesture.Anchor);
        Assert.Equal(3, selection.Count);
        var range = Assert.Single(selection.OrderedRanges);
        Assert.Equal((8L, 10L), (range.Start, range.End));
    }

    [Fact]
    public void ShiftClickWithoutAnchorFallsBackToPlainClick()
    {
        var (selection, gesture) = Make();
        Assert.True(gesture.Click(10, SelectionClickKind.Extend));
        Assert.Equal(10, gesture.Anchor);
        Assert.Equal(1, selection.Count);
        Assert.True(selection.Contains(10));
    }
}

/// <summary>指针命中契约（design D2）：换算行序，排除滚动条与留白，空文件无行。</summary>
public class SelectionHitTestTests
{
    [Fact]
    public void MapsOffsetToRow()
    {
        Assert.Equal(10, SelectionHitTest.RowAt(170, 0, 17, 100));
        Assert.Equal(10, SelectionHitTest.RowAt(170, 16, 17, 100));
        Assert.Equal(11, SelectionHitTest.RowAt(170, 17, 17, 100));
        Assert.Equal(0, SelectionHitTest.RowAt(0, 0, 17, 100));
    }

    [Fact]
    public void IgnoresScrollBarPress()
    {
        Assert.Null(SelectionHitTest.RowAt(0, 5, 17, 100, x: 820, viewportWidth: 800));
        Assert.Equal(0, SelectionHitTest.RowAt(0, 5, 17, 100, x: 799, viewportWidth: 800));
    }

    [Fact]
    public void OutOfRangeYieldsNoRow()
    {
        // 末行之后的留白
        Assert.Null(SelectionHitTest.RowAt(0, 60, 17, 3));
        // 空文件
        Assert.Null(SelectionHitTest.RowAt(0, 0, 17, 0));
        // 负坐标
        Assert.Null(SelectionHitTest.RowAt(0, -1, 17, 10));
    }

    [Fact]
    public void BlankAreaClickLeavesSelectionUnchanged()
    {
        var selection = new RowSelection();
        var gesture = new SelectionGesture(selection);
        gesture.Click(3, SelectionClickKind.Plain);

        var row = SelectionHitTest.RowAt(0, 200, 17, 10);   // 留白
        Assert.Null(row);
        Assert.False(gesture.Click(row, SelectionClickKind.Plain));
        Assert.False(gesture.Click(row, SelectionClickKind.Toggle));
        Assert.False(gesture.Click(row, SelectionClickKind.Extend));
        Assert.True(selection.Contains(3));
        Assert.Equal(1, selection.Count);
    }
}

/// <summary>双击打开详情契约（design D7）：普通双击单选并打开，修饰键双击只选择，留白不动作。</summary>
public class DetailOpenTests
{
    [Fact]
    public void PlainDoubleClickSelectsRowAndOpens()
    {
        var selection = new RowSelection();
        var gesture = new SelectionGesture(selection);
        selection.AddRange(0, 50);

        // 双击 = 两次普通单击
        gesture.Click(7, SelectionClickKind.Plain);
        gesture.Click(7, SelectionClickKind.Plain);

        Assert.Equal(1, selection.Count);
        Assert.True(selection.Contains(7));
        Assert.True(SelectionHitTest.ShouldOpenDetail(7, ctrl: false, shift: false));
    }

    [Fact]
    public void CtrlDoubleClickDoesNotOpen()
    {
        Assert.False(SelectionHitTest.ShouldOpenDetail(5, ctrl: true, shift: false));
    }

    [Fact]
    public void ShiftDoubleClickDoesNotOpen()
    {
        Assert.False(SelectionHitTest.ShouldOpenDetail(5, ctrl: false, shift: true));
    }

    [Fact]
    public void DoubleClickOnBlankDoesNothing()
    {
        var selection = new RowSelection();
        var gesture = new SelectionGesture(selection);
        gesture.Click(3, SelectionClickKind.Plain);

        Assert.False(SelectionHitTest.ShouldOpenDetail(null, ctrl: false, shift: false));
        Assert.False(gesture.Click(null, SelectionClickKind.Plain));
        Assert.True(selection.Contains(3));
        Assert.Equal(1, selection.Count);
    }
}
