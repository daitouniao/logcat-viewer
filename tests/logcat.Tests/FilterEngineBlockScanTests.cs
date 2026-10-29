using logcat.Models;
using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// Message 过滤的块扫描路径（change filter-msgfilter-block-scan）：
/// 跨块边界、重叠区/同行去重、头部不误报、空正文、长行跨块、CI 等价（SIMD 与标量回退）、
/// 非 ASCII 行回退与非 ASCII 关键词整体回退、and/or/exclude/空词组合，以及分派与进度。
/// 判定标准（探索笔记《UTF8-BYTE-SIMD-SEARCH》§8）：同一输入同一过滤条件，
/// 块扫描与逐条匹配路径的行号集合完全一致 —— 各用例统一按「两路径逐位相等 + 期望行集合」双断言。
/// </summary>
public class FilterEngineBlockScanTests
{
    const string T1 = "09-23 18:00:00.000";
    const string T2 = "09-23 18:00:01.000";
    const string T3 = "09-23 18:00:02.000";
    const string T4 = "09-23 18:00:03.000";
    const string T5 = "09-23 18:00:04.000";

    /// <summary>测试用分块大小：小到小文档就能产生多块与跨块边界。</summary>
    const int BS = 128;

    static int[] Run(LogDocument doc, string[] terms, bool block,
        string op = "or", bool cs = false, bool exclude = false, int blockSize = BS,
        FilterEngine.BlockScanCiMode ciMode = FilterEngine.BlockScanCiMode.Auto)
    {
        var spec = new FilterSpec { Msg = terms, MsgOp = op, MsgCase = cs, MsgExclude = exclude };
        var cand = Enumerable.Range(0, doc.RowCount).ToArray();
        return FilterEngine.MsgFilterCore(doc, cand, spec, null, CancellationToken.None,
            forceBlockScan: block, blockSizeOverride: block ? blockSize : null, ciMode: ciMode);
    }

    // ── 2.1 块扫描骨架 ──

    /// <summary>
    /// 2 行文档（字节偏移可手算），构造「err」起点恰在 128 字节块边界前 2 字节：
    /// 关键词横跨块边界，只能由前块扫到（后块起点已在词中间）。验证不遗漏。
    /// </summary>
    [Fact]
    public void 块扫描_跨块边界命中不遗漏()
    {
        string head1 = TestData.Threadtime(T1, 1, 2, 'I', "Alpha", "first");   // 40 字节
        string longHead = "09-23 18:00:01.000  1  3 I Beta: ";                 // 33 字节，无换行
        int msgStart = head1.Length + longHead.Length;                         // 全 ASCII，1 字节 1 偏移
        int pad = (BS - 2) - msgStart;                                         // "err" 落在 126..129，横跨 128
        Assert.True(pad > 0, $"构造失败：pad={pad}");
        string content = head1 + longHead + new string('a', pad) + "err tail\n";

        using var tmp = new TempLogFile(content);
        using var doc = LogDocument.Build(tmp.Path);
        Assert.Equal(2, doc.RowCount);
        Assert.Equal(40, doc.Offs[1]);
        Assert.Equal(msgStart, doc.Offs[1] + doc.Moff[1]);   // 消息段起点与推算一致

        var expected = new[] { 1 };
        Assert.Equal(expected, Run(doc, new[] { "err" }, block: true));
        Assert.Equal(expected, Run(doc, new[] { "err" }, block: false));
        Assert.Equal(expected, Run(doc, new[] { "err" }, block: true, cs: true));
    }

    /// <summary>
    /// 同行去重：该行正文含两个关键词（且同一关键词出现多次），and 语义下命中计数必须恰为
    /// 「可扫词数」。任何重复计行都会把计数推过 2 而使该行被误排除 —— 用例即钉住去重。
    /// 命中点分布在块边界两侧，同时验证跨边界与重叠区都不重复计行。
    /// </summary>
    [Fact]
    public void 块扫描_同行重复命中只计一次()
    {
        string head1 = TestData.Threadtime(T1, 1, 2, 'I', "Alpha", "first");
        string longHead = "09-23 18:00:01.000  1  3 I Beta: ";
        int msgStart = head1.Length + longHead.Length;
        int pad = BS - msgStart;                       // 第一个 "err" 落在块边界 128..130
        Assert.True(pad > 0, $"构造失败：pad={pad}");
        string content = head1 + longHead + new string('a', pad) + "err x err tail\n";

        using var tmp = new TempLogFile(content);
        using var doc = LogDocument.Build(tmp.Path);

        // 两个同词（各命中两次）+ and：计数必须恰为 2（重复计行会变 3/4 → 该行被误排除）
        Assert.Equal(new[] { 1 }, Run(doc, new[] { "err", "err" }, block: true, op: "and"));
        Assert.Equal(new[] { 1 }, Run(doc, new[] { "err", "tail" }, block: true, op: "and"));
        Assert.Equal(Run(doc, new[] { "err", "tail" }, block: false, op: "and"),
            Run(doc, new[] { "err", "tail" }, block: true, op: "and"));
    }

