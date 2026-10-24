using logcat.Models;
using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// 崩溃信息提取纯函数测试。用例口径：每类 1 正 + 2 反，
/// 误报是重点（业务日志打印 FATAL EXCEPTION / signal 字样、非白名单信号、ANR 缩写词）。
/// </summary>
public class CrashExtractorTests
{
    const string JavaCrash = """
        FATAL EXCEPTION: main
        Process: com.example.app, PID: 869
        java.lang.IllegalStateException: boom
            at com.example.app.Foo.bar(Foo.java:10)
        """;

    // ── Java ──

    [Fact]
    public void Java_标准崩溃块提取出类型与包名()
    {
        var m = CrashExtractor.TryExtract(JavaCrash);

        Assert.NotNull(m);
        Assert.Equal(CrashKind.Java, m!.Value.Kind);
        Assert.Equal("java.lang.IllegalStateException", m.Value.ExcType);
        Assert.Equal("com.example.app", m.Value.Pkg);
    }

    [Fact]
    public void Java_D级日志打印FATAL字样不误报()
    {
        // 无 Process: 行跟在后面 —— 业务代码打印「FATAL EXCEPTION」字样的普通日志
        var text = string.Join("\n",
            "FATAL EXCEPTION: simulated by unit test",
            "D/MyApp: this is just a D-level log printing the words",
            "D/MyApp: no process line follows",
            "D/MyApp: done");

        Assert.Null(CrashExtractor.TryExtract(text));
    }

    [Fact]
    public void Java_无Process行不提取()
    {
        // 包名提取的反例（无 Process: 行的崩溃）：事件不成立
        var text = string.Join("\n",
            "FATAL EXCEPTION: main",
            "java.lang.IllegalStateException: boom",
            "    at com.example.app.Foo.bar(Foo.java:10)");

        Assert.Null(CrashExtractor.TryExtract(text));
    }

    [Fact]
    public void Java_Process行缺PID标识不提取()
    {
        // 「Process: 」行还必须带「, PID」——缺 PID 形态的行不算 Process 行（误报第二道门）
        var text = string.Join("\n",
            "FATAL EXCEPTION: main",
            "Process: com.example.app",
            "java.lang.IllegalStateException: boom");

        Assert.Null(CrashExtractor.TryExtract(text));
    }

    // ── Native / tombstone ──

    [Fact]
    public void Native_tombstone头加信号归为tombstone()
    {
        var text = string.Join("\n",
            "*** *** *** -------------------------------",
            "Build fingerprint: 'google/redfin:redfin'",
            "ABI: 'arm64'",
            "signal 11 (SIGSEGV), code 1 (SEGV_MAPERR), fault addr 0x0");

        var m = CrashExtractor.TryExtract(text);

        Assert.NotNull(m);
        Assert.Equal(CrashKind.Tombstone, m!.Value.Kind);
        Assert.Equal("SIGSEGV", m.Value.ExcType);
        Assert.Equal("", m.Value.Pkg);   // native 提不到包名
    }

    [Fact]
    public void Native_无tombstone头归为native()
    {
        var m = CrashExtractor.TryExtract(
            "Fatal signal 11 (SIGSEGV), code 1 (SEGV_MAPERR), fault addr 0x0 in tid 869 (com.example.app)");

        Assert.NotNull(m);
        Assert.Equal(CrashKind.Native, m!.Value.Kind);
        Assert.Equal("SIGSEGV", m.Value.ExcType);
    }

    [Fact]
    public void Native_signal出现在聊天文本与URL里不误报()
    {
        Assert.Null(CrashExtractor.TryExtract("D/Chat: my wifi signal is weak today"));
        Assert.Null(CrashExtractor.TryExtract("I/Chat: coverage map at https://example.com/signal?strength=9"));
        Assert.Null(CrashExtractor.TryExtract("I/Sensor: signal 5 quality good"));
    }

