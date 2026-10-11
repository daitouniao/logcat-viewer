using logcat.Models;
using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// 崩溃信息提取（F-02）纯函数测试。验收口径 ANALYSIS-2.1 §7.1：每类 1 正 + 2 反，
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
        // 包名提取的反例（§7.1「无 Process: 行的崩溃」）：事件不成立
        var text = string.Join("\n",
            "FATAL EXCEPTION: main",
            "java.lang.IllegalStateException: boom",
            "    at com.example.app.Foo.bar(Foo.java:10)");

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

/// <summary>崩溃事件聚合（F-05）测试：分组、计数、首末时间、排序与字典解引用。</summary>
public class CrashAggregatorTests
{
    static CrashEvent E(int row, int excId, int pkgId, long ts, byte kind) => new()
    { Row = row, ExcId = excId, PkgId = pkgId, Ts = ts, Kind = kind, Pid = 869, Tid = 869, LineCount = 2 };

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
            E(5,  0, 0, 100, CrashKind.Java),    // ISE × app
            E(50, 0, 0, 200, CrashKind.Java),    // ISE × app（同组）
            E(60, 0, 1, 150, CrashKind.Java),    // ISE × pay
            E(70, 1, -1, 300, CrashKind.Native), // SIGSEGV（native 无包名）
            E(80, 1, -1, 400, CrashKind.Native),
        };

        var groups = CrashAggregator.Aggregate(crashes, exc, pkgs);

        Assert.Equal(3, groups.Count);
        // 次数降序；同次数按首见时间升序（app 首见 100 早于 native 首见 300）
        Assert.Equal(2, groups[0].Count);
        Assert.Equal("java.lang.IllegalStateException", groups[0].ExcType);
        Assert.Equal("com.example.app", groups[0].Pkg);
        Assert.Equal(100, groups[0].FirstTs);
        Assert.Equal(200, groups[0].LastTs);
        Assert.Equal(5, groups[0].FirstRow);

        Assert.Equal(2, groups[1].Count);
        Assert.Equal(CrashKind.Native, groups[1].Kind);
        Assert.Equal("SIGSEGV", groups[1].ExcType);
        Assert.Equal("", groups[1].Pkg);
        Assert.Equal(70, groups[1].FirstRow);

        Assert.Equal(1, groups[2].Count);
        Assert.Equal("com.example.pay", groups[2].Pkg);
    }
}

/// <summary>
/// LogDocument 崩溃提取集成（F-02）：独立行形态、缩进合并形态（c 轨补扫）、
/// Join=false 仍提取、Append 行号偏移与 Ts 回填、跨批 carry 补增 LineCount。
/// </summary>
public class CrashDocumentTests
{
    const string T1 = "09-23 18:00:00.000";
    const string T2 = "09-23 18:00:01.000";
    const string T3 = "09-23 18:00:02.000";
    const string T4 = "09-23 18:00:03.000";

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
        Assert.Equal(doc.Ts[0], ce.Ts);                   // Assemble 从列终算回填
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

    // ── 增量追加 ──

    [Fact]
    public void Append_崩溃行号加偏移且Ts回填()
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
        Assert.Equal(doc.Ts[1], ce.Ts);
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