    /// <summary>
    /// 长行跨块：单行正文远长于块大小、关键词只出现在该行尾部。归属块必须把这一行扫到行尾
    /// （扫描右界延伸到末行末尾），否则邻块按「行首不在本块」丢弃命中 → 漏报。
    /// </summary>
    [Fact]
    public void 块扫描_长行尾部命中不遗漏()
    {
        string longMsg = new string('a', BS * 3) + "needle-at-tail";
        string content =
            TestData.Threadtime(T1, 1, 2, 'I', "Alpha", longMsg) +        // 正文 ≈ 4 块长
            TestData.Threadtime(T2, 1, 3, 'E', "Beta", "short\n");
        using var tmp = new TempLogFile(content);
        using var doc = LogDocument.Build(tmp.Path);
        Assert.True(doc.Lens[0] > BS * 3);

        var expected = new[] { 0 };
        Assert.Equal(expected, Run(doc, new[] { "needle-at-tail" }, block: true));
        Assert.Equal(expected, Run(doc, new[] { "needle-at-tail" }, block: false));
        // 尾部单词也试一遍（短关键词在重叠区更易被邻块重复看到）
        Assert.Equal(expected, Run(doc, new[] { "tail" }, block: true, op: "and"));
    }

    /// <summary>时间戳 / tag 里的命中必须被 message 段边界复核剔除（CS 与 CI 各验一遍）。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 块扫描_头部命中不误报(bool cs)
    {
        // tag "ErrorTag" 含关键词、正文不含；另一行正文含关键词
        string content =
            TestData.Threadtime(T1, 1, 2, 'I', "ErrorTag", "nothing here") +
            TestData.Threadtime(T2, 1, 3, 'W', "Alpha", "real Error here\n");
        using var tmp = new TempLogFile(content);
        using var doc = LogDocument.Build(tmp.Path);

        string[] terms = { cs ? "Error" : "error" };
        var expected = new[] { 1 };
        Assert.Equal(expected, Run(doc, terms, block: true, cs: cs));
        Assert.Equal(expected, Run(doc, terms, block: false, cs: cs));
    }

    /// <summary>无正文记录（tag 冒号结尾）：正文不可能命中，关键词只出现在头部也须判不命中。</summary>
    [Fact]
    public void 块扫描_空正文记录不命中()
    {
        string content =
            "09-23 18:00:03.000  9  9 D GammaTag:\n" +
            TestData.Threadtime(T1, 1, 2, 'I', "Alpha", "has gamma inside\n");
        using var tmp = new TempLogFile(content);
        using var doc = LogDocument.Build(tmp.Path);

        var expected = new[] { 1 };
        Assert.Equal(expected, Run(doc, new[] { "gamma" }, block: true, cs: true));
        Assert.Equal(expected, Run(doc, new[] { "gamma" }, block: false, cs: true));
        Assert.Equal(Array.Empty<int>(), Run(doc, new[] { "gammatag" }, block: true, cs: true));
        Assert.Equal(Run(doc, new[] { "gammatag" }, block: false, cs: true),
            Run(doc, new[] { "gammatag" }, block: true, cs: true));
    }

    // ── 2.2 CI 路径 ──

