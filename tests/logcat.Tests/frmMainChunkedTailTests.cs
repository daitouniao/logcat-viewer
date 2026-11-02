using System.Globalization;
using System.Reflection;
using System.Text;
using logcat.Controls;
using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// 分块加载收尾路径（spec「加载完成无内容滞留」「加载完成后状态栏统计反映最终值」）：
/// <b>无泵直驱</b>——测试线程反射调用 <c>RunChunkedLoadAsync</c> 并阻塞等待，不泵消息使
/// WinForms 节流定时器全程不 tick，精确复现「≤ 首块小文件加载在节流窗口到期前即完成、
/// 从未有过成功 flush」的缺陷场景（真实窗体实测该档修复前 100 % 空白）。
/// 无 SynchronizationContext 时 await 续体走线程池，阻塞等待无 STA 死锁；控件无句柄时
/// 数据操作跨线程安全（与 frmMainTests 同口径）。生产口径差异如实声明：收尾逻辑在测试中
/// 跑在线程池续体而非 UI 线程，被测面（取缓冲 → 应用 → 刷状态栏）与生产完全一致。
/// </summary>
public class frmMainChunkedTailTests
{
    static readonly BindingFlags NonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    static object? Fld(object o, string name) =>
        o.GetType().GetField(name, NonPublicInstance)!.GetValue(o);

    static frmMain NewForm() => new();

    /// <summary>无泵直驱：反射调用 RunChunkedLoadAsync 并同步等待其完成（收尾在 async 方法返回前执行完毕）。</summary>
    static void RunLoadToCompletion(frmMain form, string path)
    {
        var m = form.GetType().GetMethod("RunChunkedLoadAsync", NonPublicInstance)
                ?? throw new InvalidOperationException("RunChunkedLoadAsync 不存在");
        int gen = (int)Fld(form, "_workerGen")!;
        var task = (Task)m.Invoke(form, new object?[] { path, gen, CancellationToken.None })!;
        task.Wait();
    }

    static TempLogFile NewSmallLog(int lines)
    {
        // 行数远小于首块（4 MB）：整份文件一次发布即加载完成，节流窗口从未到期
        var sb = new StringBuilder();
        for (int i = 0; i < lines; i++)
            sb.Append(LogDocumentTests.Line("09-23 18:00:00.000", 'I', "Tag", $"msg {i}")).Append('\n');
        return new TempLogFile(sb.ToString());
    }

    /// <summary>加载完成后视图行数与文档行数一致（钉死「≤ 首块小文件 100 % 空白」档）。</summary>
    [Fact]
    public void 加载完成后视图行数与文档行数一致()
    {
        using var tmp = NewSmallLog(50);
        using var form = NewForm();
        RunLoadToCompletion(form, tmp.Path);

        Assert.False((bool)Fld(form, "_loading")!);          // 加载标志复位
        var doc = (LogDocument?)Fld(form, "_doc");
        Assert.NotNull(doc);                                  // 文档已接管
        var listView = (LogListView)Fld(form, "_listView")!;
        Assert.Equal(doc!.RowCount, listView.RowCount);       // 视图 == 文档
        Assert.True(listView.RowCount > 0);                   // 防空列表假阳性
    }

    /// <summary>加载完成后状态栏总数等于文档行数（钉死「中途快照 / 全空」档；数字两侧同文化口径，不断言文案）。</summary>
    [Fact]
    public void 加载完成后状态栏总数等于文档行数()
    {
        using var tmp = NewSmallLog(50);
        using var form = NewForm();
        RunLoadToCompletion(form, tmp.Path);

        Assert.False((bool)Fld(form, "_loading")!);          // 加载标志复位
        var doc = (LogDocument?)Fld(form, "_doc");
        Assert.NotNull(doc);                                  // 文档已接管
        var lblStat = (System.Windows.Forms.ToolStripItem)Fld(form, "_lblStat")!;
        string expected = doc!.RowCount.ToString("N0", CultureInfo.CurrentCulture);
        Assert.Contains(expected, lblStat.Text);              // 状态栏总数 == 文档行数
    }
}
