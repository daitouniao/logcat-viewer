using Xunit;
using Next.Interop;

namespace Next.Tests;

/// <summary>装载与导出解析（tasks 1.3）：按名解析、缺失明确报错、开发树可装载。</summary>
public class LogcoreBindingTests
{
    /// <summary>开发树 cargo 产物可装载（worktree 内 logcore/target/debug/logcore.dll）。</summary>
    [Fact]
    public void LoadsWhenLibraryPresent()
    {
        var exports = LogcoreLibrary.Load();
        Assert.NotNull(exports);
        Assert.NotNull(exports.Plan);
        Assert.NotNull(exports.Page);
        Assert.False(string.IsNullOrWhiteSpace(LogcoreLibrary.LoadedPath));
    }

    /// <summary>必需导出缺失时报错信息含缺失导出名，而不是裸 EntryPointNotFound。</summary>
    [Fact]
    public void MissingExportReportsClearError()
    {
        // 系统库无 logcore_* 导出，首个绑定即失败
        var path = Path.Combine(Environment.SystemDirectory, "user32.dll");
        var ex = Assert.Throws<LogcoreUnavailableException>(() => _ = new Exports(path));
        Assert.Contains("logcore_plan", ex.Message);
    }

    /// <summary>缺失 dll 时报错含构建指引（cargo build 提示）。</summary>
    [Fact]
    public void MissingLibraryReportsBuildHint()
    {
        var bogus = Path.Combine(Path.GetTempPath(), "no-such-logcore.dll");
        var ex = Assert.Throws<LogcoreUnavailableException>(() => LogcoreLibrary.UseLibrary(bogus));
        Assert.Contains("cargo build", ex.Message);
    }
}
