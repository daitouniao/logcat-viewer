namespace Next.App.Storage;

/// <summary>
/// 可写根解析（design D2/D10）：启动时解析一次，运行期不再探测。
/// 顺序：应用所在目录 → 当前用户应用数据目录 → 仅会话内。
/// </summary>
public static class StorageRootResolver
{
    public const string UserDirectoryName = "logcat-next";

    /// <summary>纯函数：候选顺序与探测都注入，便于覆盖三种分支。</summary>
    public static StorageRoots Resolve(
        IReadOnlyList<string> rootCandidates,
        IDirectoryProbe probe,
        string tempDirectory)
    {
        foreach (var candidate in rootCandidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && probe.CanWrite(candidate))
            {
                return new StorageRoots(candidate, tempDirectory);
            }
        }

        return new StorageRoots(null, tempDirectory);
    }

    public static StorageRoots ResolveDefault() =>
        Resolve(DefaultCandidates(), new FileSystemProbe(), DefaultTempDirectory());

    /// <summary>应用目录优先，其次用户应用数据目录（不进调试输出）。</summary>
    public static IReadOnlyList<string> DefaultCandidates()
    {
        var candidates = new List<string>
        {
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
        };

        var userRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(userRoot))
        {
            candidates.Add(Path.Combine(userRoot, UserDirectoryName));
        }

        return candidates;
    }

    public static string DefaultTempDirectory() =>
        Path.Combine(Path.GetTempPath(), UserDirectoryName);
}
