using System.Text.RegularExpressions;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// <b>CI 语言回归防线</b>：扫出「会在英文 runner 上失配」的中文断言写法。
///
/// <para>
/// <b>背景：GitHub CI 连续 4 次因语言失配而红</b>（2026-10-09 ×2、10-10、10-18），
/// 每次都是同一个机制：
///
/// <list type="number">
/// <item>被测输出走 <c>Loc.T</c>/<c>Loc.F</c>，内容随<b>界面语言</b>变化；</item>
/// <item>界面语言在 <c>Loc</c> 静态构造期按 <c>CultureInfo.CurrentUICulture</c> 探测；
/// 仓库无 <c>settings.json</c> 时完全由系统区域决定——<b>本地 zh-CN、CI runner en-US</b>；</item>
/// <item>测试把期望值硬编码成中文，于是<b>本地全绿、CI 必红</b>。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>为什么不能靠「每次记得锁语言」</b>：那靠人自觉，已经失败 4 次了。
/// 本类把判据自动化——扫测试源码，找出高危组合并当场报错。
/// </para>
///
/// <para>
/// <b>判据（三个条件同时满足才算高危）</b>：
///
/// <list type="number">
/// <item>断言里出现<b>中文字面量</b>（<c>Assert.Equal("中文", …)</c> 之类）；</item>
/// <item>该字面量<b>恰好是本地化资源键</b>（在 <c>Loc.En</c> 表里）——
/// 只有这种才说明被测输出<b>会随语言变化</b>；普通数据字面量（如 "别名"、"正文"）无关；</item>
/// <item>所在测试<b>没有锁定语言</b>（既未用 <c>LangScope.InZh/InEn</c>，
/// 也未用 <c>Loc.SetLangGuard</c>）。</item>
/// </list>
///
/// <para>
/// <b>豁免机制</b>：确实需要硬编码中文的用例，可在本类登记
/// <see cref="KnownLocaleSensitiveAssertions"/>（须写明理由）——留白名单而不是放宽判据，
/// 白名单本身也会被审阅。
/// </para>
/// </summary>
public class CiLocaleAssertionTests
{
    /// <summary>
    /// 已知的「硬编码中文断言但确实安全」清单。<b>每条都要写理由</b>——
    /// 空理由等于没豁免。
    ///
    /// <para>
    /// 判据是「该字面量虽在译文表里，但被测输出<b>不是</b>界面文案」：
    /// 例如 <c>CommandStore</c> 的内置分类，<c>Category</c> 存的是中文原文
    /// （<c>CategoryZh</c> 才是它的本地化副本），断言它与界面语言无关。
    /// </para>
    /// </summary>
    static readonly Dictionary<string, string> KnownLocaleSensitiveAssertions = new()
    {
        // CommandStore 的内置条目：Category / Remark 存的是**中文原文**（持久化字段），
        // 本地化副本是 CategoryZh / RemarkZh，界面显示走 CategoryText / RemarkText。
        // 断言中文原文与界面语言无关。
        ["tests/logcat.Tests/CommandStoreTests.cs: Seed生成的收藏带备注且分类合法() 断言了本地化文案 \"日志与异常\""] =
            "Category 字段存的是内置条目的中文原文（本地化副本是 CategoryZh），与界面语言无关",
        ["tests/logcat.Tests/CommandStoreTests.cs: Seed生成的收藏带备注且分类合法() 断言了本地化文案 \"清空日志缓冲区\""] =
            "Remark 字段存的是内置条目的中文原文（本地化副本是 RemarkZh），与界面语言无关",
        ["tests/logcat.Tests/CommandStoreTests.cs: AddFavorite的空白备注归一为null() 断言了本地化文案 \"备注\""] =
            "Remark 字段是用户输入的原文（界面显示走 RemarkText），与界面语言无关",
    };

