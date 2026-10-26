using logcat.Controls;
using logcat.Models;
using logcat.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// 面板基类的起始目录解析行为（change device-scoped-file-browser-path 任务 3.1–3.3）。
///
/// <para>
/// 覆盖：静默失败不进失败钩子 / 不留失败文案 / 不动路径栏与列表；
/// 报错模式走既有失败钩子；「用户最后导航目录」按来源区分更新。
/// </para>
/// </summary>
public class FilePaneStartDirTests
{
    /// <summary>
    /// 面板桩：可指定哪些路径列举失败；失败钩子只记录不弹框。
    /// </summary>
    sealed class StubPane : DeviceFilePane
    {
        Func<string, bool> _shouldFail = _ => false;
        public List<string> Attempts { get; } = new();
        public int FailHookCalls { get; private set; }
        public string? LastFailedPath { get; private set; }

        public StubPane(AdbManager manager) : base(manager, "stub", RootMode.None) { }

        /// <summary>指定哪些路径列举失败（供用例按需设置）。</summary>
        public void FailPaths(Func<string, bool> shouldFail) => _shouldFail = shouldFail;

        protected override Task<List<FileEntry>> ListAsync(string path, CancellationToken ct)
        {
            Attempts.Add(path);
            if (_shouldFail(path)) throw new InvalidOperationException("打不开 " + path);
            return Task.FromResult(new List<FileEntry> { new FileEntry { Name = "条目", IsDir = false } });
        }

        protected override void OnLoadFailed(string path, Exception ex)
        {
            FailHookCalls++;
            LastFailedPath = path;
        }

        public string StatusText => LblStat.Text;
        public string PathBoxText => EdPath.Text;
        public int ItemCount => List.Items.Count;
    }

    static AdbManager NewOfflineManager() => new(
        NullLogger<AdbManager>.Instance,
        statusProbe: () => false,
        startServer: () => Task.FromResult(0),
        startupStart: () => { },
        socketFactory: () => throw new InvalidOperationException("单测不应触网"));

    // ── 3.1 静默失败 vs 报错模式 ──

    [Fact]
    public async Task 静默失败时不进失败钩子且不留失败文案()
    {
        using var manager = NewOfflineManager();
        using var pane = new StubPane(manager);
        pane.FailPaths(p => p == "/bad");

        var outcome = await pane.TryOpenAsync("/bad", silent: true);

        Assert.Equal(TryOpenOutcome.Failed, outcome);
        Assert.Equal(0, pane.FailHookCalls);
        // 静默失败不写「加载失败」，面板状态标签停在解析期的占位文案
        Assert.NotEqual(Loc.T("加载失败"), pane.StatusText);
    }

    [Fact]
    public async Task 静默失败时不改动路径栏与列表()
    {
        using var manager = NewOfflineManager();
        using var pane = new StubPane(manager);
        pane.FailPaths(p => p == "/bad");

        await pane.TryOpenAsync("/bad", silent: true);

        Assert.Equal("", pane.PathBoxText);
        Assert.Equal(0, pane.ItemCount);
    }

    [Fact]
    public async Task 报错模式时走既有失败钩子()
    {
        using var manager = NewOfflineManager();
        using var pane = new StubPane(manager);
        pane.FailPaths(p => p == "/bad");

        var outcome = await pane.TryOpenAsync("/bad", silent: false);

        Assert.Equal(TryOpenOutcome.Failed, outcome);
        Assert.Equal(1, pane.FailHookCalls);
        Assert.Equal("/bad", pane.LastFailedPath);
    }

    [Fact]
    public async Task 尝试成功时提交与NavigateTo一致的界面状态()
    {
        using var manager = NewOfflineManager();
        using var pane = new StubPane(manager);

        var outcome = await pane.TryOpenAsync("/ok", silent: true);

        Assert.Equal(TryOpenOutcome.Succeeded, outcome);
        Assert.Equal("/ok", pane.PathBoxText);
        Assert.Equal(1, pane.ItemCount);
        // 成功落地后状态是结果文案，不是失败文案
        Assert.NotEqual(Loc.T("加载失败"), pane.StatusText);
    }

    // ── 3.2 「用户最后导航目录」按来源区分 ──

    [Fact]
    public void 手动导航成功后记为用户最后导航目录()
    {
        using var manager = NewOfflineManager();
        using var pane = new StubPane(manager);

        pane.NavigateTo("/ok");

        Assert.Equal("/ok", pane.LastUserNavPath);
    }

    [Fact]
    public void 手动导航到打不开的路径时该值保持为上一次成功导航的目录()
    {
        using var manager = NewOfflineManager();
        using var pane = new StubPane(manager);

        pane.NavigateTo("/ok");
        pane.FailPaths(p => p == "/bad");
        pane.NavigateTo("/bad");

        Assert.Equal("/ok", pane.LastUserNavPath);
    }

    [Fact]
    public async Task 尝试接口成功打开别的目录不改动用户最后导航目录()
    {
        using var manager = NewOfflineManager();
        using var pane = new StubPane(manager);

        pane.NavigateTo("/ok");
        await pane.TryOpenAsync("/other", silent: true);

        Assert.Equal("/ok", pane.LastUserNavPath);
    }

    [Fact]
    public void 从未手动导航过时该值为空()
    {
        using var manager = NewOfflineManager();
        using var pane = new StubPane(manager);

        Assert.Null(pane.LastUserNavPath);
    }
}
