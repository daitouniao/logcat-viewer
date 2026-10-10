using System.Text;
using System.Text.RegularExpressions;
using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// 字符串表的一致性测试。这些断言的价值在于「改动 UI 文案时把维护约定强制落地」：
/// 漏加译文、占位符数量对不上、译文里混进中文，都会被这里拦下，
/// 而不是等到英文界面跑起来才肉眼发现。
/// </summary>
// 本类会改动 Loc 的全局语言状态，必须与其它改语言的类串行（见 LangCollection）
[Collection(LangCollection.Name)]
public class LocTableTests
{
    static IReadOnlyDictionary<string, string> En => Loc.En;

    [Fact]
    public void 英文表非空()
    {
        Assert.True(En.Count > 400, $"英文译文表只有 {En.Count} 条，迁移可能不完整");
    }

    [Fact]
    public void 译文不得为空()
    {
        var empty = En.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).ToList();
        Assert.True(empty.Count == 0, "以下键的译文为空：" + string.Join(" / ", empty));
    }

    [Fact]
    public void 译文不得包含中文字符()
    {
        // 允许的例外：中文本身作为语言名展示（简体中文），以及产品专名
        var allowed = new HashSet<string> { "简体中文" };
        var bad = En
            .Where(kv => !allowed.Contains(kv.Key) && Regex.IsMatch(kv.Value, @"[\u4e00-\u9fff]"))
            .Select(kv => $"{kv.Key} => {kv.Value}")
            .ToList();
        Assert.True(bad.Count == 0, "以下译文里混进了中文：\n" + string.Join("\n", bad));
    }

    [Fact]
    public void 占位符个数与中文原文一致()
    {
        // 少一个占位符 -> string.Format 抛 FormatException（Loc.F 兜底成不格式化的串，
        // 界面会出现裸花括号）；多一个 -> 参数被忽略，英文里少信息。
        var bad = En
            .Where(kv => RealPlaceholders(kv.Key).Count != RealPlaceholders(kv.Value).Count)
            .Select(kv => $"[{RealPlaceholders(kv.Key).Count}→{RealPlaceholders(kv.Value).Count}] {kv.Key} => {kv.Value}")
            .ToList();
        Assert.True(bad.Count == 0, "以下条目的占位符个数不一致：\n" + string.Join("\n", bad));
    }

    [Fact]
    public void 占位符序号连续且从0开始()
    {
        var bad = new List<string>();
        foreach (var kv in En)
        {
            var idx = RealPlaceholders(kv.Value);
            for (int i = 0; i < idx.Count; i++)
                if (idx[i] != i) { bad.Add($"{kv.Key} => {kv.Value}"); break; }
        }
        Assert.True(bad.Count == 0, "以下译文的占位符序号不连续：\n" + string.Join("\n", bad));
    }

    /// <summary>
    /// 同一个键只能在<summary>一个</summary>译文表里定义一次。
    ///
    /// <para>
    /// <c>Loc.BuildEn</c> 用 <c>d[kv.Key] = kv.Value</c> 合并各分区，
    /// <b>重���不报编译错也不报运行错</b>，只是后者静默覆盖前者。
    /// 实测踩过：<c>APK 路径</c> 同时写在 ApkUi("APK Path") 与 CommandUi("APK path")，
    /// <c>如 05 20（空格分隔）</c> 同时写在 FilterUi 与 MainUi，
    /// 三组译文互相冲突——改其中一处看不出任何异常，英文界面却变了。
    /// </para>
    /// </summary>
    [Fact]
    public void 同一个键不得在多个分区重复定义()
    {
        var seen = new Dictionary<string, (string Part, string Val)>(StringComparer.Ordinal);
        var dup = new SortedSet<string>();

        foreach (var (name, part) in EnumerateTableParts())
            foreach (var kv in part)
            {
                if (seen.TryGetValue(kv.Key, out var prev))
                    dup.Add($"{kv.Key}\n    {prev.Part}: {prev.Val}\n    {name}: {kv.Value}" +
                           (prev.Val == kv.Value ? "   （译文相同，仍是重复定义）" : "   <== 译文不一致！"));
                else
                    seen[kv.Key] = (name, kv.Value);
            }

        Assert.True(dup.Count == 0,
            $"以下键被多个分区重复定义（后者静默覆盖前者，译文会随分区顺序漂移）：\n{string.Join("\n", dup)}");
    }

    /// <summary>
    /// 逐个分区枚举译文。<b>顺序必须与 <c>Loc.BuildEn</c> 完全一致</c>，
    /// 否则本测试的「谁覆盖谁」判断与运行时不一致。
    /// </summary>
    static IEnumerable<(string Name, Dictionary<string, string> Part)> EnumerateTableParts()
        => new (string, Dictionary<string, string>)[]
        {
            ("Common",       LocTable.Common()),
            ("MainUi",       LocTable.MainUi()),
            ("FilterUi",     LocTable.FilterUi()),
            ("DeviceUi",     LocTable.DeviceUi()),
            ("ApkUi",        LocTable.ApkUi()),
            ("CommandUi",    LocTable.CommandUi()),
            ("CommandUiMain",LocTable.CommandUiMain()),
            ("FileUi",       LocTable.FileUi()),
            ("RuntimeMsg",   LocTable.RuntimeMsg()),
            ("CommandSeed",  LocTable.CommandSeed()),
        };

    /// <summary>
    /// 译文表里不该残留「拼接碎片」键。
    ///
    /// <para>
    /// 拼接式字面量（<c>const string HINT = "前半；" + "后半。"</c>）在改成
    /// <c>Loc.Bind(label, HINT)</c> 后，键必须是<b>拼好后的整串</b>。
    /// 早期迁移时把两个半句也各自收进表里，看着齐全，实际调用点一个都匹配不上——
    /// 英文界面整段回退显示中文，而「本地化调用点的中文键都有译文」那条断言
    /// <b>照样通过</b>（它只看调用点有没有译文，不看表里有没有多余的）。
    /// </para>
    ///
    /// <para>
    /// <b>判据不能只看「以中文标点结尾」</b>：<c>"卸载失败："</c>、<c>"分类："</c>
    /// 这类本身��是完整的标签文案（后面直接跟控件值），那样判会误报一大片。
    /// 真正的碎片一定满足「以中文标点结尾」<b>且</b>「表里还存在一个以它为前缀的更长整串键」——
    /// 也就是它只可能被拼接式常量用到。
    /// </para>
    /// </summary>
    [Fact]
    public void 译文表不得残留拼接产生的半句键()
    {
        var all = En.Keys.ToList();
        var bad = new SortedSet<string>();

        foreach (var k in all)
        {
            if (k.Length == 0 || !"，、；：。！？".Contains(k[^1])) continue;

            // 存在「以本键为前缀的更长键」=> 本键是它拆分出来的碎片
            var whole = all.FirstOrDefault(x => x.Length > k.Length && x.StartsWith(k, StringComparison.Ordinal));
            if (whole != null)
                bad.Add($"{k}\n    → 疑似碎片，整串键为：{whole}");
        }

        Assert.True(bad.Count == 0,
            "以下键疑似拼接式字面量的半句（对应整串键已在表里，半句永远匹配不到调用点）：\n"
            + string.Join("\n", bad));
    }

    /// <summary>
    /// 表里的键必须真的是<b>拼接后的整串</b>。对 <c>const string X = "…" + "…"</c>
    /// 形式的说明文案，断言其整串与译文表里的某个键精确相等——
    /// 这条比「半句残留」那更硬：它直接咬住调用点与表的契约。
    /// </summary>
    [Theory]
    [InlineData("Forms/CommandEditDialog.cs", "HINT")]
    [InlineData("Forms/RunAsDialog.cs", "HINT")]
    [InlineData("Forms/CommandDialog.cs", "HINT_TEXT")]
    public void 拼接式说明文案的整串必须有译文(string relFile, string constName)
    {
        var repoRoot = FindRepoRoot();
        var path = Path.Combine(repoRoot, relFile.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"找不到源文件 {relFile}");

        var code = BlankOutComments(File.ReadAllText(path));
        int at = code.IndexOf("const", StringComparison.Ordinal);
        while (at >= 0)
        {
            // 定位 `const string <name> =` 后面的字面量拼接链
            int eq = code.IndexOf('=', at);
            int semi = code.IndexOf(';', at);
            if (eq > 0 && semi > eq)
            {
                // 跳过 `const string NAME` 这段声明头
                int nameAt = code.IndexOf(constName, at, StringComparison.Ordinal);
                if (nameAt > at && nameAt < eq)
                {
                    var chain = code[(eq + 1)..semi];
                    var full = string.Concat(UnquoteAll(chain));
                    if (full.Length > 0)
                    {
                        Assert.True(En.ContainsKey(full),
                            $"{relFile} 的 `const {constName}` 拼出的整串在译文表里没有对应键：\n  {full}\n" +
                            "（拼接式常量必须整串收进表，且不能只收半句）");
                        return;
                    }
                }
            }
            at = code.IndexOf("const", at + 5, StringComparison.Ordinal);
        }
        Assert.Fail($"{relFile} 里找不到 `const {constName}` 的声明");
    }

    /// <summary>取一段拼接链里所有字符串字面量并求值（只处理普通与 verbatim 串）。</summary>
    static List<string> UnquoteAll(string s)
    {
        var vals = new List<string>();
        for (int i = 0; i < s.Length; i++)
        {
            if (i + 1 < s.Length && s[i] == '@' && s[i + 1] == '"')
            {
                int end = s.IndexOf('"', i + 2);
                if (end < 0) break;
                vals.Add(s[(i + 2)..end].Replace("\"\"", "\""));
                i = end;
                continue;
            }
            if (s[i] == '"')
            {
                var sb = new StringBuilder();
                int j = i + 1;
                while (j < s.Length && s[j] != '"')
                {
                    if (s[j] == '\\' && j + 1 < s.Length) { sb.Append(Regex.Unescape(s[j].ToString() + s[j + 1])); j += 2; continue; }
                    sb.Append(s[j]); j++;
                }
                vals.Add(sb.ToString());
                i = j;
                continue;
            }
        }
        return vals;
    }

    /// <summary>
    /// 传给 <c>Loc.*</c> 的每个中文字面量都该有译文。漏翻不会崩（<c>Loc.T</c> 回退显示中文），
    /// 但英文界面里混中文正是要避免的，所以这里断言「不漏」。
    ///
    /// <para>
    /// <b>只扫 <c>Loc.*</c> 调用点，不扫所有字面量</b>——诊断日志（<c>StartupLog</c> /
    /// <c>DpiDiag</c> / <c>_logger</c>）与异常消息按设计保持中文不翻译，
    /// 全量扫字面量会把它们误判成漏翻。
    /// </para>
    /// </summary>
    [Fact]
    public void 本地化调用点的中文键都有译文()
    {
        var repoRoot = FindRepoRoot();
        var missing = new SortedSet<string>();

        // 扫不到东西就等于没测。断言必须先证明扫描器本身活着——
 // 这个坑踩过：成员访问解析写错时扫描器返回 0 条，"没有漏翻"就成了假绿。
        int scanned = 0;
        foreach (var (_, _) in EnumerateLocCallKeys(repoRoot)) scanned++;
        Assert.True(scanned > 300,
            $"源码扫描器只找到 {scanned} 个本地化调用点，解析逻辑大概率坏了（这个测试会变成假绿）");

        foreach (var (file, fmtKey) in EnumerateLocCallKeys(repoRoot))
        {
            var rel = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            if (!En.ContainsKey(fmtKey)) missing.Add($"{rel}: {fmtKey}");
        }

        Assert.True(missing.Count == 0,
            $"有 {missing.Count} 个本地化调用点缺译文（英文态会回退显示中文）：\n" + string.Join("\n", missing));
    }

    /// <summary>
    /// 校验 <c>Loc.F</c> 调用点的占位符个数与实参个数一致。
    /// 这是迁移插值串时最容易踩的坑：<c>$"为 {{{name}}} 输入值："</c> 转成
    /// <c>Loc.F("为 {{0}} 输入值：", name)</c> 后，<c>string.Format</c> 会把 <c>{{0}}</c>
    /// 当成<b>转义字面花括号</b>，参数名直接丢失、界面上只剩「{0}」。
    /// </summary>
    [Fact]
    public void 本地化格式串的占位符数与实参个数一致()
    {
        var repoRoot = FindRepoRoot();
        var bad = new SortedSet<string>();

        int scanned = 0;
        foreach (var (_, _, _) in EnumerateLocFCalls(repoRoot)) scanned++;
        Assert.True(scanned > 50,
            $"源码扫描器只找到 {scanned} 个 Loc.F 调用点，解析逻辑大概率坏了（这个测试会变成假绿）");

        foreach (var (file, fmt, args) in EnumerateLocFCalls(repoRoot))
        {
            var rel = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            var ph = RealPlaceholders(fmt);
            if (ph.Count != args.Count)
                bad.Add($"{rel}: 占位符 {ph.Count} 个 vs 实参 {args.Count} 个 -> \"{fmt}\"");
        }

        Assert.True(bad.Count == 0,
            $"以下 Loc.F 的占位符与实参个数不符（string.Format 会抛或静默丢参数）：\n" + string.Join("\n", bad));
    }

    /// <summary>
    /// <c>Loc.Bind*</c> 的资源键必须是<b>中文字面量</b>，不得是 <c>Loc.T/F(...)</c> 的返回值。
    ///
    /// <para>
    /// 这是 2026-10-07 实际踩过的坑：写成 <c>Loc.Bind(new ToolStripMenuItem(Loc.T("帮助")), Loc.T("帮助"))</c>，
    /// 英文态下 <c>Loc.T</c> 返回 "Help"，于是登记进表的"资源键"变成了 "Help"——
    /// 而 <c>En</c> 表的键是中文，<c>Loc.T("Help")</c> 两边都查不到，
    /// 该控件就<b>永久卡在英文</b>，切回中文也不动。编译期无警告、运行期无异常，纯静默。
    /// </para>
    /// </summary>
    [Fact]
    public void 绑定资源键不得是已译文本()
    {
        var repoRoot = FindRepoRoot();
        var bad = new SortedSet<string>();

        int scanned = 0;
        foreach (var (file, meth, args) in EnumerateBindCalls(repoRoot))
        {
            scanned++;
            var rel = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            // 资源键是该方法的最后一个实参（Bind/BindTip/BindHint/BindCol 都是 (obj, zh) 形态）
            var keyArg = args[^1].Trim();
            if (keyArg.StartsWith("Loc.", StringComparison.Ordinal))
                bad.Add($"{rel}: {meth}(..., {keyArg})");
        }

        Assert.True(scanned > 0, "源码扫描器一个 Loc.Bind* 调用点都没找到，解析逻辑坏了（这个测试会变成假绿）");
        Assert.True(bad.Count == 0,
            $"以下绑定把 Loc.T/F 的返回值当成了资源键（英文态下键会变成译文，控件永久卡在英文）：\n"
            + string.Join("\n", bad));
    }

    /// <summary>
    /// <b>长驻</b>的菜单 / 工具栏 / 状态栏项不得用 <c>Loc.T(...)</c> 一次性赋值。
    ///
    /// <para>
    /// <c>Loc.T(...)</c> 只在构造那一刻求值，控件与资源键之间没有任何记录，
    /// <c>Loc.ApplyTo</c> 遍历控件树时查不到绑定、切语言时原样跳过——
    /// 用户看到的就是「英文→中文，主页面纹丝不动」。要绑就用 <c>Loc.Bind</c>。
    /// </para>
    ///
    /// <para>
    /// <b>只查长驻项</b>（挂在字段级 ToolStrip / StatusStrip 上的），不查事件里临时 new 出来的
    /// 右键菜单项：那些项每次打开都重建，<c>Loc.T</c> 在那里是<b>正确</b>写法，
    /// 绑了反而是无谓开销。判据是「是否出现在 <c>Items.AddRange(new ToolStripItem[] { … })</c>
    /// 数组字面量里」—— 长驻项都是批量挂载的，动态项都是 <c>menu.Items.Add(单个)</c>。
    /// </para>
    /// </summary>
    [Fact]
    public void 长驻菜单工具栏项不得用LocT一次性赋值()
    {
        var repoRoot = FindRepoRoot();
        var bad = new SortedSet<string>();

        string[] mustBind =
        {
            "ToolStripMenuItem", "ToolStripButton", "ToolStripLabel", "ToolStripStatusLabel",
        };

        int scanned = 0;
        foreach (var file in EnumerateSourceFiles(repoRoot))
        {
            var rel = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            var src = BlankOutComments(File.ReadAllText(file));

            // 只取 AddRange(new ToolStripItem[] { … }) 的数组体
            foreach (var body in EnumerateAddRangeBodies(src))
            {
                foreach (var kind in mustBind)
                {
                    var needle = $"new {kind}(";
                    int from = 0;
                    while (true)
                    {
                        int at = body.IndexOf(needle, from, StringComparison.Ordinal);
                        if (at < 0) break;
                        from = at + needle.Length;

                        int end = FindMatching(body, at + needle.Length - 1, '(', ')');
                        if (end < 0) continue;
                        var args = SplitTopLevel(body[(at + needle.Length)..end]);
                        if (args.Count == 0) continue;

                        scanned++;
                        // 第 0 个实参（控件文本）直接用了 Loc.T：没绑定，切语言不会刷新
                        if (args[0].Trim().StartsWith("Loc.", StringComparison.Ordinal))
                            bad.Add($"{rel}: new {kind}({args[0].Trim()}…)");
                    }
                }
            }
        }

        Assert.True(scanned > 0, "没扫到任何长驻菜单/工具栏项，解析逻辑坏了（这个测试会变成假绿）");
        Assert.True(bad.Count == 0,
            $"以下长驻菜单/工具栏/状态栏项用 Loc.T 一次性赋值，切语言时不会刷新（须改走 Loc.Bind）：\n"
            + string.Join("\n", bad));
    }

    /// <summary>
    /// 取出 <c>X.Items.AddRange(new ToolStripItem[] { … })</c> 里的大括号块内容。
    /// 长驻项一律批量挂载，动态项是逐个 <c>Items.Add</c>，故此判据能分开两者。
    /// </summary>
    static IEnumerable<string> EnumerateAddRangeBodies(string src)
    {
        const string marker = ".Items.AddRange(new ToolStripItem[]";
        int from = 0;
        while (true)
        {
            int at = src.IndexOf(marker, from, StringComparison.Ordinal);
            if (at < 0) yield break;
            from = at + marker.Length;

            int open = src.IndexOf('{', at);
            if (open < 0) continue;
            // 大括号配平：跳过嵌套的 new Button { … } 对象初始化器
            int depth = 0;
            int close = -1;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}' && --depth == 0) { close = i; break; }
            }
            if (close < 0) continue;
            yield return src[(open + 1)..close];
        }
    }

    /// <summary>
    /// <b>控件文本不得用中文字面量一次性赋值</b>——必须走 <c>Loc.Bind</c>。
    ///
    /// <para>
    /// 2026-10-07 用户报「英文界面里命令窗口的 root 复选框还是中文『root（su -c）』」，
    /// 根因就是 <c>new CheckBox { Text = "root（su -c）" }</c>：
    /// 既没有译文（永远显示中文），也没有绑定（切语言不刷新）。
    /// 编译期无警告，纯靠肉眼发现，所以在这里断言。
    /// </para>
    ///
    /// <para>
    /// 只看<em>控件</em>的 <c>Text</c> 属性，不扫 ListViewItem 等运行时数据。
    /// 译文表自身的键不是控件文本，跳过。
    /// </para>
    /// </summary>
    [Fact]
    public void 控件文本不得硬编码中文字面量()
    {
        var repoRoot = FindRepoRoot();
        var bad = new SortedSet<string>();
        int scanned = 0;

        var assign = new Regex(@"\bText\s*=\s*""((?:[^""\\]|\\.)*)""");
        var zh = new Regex(@"[\u4e00-\u9fff]");

        foreach (var file in EnumerateSourceFiles(repoRoot))
        {
            var rel = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            if (rel.StartsWith("Services/LocTable.")) continue;
            var src = BlankOutComments(File.ReadAllText(file));

            foreach (Match m in assign.Matches(src))
            {
                scanned++;
                if (zh.IsMatch(m.Groups[1].Value))
                    bad.Add($"{rel}: Text = \"{m.Groups[1].Value}\"");
            }
        }

        // 迁移完成后源码里只剩十几处「本就无需翻译」的 Text 赋值
        // （路径 / PID / TID / ▾ / ★ / ☆ / 空串），阈值按实际留余量；
        // 真正要防的是扫描器整体失效（那会让本测试变成假绿）。
        Assert.True(scanned > 8, $"只扫到 {scanned} 处 Text 赋值，解析逻辑大概率坏了（这个测试会变成假绿）");
        Assert.True(bad.Count == 0,
            "以下控件把中文直接写进 Text，不会被翻译也不会随语言刷新（须改走 Loc.Bind）：\n"
            + string.Join("\n", bad));
    }

    /// <summary>
    /// <c>ComboBox.Items</c> 里的项不参与 <c>Loc.ApplyTo</c> 的控件树遍历
    /// （<c>ApplyTo</c> 只认 <c>Loc.Bind</c> 登记过的 <c>Text</c>），
    /// 所以凡是用译文填的下拉框，切语言时都得自己重填。
    ///
    /// <para>
    /// 同一个坑在命令窗口踩了两次：「通道」下拉（构造时 <c>CommandKinds.ShellText</c>
    /// 求值一次）和「分类」下拉都是一次性填充，结果英文界面里停在中文
    /// 「设备 shell」/「（全部）」。
    /// </para>
    /// </summary>
    [Fact]
    public void 含译文的ComboBox必须能重填()
    {
        var repoRoot = FindRepoRoot();
        var bad = new SortedSet<string>();
        int scanned = 0;

        foreach (var file in EnumerateSourceFiles(repoRoot))
        {
            var rel = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            var src = BlankOutComments(File.ReadAllText(file));
            bool fileCanRefill = Regex.IsMatch(src, @"\.(Items\.(Clear|Remove)|Refresh\w*Combo|Refresh\w*Categor|Refresh\w*Kind)\(");

            foreach (var (method, body) in EnumerateMethods(src))
            {
                if (!Regex.IsMatch(body, @"_cbo[A-Za-z]*\.Items\.(Add|AddRange)")) continue;
                if (!Regex.IsMatch(body, @"Loc\.T\(|CommandKinds\.(ShellText|AdbText)|CategoryDisplay\(")) continue;
                scanned++;
                // 构造函数里填一次是正常的（打开时的语言），只要整个类有重填入口即可
                if (!fileCanRefill && !Regex.IsMatch(body, @"_cbo[A-Za-z]*\.Items\.(Clear|Remove)"))
                    bad.Add($"{rel}: {method}() 往 ComboBox 填了译文，但该文件没有重填入口（切语言会停在旧语言）");
            }
        }

        Assert.True(scanned > 0, "没扫到任何往 ComboBox 填译文的语句，解析逻辑坏了（这个测试会变成假绿）");
        Assert.True(bad.Count == 0,
            "以下 ComboBox 填了译文却没有重填入口（切语言后英文界面会停在中文项）：\n"
            + string.Join("\n", bad));
    }

    /// <summary>粗粒度切出「方法体」：按 <c>{</c> … <c>}</c> 配平，不做语法解析。</summary>
    static IEnumerable<(string Method, string Body)> EnumerateMethods(string src)
    {
        var rx = new Regex(@"(?:void|bool|string|int)\s+(\w+)\s*\([^;{)]*\)\s*\{");
        foreach (Match m in rx.Matches(src))
        {
            int open = m.Index + m.Length - 1;
            int close = FindMatching(src, open, '{', '}');
            if (close < 0) continue;
            yield return (m.Groups[1].Value, src[(open + 1)..close]);
        }
    }

    [Theory]
    [InlineData("zh-CN", UiLang.ZhCn)]
    [InlineData("zh", UiLang.ZhCn)]
    [InlineData("zh-TW", UiLang.ZhCn)]
    [InlineData("zh-Hans", UiLang.ZhCn)]
    [InlineData("en", UiLang.En)]
    [InlineData("en-US", UiLang.En)]
    [InlineData("ja-JP", UiLang.En)]   // 既非中文也非英文 -> 英文
    [InlineData("ko-KR", UiLang.En)]
    [InlineData("", UiLang.En)]
    [InlineData(null, UiLang.En)]
    public void 语言解析符合预期(string? code, UiLang expected)
    {
        Assert.Equal(expected, Loc.ParseLang(code));
    }

    [Fact]
    public void 中文态取原文英文态取译文()
    {
        var zh = Loc.SetLangGuard(UiLang.ZhCn);
        try
        {
            Assert.Equal("重载", Loc.T("重载"));
            // 表里没有的键：两种语言都回退中文，不返回空串
            Assert.Equal("某个不存在的键", Loc.T("某个不存在的键"));
        }
        finally { zh(); }

        var en = Loc.SetLangGuard(UiLang.En);
        try
        {
            Assert.Equal("Reload", Loc.T("重载"));
            Assert.Equal("某个不存在的键", Loc.T("某个不存在的键"));
        }
        finally { en(); }
    }

    [Fact]
    public void 格式串占位符正确替换()
    {
        var en = Loc.SetLangGuard(UiLang.En);
        try
        {
            Assert.Equal("Installed 3 apps", Loc.F("已安装 {0} 个应用", 3));
            // 缺参数时不抛异常（Loc.F 兜底），返回未格式化的译文而不是把异常抛到 UI 线程
            Assert.Equal("Installed {0} apps", Loc.F("已安装 {0} 个应用"));
        }
        finally { en(); }
    }

    /// <summary>
    /// <c>{{</c> / <c>}}</c> 在 <c>string.Format</c> 里是<b>转义字面花括号</b>。
    /// 插值串 <c>$"为 {{{name}}} 输入值："</c> 迁到 <c>Loc.F</c> 时若写成 <c>{{0}}</c>，
    /// 界面就会显示「{0}」而<b>丢掉参数名</b>——这类 bug 编译期与运行期都不报错，
    /// 只能靠断言盯住。
    /// </summary>
    [Fact]
    public void 转义花括号不吞掉参数()
    {
        var en = Loc.SetLangGuard(UiLang.En);
        try
        {
            Assert.Equal("Enter a value for {pkg}:", Loc.F("为 {{{0}}} 输入值：", "pkg"));
        }
        finally { en(); }
    }

    // ── 源码扫描工具 ──

    /// <summary>
    /// 扫描源码里 <c>Loc.T/F/SizedButton/Bind/BindTip/BindHint</c> 的首个字符串实参
    /// （即资源键）。用括号配平解析实参，不靠正则硬啃。
    /// </summary>
    static readonly string[] KeyMethods = { "T", "F", "SizedButton", "Bind", "BindTip", "BindHint" };

    /// <summary>
    /// 状态栏消息入口：<c>ShowStatus("中文原文", 实参…)</c>。
    /// 它不是 <c>Loc.*</c> 的调用，但同样把资源键传给了 <c>Loc.F</c>，
    /// 必须一并纳入译文/占位符断言，否则「从 Loc.F 迁进 ShowStatus」这一步会静默脱管。
    /// </summary>
    const string StatusMethod = "ShowStatus";

    static IEnumerable<(string File, string Key)> EnumerateLocCallKeys(string repoRoot)
    {
        foreach (var file in EnumerateSourceFiles(repoRoot))
        {
            foreach (var m in KeyMethods)
                foreach (var (key, _) in EnumerateCallStringArgs(file, m))
                    if (key.Length > 0) yield return (file, key);
            foreach (var (key, _) in EnumerateBareCallStringArgs(file, StatusMethod))
                if (key.Length > 0) yield return (file, key);
        }
    }

    /// <summary>扫描 <c>Loc.F("格式串", 实参…)</c> 与 <c>ShowStatus("格式串", 实参…)</c>，返回格式串与实参列表。</summary>
    static IEnumerable<(string File, string Fmt, List<string> Args)> EnumerateLocFCalls(string repoRoot)
    {
        foreach (var file in EnumerateSourceFiles(repoRoot))
        {
            foreach (var (fmt, args) in EnumerateCallStringArgs(file, "F"))
            {
                // args[0] 是格式串本身，不是实参
                if (fmt.Length > 0) yield return (file, fmt, args.Skip(1).ToList());
            }
            foreach (var (fmt, args) in EnumerateBareCallStringArgs(file, StatusMethod))
            {
                if (fmt.Length > 0) yield return (file, fmt, args.Skip(1).ToList());
            }
        }
    }

    /// <summary>绑定类方法（资源键 = 最后一个实参）。</summary>
    static readonly string[] BindMethods = { "Bind", "BindTip", "BindHint", "BindCol" };

    /// <summary>
    /// 扫出 <c>Loc.Bind*(控件, 资源键…)</c> 调用点，返回（文件, 方法名, 全部顶层实参）。
    ///
    /// <para>
    /// 单独一套而复用不了 <see cref="EnumerateCallStringKeys"/>：绑定类方法的实参形态是
    /// <c>(new Button{…}, "中文原文")</c>，<b>首个实参是对象表达式而非字符串</b>，
    /// 资源键在<b>最后</b>一个。走「取首个字符串实参」的通用路径会一条都扫不到
    /// ——这正是 <c>Loc.Bind(x, Loc.T("帮助"))</c> 能长期潜伏的原因：
    /// 旧的 <c>KeyMethods</c> 里虽然列了 "Bind"，实际对它恒定产出空键，等于没测。
    /// </para>
    /// </summary>
    static IEnumerable<(string File, string Method, List<string> Args)> EnumerateBindCalls(string repoRoot)
    {
        foreach (var file in EnumerateSourceFiles(repoRoot))
        {
            var src = BlankOutComments(File.ReadAllText(file));
            foreach (var meth in BindMethods)
            {
                foreach (var args in EnumerateCallArgs(src, meth))
                {
                    if (args.Count >= 2) yield return (file, meth, args);
                }
            }
        }
    }

    /// <summary>
    /// 在已抹掉注释的源码里扫 <c>Loc.方法(…)</c> 的全部顶层实参（不解析实参类型）。
    /// 与 <see cref="EnumerateCallStringArgs"/> 共享同一套「Loc 独立词 + 成员访问 + 括号配平」解析。
    /// </summary>
    static IEnumerable<List<string>> EnumerateCallArgs(string src, string method)
    {
        int from = 0;
        while (true)
        {
            int at = src.IndexOf("Loc", from, StringComparison.Ordinal);
            if (at < 0) yield break;
            from = at + 3;

            // 独立词：前一个字符不能是标识符字符或 '.'（排除 logcat.Services.Loc / MyLoc / LocTable）
            if (at > 0 && (char.IsLetterOrDigit(src[at - 1]) || src[at - 1] == '_' || src[at - 1] == '.'))
                continue;

            int i = at + 3;
            if (i >= src.Length || src[i] != '.') continue;
            i++;
            while (i < src.Length && char.IsWhiteSpace(src[i])) i++;
            int nameStart = i;
            while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_')) i++;
            if (src[nameStart..i] != method) continue;

            // 泛型实参：Loc.Bind<Button>(…)
            while (i < src.Length && char.IsWhiteSpace(src[i])) i++;
            if (i < src.Length && src[i] == '<')
            {
                int close = FindMatching(src, i, '<', '>');
                if (close < 0) continue;
                i = close + 1;
            }
            while (i < src.Length && char.IsWhiteSpace(src[i])) i++;
            if (i >= src.Length || src[i] != '(') continue;

            int end = FindMatching(src, i, '(', ')');
            if (end < 0) continue;

            var args = SplitTopLevel(src[(i + 1)..end]);
            if (args.Count > 0) yield return args;
        }
    }

    /// <summary>
    /// 逐个源文件扫出 <c>Loc.方法(…)</c> 调用点，返回（首个字符串实参, 全部顶层实参）。
    /// 首个实参不是字符串字面量时返回空串，调用方自行跳过。
    ///
    /// <para>
    /// <paramref name="method"/> 传方法名（如 <c>"T"</c>），或 <c>null</c> 表示
    /// <c>Loc</c> 下的<b>任意</b>方法。用括号配平解析实参，不靠正则硬啃 ——
    /// 正则分不清嵌套的三元表达式与字符串字面量里的括号。
    /// </para>
    /// </summary>
    static IEnumerable<(string First, List<string> Args)> EnumerateCallStringArgs(string file, string method)
    {
        // 先把注释抹成等长空格再扫。原因：注释里常出现 Loc.T("…") 的举例说明
        // （如 LogDocument 的「否则…Loc.T("今天")…」），抹掉后位置不变、
        // 注释里的调用点自然失效，也就不必逐行判断「这行是不是注释」
        // ——那会漏掉 /* */ 块注释与行尾注释两种形态。
        var src = BlankOutComments(File.ReadAllText(file));
        int from = 0;
        while (true)
        {
            int at = src.IndexOf("Loc", from, StringComparison.Ordinal);
            if (at < 0) yield break;
            from = at + 3;

            // 独立词：前一个字符不能是标识符字符或 '.'（排除 logcat.Services.Loc / MyLoc / LocTable）
            if (at > 0 && (char.IsLetterOrDigit(src[at - 1]) || src[at - 1] == '_' || src[at - 1] == '.'))
                continue;

            int i = at + 3;
            // 成员访问：Loc . T / Loc.F / Loc . En
            if (i >= src.Length || src[i] != '.') continue;
            i++;
            while (i < src.Length && char.IsWhiteSpace(src[i])) i++;
            int nameStart = i;
            while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_')) i++;
            var name = src[nameStart..i];
            if (name.Length == 0) continue;
            if (name != method) continue;

            // 泛型实参：Loc.Bind<Button>(…)
            while (i < src.Length && char.IsWhiteSpace(src[i])) i++;
            if (i < src.Length && src[i] == '<')
            {
                int close = FindMatching(src, i, '<', '>');
                if (close < 0) continue;
                i = close + 1;
            }
            while (i < src.Length && char.IsWhiteSpace(src[i])) i++;
            if (i >= src.Length || src[i] != '(') continue;   // 属性访问（Loc.En / Loc.UiFont）不是调用

            int end = FindMatching(src, i, '(', ')');
            if (end < 0) continue;

            var args = SplitTopLevel(src[(i + 1)..end]);
            if (args.Count == 0) continue;

            yield return (Unquote(args[0]), args);
        }
    }

    /// <summary>
    /// 扫出<b>不带 <c>Loc.</c> 前缀</b>的方法调用点（如 <c>ShowStatus("…", …)</c>），
    /// 返回（首个字符串实参, 全部顶层实参）。首个实参不是字符串字面量时返回空串。
    ///
    /// <para>
    /// 解析规则与 <see cref="EnumerateCallStringArgs"/> 一致（抹注释 → 独立词 → 括号配平 → 顶层逗号切分），
    /// 只是入口标识从 <c>Loc.方法</c> 换成裸方法名。<c>ShowStatusRaw</c> 以 <c>ShowStatus</c> 开头，
    /// 必须靠「后一个字符不是标识符字符」排除掉 —— 它传的是已翻好的成品串或运行时数据，没有资源键。
    /// </para>
    /// </summary>
    static IEnumerable<(string First, List<string> Args)> EnumerateBareCallStringArgs(string file, string method)
    {
        var src = BlankOutComments(File.ReadAllText(file));
        int from = 0;
        while (true)
        {
            int at = src.IndexOf(method, from, StringComparison.Ordinal);
            if (at < 0) yield break;
            from = at + method.Length;

            // 前一个字符不能是标识符字符或 '.'（排除 x.ShowStatus / MyShowStatus）
            if (at > 0 && (char.IsLetterOrDigit(src[at - 1]) || src[at - 1] == '_' || src[at - 1] == '.'))
                continue;
            // 后一个字符不能是标识符字符：ShowStatusRaw / ShowStatusXxx 都不是目标方法
            int after = at + method.Length;
            if (after < src.Length && (char.IsLetterOrDigit(src[after]) || src[after] == '_'))
                continue;

            int i = after;
            while (i < src.Length && char.IsWhiteSpace(src[i])) i++;
            if (i >= src.Length || src[i] != '(') continue;   // 属性/方法组引用不是调用

            int end = FindMatching(src, i, '(', ')');
            if (end < 0) continue;

            var args = SplitTopLevel(src[(i + 1)..end]);
            if (args.Count == 0) continue;

            yield return (Unquote(args[0]), args);
        }
    }

    /// <summary>
    /// 括号 / 尖括号配平，跳过字符串字面量里的括号与转义引号。找不到配对返回 -1。
    /// </summary>
    static int FindMatching(string s, int open, char openCh, char closeCh)
    {
        int depth = 0;
        bool inStr = false;
        bool inVerbatim = false;
        for (int i = open; i < s.Length; i++)
        {
            char c = s[i];
            if (inVerbatim)
            {
                if (c == '"' && (i == 0 || s[i - 1] != '~')) inVerbatim = false;
                continue;
            }
            if (inStr)
            {
                if (c == '\\') { i++; continue; }
                if (c == '"') inStr = false;
                continue;
            }
            if (c == '@' && i + 1 < s.Length && s[i + 1] == '"') { inVerbatim = true; i++; continue; }
            if (c == '"') { inStr = true; continue; }
            if (c == openCh) depth++;
            else if (c == closeCh && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>
    /// 把注释（<c>//…</c> 与 <c>/* … */</c>）替换成等长空格。
    /// 保留换行以维持行号，且<b>不动字符串字面量里的内容</b>——
    /// 否则 <c>Loc.T("//")</c> 这种会被误当成注释。
    /// </summary>
    static string BlankOutComments(string src)
    {
        var sb = new StringBuilder(src.Length);
        bool inStr = false, inVerbatim = false, inLine = false, inBlock = false;
        for (int i = 0; i < src.Length; i++)
        {
            char c = src[i];
            if (inLine)
            {
                if (c == '\n') { inLine = false; sb.Append(c); } else sb.Append(' ');
                continue;
            }
            if (inBlock)
            {
                if (c == '*' && i + 1 < src.Length && src[i + 1] == '/') { sb.Append("  "); i++; inBlock = false; }
                else sb.Append(c == '\n' ? '\n' : ' ');
                continue;
            }
            if (inVerbatim)
            {
                sb.Append(c);
                if (c == '"' && i + 1 < src.Length && src[i + 1] == '"') { sb.Append(src[++i]); }
                else if (c == '"') inVerbatim = false;
                continue;
            }
            if (inStr)
            {
                sb.Append(c);
                if (c == '\\') { if (i + 1 < src.Length) sb.Append(src[++i]); continue; }
                if (c == '"') inStr = false;
                continue;
            }
            // 非字符串、非注释
            if (c == '@' && i + 1 < src.Length && src[i + 1] == '"') { inVerbatim = true; sb.Append("@\""); i++; continue; }
            if (c == '"') { inStr = true; sb.Append(c); continue; }
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '/') { inLine = true; sb.Append("  "); i++; continue; }
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '*') { inBlock = true; sb.Append("  "); i++; continue; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>按顶层逗号切分实参，正确跳过嵌套括号、字符串字面量与三元表达式。</summary>
    static List<string> SplitTopLevel(string s)
    {
        var args = new List<string>();
        var cur = new StringBuilder();
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

    /// <summary>剥掉字符串字面量的引号；不是字面量（非字面量则原样返回，去空白）时返回去空白结果。</summary>
    static string Unquote(string raw)
    {
        var s = raw.Trim();
        if (s.StartsWith("@\"") && s.EndsWith("\"") && s.Length >= 3)
            return s[2..^1].Replace("\"\"", "\"");
        if (s.StartsWith("\"") && s.EndsWith("\"") && s.Length >= 2)
            return Regex.Unescape(s[1..^1]);
        return "";
    }

    /// <summary>
    /// 取「真实占位符」序号。<c>string.Format</c> 语义下 <c>{{</c> / <c>}}</c>
    /// 是转义后的字面花括号，<b>不算</b>占位符——这正是插值串迁移时最容易搞错的地方。
    /// </summary>
    static List<int> RealPlaceholders(string fmt)
    {
        var list = new List<int>();
        for (int i = 0; i < fmt.Length; i++)
        {
            if (fmt[i] != '{') continue;
            if (i + 1 < fmt.Length && fmt[i + 1] == '{') { i++; continue; }   // 转义 {{
            var pm = Regex.Match(fmt[i..], @"^\{(\d+)(?:[:,][^{}]*)?\}");
            if (!pm.Success) continue;
            list.Add(int.Parse(pm.Groups[1].Value));
            // 注意：Match 没有 End() 方法（那是 Capture/Group 的），结束位置要用 Index + Length
            i += pm.Index + pm.Length - 1;
        }
        return list;
    }

    /// <summary>遍历参与本地化的源文件：排除测试工程、构建产物、字符串表自身。</summary>
    static IEnumerable<string> EnumerateSourceFiles(string repoRoot)
    {
        foreach (var file in Directory.EnumerateFiles(repoRoot, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            if (rel.StartsWith("tests/") || rel.StartsWith("obj/") || rel.StartsWith("bin/")
                || rel.StartsWith("publish/") || rel.Contains("/obj/") || rel.Contains("/bin/")
                || rel.StartsWith("Services/LocTable."))
                continue;
            yield return file;
        }
    }

    /// <summary>
    /// 仓库根目录。编译期由<code>logcat.Tests.csproj</code> 的
    /// <c>AssemblyMetadata("RepoRoot", …)</c> 注入 —— <b>不能</b>靠
    /// <c>AppContext.BaseDirectory</c> 往上找：测试可能被 tstrun 之类的外部 runner
    /// 从仓库外的目录启动，那样找不到 <c>logcat.csproj</c>（实测过）。
    /// </summary>
    static string FindRepoRoot()
    {
        var attr = typeof(LocTableTests).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepoRoot");
        var root = attr?.Value;
        Assert.False(string.IsNullOrEmpty(root), "缺少 AssemblyMetadata(\"RepoRoot\")，检查 logcat.Tests.csproj");
        Assert.True(File.Exists(Path.Combine(root!, "logcat.csproj")), $"注入的 RepoRoot 不对：{root}");
        return root!;
    }
}