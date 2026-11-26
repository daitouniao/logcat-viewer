using Xunit;
using Next.App.Storage;

namespace Next.Tests;

/// <summary>持久化根解析（tasks 1.2）：三种分支、同根布局、临时目录不跟根。</summary>
public class StorageRootTests
{
    sealed class FixedProbe(params string[] writable) : IDirectoryProbe
    {
        public bool CanWrite(string directory) =>
            writable.Contains(directory, StringComparer.OrdinalIgnoreCase);
    }

    static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), "logcat-next-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void PrefersExecutableDirectoryWhenWritable()
    {
        var exe = Path.Combine(TempRoot(), "exe");
        var user = Path.Combine(TempRoot(), "user");
        var temp = Path.Combine(TempRoot(), "temp");

        var roots = StorageRootResolver.Resolve([exe, user], new FixedProbe(exe, user), temp);

        Assert.False(roots.SessionOnly);
        Assert.Equal(exe, roots.Root);
        Assert.Equal(Path.Combine(exe, "next-settings.json"), roots.SettingsPath);
        Assert.Equal(Path.Combine(exe, "next-startup.log"), roots.LogPath);
        Assert.Equal(Path.Combine(exe, "cache", "core"), roots.CacheDir);
        Assert.Equal(temp, roots.TempDir);
    }

    [Fact]
    public void FallsBackToUserDirectoryWhenExecutableReadOnly()
    {
        var exe = Path.Combine(TempRoot(), "exe");
        var user = Path.Combine(TempRoot(), "user");
        var temp = Path.Combine(TempRoot(), "temp");

        var roots = StorageRootResolver.Resolve([exe, user], new FixedProbe(user), temp);

        Assert.Equal(user, roots.Root);
        Assert.Equal(Path.Combine(user, "cache", "core"), roots.CacheDir);
        Assert.Equal(temp, roots.TempDir);
    }

    [Fact]
    public void SessionOnlyWhenNoCandidateWritable()
    {
        var exe = Path.Combine(TempRoot(), "exe");
        var user = Path.Combine(TempRoot(), "user");
        var temp = Path.Combine(TempRoot(), "temp");

        var roots = StorageRootResolver.Resolve([exe, user], new FixedProbe(), temp);

        Assert.True(roots.SessionOnly);
        Assert.Null(roots.Root);
        Assert.Null(roots.SettingsPath);
        Assert.Null(roots.LogPath);
        Assert.Equal(Path.Combine(temp, "sidecar"), roots.CacheDir);
        Assert.Equal(temp, roots.TempDir);
    }

    [Fact]
    public void RealProbeRejectsPathThatIsAFile()
    {
        var file = Path.Combine(TempRoot(), "file");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "x");

        Assert.False(new FileSystemProbe().CanWrite(file));
    }
}
