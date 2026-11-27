using Xunit;
using Next.App.Services;

namespace Next.Tests;

/// <summary>最近文件菜单展示规则（design D5/D6/D7）：存在性过滤、网络路径、省略与禁用。</summary>
public class RecentFilesMenuTests
{
    const string Missing = @"C:\logs\gone.log";
    const string Present = @"C:\logs\here.log";
    const string Network = @"\\server\share\remote.log";

    [Fact]
    public void HidesMissingLocalEntry()
    {
        var model = RecentFilesMenu.Build([Present, Missing], p => p != Missing);

        Assert.True(model.IsEnabled);
        Assert.Equal([Present], model.Entries.Select(e => e.FullPath));
        Assert.Equal([Missing], model.MissingLocalPaths);
    }

    [Fact]
    public void ReportsMissingLocalEntryForRemoval()
    {
        var stored = new List<string> { Present, Missing };

        var model = RecentFilesMenu.Build(stored, p => p != Missing);

        Assert.Equal([Missing], model.MissingLocalPaths);
        Assert.Equal([Present, Missing], stored);   // 构建函数只上报，不直接改输入
    }

    [Fact]
    public void ShowsNetworkEntryWithoutProbe()
    {
        var probes = 0;
        var model = RecentFilesMenu.Build([Network], _ =>
        {
            probes++;
            return false;
        });

        Assert.True(model.IsEnabled);
        Assert.Equal([Network], model.Entries.Select(e => e.FullPath));
        Assert.Equal(0, probes);
    }

    [Fact]
    public void TreatsProbeFailureAsMissing()
    {
        var model = RecentFilesMenu.Build([Present], _ => throw new IOException("probe failed"));

        Assert.False(model.IsEnabled);
        Assert.Empty(model.Entries);
        Assert.Equal([Present], model.MissingLocalPaths);
    }

    [Fact]
    public void DisablesWhenNoVisibleEntry()
    {
        Assert.False(RecentFilesMenu.Build([], _ => true).IsEnabled);
        Assert.False(RecentFilesMenu.Build([Missing], _ => false).IsEnabled);
    }

    [Fact]
    public void ElidesMiddleBeyondEighty()
    {
        var path = @"C:\logs\" + new string('a', 60) + @"\" + new string('b', 40) + ".log";
        var model = RecentFilesMenu.Build([path], _ => true);

        var entry = Assert.Single(model.Entries);
        Assert.Equal(80, entry.DisplayText.Length);
        Assert.Contains("…", entry.DisplayText);
        Assert.StartsWith(@"C:\logs\", entry.DisplayText, StringComparison.Ordinal);
        Assert.EndsWith(".log", entry.DisplayText, StringComparison.Ordinal);
        Assert.Equal(path, entry.FullPath);
    }

    [Fact]
    public void KeepsShortPathUnchanged()
    {
        var model = RecentFilesMenu.Build([Present], _ => true);

        var entry = Assert.Single(model.Entries);
        Assert.Equal(Present, entry.DisplayText);
        Assert.Equal(Present, entry.FullPath);
    }
}
