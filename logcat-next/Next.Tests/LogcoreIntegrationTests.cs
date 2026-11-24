using Xunit;
using System.Text.Json.Nodes;
using Next.App.Services;
using Next.Interop;

namespace Next.Tests;

/// <summary>
/// 真实 logcore.dll 端到端（tasks 2.1 / 2.3）：plan → commit → 轮询至 done →
/// 行数与取页。前置：cargo build（开发树回退即可命中 logcore/target/debug）。
/// </summary>
public class LogcoreIntegrationTests
{
    static string RepoRoot
    {
        get
        {
            var attr = typeof(LogcoreIntegrationTests).Assembly
                .GetCustomAttributesData()
                .FirstOrDefault(a => a.AttributeType.Name == "AssemblyMetadataAttribute"
                    && (string)a.ConstructorArguments[0].Value! == "RepoRoot");
            return (string)attr!.ConstructorArguments[1].Value!;
        }
    }

    /// <summary>plan 建句柄、commit 受理、装载完成后首页行模型字段正确。</summary>
    [Fact]
    public async Task PlanCommitAndReadFirstPage()
    {
        using var ctx = await LoadAsync(SamplePath());
        Assert.True(ctx.Rows > 0, "样本应有日志行");
        Assert.Equal(ctx.Rows, ctx.Source.RowCount());

        var page = ctx.Source.GetPage(0, 10);
        Assert.NotEmpty(page);
        Assert.Equal(0u, page[0].Ord);
        // long_sample.txt 行格式 "[ ... 101: 202 I/Alpha ]" → pid=101, tid=202
        Assert.Equal(101, page[0].Pid);
        Assert.Equal(202, page[0].Tid);
        Assert.NotEmpty(page[0].Time);
        Assert.NotEmpty(page[0].Message);
    }

    /// <summary>跨页边界连续不重不漏：4096 行/页，逐页 ord 严格递增。</summary>
    [Fact]
    public async Task CrossPageBoundaryIsContiguous()
    {
        var path = await WriteSampleAsync(9000);
        using var ctx = await LoadAsync(path);
        Assert.Equal(9000ul, ctx.Rows);

        uint expect = 0;
        foreach (var start in new[] { 0, 4096, 8192 })
        {
            var count = (int)Math.Min(LogcoreErrorCodes.MaxPageRows, 9000 - start);
            var rows = ctx.Source.GetPage(start, count);
            Assert.Equal(count, rows.Length);
            foreach (var row in rows)
            {
                Assert.Equal(expect, row.Ord);
                expect++;
            }
        }
        Assert.Equal(9000u, expect);
    }

    /// <summary>记录数少于视口（页）：全行返回、无多余。</summary>
    [Fact]
    public async Task ShortFileYieldsAllRows()
    {
        var path = await WriteSampleAsync(3);
        using var ctx = await LoadAsync(path);
        Assert.Equal(3ul, ctx.Rows);
        var rows = ctx.Source.GetPage(0, LogcoreErrorCodes.MaxPageRows);
        Assert.Equal(3, rows.Length);
    }

    /// <summary>空文件：装载成功、0 行（core plan 走 LocalDirect，见 core source_tests）。</summary>
    [Fact]
    public async Task EmptyFileYieldsNoRows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"next-empty-{Guid.NewGuid():N}.log");
        File.WriteAllText(path, string.Empty);
        try
        {
            using var ctx = await LoadAsync(path);
            Assert.Equal(0ul, ctx.Rows);
            Assert.Empty(ctx.Source.GetPage(0, 10));
        }
        finally
        {
            TryDelete(path);
        }
    }

    // ── 装载流程 ──

    static string SamplePath() =>
        Path.Combine(RepoRoot, "crates", "core", "testdata", "long_sample.txt");

    static async Task<string> WriteSampleAsync(int rows)
    {
        var path = Path.Combine(Path.GetTempPath(), $"next-sample-{Guid.NewGuid():N}.log");
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < rows; i++)
        {
            sb.Append($"11-24 10:00:{(i / 60 % 60):D2}.{(i * 7 % 1000):D3}").Append("  1  1 ")
              .Append("DTag").Append(i % 10).Append(" : message ").AppendLine(i.ToString());
        }
        await File.WriteAllTextAsync(path, sb.ToString());
        return path;
    }

    static async Task<Loaded> LoadAsync(string path)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"next-it-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var sidecarDir = Path.Combine(tempDir, "sidecar");
        Directory.CreateDirectory(sidecarDir);
        var request = new JsonObject
        {
            ["kind"] = "File",
            ["mode"] = "Auto",
            ["path"] = Path.GetFullPath(path),
            // proxy 对 File 源必填；本地直开不启动代理，sidecar 一并给足形状
            ["proxy"] = new JsonObject
            {
                ["pipeName"] = $"next-it-{Guid.NewGuid():N}",
                ["launchFile"] = "next-it-sidecar.exe",
                ["launchTimeoutMs"] = 3000,
            },
            ["options"] = new JsonObject
            {
                ["tempDir"] = tempDir,
                ["sidecar"] = new JsonObject { ["dir"] = sidecarDir },
            },
        }.ToJsonString();

        var source = LogcoreSource.Plan(request);
        var loaded = new Loaded(source, tempDir);
        source.Commit();
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            var events = source.PollEvents();
            if (events.Any(e => e.Type == "error"))
            {
                throw new InvalidOperationException("装载失败：core 报 error 事件");
            }
            if (events.Any(e => e.Type == "done"))
            {
                break;
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("装载超时");
            }
            await Task.Delay(50);
        }
        loaded.Rows = source.RowCount();
        return loaded;
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }

    sealed class Loaded(LogcoreSource source, string tempDir) : IDisposable
    {
        public LogcoreSource Source { get; } = source;
        public string TempDir { get; } = tempDir;
        public ulong Rows { get; set; }

        public void Dispose()
        {
            Source.Dispose();
            try { Directory.Delete(TempDir, true); } catch (IOException) { }
        }
    }
}
