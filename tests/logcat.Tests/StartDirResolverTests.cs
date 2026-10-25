using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// 起始目录候选链与「首个可用候选」的纯逻辑（change device-scoped-file-browser-path 任务 2.1–2.2）。
///
/// <para>
/// 不依赖 WinForms：候选顺序、去重、跳过失败项、全失败这三条语义各自可独立验证。
/// </para>
/// </summary>
public class StartDirResolverTests
{
    // ── 候选链构造（任务 2.1）──

    [Fact]
    public void 设备侧候选链_有记忆时是记忆目录在前存储根目录在后()
    {
        var chain = StartDirResolver.BuildDeviceCandidates("/data/local/tmp");

        Assert.Equal(new[] { "/data/local/tmp", "/sdcard" }, chain);
    }

    [Fact]
    public void 设备侧候选链_无记忆时以存储根目录起头()
    {
        Assert.Equal(new[] { "/sdcard" }, StartDirResolver.BuildDeviceCandidates(null));
        Assert.Equal(new[] { "/sdcard" }, StartDirResolver.BuildDeviceCandidates(""));
    }

    [Fact]
    public void 设备侧候选链_记忆目录等于末位候选时不重复()
    {
        var chain = StartDirResolver.BuildDeviceCandidates("/sdcard");

        Assert.Equal(new[] { "/sdcard" }, chain);
    }

    [Fact]
    public void 本机侧候选链_有记忆时是记忆目录在前驱动器列表在后()
    {
        var chain = StartDirResolver.BuildLocalCandidates(@"D:\logs");

        Assert.Equal(2, chain.Count);
        Assert.Equal(@"D:\logs", chain[0]);
        Assert.Equal(StartDirResolver.LocalDriveList, chain[1]);
    }

    [Fact]
    public void 本机侧候选链_无记忆时以驱动器列表起头()
    {
        var chain = StartDirResolver.BuildLocalCandidates("");

        Assert.Single(chain);
        Assert.Equal(StartDirResolver.LocalDriveList, chain[0]);
    }

    [Fact]
    public void 本机侧候选链_记忆目录等于末位候选时不重复()
    {
        var chain = StartDirResolver.BuildLocalCandidates(StartDirResolver.LocalDriveList);

        Assert.Single(chain);
    }

    [Fact]
    public void 候选链中不含跨设备共享的目录()
    {
        // 全局单值字段已随本 change 移除：设备侧候选只能来自本设备记忆或存储根目录
        var chain = StartDirResolver.BuildDeviceCandidates("/data/local/tmp");

        Assert.DoesNotContain("", chain);
        Assert.All(chain, p => Assert.StartsWith("/", p));
    }

    // ── 首个可用候选的选取（任务 2.2）──

    [Fact]
    public async Task 第一个候选就成功时不再尝试后续候选()
    {
        var tried = new List<string>();

        var picked = await StartDirResolver.PickFirstAvailableAsync(
            new[] { "/a", "/b" },
            p => { tried.Add(p); return Task.FromResult(true); });

        Assert.Equal("/a", picked);
        Assert.Equal(new[] { "/a" }, tried);
    }

    [Fact]
    public async Task 第一个失败第二个成功时选中第二个()
    {
        var tried = new List<string>();

        var picked = await StartDirResolver.PickFirstAvailableAsync(
            new[] { "/a", "/b" },
            p => { tried.Add(p); return Task.FromResult(p == "/b"); });

        Assert.Equal("/b", picked);
        Assert.Equal(new[] { "/a", "/b" }, tried);
    }

    [Fact]
    public async Task 全部失败时返回无结果且每个候选都试过()
    {
        var tried = new List<string>();

        var picked = await StartDirResolver.PickFirstAvailableAsync(
            new[] { "/a", "/b" },
            p => { tried.Add(p); return Task.FromResult(false); });

        Assert.Null(picked);
        Assert.Equal(new[] { "/a", "/b" }, tried);
    }

    [Fact]
    public async Task 委托抛出取消异常时中止整条链()
    {
        var tried = new List<string>();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            StartDirResolver.PickFirstAvailableAsync(
                new[] { "/a", "/b" },
                p =>
                {
                    tried.Add(p);
                    throw new OperationCanceledException();
                }));

        // 取消 ≠ 候选失败：不得当成失败去试下一个候选
        Assert.Equal(new[] { "/a" }, tried);
    }
}
