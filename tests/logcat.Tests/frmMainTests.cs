using System.Reflection;
using logcat.Controls;
using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// 分块渐进加载的 frmMain 侧行为（chunked-progressive-load 任务 3.5 / 4.3）：
/// 加载期 UI 状态（导出 / 崩溃汇总置灰与恢复）、取消加载后整份清空。
/// 通过真实 frmMain 实例 + 内部方法驱动，不依赖异步时序（取消清空路径已从
/// RunChunkedLoadAsync 的 finally 提取为 CleanupAbortedLoad，生产路径即调用它）。
/// </summary>
public class frmMainTests
{
    static readonly BindingFlags NonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    static object? Fld(object o, string name) =>
        o.GetType().GetField(name, NonPublicInstance)!.GetValue(o);

    static void SetFld(object o, string name, object? value) =>
        o.GetType().GetField(name, NonPublicInstance)!.SetValue(o, value);

    static frmMain NewForm() => new();

    static void Call(object o, string name) =>
        o.GetType().GetMethod(name, NonPublicInstance)!.Invoke(o, Array.Empty<object>());

    /// <summary>加载期导出与崩溃汇总入口置灰，加载完成后恢复（spec「加载期 UI 状态」）。</summary>
    [Fact]
    public void 加载期导出与崩溃面板置灰加载完成后恢复()
    {
        using var form = NewForm();

        form.SetLoadUiState(true);
        Assert.False(((System.Windows.Forms.ToolStripMenuItem)Fld(form, "_actExport")!).Enabled);
        Assert.False(((System.Windows.Forms.ToolStripMenuItem)Fld(form, "_actExportMarked")!).Enabled);
        Assert.False(((System.Windows.Forms.ToolStripMenuItem)Fld(form, "_actCrashSummary")!).Enabled);

        form.SetLoadUiState(false);
        Assert.True(((System.Windows.Forms.ToolStripMenuItem)Fld(form, "_actExport")!).Enabled);
        Assert.True(((System.Windows.Forms.ToolStripMenuItem)Fld(form, "_actExportMarked")!).Enabled);
        Assert.True(((System.Windows.Forms.ToolStripMenuItem)Fld(form, "_actCrashSummary")!).Enabled);
    }

    /// <summary>
    /// 取消加载整份清空（spec「取消加载整份清空」）：加载中途取消后文档丢弃、
    /// 列表与过滤结果回空状态（行数为 0、过滤覆盖行数归零）、标记清空。
    /// 生产路径：RunChunkedLoadAsync 的 finally 在未完成时调用 CleanupAbortedLoad。
    /// </summary>
    [Fact]
    public void 取消加载后列表与过滤结果清空()
    {
        using var tmp = new TempLogFile(
            LogDocumentTests.Line("09-23 18:00:00.000", 'I', "Tag", "a") + "\n" +
            LogDocumentTests.Line("09-23 18:00:01.000", 'D', "Tag", "b") + "\n" +
            LogDocumentTests.Line("09-23 18:00:02.000", 'E', "Tag", "c"));
        using var doc = LogDocument.Build(tmp.Path);
        Assert.Equal(3, doc.RowCount);

        using var form = NewForm();
        // 模拟加载中途状态：文档已接管、视图已有过滤结果
        SetFld(form, "_doc", doc);
        SetFld(form, "_loadOwnedDoc", doc);
        var listView = (LogListView)Fld(form, "_listView")!;
        listView.SetDocument(doc);
        listView.SetRows(new[] { 0, 2 });          // 过滤后的视图（3 行文档里留 2 行）
        SetFld(form, "_filterDocRows", 3);
        var marked = (HashSet<int>)Fld(form, "_marked")!;
        marked.Add(1);

        Assert.Equal(2, listView.RowCount);

        // 取消清空：gen 取当前 worker 代（模拟同一代内的取消）
        int gen = (int)Fld(form, "_workerGen")!;
        form.CleanupAbortedLoad(doc, gen);

        Assert.Null(Fld(form, "_doc"));
        Assert.Equal(0, (int)Fld(form, "_filterDocRows")!);   // 过滤结果覆盖行数归零
        Assert.Equal(0, listView.RowCount);                    // 列表清空
        Assert.Equal(-1, listView.DocRow(0));                  // 行号换算回空状态
        Assert.Empty(marked);
        Assert.Null(Fld(form, "_loadOwnedDoc"));
    }

