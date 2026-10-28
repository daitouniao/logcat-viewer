using System.Text;
using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// LogDocument 零拷贝正文读取会话（<see cref="LogDocument.MessageSession"/>）：
/// 边界与 <see cref="LogDocument.MessageBytes"/> 逐字节一致、回调形态、
/// 健壮性边界（行号为负 / 超出行数 / 无正文 / 超映射容量）。
/// </summary>
public class LogDocumentZeroCopyTests
{
    const string T1 = "09-23 18:00:00.000";
    const string T2 = "09-23 18:00:01.000";
    const string T3 = "09-23 18:00:02.000";
    const string T4 = "09-23 18:00:03.000";

    /// <summary>
    /// 4 行样本：
    /// 0  普通 threadtime 行（大小写混合 ASCII 正文）
    /// 1  多行记录首行（续行 "    at Foo.bar(Foo.java:1)"，正文跨两个物理行）
    /// 2  中文正文行（含非 ASCII 字节）
    /// 3  无正文行（tag 冒号结尾，消息偏移等于行长）
    /// </summary>
    static string Sample() =>
        TestData.Threadtime(T1, 1, 2, 'I', "AlphaTag", "Hello World") +
        TestData.Threadtime(T2, 1, 3, 'E', "BetaTag", "crash") +
        "    at Foo.bar(Foo.java:1)\n" +
        TestData.Threadtime(T3, 2, 4, 'W', "中文Tag", "数据库连接失败") +
        "09-23 18:00:03.000  9  9 D GammaTag:\n";

    [Fact]
    public void ZeroCopy_正文边界与MessageBytes逐字节一致()
    {
        using var tmp = new TempLogFile(Sample());
        using var doc = LogDocument.Build(tmp.Path);
        Assert.Equal(4, doc.RowCount);

        using var session = doc.OpenMessageSession();
        for (int row = 0; row < doc.RowCount; row++)
        {
            byte[] expected = doc.MessageBytes(row);
            int calls = 0;
            session.ReadMessage(row, span =>
            {
                calls++;
                Assert.Equal(expected.Length, span.Length);
                Assert.True(span.SequenceEqual(expected));
            });
            Assert.Equal(1, calls);   // 每行恰好回调一次
        }
    }

    [Fact]
    public void ZeroCopy_会话释放后可重新获取()
    {
        using var tmp = new TempLogFile(Sample());
        using var doc = LogDocument.Build(tmp.Path);

        var s1 = doc.OpenMessageSession();
        s1.Dispose();
        s1.Dispose();                      // 幂等：重复释放不抛
        var s2 = doc.OpenMessageSession(); // 释放后可重新获取，不抛异常
        bool ok = false;
        s2.ReadMessage(0, span => ok = span.SequenceEqual("Hello World"u8));
        Assert.True(ok);
        s2.Dispose();
    }

    /// <summary>
    /// 行号为负、超出行数都返回空片段而非越界（裸指针无行界保护，
    /// 行列由文档层校验，调用方传错行号不应变成段错误）。
    /// </summary>
    [Fact]
    public void ZeroCopy_越界行号返回空()
    {
        using var tmp = new TempLogFile(Sample());
        using var doc = LogDocument.Build(tmp.Path);

        using var session = doc.OpenMessageSession();
        foreach (int bad in new[] { -1, -100, doc.RowCount, doc.RowCount + 7, int.MaxValue })
        {
            int calls = 0;
            session.ReadMessage(bad, span => { calls++; Assert.True(span.IsEmpty); });
            Assert.Equal(1, calls);
        }
    }

    /// <summary>消息偏移大于等于行长（无正文）时返回空片段，与 MessageBytes 一致。</summary>
    [Fact]
    public void ZeroCopy_无正文行返回空()
    {
        using var tmp = new TempLogFile(Sample());
        using var doc = LogDocument.Build(tmp.Path);

        // 第 3 行 tag 冒号结尾、无正文（MessageBytes 同为空）
        Assert.True(doc.MessageBytes(3).Length == 0);

        using var session = doc.OpenMessageSession();
        int calls = 0;
        session.ReadMessage(3, span => { calls++; Assert.True(span.IsEmpty); });
        Assert.Equal(1, calls);
    }

    /// <summary>
    /// 容量护栏：会话指针指向建会话时刻的映射，容量不随文件增长。
    /// 文档 Append（列扩展 + 重映射）后，旧会话读新行时「偏移+长度」超出旧映射容量 ——
    /// 必须返回空片段而不是段错误（裸指针越界读是进程崩溃，不是托管异常）。
    /// </summary>
    [Fact]
    public void ZeroCopy_超出映射容量返回空()
    {
        using var tmp = new TempLogFile(TestData.Threadtime(T1, 1, 2, 'I', "Tag", "first"));
        using var doc = LogDocument.Build(tmp.Path);
        Assert.Equal(1, doc.RowCount);
        long origLen = tmp.Length;

        using var session = doc.OpenMessageSession();
        // 视图容量按页取整（>= 建映射那一刻的文件大小），不是精确文件长度
        Assert.True(session.Capacity >= origLen);

        // 旧会话读现有行：在容量内，正常取到
        bool ok = false;
        session.ReadMessage(0, span => ok = span.SequenceEqual("first"u8));
        Assert.True(ok);

        // 追加第二行并 Reload：列扩展 + OpenMap 重映射（新视图容量变大，旧指针仍在旧映射）。
        // 追加行足够大（70 KB），保证「偏移+长度」稳定超出旧映射的页取整容量
        tmp.Append(TestData.Threadtime(T2, 1, 3, 'I', "Tag", new string('y', 70_000)));
        doc.Reload();
        Assert.Equal(2, doc.RowCount);
        Assert.True(doc.MessageBytes(1).Length > 0);   // 新视图读新行正常

        // 旧会话读新行：「偏移+长度」超旧容量 → 空片段（而非段错误）
        int calls = 0;
        session.ReadMessage(1, span => { calls++; Assert.True(span.IsEmpty); });
        Assert.Equal(1, calls);

        // 旧会话读旧行：仍在旧容量内，内容不变
        ok = false;
        session.ReadMessage(0, span => ok = span.SequenceEqual("first"u8));
        Assert.True(ok);
    }
}
