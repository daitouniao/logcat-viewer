using System.Reflection;
using System.Text;
using logcat.Controls;
using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// 等价性不变量（spec「命中结果与全量解析完全等价」）：sidecar 装载与从零全量解析在
/// 行数、8 列、三字典、崩溃事件、自然行边界上逐项一致；以及命中路径确实绕过分块编排、
/// 装载后立即按当前条件过滤（spec「命中后过滤立即生效」）。
/// </summary>
[Collection(WinFormsCollection.Name)]
public class SidecarEquivalenceTests
{
    static readonly BindingFlags NonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    static object? Fld(object o, string name) =>
        o.GetType().GetField(name, NonPublicInstance)!.GetValue(o);

    static int Gen(frmMain form) => (int)Fld(form, "_workerGen")!;

    static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    /// <summary>把 8 列逐行比对（等价性断言的公共部分）。</summary>
    static void AssertSameColumns(LogDocument whole, LogDocument loaded)
    {
        Assert.Equal(whole.RowCount, loaded.RowCount);
        for (int r = 0; r < whole.RowCount; r++)
        {
            Assert.Equal(whole.Offs[r], loaded.Offs[r]);
            Assert.Equal(whole.Lens[r], loaded.Lens[r]);
            Assert.Equal(whole.Moff[r], loaded.Moff[r]);
            Assert.Equal(whole.Minute[r], loaded.Minute[r]);
            Assert.Equal(whole.Pid[r], loaded.Pid[r]);
            Assert.Equal(whole.Tid[r], loaded.Tid[r]);
            Assert.Equal(whole.Lvl[r], loaded.Lvl[r]);
            Assert.Equal(whole.TagId[r], loaded.TagId[r]);
            Assert.Equal(whole.TagOf(r), loaded.TagOf(r));
        }
    }

    /// <summary>行数、8 列逐行一致（含大小与索引口径）。</summary>
    [Fact]
    public void SidecarLoad_MatchesFullBuild_RowForRow()
    {
        using var tmp = new TempLogFile(SidecarCacheTests.Sample());
        using var whole = LogDocument.Build(tmp.Path);
        var payload = whole.SnapshotIndex();
        using var loaded = LogDocument.LoadIndexed(tmp.Path, payload);

        Assert.True(loaded.LoadedFromSidecar);
        Assert.Equal(whole.Size, loaded.Size);
        Assert.Equal(whole.IndexedSize, loaded.IndexedSize);
        AssertSameColumns(whole, loaded);
    }

    /// <summary>三字典与崩溃事件表一致（含字典下标语义）。</summary>
    [Fact]
    public void SidecarLoad_MatchesFullBuild_DictsAndCrashes()
    {
        using var tmp = new TempLogFile(SidecarCacheTests.Sample());
        using var whole = LogDocument.Build(tmp.Path);
        Assert.NotEmpty(whole.Tags);
        Assert.NotEmpty(whole.Crashes);

        var payload = whole.SnapshotIndex();
        using var loaded = LogDocument.LoadIndexed(tmp.Path, payload);

        Assert.Equal(whole.Tags, loaded.Tags);
        Assert.Equal(whole.ExcTypes, loaded.ExcTypes);
        Assert.Equal(whole.Pkgs, loaded.Pkgs);
        Assert.Equal(whole.TagIndex, loaded.TagIndex);
        Assert.Equal(whole.ExcTypeIndex, loaded.ExcTypeIndex);
        Assert.Equal(whole.PkgIndex, loaded.PkgIndex);
        Assert.Equal(whole.Crashes, loaded.Crashes);
    }

    /// <summary>
    /// 自然行边界一致：逐行原文、头部时间戳、正文切片全部相同（含崩溃块这类
    /// 多行记录里被前瞻引用的行，装载后仍能取到相同字节）。
    /// </summary>
    [Fact]
    public void SidecarLoad_MatchesFullBuild_RecordBoundaries()
    {
        using var tmp = new TempLogFile(SidecarCacheTests.Sample());
        using var whole = LogDocument.Build(tmp.Path);
        var payload = whole.SnapshotIndex();
        using var loaded = LogDocument.LoadIndexed(tmp.Path, payload);

        for (int r = 0; r < whole.RowCount; r++)
        {
            Assert.Equal(Text(whole.LineBytes(r)), Text(loaded.LineBytes(r)));
            Assert.Equal(Text(whole.HeaderBytes(r)), Text(loaded.HeaderBytes(r)));
            Assert.Equal(Text(whole.MessageBytes(r)), Text(loaded.MessageBytes(r)));
        }

        // 崩溃事件引用的行号在两条路径上指向同一行原文
        Assert.Equal(whole.Crashes.Count, loaded.Crashes.Count);
        for (int i = 0; i < whole.Crashes.Count; i++)
        {
            Assert.Equal(whole.Crashes[i].Row, loaded.Crashes[i].Row);
            Assert.Equal(Text(whole.LineBytes(whole.Crashes[i].Row)),
                         Text(loaded.LineBytes(loaded.Crashes[i].Row)));
        }
    }

