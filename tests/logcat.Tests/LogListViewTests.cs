using logcat.Controls;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// LogListView 分段行号结构（chunked-progressive-load 任务 2.1，design 决策 6）：
/// AppendRows 只追加段/填尾段，不再整表拷贝；DocRow / ModelRowOf / NearestModelRow
/// 对外语义必须与原整表数组完全一致。
/// </summary>
public class LogListViewTests
{
    /// <summary>构造一条升序文档行号序列（非连续，模拟过滤结果）。</summary>
    static int[] Sequence(int count, int step = 3, int start = 7) =>
        Enumerable.Range(0, count).Select(i => start + i * step).ToArray();

    /// <summary>
    /// 分段结构与整表结构在 DocRow / ModelRowOf / NearestModelRow 上逐元素等价：
    /// 先 SetRows 一段、再连续 AppendRows（块长含「尾段放不下」「尾段放得下」「空追加」），
    /// 与一次 SetRows 的整表参照比对全部往返。
    /// </summary>
    [Fact]
    public void 分段结构与整表结构_ModelRowOf_DocRow_往返一致()
    {
        int[] whole = Sequence(100_000);
        var view = new LogListView();

        // 整表参照
        var refView = new LogListView();
        refView.SetRows(whole);

        // 分段构造：首段 1000，之后块长变化（137 小块走尾段余量、50_000 大块触发新段、空块跳过）
        view.SetRows(whole[..1000]);
        int pos = 1000;
        var rnd = new Random(20261101);
        while (pos < whole.Length)
        {
            int n = rnd.Next(1, 300);
            n = Math.Min(n, whole.Length - pos);
            view.AppendRows(whole[pos..(pos + n)]);
            pos += n;
            if (pos + 50_000 <= whole.Length) { view.AppendRows(whole[pos..(pos + 50_000)]); pos += 50_000; }
            view.AppendRows(Array.Empty<int>());
        }

        Assert.Equal(refView.RowCount, view.RowCount);

        // 往返 1：模型行 → 文档行号
        for (int mr = 0; mr < whole.Length; mr++)
            Assert.Equal(whole[mr], view.DocRow(mr));
        Assert.Equal(-1, view.DocRow(-1));
        Assert.Equal(-1, view.DocRow(whole.Length));

        // 往返 2：文档行号 → 模型行（含存在的与不存在的）
        for (int mr = 0; mr < whole.Length; mr++)
            Assert.Equal(mr, view.ModelRowOf(whole[mr]));
        Assert.Equal(-1, view.ModelRowOf(-1));
        Assert.Equal(-1, view.ModelRowOf(whole[0] - 1));          // 首行之前
        Assert.Equal(-1, view.ModelRowOf(whole[^1] + 1));         // 末行之后
        for (int mr = 0; mr < whole.Length - 1; mr++)
            Assert.Equal(-1, view.ModelRowOf(whole[mr] + 1));     // 段间空隙（序列步长 3）

        // NearestModelRow：对每个空隙与两端，与整表参照一致
        Assert.Equal(refView.NearestModelRow(whole[0] - 2), view.NearestModelRow(whole[0] - 2));
        Assert.Equal(refView.NearestModelRow(whole[^1] + 5), view.NearestModelRow(whole[^1] + 5));
        for (int mr = 0; mr < whole.Length - 1; mr++)
        {
            int gap = whole[mr] + 1;   // 空隙：前后等距（步长 3 时前后各差 1）
            Assert.Equal(refView.NearestModelRow(gap), view.NearestModelRow(gap));
            int gap2 = whole[mr] + 2;  // 空隙：后行更近
            Assert.Equal(refView.NearestModelRow(gap2), view.NearestModelRow(gap2));
        }
    }

