using Xunit;
using Next.App.Services;
using Next.App.Storage;

namespace Next.Tests;

/// <summary>
/// 最近文件（spec next/recent-files）：列表维护、持久化、会话内降级，
/// 以及装载流程只在 done 后记录的时点约束。
/// </summary>
public class RecentFilesTests
{
    static string TempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "logcat-next-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static (NextSettingsService Service, string Path) CreateService()
    {
        var path = Path.Combine(TempDirectory(), "next-settings.json");
        return (new NextSettingsService(new JsonNextSettingsStore(path)), path);
    }

    // ── 列表维护 ──

    [Fact]
    public void AddsNewestFirst()
    {
        var (service, _) = CreateService();

        service.AddRecentFile(@"C:\logs\a.log");
        service.AddRecentFile(@"C:\logs\b.log");

        Assert.Equal<IEnumerable<string>>([@"C:\logs\b.log", @"C:\logs\a.log"], service.RecentFiles);
    }

    [Fact]
    public void DedupesCaseInsensitive()
    {
        var (service, _) = CreateService();
        service.AddRecentFile(@"C:\logs\a.log");
        service.AddRecentFile(@"C:\logs\b.log");

        service.AddRecentFile(@"C:\LOGS\A.LOG");

        Assert.Equal<IEnumerable<string>>([@"C:\LOGS\A.LOG", @"C:\logs\b.log"], service.RecentFiles);
    }

    [Fact]
    public void KeepsNewestPathText()
    {
        var (service, _) = CreateService();
        service.AddRecentFile(@"C:\logs\a.log");

        service.AddRecentFile(@"c:\logs\A.log");

        Assert.Equal(@"c:\logs\A.log", Assert.Single(service.RecentFiles));
    }

    [Fact]
    public void CapsAtTen()
    {
        var (service, _) = CreateService();

        for (var i = 0; i < 12; i++)
        {
            service.AddRecentFile($@"C:\logs\{i}.log");
        }

        Assert.Equal(10, service.RecentFiles.Count);
        Assert.Equal(@"C:\logs\11.log", service.RecentFiles[0]);
        Assert.Equal(@"C:\logs\2.log", service.RecentFiles[9]);
    }

    [Fact]
    public void IgnoresBlankPath()
    {
        var (service, _) = CreateService();

        service.AddRecentFile("");
        service.AddRecentFile("   ");

        Assert.Empty(service.RecentFiles);
    }

    [Fact]
    public void RemovesSpecifiedEntries()
    {
        var (service, path) = CreateService();
        service.AddRecentFile(@"C:\logs\a.log");
        service.AddRecentFile(@"C:\logs\b.log");

        service.RemoveRecentFiles([@"C:\logs\A.LOG"]);   // 大小写不敏感

        Assert.Equal(@"C:\logs\b.log", Assert.Single(service.RecentFiles));
        Assert.Equal(@"C:\logs\b.log",
            Assert.Single(new NextSettingsService(new JsonNextSettingsStore(path)).RecentFiles));
    }

    [Fact]
    public void RemovingUnknownEntryLeavesListUnchanged()
    {
        var (service, _) = CreateService();
        service.AddRecentFile(@"C:\logs\a.log");

        service.RemoveRecentFiles([@"C:\logs\other.log"]);

        Assert.Equal(@"C:\logs\a.log", Assert.Single(service.RecentFiles));
    }

    // ── 持久化与降级 ──

    [Fact]
    public void PersistsAcrossReload()
    {
        var (service, path) = CreateService();
        service.AddRecentFile(@"C:\logs\a.log");
        service.AddRecentFile(@"C:\logs\b.log");

        var reloaded = new NextSettingsService(new JsonNextSettingsStore(path));

        Assert.Equal<IEnumerable<string>>([@"C:\logs\b.log", @"C:\logs\a.log"], reloaded.RecentFiles);
    }

