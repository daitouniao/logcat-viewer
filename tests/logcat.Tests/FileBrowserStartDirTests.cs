using logcat.Controls;
using logcat.Forms;
using logcat.Models;
using logcat.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// 文件浏览窗体的起始目录解析与记忆写回（change device-scoped-file-browser-path 任务 4.1–4.2）。
///
/// <para>
/// 覆盖：记忆写入的是按设备记录集合（不再是全局单值字段）、起始目录降级不覆盖既有记录、
/// 未手动导航时不写回、本机侧写入取「用户最后导航目录」。
/// </para>
/// </summary>
public class FileBrowserStartDirTests
{
    /// <summary>设备面板桩：可指定哪些路径列举失败。</summary>
    sealed class StubDevicePane : DeviceFilePane
    {
        Func<string, bool> _shouldFail = _ => false;
        Action? _onListing;
        bool _fired;
        public List<string> Attempts { get; } = new();

        public StubDevicePane(AdbManager manager) : base(manager, "stub", RootMode.None) { }

        /// <summary>指定哪些路径列举失败（供用例按需设置）。</summary>
        public void FailPaths(Func<string, bool> shouldFail) => _shouldFail = shouldFail;

        /// <summary>设置列举途中触发的回调（只触发一次，用于模拟「解析期间用户手动导航」）。</summary>
        public void FireOnListing(Action onListing) => _onListing = onListing;

        protected override Task<List<FileEntry>> ListAsync(string path, CancellationToken ct)
        {
            Attempts.Add(path);
            if (_shouldFail(path)) throw new InvalidOperationException("打不开 " + path);
            if (!_fired) { _fired = true; _onListing?.Invoke(); }
            return Task.FromResult(new List<FileEntry>());
        }

        public string PathBoxText => EdPath.Text;
        public string StatusText => LblStat.Text;
    }

    static AdbManager NewOfflineManager() => new(
        NullLogger<AdbManager>.Instance,
        statusProbe: () => false,
        startServer: () => Task.FromResult(0),
        startupStart: () => { },
        socketFactory: () => throw new InvalidOperationException("单测不应触网"));

    /// <summary>本机侧用的真实临时目录（LocalFilePane 走真实文件系统，无法用桩替换）。</summary>
    static string NewLocalTempDir()
    {
        string dir = Path.Combine(TempRoot.Dir, "startdir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task 关闭时写的是按设备记录集合而不是全局单值字段()
    {
        using var manager = NewOfflineManager();
        var remote = new StubDevicePane(manager);
        using var dlg = new FileBrowserDialog(remote, new LocalFilePane(), "stub");
        using var file = new TempStoreFile(null);
        var settings = new AppSettings(file.Path);

        remote.NavigateTo("/data/local/tmp");
        await remote.RefreshAsync();          // 等这次列举落地，「用户最后导航目录」才写上
        dlg.SaveLastPaths(settings);

        // 写进了该设备的记忆记录
        Assert.Equal("/data/local/tmp", settings.GetDeviceLastDir("stub"));
        // 旧的全局单值字段已随本 change 移除，保存后的 JSON 里不该再有它
        Assert.DoesNotContain("LastRemotePath", file.Read());
    }

    [Fact]
    public async Task 起始目录降级不覆盖该设备既有记忆()
    {
        using var manager = NewOfflineManager();
        var remote = new StubDevicePane(manager);
        remote.FailPaths(p => p == "/old");
        using var dlg = new FileBrowserDialog(remote, new LocalFilePane(), "stub");
        using var file = new TempStoreFile(null);
        var settings = new AppSettings(file.Path);
        settings.RecordDeviceLastDir("stub", "/old");

        await dlg.ResolveStartDirsAsync(settings);
        dlg.SaveLastPaths(settings);

        // 记忆目录打不开 → 降级到存储根目录，但降级结果不写回记忆
        Assert.Equal("/old", settings.GetDeviceLastDir("stub"));
        Assert.Contains("/sdcard", remote.Attempts);
    }

    [Fact]
    public async Task 失败候选不产生任何界面变化最终落在成功候选()
    {
        using var manager = NewOfflineManager();
        var remote = new StubDevicePane(manager);
        remote.FailPaths(p => p == "/old");
        using var dlg = new FileBrowserDialog(remote, new LocalFilePane(), "stub");
        using var file = new TempStoreFile(null);
        var settings = new AppSettings(file.Path);
        settings.RecordDeviceLastDir("stub", "/old");

        await dlg.ResolveStartDirsAsync(settings);

        // 失败的候选不提交：当前目录与路径栏最终显示成功候选
        Assert.Equal("/sdcard", remote.CurrentPath);
        Assert.Equal("/sdcard", remote.PathBoxText);
        // 静默失败不留失败文案
        Assert.NotEqual(Loc.T("加载失败"), remote.StatusText);
    }

    [Fact]
    public async Task 解析期间用户手动导航则解析结果不覆盖用户操作()
    {
        using var manager = NewOfflineManager();
        var remote = new StubDevicePane(manager);
        // 列举途中用户手动导航到别处：解析结果必须作废（design D4）
        remote.FireOnListing(() => remote.NavigateTo("/user-picked"));
        using var dlg = new FileBrowserDialog(remote, new LocalFilePane(), "stub");
        using var file = new TempStoreFile(null);
        var settings = new AppSettings(file.Path);

        await dlg.ResolveStartDirsAsync(settings);

        // 解析没有覆盖用户当前状态
        Assert.Equal("/user-picked", remote.CurrentPath);
    }

    [Fact]
    public async Task 未手动导航时关闭不写回任何设备记忆()
    {
        using var manager = NewOfflineManager();
        var remote = new StubDevicePane(manager);
        using var dlg = new FileBrowserDialog(remote, new LocalFilePane(), "stub");
        using var file = new TempStoreFile(null);
        var settings = new AppSettings(file.Path);

        await dlg.ResolveStartDirsAsync(settings);
        dlg.SaveLastPaths(settings);

        Assert.Empty(settings.DeviceLastDirs);
    }

    [Fact]
    public async Task 本机侧写入取用户最后导航目录而非当前路径()
    {
        using var manager = NewOfflineManager();
        var remote = new StubDevicePane(manager);
        using var local = new LocalFilePane();
        using var dlg = new FileBrowserDialog(remote, local, "stub");
        using var file = new TempStoreFile(null);
        var settings = new AppSettings(file.Path);
        string dir = NewLocalTempDir();

        local.NavigateTo(dir);
        await local.RefreshAsync();
        dlg.SaveLastPaths(settings);

        Assert.Equal(dir, settings.LastLocalPath);
    }

    [Fact]
    public async Task 本机侧导航到驱动器列表时不写回保留旧值()
    {
        using var manager = NewOfflineManager();
        var remote = new StubDevicePane(manager);
        using var local = new LocalFilePane();
        using var dlg = new FileBrowserDialog(remote, local, "stub");
        using var file = new TempStoreFile(null);
        var settings = new AppSettings(file.Path) { LastLocalPath = @"D:\旧值" };

        // 空路径 = 本机驱动器列表
        local.NavigateTo("");
        await local.RefreshAsync();
        dlg.SaveLastPaths(settings);

        Assert.Equal(@"D:\旧值", settings.LastLocalPath);
    }
}