    [Fact]
    public void Native_非白名单信号不提取()
    {
        // SIGKILL / SIGTERM 是正常信号，白名单外的直接放行（信号名误报的最后防线）
        Assert.Null(CrashExtractor.TryExtract("E/Kernel: Fatal signal 9 (SIGKILL), code -1 (Request terminated)"));
        Assert.Null(CrashExtractor.TryExtract("E/Kernel: Fatal signal 15 (SIGTERM), code 0"));
    }

    // ── ANR ──

    [Fact]
    public void ANR_包名形态正确时提取()
    {
        var text = string.Join("\n",
            "ANR in com.example.app (com.example.app/.MainActivity)",
            "Reason: Input dispatching timed out (Waiting to send key event)");

        var m = CrashExtractor.TryExtract(text);

        Assert.NotNull(m);
        Assert.Equal(CrashKind.Anr, m!.Value.Kind);
        Assert.Equal("com.example.app", m.Value.Pkg);
        Assert.Equal("", m.Value.ExcType);   // ANR 无异常类型
    }

    [Fact]
    public void ANR_缩写词出现在业务消息里不误报()
    {
        Assert.Null(CrashExtractor.TryExtract("I/App: user typed ANR in the search box"));
        Assert.Null(CrashExtractor.TryExtract("I/Help: what does ANR mean?"));
    }

    [Fact]
    public void ANR_非包名形态不提取()
    {
        // 首 token 不是点分包名形态 → 不成立
        Assert.Null(CrashExtractor.TryExtract("ANR in the middle of nowhere"));
        Assert.Null(CrashExtractor.TryExtract("ANR in progress for pid 123"));
    }

    [Fact]
    public void ANR_行首ANR但非ANR_in前缀不提取()
    {
        // 判据是整段前缀「ANR in 」而非缩写词：行首 ANR 后接其他内容（如多一个空格再接包名形态）不算
        Assert.Null(CrashExtractor.TryExtract("ANR  com.example.app (main thread)"));
    }

    [Fact]
    public void 包名形态_大写字母开头不认()
    {
        // 包名形态要求小写字母开头；「Com.Example.App」是类名形态，不是包名
        Assert.Null(CrashExtractor.TryExtract("ANR in Com.Example.App (Com.Example.App/.MainActivity)"));

        // Java 路径：崩溃事件仍成立（计数不丢），但包名提取留空
        var m = CrashExtractor.TryExtract(string.Join("\n",
            "FATAL EXCEPTION: main",
            "Process: Com.Example.App, PID: 1",
            "java.lang.IllegalStateException: boom"));
        Assert.NotNull(m);
        Assert.Equal("", m!.Value.Pkg);
    }

    // ── 空态 / 快筛 ──

    [Fact]
    public void 空态与普通行返回null()
    {
        Assert.Null(CrashExtractor.TryExtract(""));
        Assert.Null(CrashExtractor.TryExtract("09-23 18:00:00.000  1  2 I Tag: hello world"));
        Assert.Null(CrashExtractor.TryExtract("E/Tag: something went wrong"));
    }

    [Fact]
    public void 快筛_关键字命中与普通行放行()
    {
        Assert.True(CrashExtractor.ContainsCrashKeyword("E/AndroidRuntime: FATAL EXCEPTION: main"u8));
        Assert.True(CrashExtractor.ContainsCrashKeyword("F: signal 11 (SIGSEGV) in tid 869"u8));
        Assert.True(CrashExtractor.ContainsCrashKeyword("I/ActivityManager: ANR in com.example.app"u8));
        Assert.True(CrashExtractor.ContainsCrashKeyword("F: *** *** *** ----------------"u8));
        Assert.False(CrashExtractor.ContainsCrashKeyword("I/Tag: hello world"u8));
        Assert.False(CrashExtractor.ContainsCrashKeyword("D/Net: fatal signal handler installed"u8));
    }
}

/// <summary>崩溃事件聚合测试：分组、计数、首末行号、排序与字典解引用。</summary>
public class CrashAggregatorTests
{
    static CrashEvent E(int row, int excId, int pkgId, byte kind) => new()
    { Row = row, ExcId = excId, PkgId = pkgId, Kind = kind, Pid = 869, Tid = 869, LineCount = 2 };

