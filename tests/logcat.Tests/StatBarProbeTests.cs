using System.Reflection;
using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// 状态栏取证探针（<c>[StatBar]</c> 诊断流）的口径：快照字段齐全、空文本长度记 0、
/// 无消息泵时延迟快照不抛异常、取消路径同样落尾帧。
///
/// <para>
/// 探针不改任何界面行为，因此这里只验「记录内容与落帧时机」，不验 WinForms 布局结果
/// （布局本身无法在无界面会话里复现，取证靠实机日志）。
/// </para>
/// </summary>
[Collection(WinFormsCollection.Name)]
public class StatBarProbeTests
{
    static readonly BindingFlags NonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    static object? Fld(object o, string name) =>
        o.GetType().GetField(name, NonPublicInstance)!.GetValue(o);

    [Fact]
    public void 快照包含固定前缀与全部项字段()
    {
        using var strip = new StatusStrip();
        var stat = new ToolStripStatusLabel("总 1 条 | 命中 1");
        var pos = new ToolStripStatusLabel("");
        strip.Items.AddRange(new ToolStripItem[] { stat, pos });

        string line = StatBarProbe.Describe("stat", strip, ("stat", stat), ("pos", pos));

        Assert.StartsWith("[StatBar] stat: ", line);
        Assert.Contains("strip=", line);          // 状态条宽高
        Assert.Contains(" style=", line);         // 布局风格（判定是否真的走 Table）
        Assert.Contains(" overflow=", line);      // 能否溢出（判定是否整条被裁）
        Assert.Contains(" | stat(w=", line);      // 统计项本身
        Assert.Contains(" | pos(w=", line);       // 相邻项（对照组）
        foreach (string field in new[] { ",len=", ",avail=", ",place=", ",vis=" })
            Assert.Contains(field, line);
    }

    [Fact]
    public void 空文本项长度记0()
    {
        using var strip = new StatusStrip();
        var empty = new ToolStripStatusLabel("");
        strip.Items.AddRange(new ToolStripItem[] { empty });

        string line = StatBarProbe.Describe("clear", strip, ("stat", empty));

        Assert.StartsWith("[StatBar] clear: ", line);
        Assert.Contains(" | stat(w=", line);
        Assert.Contains(",len=0,", line);   // 文本空 = 统计没写回（机制 B 的观测位）
    }

    [Fact]
    public void 无消息泵时延迟快照不抛异常()
    {
        using var form = new frmMain();
        var m = typeof(frmMain).GetMethod("ScheduleStatBarProbe", NonPublicInstance)!;

        // 未 Show 过 → 无句柄：必须静默丢弃，而不是建定时器或抛异常
        var ex = Record.Exception(() => m.Invoke(form, new object?[] { "idle+200", 200 }));

        Assert.Null(ex);
    }

    [Fact]
    public void 换代抢占后仍产出尾部快照()
    {
        var lines = new List<string>();
        var prev = StatBarProbe.Sink;
        StatBarProbe.Sink = lines.Add;
        try
        {
            using var form = new frmMain();
            var stopWorker = typeof(frmMain).GetMethod("StopWorker", NonPublicInstance)!;
            int gen = (int)Fld(form, "_workerGen")!;

            // 模拟取消 / 换文件抢占：代际先被推进，此时旧一代的开文件收尾仍必须落帧
            stopWorker.Invoke(form, Array.Empty<object>());
            form.FinishOpenFileProbe(gen);

            Assert.Contains(lines, l => l.Contains(" tail-finally:"));
        }
        finally
        {
            StatBarProbe.Sink = prev;
        }
    }
}