    /// <summary>
    /// 硬编码中文断言 + 字面量是本地化资源键 + 未锁语言 → 报错。
    /// </summary>
    [Fact]
    public void 测试中的中文断言不得依赖界面语言()
    {
        var repoRoot = FindRepoRoot();
        var bad = new SortedSet<string>();

        int scannedFiles = 0, scannedAsserts = 0, expectedLiterals = 0;

        foreach (var file in EnumerateTestFiles(repoRoot))
        {
            var rel = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            var src = BlankOutComments(File.ReadAllText(file));
            scannedFiles++;

            // 类级语言锁：在字段初始化 / 构造函数里 SetLangGuard，整个类的断言都锁着。
            // FileProxyLaunchTests 就是这种写法（ctor 里固定锁中文，Dispose 还原）——
            // 只看方法体会把它误判成「没锁语言」。
            bool classLocksLang = Regex.IsMatch(src, @"SetLangGuard\s*\(");

            foreach (var (method, body) in EnumerateMethods(src))
            {
                // 方法级语言锁：LangScope.InZh/InEn 或 Loc.SetLangGuard
                bool locksLang = classLocksLang
                             || Regex.IsMatch(body, @"LangScope\.(InZh|InEn)\s*\(")
                             || Regex.IsMatch(body, @"Loc\.SetLangGuard\s*\(");

                foreach (var (literal, actual) in EnumerateExpectedChineseLiterals(body))
                {
                    scannedAsserts++;
                    expectedLiterals++;

                    // 条件 2：字面量是否是本地化资源键
                    if (!Services.Loc.En.ContainsKey(literal)) continue;

                    // 条件 3：所在方法未锁语言
                    if (locksLang) continue;

                    var key = $"{rel}: {method}() 断言了本地化文案 \"{literal}\"";
                    if (KnownLocaleSensitiveAssertions.ContainsKey(key)) continue;
                    bad.Add(key + "（该文案随界面语言变化，英文 runner 上会失配；" +
                           "请改用 LangScope.InZh/InEn 双语各断言一次）");
                }
            }
        }

        // 防「扫描器整体失效 → 假绿」：断言确实扫到了东西
        Assert.True(scannedFiles > 10,
            $"只扫到 {scannedFiles} 个测试文件，扫描逻辑大概率坏了（这个测试会变成假绿）");
        Assert.True(scannedAsserts > 20,
            $"只扫到 {scannedAsserts} 处期望值位置的中文断言，扫描逻辑大概率坏了（这个测试会变成假绿）");
        Assert.True(expectedLiterals > 20,
            $"只扫到 {expectedLiterals} 个中文期望值，扫描逻辑大概率坏了（这个测试会变成假绿）");

        Assert.True(bad.Count == 0,
            "以下断言硬编码了本地化文案却没锁定界面语言——本地 zh-CN 全绿，GitHub CI 的 en-US runner 必红：\n"
            + string.Join("\n", bad));
    }

    /// <summary>
    /// 扫到的中文断言总数与「高危数」都应被记录，便于排查扫描器行为。
    /// </summary>
    /// <remarks>
    /// 这条本身不设失败条件（是诊断用例），但它保证「高危集合为空」不是因为
    /// 扫描器什么都没扫到——那条由主用例的 <c>scannedAsserts</c> 断言兜底。
    /// </remarks>
    [Fact]
    public void 扫描器能区分资源键与普通数据字面量()
    {
        // 校准数据：确认「是资源键」与「不是资源键」的判定符合预期
        Assert.True(Services.Loc.En.ContainsKey("日志与异常"), "「日志与异常」应是资源键（分类显示名）");
        Assert.False(Services.Loc.En.ContainsKey("别名"), "「别名」不是资源键（普通数据字面量）");
        Assert.False(Services.Loc.En.ContainsKey("正文"), "「正文」不是资源键（测试数据）");
    }

    // ── 源码扫描工具 ──