    [Fact]
    public void 空清单聚合为空()
    {
        var groups = CrashAggregator.Aggregate(new List<CrashEvent>(), new List<string>(), new List<string>());
        Assert.Empty(groups);
    }

    [Fact]
    public void 按类别类型包名分组并按次数降序()
    {
        var exc = new List<string> { "java.lang.IllegalStateException", "SIGSEGV" };
        var pkgs = new List<string> { "com.example.app", "com.example.pay" };
        var crashes = new List<CrashEvent>
        {
            E(5,  0, 0, CrashKind.Java),    // ISE × app
            E(50, 0, 0, CrashKind.Java),    // ISE × app（同组）
            E(60, 0, 1, CrashKind.Java),    // ISE × pay
            E(70, 1, -1, CrashKind.Native), // SIGSEGV（native 无包名）
            E(80, 1, -1, CrashKind.Native),
        };

        var groups = CrashAggregator.Aggregate(crashes, exc, pkgs);

        Assert.Equal(3, groups.Count);
        // 次数降序；同次数按首次行号升序（app 首见行 5 早于 native 首见行 70）
        Assert.Equal(2, groups[0].Count);
        Assert.Equal("java.lang.IllegalStateException", groups[0].ExcType);
        Assert.Equal("com.example.app", groups[0].Pkg);
        Assert.Equal(5, groups[0].FirstRow);
        Assert.Equal(50, groups[0].LastRow);     // 组内末次 = 行序最后的事件

        Assert.Equal(2, groups[1].Count);
        Assert.Equal(CrashKind.Native, groups[1].Kind);
        Assert.Equal("SIGSEGV", groups[1].ExcType);
        Assert.Equal("", groups[1].Pkg);
        Assert.Equal(70, groups[1].FirstRow);
        Assert.Equal(80, groups[1].LastRow);

        Assert.Equal(1, groups[2].Count);
        Assert.Equal("com.example.pay", groups[2].Pkg);
        Assert.Equal(60, groups[2].FirstRow);
        Assert.Equal(60, groups[2].LastRow);
    }

    /// <summary>
    /// 组内时间乱序（时钟回跳）时，末次事件取<b>行序最后</b>的那条，而非时间最大的那条
    /// （spec「组内时间乱序时末次取行序最后」）。
    /// 旧口径按组内最大绝对时间取末次，原用例数据是 Ts 100→300→200→-1（期望 300）；
    /// 改行序后同数据改写为行号落位——末次为行序最后的事件（原 ts=200 的那条），
    /// 「时间未知排最后」的旧语义随之消失。
    /// </summary>
    [Fact]
    public void 组内时间乱序时末次取行序最后()
    {
        var exc = new List<string> { "E" };
        var pkgs = new List<string> { "p" };
        var crashes = new List<CrashEvent>
        {
            E(10, 0, 0, CrashKind.Java),   // 旧口径 ts 100
            E(20, 0, 0, CrashKind.Java),   // 旧口径 ts 300（旧实现的「末次」：时间最大）
            E(30, 0, 0, CrashKind.Java),   // 旧口径 ts -1（时间未知）
            E(40, 0, 0, CrashKind.Java),   // 旧口径 ts 200 → 行序最后，新口径的「末次」
        };

        var g = Assert.Single(CrashAggregator.Aggregate(crashes, exc, pkgs));

        Assert.Equal(4, g.Count);
        Assert.Equal(10, g.FirstRow);
        Assert.Equal(40, g.LastRow);
    }