    /// <summary>ASCII 过滤词在纯 ASCII / 混合中英文档上，忽略大小写与逐条路径逐位一致；needle 大写/小写双向不漏。</summary>
    [Theory]
    [InlineData("error")]
    [InlineData("Error")]
    [InlineData("ERROR")]
    [InlineData("exception")]
    [InlineData("Exception")]
    public void 块扫描_CI_与逐条路径逐位一致(string needle)
    {
        string content =
            TestData.Threadtime(T1, 1, 2, 'I', "Alpha", "Exception thrown in module") +
            TestData.Threadtime(T2, 1, 3, 'E', "Beta", "ERROR: disk failure") +
            TestData.Threadtime(T3, 2, 4, 'D', "Gamma", "error log entry") +
            TestData.Threadtime(T4, 2, 5, 'W', "Delta", "数据库错误 error in db") +
            TestData.Threadtime(T5, 3, 6, 'V', "Eps", "no match here\n");
        using var tmp = new TempLogFile(content);
        using var doc = LogDocument.Build(tmp.Path);

        var expected = needle.ToLowerInvariant() == "exception" ? new[] { 0 } : new[] { 1, 2, 3 };
        Assert.Equal(expected, Run(doc, new[] { needle }, block: false));
        Assert.Equal(expected, Run(doc, new[] { needle }, block: true));
    }

