using Xunit;

namespace Next.Tests;

/// <summary>
/// 边界约束（tasks 1.4）：应用目录解析只允许出现在存储模块，
/// 其余代码必须使用注入的解析结果，避免后续切片绕过回退链。
/// </summary>
public class StorageBoundaryTests
{
    [Fact]
    public void BaseDirectoryOnlyInStorage()
    {
        var appRoot = Path.Combine(RepoRoot(), "logcat-next", "Next.App");
        var offenders = Directory
            .EnumerateFiles(appRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Storage{Path.DirectorySeparatorChar}"))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(path => File.ReadAllText(path).Contains("AppContext.BaseDirectory", StringComparison.Ordinal))
            .Select(path => Path.GetFileName(path))
            .ToList();

        Assert.Empty(offenders);
    }

    static string RepoRoot()
    {
        var attr = typeof(StorageBoundaryTests).Assembly.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType.Name == "AssemblyMetadataAttribute"
                && (string)a.ConstructorArguments[0].Value! == "RepoRoot");
        return (string)attr!.ConstructorArguments[1].Value!;
    }
}