    /// <summary>
    /// 命中重开不进入分块编排：命中返回的文档标记为 sidecar 装载，且分块编排调用次数不增；
    /// 无缓存的文件仍走原路径（对照组）。
    /// </summary>
    [Fact]
    public void Reopen_CachedFile_SkipsChunkedPipeline()
    {
        using var tmp = new TempLogFile(SidecarCacheTests.Sample());
        using var uncached = new TempLogFile(SidecarCacheTests.Sample(3));
        using var storeDir = new TempDir();
        var store = new SidecarCacheStore(storeDir.Path);
        var identity = SidecarIdentity.Capture(tmp.Path);
        using (var doc = LogDocument.Build(tmp.Path))
            Assert.True(store.Save(tmp.Path, identity, identity, doc.SnapshotIndex()));

        using var ui = new UiThread();
        var form = ui.Invoke(() =>
        {
            var f = new frmMain();
            f._sidecarStore = store;
            return f;
        });
        try
        {
            int gen = ui.Invoke(() => Gen(form));
            var hit = ui.RunAsync(() => form.LoadLocalFileAsync(tmp.Path, gen, CancellationToken.None));

            Assert.NotNull(hit);
            Assert.True(hit!.LoadedFromSidecar);
            Assert.Equal(0, ui.Invoke(() => form.ChunkedLoadInvocations));
            Assert.True(ui.Invoke(() => form.LastSidecarLoadMs) > 0);
            ui.Invoke(() => hit.Dispose());

            // 对照组：无缓存的文件照旧走分块编排
            int gen2 = ui.Invoke(() => Gen(form));
            var chunked = ui.RunAsync(() => form.LoadLocalFileAsync(uncached.Path, gen2, CancellationToken.None));

            Assert.NotNull(chunked);
            Assert.False(chunked!.LoadedFromSidecar);
            Assert.Equal(1, ui.Invoke(() => form.ChunkedLoadInvocations));
            ui.Invoke(() => chunked.Dispose());
        }
        finally
        {
            ui.Invoke(() => form.Dispose());
        }
    }

    /// <summary>
    /// 命中装载完成后立即按当前条件过滤（用户上次会话改过的过滤条件无需再手动应用）。
    /// </summary>
    [Fact]
    public void Reopen_CachedFile_FilterAppliedImmediately()
    {
        using var tmp = new TempLogFile(SidecarCacheTests.Sample());
        using var storeDir = new TempDir();
        var store = new SidecarCacheStore(storeDir.Path);
        var identity = SidecarIdentity.Capture(tmp.Path);
        using (var doc = LogDocument.Build(tmp.Path))
            Assert.True(store.Save(tmp.Path, identity, identity, doc.SnapshotIndex()));

        using var ui = new UiThread();
        var form = ui.Invoke(() =>
        {
            var f = new frmMain();
            f._sidecarStore = store;
            // 引号包裹 = 单个短语（SplitTerms 语义），样本里只有一行含它（定长断言的前提）
            ((System.Windows.Forms.TextBox)Fld(f, "_edMsg")!).Text = "\"message body 11\"";
            return f;
        });
        try
        {
            int gen = ui.Invoke(() => Gen(form));
            var loaded = ui.RunAsync(() => form.LoadLocalFileAsync(tmp.Path, gen, CancellationToken.None));

            Assert.NotNull(loaded);
            Assert.True(loaded!.LoadedFromSidecar);
            Assert.True(loaded.RowCount > 1);

            var listView = ui.Invoke(() => (LogListView)Fld(form, "_listView")!);
            Assert.Equal(1, ui.Invoke(() => listView.RowCount));                       // 视图已是过滤结果
            Assert.Equal(loaded.RowCount, ui.Invoke(() => (int)Fld(form, "_filterDocRows")!));
            ui.Invoke(() => loaded.Dispose());
        }
        finally
        {
            ui.Invoke(() => form.Dispose());
        }
    }