    /// <summary>连续 AppendRows（多块、块长不一）的最终行号序列与一次 SetRows 完全一致。</summary>
    [Fact]
    public void 连续AppendRows与一次SetRows行号序列一致()
    {
        int[] whole = Sequence(50_000, step: 1, start: 0);
        var view = new LogListView();
        view.SetRows(whole[..17]);
        int pos = 17;
        var rnd = new Random(42);
        while (pos < whole.Length)
        {
            int n = Math.Min(rnd.Next(1, 5000), whole.Length - pos);
            view.AppendRows(whole[pos..(pos + n)]);
            pos += n;
        }

        Assert.Equal(whole.Length, view.RowCount);
        Assert.Equal(whole, view.GetRowsCopy());
    }

    /// <summary>边界：空表 SetRows / 单元素追加 / 重复 SetRows 覆盖。</summary>
    [Fact]
    public void 分段结构边界行为()
    {
        var view = new LogListView();
        Assert.Equal(0, view.RowCount);
        Assert.Equal(-1, view.DocRow(0));
        Assert.Equal(-1, view.ModelRowOf(0));
        Assert.Equal(-1, view.NearestModelRow(0));
        Assert.Empty(view.GetRowsCopy());

        view.AppendRows(Array.Empty<int>());   // 空追加不动
        Assert.Equal(0, view.RowCount);

        view.SetRows(new[] { 5 });
        Assert.Equal(1, view.RowCount);
        Assert.Equal(5, view.DocRow(0));
        Assert.Equal(0, view.ModelRowOf(5));
        Assert.Equal(0, view.NearestModelRow(999));   // 只有末行可近

        view.SetRows(new[] { 2, 9 });           // 重复 SetRows 覆盖旧段
        Assert.Equal(2, view.RowCount);
        Assert.Equal(-1, view.ModelRowOf(5));
        Assert.Equal(9, view.DocRow(1));
    }

    /// <summary>
    /// 加载期发布节流（任务 2.2，design 决策 8）：节流窗口内多次追加只触发一次视图刷新
    /// （TakeIfDue 非空一次、聚合全部行）；窗口未到期返回 null；TakeAll 无条件取走全部
    /// （最终整批发布）；ReplaceAll（加载期过滤条件变更）与追加按序混排。
    /// </summary>
    [Fact]
    public void 节流窗口内多次追加只触发一次视图刷新()
    {
        long now = 1_000;
        var buf = new LoadPublishBuffer(windowMs: 100, now: () => now);

        buf.Add(new[] { 1, 2 });      // 开启窗口
        buf.Add(new[] { 3 });         // 窗口内并入
        buf.Add(new[] { 4, 5, 6 });   // 窗口内并入
        Assert.Null(buf.TakeIfDue()); // 未到期：不刷新

        now += 99;
        Assert.Null(buf.TakeIfDue()); // 差 1 ms 仍未到期

        now += 1;
        var batch = buf.TakeIfDue();  // 到期：一次取走全部累积操作 = 一次视图刷新
        Assert.NotNull(batch);
        Assert.Equal(3, batch!.Count);            // 三次 Add 各一条操作，一次排空
        Assert.All(batch, op => Assert.False(op.ReplaceAll));
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 },
            batch.SelectMany(op => op.Rows).ToArray());
        Assert.Null(buf.TakeIfDue()); // 取走后无待发

        // 加载完成的最终整批发布：窗口未到期也无条件取走
        buf.Add(new[] { 7 });
        Assert.Null(buf.TakeIfDue());
        var all = buf.TakeAll();
        Assert.Single(all);
        Assert.Equal(new[] { 7 }, all[0].Rows);
        Assert.Empty(buf.TakeAll());

        // ReplaceAll 与追加按序混排（加载期过滤条件变更场景）
        buf.Add(new[] { 1 });
        buf.ReplaceAll(new[] { 2, 3 });
        buf.Add(new[] { 4 });
        var ops = buf.TakeAll();
        Assert.Equal(3, ops.Count);
        Assert.False(ops[0].ReplaceAll);
        Assert.True(ops[1].ReplaceAll);
        Assert.False(ops[2].ReplaceAll);
        Assert.Equal(new[] { 4 }, ops[2].Rows);
    }
}
