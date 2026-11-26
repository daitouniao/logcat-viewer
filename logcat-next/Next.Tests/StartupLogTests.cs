using Xunit;
using Next.App.Storage;

namespace Next.Tests;

[CollectionDefinition("startup-log", DisableParallelization = true)]
public sealed class StartupLogCollection
{
}

/// <summary>诊断日志（tasks 3.1）：追加、前缀、轮转、失败静默。</summary>
[Collection("startup-log")]
public class StartupLogTests
{
    static string TempLog()
    {
        var dir = Path.Combine(Path.GetTempPath(), "logcat-next-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "next-startup.log");
    }

    [Fact]
    public void AppendsEntries()
    {
        var path = TempLog();
        try
        {
            StartupLog.Initialize(path);
            StartupLog.Write("INFO", "first");
            StartupLog.Write("ERROR", "second");

            var text = File.ReadAllText(path);
            Assert.Contains("first", text);
            Assert.Contains("second", text);
        }
        finally
        {
            StartupLog.Initialize(null);
        }
    }

    [Fact]
    public void IncludesProcessAndThreadPrefix()
    {
        var path = TempLog();
        try
        {
            StartupLog.Initialize(path);
            StartupLog.Write("INFO", "prefix");

            var line = File.ReadAllText(path);
            Assert.Matches(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[\d+:\d+\] INFO prefix", line);
        }
        finally
        {
            StartupLog.Initialize(null);
        }
    }

    [Fact]
    public void RotatesAtSizeLimit()
    {
        var path = TempLog();
        try
        {
            StartupLog.Initialize(path, limitBytes: 200);
            for (var i = 0; i < 8; i++)
            {
                StartupLog.Write("INFO", $"entry-{i}");
            }

            Assert.True(File.Exists(path + ".1"), "达到上限后应保留一份旧档");
            Assert.True(File.Exists(path), "轮转后应继续写当前文件");
        }
        finally
        {
            StartupLog.Initialize(null);
        }
    }

    [Fact]
    public void WriteFailureDoesNotThrow()
    {
        // 目标是一个已存在的目录：追加写会失败，必须被吞掉
        var directory = Path.Combine(Path.GetTempPath(), "logcat-next-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            StartupLog.Initialize(directory);
            StartupLog.Write("ERROR", "should not throw", new InvalidOperationException("boom"));
        }
        finally
        {
            StartupLog.Initialize(null);
        }
    }
}