    static readonly MethodInfo ApplyFilterMethod =
        typeof(frmMain).GetMethod("ApplyFilter", NonPublicInstance)!;

    /// <summary>在 UI 线程上跑一次全量过滤（private，反射调用后 await 真完成）。</summary>
    static async Task RunFilter(frmMain form)
    {
        var task = (Task)ApplyFilterMethod.Invoke(form, new object?[] { null, false })!;
        await task;
    }

    /// <summary>同上，返回可被 <see cref="UiThread.RunAsync{T}"/> 接受的泛型 Task。</summary>
    static async Task<object?> RunFilterOf(frmMain form)
    {
        await RunFilter(form);
        return null;
    }

    /// <summary>
    /// 零命中装载的绑定不变量：装载期过滤条件把新文件全滤掉时不产生任何发布操作，
    /// 文档仍必须接管并绑定视图。若只在发布路径绑定，文档会成为「当前文档」而视图始终无绑定，
    /// 此后任何过滤只改行数不补绑定——现场表现为「总 N 条 / 命中 N，但每行渲染为空」。
    /// </summary>
    [Fact]
    public void ZeroHitLoad_StillBindsDocumentToView()
    {
        using var tmp = new TempLogFile(SidecarCacheTests.Sample(3));
        using var storeDir = new TempDir();
        using var ui = new UiThread();
        var form = ui.Invoke(() =>
        {
            var f = new frmMain();
            f._sidecarStore = new SidecarCacheStore(storeDir.Path);
            // 装载期就带一个必不命中的关键词 → 全程零发布
            ((System.Windows.Forms.TextBox)Fld(f, "_edMsg")!).Text = "QqZzNoSuchTerm";
            return f;
        });
        try
        {
            int gen = ui.Invoke(() => Gen(form));
            var doc = ui.RunAsync(() => form.LoadLocalFileAsync(tmp.Path, gen, CancellationToken.None));

            Assert.NotNull(doc);
            var listView = ui.Invoke(() => (LogListView)Fld(form, "_listView")!);

            Assert.Equal(0, ui.Invoke(() => listView.RowCount));   // 确实零命中、零发布
            Assert.NotNull(ui.Invoke(() => Fld(form, "_doc")));    // 文档必须已接管（零发布也要接管）
            Assert.True(ui.Invoke(() => listView.Document != null),
                "装载完成但视图未绑定文档：后续过滤将只改行数、整屏空白");
            Assert.True(ui.Invoke(() => ReferenceEquals(listView.Document, Fld(form, "_doc"))));

            ui.Invoke(() => doc!.Dispose());
        }
        finally
        {
            ui.Invoke(() => form.Dispose());
        }
    }

    /// <summary>清掉过滤条件后再过滤：行必须真的上屏（绑定在装载时已就位，不依赖重新装载）。</summary>
    [Fact]
    public void ClearingFilterAfterZeroHitLoad_RendersRows()
    {
        using var tmp = new TempLogFile(SidecarCacheTests.Sample(3));
        using var storeDir = new TempDir();
        using var ui = new UiThread();
        var form = ui.Invoke(() =>
        {
            var f = new frmMain();
            f._sidecarStore = new SidecarCacheStore(storeDir.Path);
            ((System.Windows.Forms.TextBox)Fld(f, "_edMsg")!).Text = "QqZzNoSuchTerm";
            return f;
        });
        try
        {
            int gen = ui.Invoke(() => Gen(form));
            var doc = ui.RunAsync(() => form.LoadLocalFileAsync(tmp.Path, gen, CancellationToken.None));
            Assert.NotNull(doc);

            ui.Invoke(() => ((System.Windows.Forms.TextBox)Fld(form, "_edMsg")!).Text = "");   // 条件清零
            ui.RunAsync(() => RunFilterOf(form));

            var listView = ui.Invoke(() => (LogListView)Fld(form, "_listView")!);
            Assert.Equal(ui.Invoke(() => doc!.RowCount), ui.Invoke(() => listView.RowCount));
            Assert.True(ui.Invoke(() => ReferenceEquals(listView.Document, Fld(form, "_doc"))));

            ui.Invoke(() => doc!.Dispose());
        }
        finally
        {
            ui.Invoke(() => form.Dispose());
        }
    }