    /// <summary>
    /// 输入未按行序排时末次仍取行号最大的那条（Math.Max 容忍乱序输入）：
    /// 生产路径 crashes 恒按 Row 升序，这里钉死的是「末次 = 行序最后」这个语义本身
    /// 而非输入顺序。FirstRow 与旧口径 FirstTs 一致，是「首个遇到的事件」而非最小行号。
    /// </summary>
    [Fact]
    public void 末次取行序最后_输入乱序也正确()
    {
        var exc = new List<string> { "E" };
        var pkgs = new List<string> { "p" };
        var crashes = new List<CrashEvent>
        {
            E(40, 0, 0, CrashKind.Java),
            E(10, 0, 0, CrashKind.Java),
            E(30, 0, 0, CrashKind.Java),
            E(20, 0, 0, CrashKind.Java),
        };

        var g = Assert.Single(CrashAggregator.Aggregate(crashes, exc, pkgs));

        Assert.Equal(4, g.Count);
        Assert.Equal(40, g.LastRow);   // 末次 = 行号最大，与输入顺序无关
    }

    /// <summary>
    /// 多组次数相同时按首次行号升序（spec「多组崩溃按首次行号排序」）：
    /// 行号较小的组排在前面——旧口径按首次时间戳排，仅在时钟回跳文件上有差异。
    /// </summary>
    [Fact]
    public void 多组崩溃按首次行号排序()
    {
        var exc = new List<string> { "A", "B" };
        var pkgs = new List<string> { "p" };
        var crashes = new List<CrashEvent>
        {
            E(100, 1, 0, CrashKind.Java),   // B 组，首见行 100
            E(10, 0, 0, CrashKind.Java),    // A 组，首见行 10
            E(20, 0, 0, CrashKind.Java),    // A 组第二跳
            E(110, 1, 0, CrashKind.Java),   // B 组第二跳
        };

        var groups = CrashAggregator.Aggregate(crashes, exc, pkgs);

        Assert.Equal(2, groups.Count);
        Assert.Equal(2, groups[0].Count);
        Assert.Equal(2, groups[1].Count);
        Assert.Equal(10, groups[0].FirstRow);    // 同次数 → 首次行号小的在前
        Assert.Equal("A", groups[0].ExcType);
        Assert.Equal(100, groups[1].FirstRow);
        Assert.Equal("B", groups[1].ExcType);
    }
}

/// <summary>
/// LogDocument 崩溃提取集成：独立行形态、缩进合并形态（c 轨补扫）、
/// Join=false 仍提取、Append 行号偏移、跨批 carry 补增 LineCount。
/// </summary>
public class CrashDocumentTests
{
    const string T1 = "09-23 18:00:00.000";
    const string T2 = "09-23 18:00:01.000";
    const string T3 = "09-23 18:00:02.000";
    const string T4 = "09-23 18:00:03.000";
    const string T5 = "09-23 18:00:04.000";
    const string T6 = "09-23 18:00:05.000";
    const string T7 = "09-23 18:00:06.000";
    const string T8 = "09-23 18:00:07.000";

    static string Line(string time, char level, string tag, string msg) =>
        TestData.Threadtime(time, 869, 869, level, tag, msg);

    // ── 全量构建 ──

    [Fact]
    public void Build_独立行形态的Java崩溃被提取()
    {
        // 真实 AndroidRuntime 崩溃：每行都带时间戳，是 4 条独立记录
        using var tmp = new TempLogFile(
            Line(T1, 'E', "AndroidRuntime", "FATAL EXCEPTION: main") +
            Line(T2, 'E', "AndroidRuntime", "Process: com.example.app, PID: 869") +
            Line(T3, 'E', "AndroidRuntime", "java.lang.IllegalStateException: boom") +
            Line(T4, 'E', "AndroidRuntime", "    at com.example.app.Foo.bar(Foo.java:10)"));
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(4, doc.RowCount);
        var ce = Assert.Single(doc.Crashes);
        Assert.Equal(0, ce.Row);
        Assert.Equal(CrashKind.Java, ce.Kind);
        Assert.Equal(869, ce.Pid);
        Assert.Equal(1, ce.LineCount);                    // 无续行并入
        Assert.Equal(new[] { "java.lang.IllegalStateException" }, doc.ExcTypes);
        Assert.Equal(new[] { "com.example.app" }, doc.Pkgs);
    }

