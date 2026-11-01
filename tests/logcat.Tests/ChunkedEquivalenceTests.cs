using System.Text;
using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// 分块加载等价性不变量（chunked-progressive-load 任务 5.1，design 迁移计划）：
/// 多块长 × 多样本形态的参数化等价性进常规测试集——分块渐进加载与整份构建
/// 在全部列、tag 字典、崩溃事件上逐元素等价，且发布前缀 append-only。
/// 任务 3.2 / 4.1 的单点等价测试继续保留（针对性边界语义）。
/// </summary>
public class ChunkedEquivalenceTests
{
    // 样本形态 × 块长组合（保持测试总量可控：6 样本 × 3 块长 = 18 例）
    public static IEnumerable<object[]> Cases()
    {
        foreach (var s in new[] { "plain", "crashy", "nonascii", "no-trailing-newline", "tiny", "empty" })
            foreach (var b in new[] { (64, 128), (97, 97), (512, 4096) })
                yield return new object[] { s, b.Item1, b.Item2 };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void 分块与整份全维等价(string sampleKind, int firstBlock, int steadyBlock)
    {
        using var tmp = new TempLogFile(Sample(sampleKind));

        // 发布轨迹：append-only 快照，锁「已发布前缀稳定」
        var pubOffs = new List<long>();
        var pubLens = new List<int>();
        var pubLvls = new List<byte>();
        int lastPublished = 0;

        using var chunked = LogDocument.BuildChunkedCore(tmp.Path,
            (d, from, to) =>
            {
                Assert.Equal(lastPublished, from);            // 区间连续无重叠
                for (int r = from; r < to; r++)
                {
                    pubOffs.Add(d.Offs[r]);
                    pubLens.Add(d.Lens[r]);
                    pubLvls.Add(d.Lvl[r]);
                }
                lastPublished = to;
            }, progress: null, ct: default, displayPath: null,
               firstChunkBytes: firstBlock, steadyChunkBytes: steadyBlock);

        using var whole = LogDocument.Build(tmp.Path);

        Assert.Equal(whole.RowCount, chunked.RowCount);
        Assert.Equal(lastPublished, whole.RowCount);          // 发布覆盖全部记录
        Assert.Equal(tmp.Length, chunked.IndexedSize);        // 索引口径推进到文件末尾
        Assert.Equal(tmp.Length, chunked.Size);

        for (int r = 0; r < whole.RowCount; r++)
        {
            Assert.Equal(whole.Offs[r], chunked.Offs[r]);
            Assert.Equal(whole.Lens[r], chunked.Lens[r]);
            Assert.Equal(whole.Lvl[r], chunked.Lvl[r]);
            Assert.Equal(whole.Pid[r], chunked.Pid[r]);
            Assert.Equal(whole.Tid[r], chunked.Tid[r]);
            Assert.Equal(whole.Minute[r], chunked.Minute[r]);
            Assert.Equal(whole.TagOf(r), chunked.TagOf(r));
            // 已发布内容 = 最终内容（前缀稳定：append-only，永不回改）
            Assert.Equal(whole.Offs[r], pubOffs[r]);
            Assert.Equal(whole.Lens[r], pubLens[r]);
            Assert.Equal(whole.Lvl[r], pubLvls[r]);
        }
        Assert.Equal(whole.Tags, chunked.Tags);
        Assert.Equal(whole.Crashes.Select(c => (c.Row, c.Kind, c.ExcId, c.PkgId, c.Pid)),
                     chunked.Crashes.Select(c => (c.Row, c.Kind, c.ExcId, c.PkgId, c.Pid)));
        Assert.Equal(whole.ExcTypes, chunked.ExcTypes);
        Assert.Equal(whole.Pkgs, chunked.Pkgs);
    }

    /// <summary>样本形态：普通行 / 崩溃+横幅+空行 / 非 ASCII / EOF 残行 / 两行 / 空文件。</summary>
    static string Sample(string kind)
    {
        switch (kind)
        {
            case "empty":
                return "";
            case "tiny":
                return LogDocumentTests.Line("09-23 18:00:00.000", 'I', "Tag", "a") + "\n" +
                       LogDocumentTests.Line("09-23 18:00:01.000", 'D', "Tag", "b") + "\n";
            case "no-trailing-newline":
                return Body(40).TrimEnd('\n');
            case "nonascii":
            {
                var sb = new StringBuilder();
                for (int i = 0; i < 60; i++)
                    sb.AppendLine(LogDocumentTests.Line($"09-23 18:{i / 60:00}:{i % 60:00}.000",
                        i % 2 == 0 ? 'I' : 'W', i % 3 == 0 ? "输入法" : "系统UI",
                        $"中文消息 {i}：错误代码 0x{i:X4}"));
                return sb.ToString();
            }
            case "crashy":
            {
                var sb = new StringBuilder();
                sb.AppendLine("--------- beginning of main");
                sb.AppendLine("----- timezone:Asia/Kuala_Lumpur");
                for (int i = 0; i < 120; i++)
                {
                    sb.AppendLine(LogDocumentTests.Line($"09-23 18:{i / 60:00}:{i % 60:00}.000",
                        i % 2 == 0 ? 'I' : 'D', i % 3 == 0 ? "TagA" : "TagB", $"message body {i}"));
                    if (i % 9 == 0) sb.AppendLine();                    // 空行
                    if (i == 30)
                    {
                        sb.AppendLine(LogDocumentTests.Line("09-23 18:00:30.000", 'E', "AndroidRuntime", "FATAL EXCEPTION: main"));
                        sb.AppendLine(LogDocumentTests.Line("09-23 18:00:30.001", 'E', "AndroidRuntime", "Process: com.example.app, PID: 869"));
                        sb.AppendLine(LogDocumentTests.Line("09-23 18:00:30.002", 'E', "AndroidRuntime", "java.lang.IllegalStateException: boom"));
                    }
                    if (i == 70)
                        sb.AppendLine(LogDocumentTests.Line("09-23 18:01:10.000", 'E', "libc",
                            "Fatal signal 11 (SIGSEGV), code 1 (SEGV_MAPERR), fault addr 0x0 in tid 869 (com.example.app)"));
                }
                return sb.ToString();
            }
            default:   // plain
                return Body(200);
        }
    }

    static string Body(int rows)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < rows; i++)
            sb.AppendLine(LogDocumentTests.Line($"09-23 18:{i / 60:00}:{i % 60:00}.000",
                i % 2 == 0 ? 'I' : 'D', i % 4 == 0 ? "TagA" : "TagB", $"message body {i} with padding"));
        return sb.ToString();
    }
}