    /// <summary>
    /// 绑定丢失后过滤自愈：视图被清掉绑定（行数也清零）时，下一次过滤必须重新绑定当前文档，
    /// 而不是只塞回行数——这是「清条件仍空白」的直接成因。
    /// </summary>
    [Fact]
    public void Filter_RebindsViewWhenBindingLost()
    {
        using var tmp = new TempLogFile(SidecarCacheTests.Sample(3));
        using var storeDir = new TempDir();
        using var ui = new UiThread();
        var form = ui.Invoke(() =>
        {
            var f = new frmMain();
            f._sidecarStore = new SidecarCacheStore(storeDir.Path);
            return f;
        });
        try
        {
            int gen = ui.Invoke(() => Gen(form));
            var doc = ui.RunAsync(() => form.LoadLocalFileAsync(tmp.Path, gen, CancellationToken.None));
            Assert.NotNull(doc);

            var listView = ui.Invoke(() => (LogListView)Fld(form, "_listView")!);
            ui.Invoke(() => listView.SetDocument(null));   // 制造「有文档、无绑定」的历史现场

            ui.RunAsync(() => RunFilterOf(form));

            Assert.True(ui.Invoke(() => ReferenceEquals(listView.Document, Fld(form, "_doc"))),
                "过滤上屏前未补绑定");
            Assert.True(ui.Invoke(() => listView.RowCount) > 0);

            ui.Invoke(() => doc!.Dispose());
        }
        finally
        {
            ui.Invoke(() => form.Dispose());
        }
    }

    /// <summary>
    /// 最小 UI 线程（STA + 消息泵）：窗体必须在同一线程上创建并驱动。
    /// <para>
    /// 为什么需要：命中装载是 async 流程，<c>await</c> 续体会离开调用线程；
    /// 虚拟列表的 <c>SetRows</c> 必须落在控件线程上，直接在 xUnit 线程上驱动会跨线程死锁
    /// （表现为用例悬挂、无断言输出）。这里让窗体独占一个带消息泵的 STA 线程，
    /// 与生产环境的 UI 线程模型一致。
    /// </para>
    /// </summary>
    sealed class UiThread : IDisposable
    {
        readonly Thread _thread;
        readonly ManualResetEventSlim _ready = new();
        System.Windows.Forms.Form _host = null!;
        Exception? _startError;

        internal UiThread()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "test-ui" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _ready.Wait();
            if (_startError != null) throw new InvalidOperationException("UI 线程启动失败", _startError);
        }

        void Run()
        {
            try
            {
                _host = new System.Windows.Forms.Form
                {
                    ShowInTaskbar = false,
                    Opacity = 0,
                    Size = new System.Drawing.Size(1, 1),
                };
                _ = _host.Handle;                                  // 强制建句柄：BeginInvoke 依赖它
                SynchronizationContext.SetSynchronizationContext(
                    new System.Windows.Forms.WindowsFormsSynchronizationContext());
                _ready.Set();
                System.Windows.Forms.Application.Run(new System.Windows.Forms.ApplicationContext(_host));
            }
            catch (Exception ex)
            {
                _startError = ex;
                _ready.Set();
            }
        }

        internal void Invoke(Action action) => Invoke<object?>(() => { action(); return null; });

        internal T Invoke<T>(Func<T> func)
        {
            T result = default!;
            Exception? error = null;
            using var done = new ManualResetEventSlim();
            _host.BeginInvoke(new Action(() =>
            {
                try { result = func(); }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            }));
            done.Wait();
            if (error != null) throw new InvalidOperationException("UI 线程执行失败", error);
            return result;
        }

        /// <summary>在 UI 线程上启动异步操作，调用线程等待结果（UI 线程保持消息泵运转）。</summary>
        internal T RunAsync<T>(Func<Task<T>> op)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Invoke(() => { _ = Await(op, tcs); });
            return tcs.Task.GetAwaiter().GetResult();
        }

        static async Task Await<T>(Func<Task<T>> op, TaskCompletionSource<T> tcs)
        {
            try { tcs.SetResult(await op()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }

        public void Dispose()
        {
            try { _host.BeginInvoke(new Action(System.Windows.Forms.Application.ExitThread)); }
            catch { /* 线程已退出 */ }
            _thread.Join(3000);
            _ready.Dispose();
        }
    }
}