    [Fact]
    public void Build_缩进合并形态走c轨补扫()
    {
        // 崩溃头是父记录的缩进续行（Join=true 默认合并），a 轨看不到 → 批末 multiline 补扫
        using var tmp = new TempLogFile(
            Line(T1, 'E', "MyApp", "doing work") +
            "    FATAL EXCEPTION: main\n" +
            "    Process: com.example.app, PID: 869\n" +
            "    java.lang.NullPointerException: x\n");
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(1, doc.RowCount);
        Assert.True(doc.IsMultiline(0));
        var ce = Assert.Single(doc.Crashes);
        Assert.Equal(0, ce.Row);
        Assert.Equal(CrashKind.Java, ce.Kind);
        Assert.Equal(4, ce.LineCount);
        Assert.Equal("java.lang.NullPointerException", Assert.Single(doc.ExcTypes));
        Assert.Equal("com.example.app", Assert.Single(doc.Pkgs));
    }

    [Fact]
    public void Build_Join为假时仍提取()
    {
        // 提取与 Join 无关（v3 口径）：关掉合并，靠行级前瞻照样拿到 Process 行
        using var tmp = new TempLogFile(
            Line(T1, 'E', "AndroidRuntime", "FATAL EXCEPTION: main") +
            Line(T2, 'E', "AndroidRuntime", "Process: com.example.app, PID: 869") +
            Line(T3, 'E', "AndroidRuntime", "java.lang.IllegalStateException: boom"));
        using var doc = LogDocument.Build(tmp.Path, join: false);

        Assert.Equal(3, doc.RowCount);
        var ce = Assert.Single(doc.Crashes);
        Assert.Equal(CrashKind.Java, ce.Kind);
        Assert.Equal("java.lang.IllegalStateException", doc.ExcTypes[ce.ExcId]);
        Assert.Equal("com.example.app", doc.Pkgs[ce.PkgId]);
    }

    [Fact]
    public void Build_普通日志无崩溃事件()
    {
        using var tmp = new TempLogFile(
            Line(T1, 'I', "Tag", "hello") +
            Line(T2, 'D', "Tag", "FATAL EXCEPTION is just a word users type"));
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(2, doc.RowCount);
        Assert.Empty(doc.Crashes);
        Assert.Empty(doc.ExcTypes);
        Assert.Empty(doc.Pkgs);
    }