    /// <summary>取消清空的代际守卫：被新一代抢占（gen 不匹配）时不动当前状态（新加载接管）。</summary>
    [Fact]
    public void 取消清空不越代污染新加载状态()
    {
        using var tmp = new TempLogFile(LogDocumentTests.Line("09-23 18:00:00.000", 'I', "Tag", "a"));
        using var doc = LogDocument.Build(tmp.Path);

        using var form = NewForm();
        SetFld(form, "_doc", doc);
        SetFld(form, "_loadOwnedDoc", doc);
        var listView = (LogListView)Fld(form, "_listView")!;
        listView.SetDocument(doc);
        listView.SetRows(new[] { 0 });

        int staleGen = (int)Fld(form, "_workerGen")! - 1;      // 旧代
        form.CleanupAbortedLoad(doc, staleGen);

        Assert.Same(doc, Fld(form, "_doc"));                   // 新代文档未被误清
        Assert.Equal(1, listView.RowCount);
    }

    /// <summary>
    /// 关闭前清理后视图行数归零，销毁期不再逐行构造行对象（spec「关闭主窗口的耗时
    /// 不随视图行数增长」）。20 万虚拟行 + 逐行构造回调计数，不依赖计时。
    /// 必须先把控件句柄建出来（<c>_ = listView.Handle</c>）：没有句柄就没有窗口销毁流程，
    /// 枚举与回调都不会发生，用例会退化成空断言。未接清理时销毁期回调数等于行数
    /// （临时探针实测 200000），接上清理后为 0。
    /// </summary>
    [Fact]
    public void 关闭前清理后视图行数归零且销毁不再逐行构造()
    {
        var form = NewForm();
        var listView = (LogListView)Fld(form, "_listView")!;

        var rows = new int[200_000];
        for (int i = 0; i < rows.Length; i++) rows[i] = i;
        listView.SetRows(rows);
        Assert.Equal(200_000, listView.RowCount);
        _ = listView.Handle;                        // 建句柄：销毁期枚举的前提

        int constructed = 0;
        listView.RetrieveVirtualItem += (_, _) => constructed++;

        form.CleanupForClose();

        Assert.Equal(0, listView.RowCount);
        Assert.Equal(0, listView.VirtualListSize);

        form.Dispose();
        Assert.Equal(0, constructed);               // 销毁期一次也没有逐行构造
    }

    /// <summary>
    /// 清理后残留发布不回流（spec「关闭期间残留的视图刷新请求」）：清理时缓冲内还有未上屏的
    /// 发布、清理后 worker 仍向同一缓冲投递，视图都保持为空且不阻塞。
    /// </summary>
    [Fact]
    public void 关闭前清理后残留发布不回流视图()
    {
        var form = NewForm();
        var listView = (LogListView)Fld(form, "_listView")!;
        listView.SetRows(new[] { 0, 1, 2 });
        _ = listView.Handle;

        var buffer = new LoadPublishBuffer();
        SetFld(form, "_loadPub", buffer);
        buffer.Add(new[] { 0, 1 });                 // 清理前已投递、未上屏

        form.CleanupForClose();
        Assert.Null(Fld(form, "_loadPub"));

        buffer.Add(new[] { 0, 1, 2 });              // 清理后 worker 仍向原缓冲投递
        Call(form, "FlushLoadOps");                 // 迟到的节流 tick / 收尾强制消费

        Assert.Equal(0, listView.RowCount);
        Assert.Equal(0, listView.VirtualListSize);
        form.Dispose();
    }
}