    [Fact]
    public void SessionOnlyKeepsInMemory()
    {
        var service = new NextSettingsService(new InMemorySettingsStore());

        service.AddRecentFile(@"C:\logs\a.log");

        Assert.Equal(@"C:\logs\a.log", Assert.Single(service.RecentFiles));
        Assert.Empty(new NextSettingsService(new InMemorySettingsStore()).RecentFiles);
    }

    [Fact]
    public void SaveFailureDoesNotBlockRecording()
    {
        var dir = TempDirectory();
        var fileAsParent = Path.Combine(dir, "blocked");
        File.WriteAllText(fileAsParent, "x");
        var service = new NextSettingsService(
            new JsonNextSettingsStore(Path.Combine(fileAsParent, "next-settings.json")));

        service.AddRecentFile(@"C:\logs\a.log");

        Assert.Equal(@"C:\logs\a.log", Assert.Single(service.RecentFiles));
    }

    // ── 装载时点：只在 done 后记录 ──

    [Fact]
    public async Task RecordsAfterSuccessfulLoad()
    {
        var recorded = new List<string>();
        var roots = TestRoots();
        try
        {
            var outcome = await LogFileLoader.LoadAsync(SamplePath(), roots, recorded.Add, CancellationToken.None);
            outcome.Source.Dispose();

            Assert.Equal([SamplePath()], recorded);
        }
        finally
        {
            TryDelete(roots.Root);
        }
    }

    [Fact]
    public async Task RecordsEmptyFile()
    {
        var path = Path.Combine(TempDirectory(), "empty.log");
        File.WriteAllText(path, string.Empty);
        var recorded = new List<string>();
        var roots = TestRoots();
        try
        {
            var outcome = await LogFileLoader.LoadAsync(path, roots, recorded.Add, CancellationToken.None);

            Assert.Equal(0ul, outcome.Rows);
            Assert.Equal([path], recorded);
            outcome.Source.Dispose();
        }
        finally
        {
            TryDelete(roots.Root);
        }
    }

    [Fact]
    public async Task IgnoresFailedLoad()
    {
        var recorded = new List<string>();
        var roots = TestRoots();
        try
        {
            var missing = Path.Combine(TempDirectory(), "missing.log");

            await Assert.ThrowsAnyAsync<Exception>(() =>
                LogFileLoader.LoadAsync(missing, roots, recorded.Add, CancellationToken.None));

            Assert.Empty(recorded);
        }
        finally
        {
            TryDelete(roots.Root);
        }
    }

    [Fact]
    public async Task IgnoresSupersededLoad()
    {
        var path = Path.Combine(TempDirectory(), "superseded.log");
        File.WriteAllText(path, "11-24 10:00:00.000  1  1 DTag : message" + Environment.NewLine);
        var recorded = new List<string>();
        var roots = TestRoots();
        using var cts = new CancellationTokenSource();
        try
        {
            var task = LogFileLoader.LoadAsync(path, roots, recorded.Add, cts.Token);
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.Empty(recorded);
        }
        finally
        {
            TryDelete(roots.Root);
        }
    }

    // ── 夹具 ──

    static string RepoRoot
    {
        get
        {
            var attr = typeof(RecentFilesTests).Assembly
                .GetCustomAttributesData()
                .FirstOrDefault(a => a.AttributeType.Name == "AssemblyMetadataAttribute"
                    && (string)a.ConstructorArguments[0].Value! == "RepoRoot");
            return (string)attr!.ConstructorArguments[1].Value!;
        }
    }

    static string SamplePath() =>
        Path.Combine(RepoRoot, "crates", "core", "testdata", "long_sample.txt");

    static StorageRoots TestRoots()
    {
        var root = TempDirectory();
        return new StorageRoots(root, Path.Combine(root, "tmp"));
    }

    static void TryDelete(string? path)
    {
        if (path is null)
        {
            return;
        }
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
            // 测试清理失败不影响判定
        }
    }
}