    /// <summary>
    /// 三类崩溃（Java×2 / Native / ANR）同文档全量 Build：现有集成用例全是 Java 形态，
    /// Native/ANR 只在提取器单元层验过，文档层的字典去重、<c>Intern("")→-1</c>、
    /// 行号升序在这里第一次被整体锁住，并把 doc 输出直接喂给 <see cref="CrashAggregator"/> 验全链路。
    /// <para>
    /// <b>行的排布是有意的</b>：a 轨提取带 3 行前瞻，若把 ANR/Native 行放在某组
    /// 「FATAL EXCEPTION + Process」紧前面，前瞻窗口会把后组的头拼进本行文本，
    /// <c>TryJava</c> 先于 <c>TryAnr</c> 命中、ANR 会被记成 Java。所以两个 Java 块相邻在前，
    /// Native、ANR 殿后，各自的前瞻窗口里不再出现别的崩溃头。
    /// </para>
    /// </summary>
    [Fact]
    public void Build_混合类别事件字典去重且空维度取负一()
    {
        using var tmp = new TempLogFile(
            Line(T1, 'E', "AndroidRuntime", "FATAL EXCEPTION: main") +
            Line(T2, 'E', "AndroidRuntime", "Process: com.example.app, PID: 869") +
            Line(T3, 'E', "AndroidRuntime", "java.lang.IllegalStateException: boom") +
            Line(T4, 'E', "AndroidRuntime", "FATAL EXCEPTION: main") +
            Line(T5, 'E', "AndroidRuntime", "Process: com.example.app, PID: 869") +
            Line(T6, 'E', "AndroidRuntime", "java.lang.IllegalStateException: boom2") +
            Line(T7, 'E', "libc", "Fatal signal 11 (SIGSEGV), code 1 (SEGV_MAPERR), fault addr 0x0 in tid 869 (com.example.app)") +
            Line(T8, 'I', "ActivityManager", "ANR in com.example.pay (com.example.pay/.MainActivity)"));
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(4, doc.Crashes.Count);
        Assert.Equal(new[] { 0, 3, 6, 7 }, doc.Crashes.Select(c => c.Row));   // 行号升序
        Assert.Equal(new[] { CrashKind.Java, CrashKind.Java, CrashKind.Native, CrashKind.Anr },
                     doc.Crashes.Select(c => c.Kind));

        // 字典只收非空值：两条 Java 同类型同包名 intern 到同一 id；
        // ANR 无异常类型、Native 无包名 → id = -1（不进字典，解引用在聚合器里落空串）
        Assert.Equal(new[] { "java.lang.IllegalStateException", "SIGSEGV" }, doc.ExcTypes);
        Assert.Equal(new[] { "com.example.app", "com.example.pay" }, doc.Pkgs);
        Assert.Equal(doc.Crashes[0].ExcId, doc.Crashes[1].ExcId);
        Assert.Equal(doc.Crashes[0].PkgId, doc.Crashes[1].PkgId);
        Assert.Equal(-1, doc.Crashes[2].PkgId);    // Native
        Assert.Equal(-1, doc.Crashes[3].ExcId);    // ANR

        var groups = CrashAggregator.Aggregate(doc.Crashes, doc.ExcTypes, doc.Pkgs);
        Assert.Equal(3, groups.Count);
        Assert.Equal(2, groups[0].Count);                                  // Java 两条归一组
        Assert.Equal("java.lang.IllegalStateException", groups[0].ExcType);
        Assert.Equal("com.example.app", groups[0].Pkg);
        Assert.Equal("SIGSEGV", groups[1].ExcType);
        Assert.Equal("", groups[1].Pkg);                                   // -1 解引用为空串（显示层回退 "-"）
        Assert.Equal("", groups[2].ExcType);
        Assert.Equal("com.example.pay", groups[2].Pkg);
        // 同为 1 次的两组按首次行号升序：Native(T7) 早于 ANR(T8)
        Assert.Equal(6, groups[1].FirstRow);
        Assert.Equal(7, groups[2].FirstRow);
    }

    // ── 增量追加 ──

    [Fact]
    public void Append_崩溃行号加偏移()
    {
        using var tmp = new TempLogFile(Line(T1, 'I', "Tag", "a"));
        using var doc = LogDocument.Build(tmp.Path);
        Assert.Empty(doc.Crashes);

        tmp.Append(
            Line(T2, 'E', "AndroidRuntime", "FATAL EXCEPTION: main") +
            Line(T3, 'E', "AndroidRuntime", "Process: com.example.app, PID: 869") +
            Line(T4, 'E', "AndroidRuntime", "java.lang.IllegalStateException: boom"));
        Assert.Equal("appended", doc.Reload());

        Assert.Equal(4, doc.RowCount);
        var ce = Assert.Single(doc.Crashes);
        Assert.Equal(1, ce.Row);                          // 批内 Row=0 + 偏移 n=1
        Assert.Equal("java.lang.IllegalStateException", doc.ExcTypes[ce.ExcId]);
        Assert.Equal("com.example.app", doc.Pkgs[ce.PkgId]);
    }