    /// <summary>枚举测试工程的源文件（排除 bin/obj 与本文件自身）。</summary>
    static IEnumerable<string> EnumerateTestFiles(string repoRoot)
    {
        var dir = Path.Combine(repoRoot, "tests", "logcat.Tests");
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.TopDirectoryOnly))
        {
            var rel = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            if (rel.Contains("/bin/") || rel.Contains("/obj/")) continue;
            if (rel.EndsWith("CiLocaleAssertionTests.cs", StringComparison.Ordinal)) continue;
            yield return file;
        }
    }

    /// <summary>
    /// 粗粒度切出「方法体」：按 <c>{</c> … <c>}</c> 配平，不做语法解析。
    /// 与 <c>LocTableTests.EnumerateMethods</c> 同一套做法。
    /// </summary>
    static IEnumerable<(string Method, string Body)> EnumerateMethods(string src)
    {
        var rx = new Regex(@"(?:void|bool|string|int|async\s+Task|Task)\s+(\w+)\s*\([^;{)]*\)\s*\{");
        foreach (Match m in rx.Matches(src))
        {
            int open = m.Index + m.Length - 1;
            int close = FindMatching(src, open, '{', '}');
            if (close < 0) continue;
            yield return (m.Groups[1].Value, src[(open + 1)..close]);
        }
    }

    /// <summary>
    /// 取出方法体里所有 <c>Assert.*(期望值, 实际值…)</c> 断言中，<b>期望值位置</b>的
    /// 中文字面量。
    ///
    /// <para>
    /// <b>只取第一个实参（期望值位置）</b>——这是关键，否则会大量误报：
    /// <c>Assert.Equal(new[] { 3 }, Apply(doc, new FilterSpec { Msg = new[] { "日志" } }))</c>
    /// 里的中文在<b>筛选词</b>（测试数据）而非期望值，与界面语言无关，误报。
    /// </para>
    ///
    /// <para>
    /// 返回值第二项是<b>实际值表达式</b>，供调用方判断被测输出是否真的会随语言变化。
    /// </para>
    /// </summary>
    static IEnumerable<(string Literal, string Actual)> EnumerateExpectedChineseLiterals(string body)
    {
        var assertRx = new Regex(@"\bAssert\.(Equal|Contains|StartsWith|DoesNotContain|EqualIgnoreCase)\s*\(");
        var zh = new Regex(@"[\u4e00-\u9fff]");

        foreach (Match am in assertRx.Matches(body))
        {
            int open = am.Index + am.Length - 1;
            int close = FindMatching(body, open, '(', ')');
            if (close < 0) continue;

            var args = body[(open + 1)..close];
            var parts = SplitTopLevel(args);
            if (parts.Count == 0) continue;

            var expected = parts[0].Trim();
            // 期望值必须是一个纯字符串字面量（不是数组、不是变量、不是拼接）
            if (!expected.StartsWith("\"", StringComparison.Ordinal)) continue;
            if (expected.Length < 2 || !expected.EndsWith("\"", StringComparison.Ordinal)) continue;

            var lit = expected[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
            if (!zh.IsMatch(lit)) continue;

            string actual = parts.Count > 1 ? parts[1].Trim() : "";
            yield return (lit, actual);
        }
    }

    /// <summary>按顶层逗号切分实参，正确跳过嵌套括号、字符串字面量与集合初始化器。</summary>
    static List<string> SplitTopLevel(string s)
    {
        var args = new List<string>();
        var cur = new System.Text.StringBuilder();
        int depth = 0;
        bool inStr = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (inStr)
            {
                cur.Append(c);
                if (c == '\\' && i + 1 < s.Length) { cur.Append(s[++i]); continue; }
                if (c == '"') inStr = false;
                continue;
            }
            switch (c)
            {
                case '"': inStr = true; cur.Append(c); break;
                case '(' or '[' or '{': depth++; cur.Append(c); break;
                case ')' or ']' or '}': depth--; cur.Append(c); break;
                case ',' when depth == 0: args.Add(cur.ToString()); cur.Clear(); break;
                default: cur.Append(c); break;
            }
        }
        if (cur.ToString().Trim().Length > 0) args.Add(cur.ToString());
        return args;
    }

    /// <summary>括号配平，跳过字符串字面量里的括号与转义引号。</summary>
    static int FindMatching(string s, int open, char openCh, char closeCh)
    {
        int depth = 0;
        bool inStr = false;
        for (int i = open; i < s.Length; i++)
        {
            char c = s[i];
            if (inStr)
            {
                if (c == '\\') { i++; continue; }
                if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') { inStr = true; continue; }
            if (c == openCh) depth++;
            else if (c == closeCh && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>把注释替换成等长空格（保留换行以维持结构）。</summary>
    static string BlankOutComments(string src)
    {
        var sb = new System.Text.StringBuilder(src.Length);
        bool inStr = false, inLine = false, inBlock = false;
        for (int i = 0; i < src.Length; i++)
        {
            char c = src[i];
            if (inLine) { if (c == '\n') { inLine = false; sb.Append(c); } else sb.Append(' '); continue; }
            if (inBlock)
            {
                if (c == '*' && i + 1 < src.Length && src[i + 1] == '/') { sb.Append("  "); i++; inBlock = false; }
                else sb.Append(c == '\n' ? '\n' : ' ');
                continue;
            }
            if (inStr)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < src.Length) { sb.Append(src[++i]); continue; }
                if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') { inStr = true; sb.Append(c); continue; }
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '/') { inLine = true; sb.Append("  "); i++; continue; }
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '*') { inBlock = true; sb.Append("  "); i++; continue; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 仓库根目录（编译期由 <c>AssemblyMetadata("RepoRoot")</c> 注入）。
    /// </summary>
    static string FindRepoRoot()
    {
        var attr = typeof(CiLocaleAssertionTests).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepoRoot");
        var root = attr?.Value;
        Assert.False(string.IsNullOrEmpty(root), "缺少 AssemblyMetadata(\"RepoRoot\")，检查 logcat.Tests.csproj");
        Assert.True(File.Exists(Path.Combine(root!, "logcat.csproj")), $"注入的 RepoRoot 不对：{root}");
        return root!;
    }
}