    /// <summary>
    /// SIMD 候选提取与标量回退结论逐位一致（块内任意位置、needle 贴块尾、混合大小写）。
    /// 无 AVX2 硬件时 SIMD 形态由 JIT 软件模拟，结论仍须一致。
    /// </summary>
    [Theory]
    [InlineData("ee")]
    [InlineData("e")]
    [InlineData("ab")]
    [InlineData("xe")]
    [InlineData("error")]
    public void 块扫描_CI_SIMD与标量回退结论一致(string needle)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 40; i++)
            sb.Append(TestData.Threadtime(T1, 1, 2 + i % 3, 'I', "Alpha",
                i % 7 == 0 ? "eE mixed ERROR" : i % 3 == 0 ? "xE aa" : "ab plain"));
        using var tmp = new TempLogFile(sb.ToString());
        using var doc = LogDocument.Build(tmp.Path);

        var simd = Run(doc, new[] { needle }, block: true, ciMode: FilterEngine.BlockScanCiMode.Simd);
        var scalar = Run(doc, new[] { needle }, block: true, ciMode: FilterEngine.BlockScanCiMode.Scalar);
        Assert.Equal(scalar, simd);
        Assert.Equal(Run(doc, new[] { needle }, block: false), simd);
    }

    // ── 2.3 非 ASCII 行回退 ──

    /// <summary>
    /// 含 İ、ß/ẞ、中文正文的文档，ASCII 与非 ASCII 过滤词（区分/忽略大小写各一遍）
    /// 命中集合与逐条路径完全一致 —— 折叠才能产生的命中（ẞ→ß）由回退路径兜住。
    /// </summary>
    [Theory]
    [InlineData("i", false)]      // İ 所在行折叠后与原始字节不同：必须走折叠路径
    [InlineData("ß", false)]      // ẞ 折叠为 ß：原始字节 E1 BA 9E ≠ C3 9F，纯字节扫描必漏
    [InlineData("错误", false)]   // 非 ASCII 过滤词：整体不进字节扫描（强制路径下也只走折叠行）
    [InlineData("error", false)]  // ASCII 词在含非 ASCII 行上的判定
    [InlineData("ß", true)]       // CS：ß 原始字节精确匹配（ẞ 行不命中）
    [InlineData("错误", true)]    // CS：中文原始字节精确匹配
    public void 块扫描_非ASCII行回退后与逐条路径完全一致(string needle, bool cs)
    {
        string content =
            TestData.Threadtime(T1, 1, 2, 'I', "Alpha", "İstanbul") +          // U+0130
            TestData.Threadtime(T2, 1, 3, 'E', "Beta", "STRASSE ẞe") +         // U+1E9E
            TestData.Threadtime(T3, 2, 4, 'D', "Gamma", "straße buß") +        // U+00DF
            TestData.Threadtime(T4, 2, 5, 'W', "Delta", "数据库错误") +
            TestData.Threadtime(T5, 3, 6, 'V', "Eps", "mixed 中文 and error text") +
            TestData.Threadtime(T5, 3, 7, 'I', "Zeta", "plain ascii error\n");
        using var tmp = new TempLogFile(content);
        using var doc = LogDocument.Build(tmp.Path);

        var expected = Run(doc, new[] { needle }, block: false, cs: cs);
        Assert.Equal(expected, Run(doc, new[] { needle }, block: true, cs: cs));
        // 回退路径存在性：ẞ→ß 在忽略大小写下必须真有命中（否则两路径同为空、测不出回退正确性）
        if (!cs && needle == "ß")
            Assert.Contains(expected, r => r == 1);
    }

    /// <summary>
    /// 分派条件（design D1）：规模与关键词可用性各自独立成条件；含非 ASCII 词、词数超上限、
    /// 小文档、候选占比过低都不走块扫描。
    /// </summary>
    [Fact]
    public void 分派_关键词可用性三条件()
    {
        // 规模条件
        Assert.True(FilterEngine.BlockScanScaleOk(2_000_000, 2_000_000));
        Assert.False(FilterEngine.BlockScanScaleOk(1_999_999, 1_999_999));
        // 候选占比：cand*8 必须 > 行数
        Assert.True(FilterEngine.BlockScanScaleOk(2_000_000, 250_001));
        Assert.False(FilterEngine.BlockScanScaleOk(2_000_000, 250_000));

        // 关键词条件
        Assert.True(FilterEngine.BlockScanKeysUsable(new[] { "error" }));
        Assert.True(FilterEngine.BlockScanKeysUsable(Enumerable.Repeat("k", 8).ToArray()));
        Assert.False(FilterEngine.BlockScanKeysUsable(Enumerable.Repeat("k", 9).ToArray()));
        Assert.False(FilterEngine.BlockScanKeysUsable(new[] { "error", "错误" }));
        Assert.True(FilterEngine.BlockScanKeysUsable(new[] { "", "error" }));   // 空词不计轮次
    }

    /// <summary>小文档下 ApplyFilter 自动走逐条路径：结果必须与强制块扫描一致。</summary>
    [Fact]
    public void 分派_小文档自动逐条与强制块扫描同结果()
    {
        string content =
            TestData.Threadtime(T1, 1, 2, 'I', "Alpha", "error one") +
            TestData.Threadtime(T2, 1, 3, 'E', "Beta", "two") +
            TestData.Threadtime(T3, 2, 4, 'D', "Gamma", "ERROR three") +
            TestData.Threadtime(T4, 2, 5, 'W', "Delta", "four error\n");
        using var tmp = new TempLogFile(content);
        using var doc = LogDocument.Build(tmp.Path);
        var spec = new FilterSpec { Msg = new[] { "error" } };

        var viaApply = FilterEngine.ApplyFilter(doc, spec, null, null, CancellationToken.None);
        Assert.False(FilterEngine.BlockScanEligible(doc, Enumerable.Range(0, doc.RowCount).ToArray(), spec));
        Assert.Equal(viaApply, Run(doc, new[] { "error" }, block: true));
    }

    // ── 2.4 组合语义 ──

    /// <summary>空词 + 实词的 and/or、exclude 开/关、多关键词交/并，块扫描与逐条路径结论一致。</summary>
    [Theory]
    [InlineData(new[] { "", "error" }, "or", false)]
    [InlineData(new[] { "", "error" }, "and", false)]
    [InlineData(new[] { "", "error" }, "or", true)]
    [InlineData(new[] { "", "error" }, "and", true)]
    [InlineData(new[] { "error", "failure" }, "and", false)]
    [InlineData(new[] { "error", "failure" }, "or", false)]
    [InlineData(new[] { "error", "failure" }, "and", true)]
    [InlineData(new[] { "error", "failure" }, "or", true)]
    [InlineData(new[] { "error", "不存在词" }, "or", false)]
    [InlineData(new[] { "error", "不存在词" }, "and", false)]
    [InlineData(new[] { "error", "不存在词" }, "or", true)]
    public void 块扫描_组合语义与逐条路径一致(string[] terms, string op, bool exclude)
    {
        string content =
            TestData.Threadtime(T1, 1, 2, 'I', "Alpha", "ERROR disk failure") +
            TestData.Threadtime(T2, 1, 3, 'E', "Beta", "error alone") +
            TestData.Threadtime(T3, 2, 4, 'D', "Gamma", "failure alone") +
            TestData.Threadtime(T4, 2, 5, 'W', "Delta", "干净正文\n");
        using var tmp = new TempLogFile(content);
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(
            Run(doc, terms, block: false, op: op, exclude: exclude),
            Run(doc, terms, block: true, op: op, exclude: exclude));
    }

    /// <summary>or + 空词短路：全部候选命中（exclude 下全部排除），不进扫描。</summary>
    [Fact]
    public void 块扫描_空词or短路全命中()
    {
        string content =
            TestData.Threadtime(T1, 1, 2, 'I', "Alpha", "abc") +
            TestData.Threadtime(T2, 1, 3, 'E', "Beta", "def\n");
        using var tmp = new TempLogFile(content);
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(new[] { 0, 1 }, Run(doc, new[] { "", "x" }, block: true));
        Assert.Empty(Run(doc, new[] { "", "x" }, block: true, exclude: true));
        Assert.Equal(new[] { 0, 1 }, Run(doc, new[] { "" }, block: true, op: "and"));
    }

    // ── 3.1 分派与进度 ──

    /// <summary>ApplyFilter 进度回调仍按序覆盖 0→1 且单调不减（小文档走逐条路径）。</summary>
    [Fact]
    public void 分派_ApplyFilter进度覆盖0到1()
    {
        string content = string.Concat(Enumerable.Range(0, 50)
            .Select(i => TestData.Threadtime(T1, 1, 2, 'I', "Alpha", $"line {i} error\n")));
        using var tmp = new TempLogFile(content);
        using var doc = LogDocument.Build(tmp.Path);
        var spec = new FilterSpec { Msg = new[] { "error" } };

        var pcts = new List<double>();
        var progress = new SynchronousProgress(p => pcts.Add(p.Item1));
        FilterEngine.ApplyFilter(doc, spec, null, progress, CancellationToken.None);

        Assert.Equal(0.0, pcts[0]);
        Assert.Equal(1.0, pcts[^1]);
        for (int i = 1; i < pcts.Count; i++)
            Assert.True(pcts[i] >= pcts[i - 1], $"进度回退：{pcts[i - 1]} → {pcts[i]}");
    }

    /// <summary>强制块扫描路径的进度：数值单调、落在 [0, 1]，且上报的 x/y 中 x ≤ y 单调不减。</summary>
    [Fact]
    public void 分派_块扫描进度单调有界()
    {
        string content = string.Concat(Enumerable.Range(0, 50)
            .Select(i => TestData.Threadtime(T1, 1, 2, 'I', "Alpha", $"line {i} error\n")));
        using var tmp = new TempLogFile(content);
        using var doc = LogDocument.Build(tmp.Path);

        var items = new List<(double, string)>();
        var progress = new SynchronousProgress(p => items.Add(p));
        var cand = Enumerable.Range(0, doc.RowCount).ToArray();
        FilterEngine.MsgFilterCore(doc, cand, new FilterSpec { Msg = new[] { "error" } },
            progress, CancellationToken.None, forceBlockScan: true, blockSizeOverride: 64);

        Assert.NotEmpty(items);
        Assert.All(items, p => Assert.InRange(p.Item1, 0.0, 1.0));
        int lastX = 0;
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0) Assert.True(items[i].Item1 >= items[i - 1].Item1);
            var m = System.Text.RegularExpressions.Regex.Match(items[i].Item2, @"(\d+)/(\d+)$");
            Assert.True(m.Success, $"进度文案不符合既有键：{items[i].Item2}");
            int x = int.Parse(m.Groups[1].Value), y = int.Parse(m.Groups[2].Value);
            Assert.Equal(cand.Length, y);
            Assert.InRange(x, lastX, y);
            lastX = x;
        }
    }

    /// <summary>取消检查逐块生效：已取消的令牌在块扫描启动时即抛出。</summary>
    [Fact]
    public void 分派_块扫描响应取消()
    {
        string content = string.Concat(Enumerable.Range(0, 50)
            .Select(i => TestData.Threadtime(T1, 1, 2, 'I', "Alpha", $"line {i} error\n")));
        using var tmp = new TempLogFile(content);
        using var doc = LogDocument.Build(tmp.Path);
        var cand = Enumerable.Range(0, doc.RowCount).ToArray();

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            FilterEngine.MsgFilterCore(doc, cand, new FilterSpec { Msg = new[] { "error" } },
                null, cts.Token, forceBlockScan: true, blockSizeOverride: 64));
    }

    /// <summary>立即上报（不走 Progress&lt;T&gt; 的同步队列），块扫描内部多线程 Report 不经调度器。</summary>
    sealed class SynchronousProgress : IProgress<(double, string)>
    {
        readonly Action<(double, string)> _action;
        readonly object _lock = new();

        public SynchronousProgress(Action<(double, string)> action) => _action = action;

        public void Report((double, string) value)
        {
            lock (_lock) _action(value);
        }
    }
}