    [Fact]
    public void Append_同类型跨批重用全局id()
    {
        var first = Line(T1, 'E', "AndroidRuntime", "FATAL EXCEPTION: main") +
                    Line(T2, 'E', "AndroidRuntime", "Process: com.example.app, PID: 869") +
                    Line(T3, 'E', "AndroidRuntime", "java.lang.IllegalStateException: boom");
        using var tmp = new TempLogFile(first);
        using var doc = LogDocument.Build(tmp.Path);
        Assert.Single(doc.Crashes);

        tmp.Append(
            Line(T4, 'E', "AndroidRuntime", "FATAL EXCEPTION: main") +
            TestData.Threadtime("09-23 18:00:04.000", 900, 900, 'E', "AndroidRuntime",
                "Process: com.example.other, PID: 900") +
            TestData.Threadtime("09-23 18:00:05.000", 900, 900, 'E', "AndroidRuntime",
                "java.lang.IllegalStateException: again"));
        Assert.Equal("appended", doc.Reload());

        Assert.Equal(2, doc.Crashes.Count);
        Assert.Equal(3, doc.Crashes[1].Row);              // 3 条旧行 + 批内第 0 行
        Assert.Equal(0, doc.Crashes[0].ExcId);
        Assert.Equal(0, doc.Crashes[1].ExcId);            // 同类型 → 复用全局 id 0
        Assert.Equal(1, doc.Crashes[1].PkgId);            // 新包名 → 追加为 id 1
        Assert.Equal(2, doc.Pkgs.Count);
        Assert.Single(doc.ExcTypes);
    }

    [Fact]
    public void Append_跨批carry补增末项LineCount()
    {
        // native 崩溃头单行即可成立（不依赖前瞻），堆栈跨批缩进并入 → LineCount 补增
        using var tmp = new TempLogFile(
            Line(T1, 'E', "Kernel", "Fatal signal 11 (SIGSEGV), code 1 (SEGV_MAPERR), fault addr 0x0"));
        using var doc = LogDocument.Build(tmp.Path);
        Assert.Equal(1, doc.Crashes[0].LineCount);

        tmp.Append("    #00 pc 0000000000012345 /system/lib64/libc.so\n" +
                   "    #01 pc 0000000000023456 /system/lib64/libfoo.so\n");
        Assert.Equal("appended", doc.Reload());           // 整批都是续行的早退分支

        Assert.Equal(1, doc.RowCount);
        var ce = doc.Crashes[0];
        Assert.Equal(3, ce.LineCount);                    // 1 + carry 2
        Assert.Equal(CrashKind.Native, ce.Kind);
        Assert.Contains("#00 pc", System.Text.Encoding.UTF8.GetString(doc.LineBytes(0)));
    }

    [Fact]
    public void Append_carry之后还有新行也补增LineCount()
    {
        using var tmp = new TempLogFile(
            Line(T1, 'E', "Kernel", "Fatal signal 11 (SIGSEGV), code 1 (SEGV_MAPERR), fault addr 0x0"));
        using var doc = LogDocument.Build(tmp.Path);

        tmp.Append("    #00 pc 0x1 /lib.so\n" + Line(T2, 'I', "Tag", "after"));
        Assert.Equal("appended", doc.Reload());

        Assert.Equal(2, doc.RowCount);
        Assert.Equal(2, doc.Crashes[0].LineCount);        // 1 + carry 1；崩溃行本身仍是 Row 0
        Assert.Equal(0, doc.Crashes[0].Row);
    }
}

/// <summary>
/// <see cref="CrashTime"/>：崩溃行的时间文本取自行首原始时间戳（spec「崩溃事件的时间呈现与排序」）。
/// 汇总行到秒、列表摘要时分秒、无时间戳返回 null（展示侧落 "-"）。
/// </summary>
public class CrashTimeTests
{
    static string Line(string time, char level, string tag, string msg) =>
        TestData.Threadtime(time, 869, 869, level, tag, msg);

    [Fact]
    public void 汇总行时间文本显示到秒()
    {
        using var tmp = new TempLogFile(Line("09-23 18:06:48.123", 'E', "Tag", "FATAL EXCEPTION: main"));
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal("09-23 18:06:48", CrashTime.GroupText(doc, 0));   // 跨天日志带日期，小数秒截掉
    }

    [Fact]
    public void 摘要时间文本只取时分秒()
    {
        using var tmp = new TempLogFile(Line("09-23 18:06:48.123", 'E', "Tag", "FATAL EXCEPTION: main"));
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal("18:06:48", CrashTime.SummaryText(doc, 0));
    }

