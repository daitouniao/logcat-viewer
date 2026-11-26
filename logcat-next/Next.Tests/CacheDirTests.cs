using System.Text.Json.Nodes;
using Xunit;
using Next.App.Storage;
using Next.App.ViewModels;

namespace Next.Tests;

/// <summary>请求目录（tasks 4.1 / 4.2）：缓存走持久化根，临时目录保持系统临时目录。</summary>
public class CacheDirTests
{
    sealed class AlwaysProbe : IDirectoryProbe
    {
        public bool CanWrite(string directory) => true;
    }

    sealed class NeverProbe : IDirectoryProbe
    {
        public bool CanWrite(string directory) => false;
    }

    static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), "logcat-next-tests", Guid.NewGuid().ToString("N"));

    static JsonNode Request(StorageRoots roots) =>
        JsonNode.Parse(MainViewModel.BuildRequest(@"C:\logs\a.log", roots))!;

    [Fact]
    public void RequestUsesPersistentCacheDir()
    {
        var root = TempRoot();
        var temp = Path.Combine(Path.GetTempPath(), "logcat-next");
        var roots = StorageRootResolver.Resolve([root], new AlwaysProbe(), temp);
        try
        {
            var json = Request(roots);
            Assert.Equal(roots.CacheDir, (string)json["options"]!["sidecar"]!["dir"]!);
            Assert.Equal(roots.TempDir, (string)json["options"]!["tempDir"]!);
            Assert.Equal(Path.Combine(root, "cache", "core"), roots.CacheDir);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void FallsBackToTempWhenSessionOnly()
    {
        var temp = Path.Combine(Path.GetTempPath(), "logcat-next");
        var roots = StorageRootResolver.Resolve([TempRoot()], new NeverProbe(), temp);

        var json = Request(roots);

        Assert.True(roots.SessionOnly);
        Assert.Equal(Path.Combine(temp, "sidecar"), roots.CacheDir);
        Assert.Equal(roots.CacheDir, (string)json["options"]!["sidecar"]!["dir"]!);
    }

    [Fact]
    public void TempDirStaysUnderSystemTemp()
    {
        var systemTemp = Path.GetTempPath();
        var root = TempRoot();
        var roots = StorageRootResolver.Resolve([root], new AlwaysProbe(), Path.Combine(systemTemp, "logcat-next"));
        try
        {
            Assert.StartsWith(systemTemp, roots.TempDir, StringComparison.OrdinalIgnoreCase);
            Assert.True(roots.TempDir == Path.Combine(systemTemp, "logcat-next"));
        }
        finally
        {
            TryDelete(root);
        }
    }

    static void TryDelete(string path)
    {
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