    /// <summary>
    /// 行首无时间戳的崩溃行（brief 形态）：时间文本返回 null，展示侧落 "-"——
    /// 不显示为空、也不是不显示（spec「无时间戳崩溃行显示占位」）。
    /// </summary>
    [Fact]
    public void 无时间戳崩溃行时间文本为null()
    {
        using var tmp = new TempLogFile(
            "E/AndroidRuntime: FATAL EXCEPTION: main\n" +
            "E/AndroidRuntime: Process: com.example.app, PID: 869\n" +
            "E/AndroidRuntime: java.lang.IllegalStateException: boom\n");
        using var doc = LogDocument.Build(tmp.Path);

        var ce = Assert.Single(doc.Crashes);
        Assert.Null(CrashTime.GroupText(doc, ce.Row));
        Assert.Null(CrashTime.SummaryText(doc, ce.Row));
    }
}

/// <summary>
/// <see cref="CrashKind.Name"/> 的类别 → 显示名映射（汇总窗口的「类别」列、摘要行的类别字段）。
/// </summary>
// 本类会改动 Loc 的全局语言状态，必须与其它改语言的类串行（见 LangCollection）
[Collection(LangCollection.Name)]
public class CrashKindNameTests
{
    /// <summary>
    /// 每个已知类别都要有译文。<b>两种语言各断言一次</b>：只测一种的话，
    /// 把<code>Loc.T(...)</code> 退回硬编码中文仍然全绿，抓不住回归。
    /// <para>
    /// 顺带把「映射本身」锁住——<see cref="LocTableTests"/> 只保证这些中文键<b>存在译文</b>，
    /// 不保证「Java 类别指向的是 Java 那一行」。把<code>Java</code> 和 <code>Native</code>
///    的分支写反，LocTable 全绿但用户看到的是错类别名。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(CrashKind.Java, "Java 崩溃", "Java Crash")]
    [InlineData(CrashKind.Native, "Native 崩溃", "Native Crash")]
    [InlineData(CrashKind.Anr, "ANR 应用无响应", "ANR (App Not Responding)")]
    [InlineData(CrashKind.Tombstone, "Native 崩溃（tombstone）", "Native Crash (tombstone)")]
    public void 已知类别映射到对应显示名(byte kind, string zh, string en)
    {
        LangScope.InZh(() => Assert.Equal(zh, CrashKind.Name(kind)));
        LangScope.InEn(() => Assert.Equal(en, CrashKind.Name(kind)));
    }

    /// <summary>
    /// 未知类别（字典里没有的 byte）回退「未知 / Unknown」，不能抛也不能返回空串——
    /// 汇总窗口拿到空串会显示成一个空单元格，看着像bug。
    /// </summary>
    [Theory]
    [InlineData((byte)4)]
    [InlineData((byte)99)]
    [InlineData(byte.MaxValue)]
    public void 未知类别回退未知(byte kind)
    {
        LangScope.InZh(() => Assert.Equal("未知", CrashKind.Name(kind)));
        LangScope.InEn(() => Assert.Equal("Unknown", CrashKind.Name(kind)));
    }

    /// <summary>
    /// 四个已知类别的显示名互不相同：Tombstone 与 Native 描述的是同一件事，
    /// 显示上要能区分（tombstone 是带符号栈的 native 崩溃），但中文名不能撞车到看不出区别。
    /// </summary>
    [Fact]
    public void 各已知类别显示名互不相同()
    {
        LangScope.InZh(() =>
        {
            var names = new[] { CrashKind.Java, CrashKind.Native, CrashKind.Anr, CrashKind.Tombstone }
                .Select(CrashKind.Name).ToArray();
            Assert.Equal(names.Length, names.Distinct().Count());
        });
        LangScope.InEn(() =>
        {
            var names = new[] { CrashKind.Java, CrashKind.Native, CrashKind.Anr, CrashKind.Tombstone }
                .Select(CrashKind.Name).ToArray();
            Assert.Equal(names.Length, names.Distinct().Count());
        });
    }
}
