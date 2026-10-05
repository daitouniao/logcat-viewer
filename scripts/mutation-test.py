#!/usr/bin/env python3
"""
变异测试（mutation testing）——检验单元测试是否真的咬得住实现。

原理：把实现里的某处逻辑改坏（变异），重跑全量测试。
    KILLED   = 有用例抓到 → 该处有有效断言
    SURVIVED = 全绿       → 该处没有有效断言（可能是测试空洞，也可能是等价变异）
    BUILD-FAIL = 编译失败 → 变异本身写错了

为什么需要它：行覆盖率只证明「代码被执行过」，不证明「断言咬得住」。
本项目实测三处被覆盖率统计覆盖、却一个都测不出来的代码，见 docs/UT-AUDIT.md。

依赖：`dotnet test` 在本机跑不起来（testhost 加载 hostfxr.dll 报 0x80070005），
需先准备反射 runner `%TEMP%/tstrun`（ProjectReference → logcat.Tests.csproj，
反射扫 [Fact] / [Theory] 按 [InlineData] 展开后 m.Invoke）。
做法见 .workbuddy/memory/MEMORY.md「智能体跑不了 dotnet test」一节。

用法：
    python scripts/mutation-test.py                # 跑全部
    python scripts/mutation-test.py FilterEngine   # 只跑路径/描述含该关键词的

⚠️ 纪律（踩过的坑）：
  1. 运行前确认 `git status` 干净——本脚本只回滚自己改的那一个文件，
     但如果你正在编辑仓库，并发写入会让结果失真（曾出现 BUILD-FAIL 误报）。
  2. 本脚本对每个变异执行 `git checkout -- <单个文件>`，不会碰你的其他改动。
  3. SURVIVED 必须逐条甄别，别直接下结论——先确认「变异本身真的改了行为」。
     用探针打印中间状态，别只静态推理（见 docs/UT-AUDIT.md §4）。
"""
import argparse
import os
import subprocess
import sys
import time

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RUNNER = os.path.join(os.environ.get("TEMP", "/tmp"), "tstrun")

# ── 文本替换型变异 ────────────────────────────────────────────────────────────
# (相对路径, 旧文本, 新文本, 说明)
TEXT_MUTATIONS = [
    # ── LogParser：横幅行 / 续行判定 ──
    ("Services/LogParser.cs",
     "        if (d < 5) return false;\n        return d >= line.Length || line[d] == SP || line[d] == TAB;",
     "        if (d < 5) return false;\n        return d >= line.Length || line[d] == SP;",
     "IsBanner(byte[])：去掉 TAB 分隔符判定"),
    ("Services/LogParser.cs",
     "        if (d < 5) return false;\n        int j = i + d;\n        return j >= line.Length || line[j] == ' ' || line[j] == '\\t';",
     "        if (d < 5) return false;\n        int j = i + d;\n        return j >= line.Length || line[j] == ' ';",
     "IsBanner(string)：去掉 TAB 分隔符判定"),
    ("Services/LogParser.cs",
     "        if (c == SP || c == TAB) return false;",
     "        if (c == SP) return false;",
     "LooksLikeNewRecord：TAB 缩进不再算续行"),
    ("Services/LogParser.cs",
     "        if (n >= 9 && line[..9].SequenceEqual(DASHES)) return true;",
     "        if (false && n >= 9 && line[..9].SequenceEqual(DASHES)) return true;",
     "LooksLikeNewRecord：去掉 9 连横线前缀判定"),
    ("Services/LogParser.cs",
     "        if (n >= 24 && line[4] == DASH && line[7] == DASH && line[10] == SP && line[13] == COLON) return true;",
     "        if (false) return true;",
     "LooksLikeNewRecord：去掉 ymd 时间戳前缀判定"),

    # ── LogParser：字段解析 ──
    ("Services/LogParser.cs",
     "            int tagEnd = c;\n            while (tagEnd > start && line[tagEnd - 1] == SP) tagEnd--;",
     "            int tagEnd = c;",
     "SplitTagMsg：不再裁掉 tag 尾部对齐空格"),
    ("Services/LogParser.cs",
     "            if (b < ZERO || b > NINE) return -1;",
     "            if (false) return -1;",
     "TryParseInt：括号内非数字不再返回 -1"),
    ("Services/LogParser.cs",
     "        year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);",
     "        year % 4 == 0;",
     "IsLeap：去掉 100/400 世纪规则"),
    ("Services/LogParser.cs",
     "        for (int i = start, scale = 100; i < end && scale > 0; i++, scale /= 10)\n            ms += (line[i] - ZERO) * scale;",
     "        for (int i = start, scale = 100; i < end && scale > 0; i++, scale /= 10)\n            ms += scale > 0 ? (line[i] - ZERO) * scale : (line[i] - ZERO) * 100;",
     "FracToMs：不足三位小数秒改为左补零而非右补零"),

    # ── LogDocument：ScanRange 行边界对齐（记忆里的经典 bug）──
    ("Services/LogDocument.cs",
     "            f.Position = start - 1;\n            int prevByte = f.ReadByte();\n            if (prevByte != '\\n')",
     "            f.Position = start;\n            int prevByte = f.ReadByte();\n            if (prevByte != '\\n')",
     "ScanRange：start 对齐改回「看 start 自身首字节」（吞行 bug）"),
    ("Services/LogDocument.cs",
     "                long q = -1;\n                for (long p = end - 1; p >= start; p--)\n                {\n                    f.Position = p;\n                    if (f.ReadByte() == '\\n') { q = p; break; }\n                }\n                end = q >= 0 ? q + 1 : start;",
     "                f.Position = end;\n                int b2 = f.ReadByte();\n                while (b2 >= 0 && b2 != '\\n') b2 = f.ReadByte();\n                end = b2 < 0 ? start : f.Position;",
     "ScanRange：end 对齐改为「向后推进到下一换行」（旧 bug）"),
    ("Services/LogDocument.cs",
     "            if (join && prevHead && joined < MAX_JOIN && !LogParser.LooksLikeNewRecord(line))",
     "            if (prevHead && joined < MAX_JOIN && !LogParser.LooksLikeNewRecord(line))",
     "ScanRange：忽略 join 开关，无条件合并续行"),

    # ── LogDocument：续行合并规则 ──
    ("Services/LogDocument.cs",
     "const long HALF_DAY = LogParser.DAY_MS / 2;",
     "const long HALF_DAY = LogParser.DAY_MS * 30;",
     "跨年回退阈值从半天改成 30 天"),
    ("Services/LogDocument.cs",
     "            if (lineLen == 0)\n            {\n                // 空行：不索引，中断合并\n                prev = -1;",
     "            if (lineLen == 0)\n            {\n                // 空行：不索引，中断合并\n                prev = 0;",
     "空行不再中断续行合并（prev=-1 → prev=0）"),
    ("Services/LogDocument.cs",
     "            if (LogParser.IsBanner(line))\n            {\n                pos = nl >= 0 ? nl + 1 : data.Length;\n                continue;\n            }",
     "            if (LogParser.IsBanner(line))\n            {\n                prev = -1;\n                pos = nl >= 0 ? nl + 1 : data.Length;\n                continue;\n            }",
     "横幅行改为中断续行合并"),

    # ── LogDocument：解析细节 ──
    ("Services/LogDocument.cs",
     "            if (lineLen > 0 && data[pos + lineLen - 1] == 13) lineLen--;",
     "            if (false) lineLen--;",
     "ScanRange：不再剥离行尾 \\r（CRLF）"),
    ("Services/LogDocument.cs",
     "            if (pos == 0 && lineLen >= 3 &&\n                data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)",
     "            if (false)",
     "ScanRange：不再剥离首行 UTF-8 BOM"),
    ("Services/LogDocument.cs",
     "        int len = Math.Min(mo, 40);",
     "        int len = Math.Min(mo, 20);",
     "HeaderBytes：截断上限 40 → 20（高精小数秒会被切）"),
    ("Services/LogDocument.cs",
     "        if (mlen <= 0) return Array.Empty<byte>();",
     "        if (mlen < 0) return Array.Empty<byte>();",
     "MessageBytes：mlen==0 不再返回空（边界）"),

    # ── LogDocument：取消 / 进度 / 容量（只影响性能的部分也有对照）──
    ("Services/LogDocument.cs",
     "            if (lineInChunk % 5000 == 0)\n            {\n                ct.ThrowIfCancellationRequested();\n            }",
     "            if (lineInChunk % 1000000 == 0)\n            {\n                ct.ThrowIfCancellationRequested();\n            }",
     "扫描期取消检查从每 5000 行改成每 100 万行（docs/UT-AUDIT.md P1-2）"),
    ("Services/LogDocument.cs",
     "        long nextReport = 1 << 22;",
     "        long nextReport = 1 << 40;",
     "扫描进度上报阈值 4MB → 1TB（注意：等价变异会误报，见 UT-AUDIT §4.2）"),
    ("Services/LogDocument.cs",
     "        int cap = Math.Max(need, _cap + Math.Max(_cap >> 2, 1 << 16));",
     "        int cap = Math.Max(need, _cap + 1);",
     "EnsureCap 扩容步长改为 +1（只影响性能）"),
    ("Services/LogDocument.cs",
     "        _cap = n + (n >> 2) + 1024;",
     "        _cap = n + (n >> 2) + 1;",
     "SetColumns 初始容量 1024 → 1（只影响性能）"),
    ("Services/LogDocument.cs",
     "        if (!headBuf.AsSpan().SequenceEqual(oldHead))",
     "        if (false)",
     "Reload：去掉「头部变化则重建」判定（单测无法构造，见用例注释）"),

    # ── LogDocument：定位 / 时间戳 ──
    ("Services/LogDocument.cs",
     "        return diffLo < diffPrev ? lo : lo - 1;",
     "        return lo;",
     "NearestPos：不再比较左右距离"),
    ("Services/LogDocument.cs",
     "        if (!LogParser.IsLeap(now.Year) && now.DayOfYear > 59) todayWithin += LogParser.DAY_MS;",
     "",
     "GuessYear：去掉非闰年 3 月后的闰日序修正"),
    ("Services/LogDocument.cs",
     "        if (within >= LogParser.LEAP_BOUNDARY) result -= adj[yi];\n        return result;",
     "        return result;",
     "TsFromParts：去掉非闰年闰日序修正 adj"),
    ("Services/LogDocument.cs",
     "            adj[i] = LogParser.IsLeap(baseYear + y) ? 0 : LogParser.DAY_MS;",
     "            adj[i] = 0;",
     "YearTables：adj 全置 0（去掉非闰年修正）"),

    # ── FilterEngine：匹配语义 ──
    ("Services/FilterEngine.cs",
     "            if (opAnd && !hit) return false;\n            if (!opAnd && hit) return true;",
     "            if (!opAnd && hit) return true;",
     "MatchAny：and 模式不再要求全部命中（退化为 or）"),
    ("Services/FilterEngine.cs",
     "            if (needle.Length == 0)\n            {\n                if (!opAnd) return true;\n                continue;\n            }",
     "            if (needle.Length == 0)\n            {\n                return !opAnd;\n            }",
     "MatchAny：空 needle 在 and 模式下改为直接不命中"),
    ("Services/FilterEngine.cs",
     "                if (ok != spec.MsgExclude)\n                    result.Add(r);",
     "                if (ok)\n                    result.Add(r);",
     "MsgFilter：忽略 MsgExclude（排除语义失效）"),
    ("Services/FilterEngine.cs",
     "        bool filterLevels = spec.Levels.Length > 0 && !spec.Levels.OrderBy(x => x).SequenceEqual(allLevels.OrderBy(x => x));\n\n        var pidSet",
     "        bool filterLevels = spec.Levels.Length > 0;\n\n        var pidSet",
     "ApplyFilter：不再识别「全集级别 = 不过滤」"),
    ("Services/FilterEngine.cs",
     "        if (spec.Tags.Length == 0 || doc.Tags.Count == 0) return result;",
     "        if (spec.Tags.Length == 0) return result;",
     "TagIds：去掉 doc.Tags 为空的短路（等价变异，见 UT-AUDIT §4.1）"),
    ("Services/FilterEngine.cs",
     "            if (b >= 0x80) return false;      // 非 ASCII：交给调用方回退，不做部分折叠",
     "            if (b >= 0x80) continue;",
     "FoldLowerAsciiInPlace：非 ASCII 改为继续（破坏 Unicode 回退）"),
    ("Services/FilterEngine.cs",
     "            if (b >= (byte)'A' && b <= (byte)'Z') buf[i] = (byte)(b + 32);",
     "            if (b >= (byte)'A' && b <= (byte)'Z') buf[i] = (byte)(b + 1);",
     "FoldLowerAsciiInPlace：大写折叠 +1 而不是 +32"),
    ("Services/FilterEngine.cs",
     "            if (ch == ' ' || ch == ',' || ch == ';' || ch == '\\n' || ch == '\\r') { Flush(); continue; }",
     "            if (ch == ' ') { Flush(); continue; }",
     "SplitTerms：不再按逗号/分号/换行切分"),
    ("Services/FilterEngine.cs",
     "            if (ch == '\"' || ch == '\\'') { quote = ch; continue; }",
     "            if (false) { quote = ch; continue; }",
     "SplitTerms：引号不再成组"),
    ("Services/FilterEngine.cs",
     "            if (v < 0 || v > 59)\n                throw new ArgumentException($\"分钟应在 0~59 之间：{v}\");",
     "            if (v < 0)\n                throw new ArgumentException($\"分钟应在 0~59 之间：{v}\");",
     "ParseMinutes：不再校验上界 59"),
    ("Services/FilterEngine.cs",
     "        if (n <= start) return Array.Empty<int>();",
     "        if (n < start) return Array.Empty<int>();",
     "FilterTail：起始行等于行数时不再返回空（等价变异）"),

    # ── FilterEngine：导出 ──
    ("Services/FilterEngine.cs",
     "                if (singleLine)\n                {\n                    string text = Encoding.UTF8.GetString(line);\n                    text = text.Replace(\"\\r\\n\", \"\\\\n\").Replace(\"\\n\", \"\\\\n\").Replace(\"\\r\", \"\\\\n\");\n                    line = Encoding.UTF8.GetBytes(text);\n                }",
     "                if (singleLine)\n                {\n                    string text = Encoding.UTF8.GetString(line);\n                    text = text.Replace(\"\\r\\n\", \"\\\\n\");\n                    line = Encoding.UTF8.GetBytes(text);\n                }",
     "ExportRows：singleLine 只替换 \\r\\n（裸 \\n 不转义）"),
    ("Services/FilterEngine.cs",
     "            written = e;\n            progress?.Report",
     "            progress?.Report",
     "ExportRows：written 不再赋值（返回值恒为 0）"),

    # ── CommandStore ──
    ("Services/CommandStore.cs",
     "        if (!HasCategory(oldName) || HasCategory(newName)) return false;",
     "        if (!HasCategory(oldName)) return false;",
     "RenameCategory：不再拒绝重名"),
    ("Services/CommandStore.cs",
     "        if (dup != null && !ReferenceEquals(dup, entry)) return false;",
     "        if (false) return false;",
     "UpdateFavorite：不再拒绝重复命令"),
    ("Services/CommandStore.cs",
     "        if (FindFavorite(command, kind) != null) return null;",
     "        if (false) return null;",
     "AddFavorite：不再拒绝重复命令"),
    ("Services/CommandStore.cs",
     "        if (name.Length == 0) return;\n        _data.Placeholders[name] = value ?? \"\";",
     "        _data.Placeholders[name] = value ?? \"\";",
     "RememberPlaceholder：不再忽略空名"),
    ("Services/CommandStore.cs",
     "    public const string FallbackCategory = \"shell\";",
     "    public const string FallbackCategory = \"Shell\";",
     "FallbackCategory 改成 \"Shell\"（docs/UT-AUDIT.md P2-1：断言自证，抓不到）"),

    # ── FavoritesStore ──
    ("Services/FavoritesStore.cs",
     "        list.Insert(0, value);\n        while (list.Count > MaxPerList) list.RemoveAt(list.Count - 1);\n        return true;",
     "        list.Insert(0, value);\n        while (list.Count > MaxPerList) list.RemoveAt(0);\n        return true;",
     "AddRecent：超上限丢最早而非最后（docs/UT-AUDIT.md P2-2：没测淘汰方向）"),
    ("Services/FavoritesStore.cs",
     "        list.RemoveAll(p => string.Equals(p, value, StringComparison.OrdinalIgnoreCase));",
     "        if (list.Contains(value, StringComparer.Ordinal)) return false;",
     "AddRecent：去重不再忽略大小写（改为精确匹配后拒绝）"),
    ("Services/FavoritesStore.cs",
     "        if (string.IsNullOrWhiteSpace(path)) return false;\n        if (Contains(remote, path)) return false;",
     "        if (string.IsNullOrWhiteSpace(path)) return false;",
     "FavoritesStore.Add：不再拒绝重复路径"),
    ("Services/FavoritesStore.cs",
     "        if (_data.RunAsPackages.Contains(pkg)) return false;",
     "        if (false) return false;",
     "AddRunAsPackage：不再去重"),
    ("Services/FavoritesStore.cs",
     "            p = p.TrimEnd('/');\n            return p.Length == 0 ? \"/\" : p;",
     "            p = p.TrimEnd('/');\n            return p;",
     "Normalize(remote)：空路径不再归一为 \"/\""),
    ("Services/FavoritesStore.cs",
     "        if (p.Length > 3) p = p.TrimEnd('\\\\');",
     "        if (false) p = p.TrimEnd('\\\\');",
     "Normalize(local)：不再裁掉末尾反斜杠"),
    ("Services/FavoritesStore.cs",
     "            remote ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);",
     "            StringComparison.Ordinal);",
     "PathEquals：本机路径也区分大小写"),

    # ── AppSettings ──
    ("Services/AppSettings.cs",
     "    public bool Join { get; set; } = true;",
     "    public bool Join { get; set; }",
     "AppSettings：Join 默认值 true → false"),
    ("Services/AppSettings.cs",
     "    public int FontPt { get; set; } = 10;",
     "    public int FontPt { get; set; } = 11;",
     "AppSettings：FontPt 默认 10 → 11"),
    ("Services/AppSettings.cs",
     "                    s.StorePath = path;    // 反序列化走无参构造，路径要重新绑到实际加载的文件",
     "",
     "AppSettings：反序列化后不重绑 StorePath"),
    ("Services/AppSettings.cs",
     "            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);\n            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));",
     "            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);\n            File.WriteAllText(path, JsonSerializer.Serialize(new AppSettings(), JsonOpts));",
     "AppSettings.SaveTo：写的是默认对象而非 this"),

    # ── FilterSpec ──
    ("Models/FilterSpec.cs",
     "        if (Minutes.Length > 0) return false;\n        return !MarkedOnly;",
     "        return !MarkedOnly;",
     "FilterSpec.IsEmpty：不再考虑 Minutes"),
    ("Models/FilterSpec.cs",
     "        if (Tags.Length > 0 || Msg.Length > 0 || Pids.Length > 0 || Tids.Length > 0)\n            return false;",
     "        if (Tags.Length > 0 || Msg.Length > 0 || Pids.Length > 0)\n            return false;",
     "FilterSpec.IsEmpty：不再考虑 Tids"),
    ("Models/FilterSpec.cs",
     "            parts.Add($\"tag{(TagExclude ? \"!\" : \"\")} {TagOp.ToUpper()}({string.Join(\", \", Tags)})\");",
     "            parts.Add($\"tag {TagOp.ToUpper()}({string.Join(\", \", Tags)})\");",
     "FilterSpec.Describe：tag 丢失排除标记"),
    ("Models/FilterSpec.cs",
     "        if (Levels.Length > 0 && !Levels.OrderBy(x => x).SequenceEqual(allLevels.OrderBy(x => x)))\n        {\n            var names",
     "        if (Levels.Length > 0)\n        {\n            var names",
     "FilterSpec.Describe：全集级别也输出「级别 ...」（docs/UT-AUDIT.md P2-3：缺用例）"),
]

# ── 行号型变异（解决同构代码的 str.replace 歧义）────────────────────────────
# FilterEngine 里 ApplyFilter(301/308/315) 与 FilterTail(372/377/382) 的
# pid/tid 排除、分钟守卫是逐字符相同的代码，文本替换会匹配到 2 处而跳过。
# 改用「行号 + 该行内容片段」双重校验定位。
#
# (相对路径, 1-based 行号, 该行须含的片段, 变异后内容, 说明)
LINE_MUTATIONS = [
    # 行号会随FilterEngine.cs 增删漂移（2026-10-04 移除正则后下移约 47 行）。
    # 脚本按「行号 + 该行内容」双重校验，内容不符会 SKIP 而非误改，所以漂移是安全失败、
    # 但会静默漏掉一半变异点 —— 改完引擎记得回来核对行号。
    ("Services/FilterEngine.cs", 254,
     "if (spec.PidExclude ? hit : !hit) continue;",
     "if (hit) continue;",
     "ApplyFilter(254)：pid 排除退化为包含"),
    ("Services/FilterEngine.cs", 261,
     "if (spec.TidExclude ? hit : !hit) continue;",
     "if (spec.TidExclude ? !hit : hit) continue;",
     "ApplyFilter(261)：tid 排除/包含语义整体反转（对照组）"),
    ("Services/FilterEngine.cs", 268,
     "if (ts < 0) continue;",
     "if (ts < 0 && minSet.Count == int.MaxValue) continue;",
     "ApplyFilter(268)：分钟过滤不再跳过无时间戳行"),
    ("Services/FilterEngine.cs", 325,
     "if (spec.PidExclude ? hit : !hit) continue;",
     "if (spec.PidExclude ? !hit : hit) continue;",
     "FilterTail(325)：pid 排除/包含语义整体反转（对照组）"),
    ("Services/FilterEngine.cs", 330,
     "if (spec.TidExclude ? hit : !hit) continue;",
     "if (spec.TidExclude ? !hit : hit) continue;",
     "FilterTail(330)：tid 排除/包含语义整体反转"),
    ("Services/FilterEngine.cs", 335,
     "if (ts < 0) continue;",
     "if (ts < 0 && minSet.Count == int.MaxValue) continue;",
     "FilterTail(335)：分钟过滤不再跳过无时间戳行"),
    # 2026-10-05 核对：本行原为 281（上面多了一行 Trim 防御），已下移到 280。
    ("Services/CommandStore.cs", 280,
     "if (oldName.Length == 0 || newName.Length == 0) return false;",
     "if (oldName.Length == 0) return false;",
     "RenameCategory：不再拒绝空的新名"),
    ("Services/CommandStore.cs", 280,
     "if (oldName.Length == 0 || newName.Length == 0) return false;",
     "if (false) return false;",
     "RenameCategory：不再拒绝任一空名"),
    ("Services/LogDocument.cs", 13,
     "const int MAX_JOIN = 200;",
     "const int MAX_JOIN = 1;",
     "MAX_JOIN 200 → 1（续行合并上限）"),
    ("Services/LogDocument.cs", 204,
     'if (row < 0 || row >= _n) return "";',
     'if (row < 0) return "";',
     "TagOf：去掉上界检查（只挡负数）"),
]


def run_tests():
    """跑全量测试，解析反射 runner 的输出。返回 (失败数, 摘要行, 错误详情)。"""
    r = subprocess.run(
        ["dotnet", "run", "-c", "Release", "--no-restore"],
        cwd=RUNNER, capture_output=True, text=True,
        encoding="utf-8", errors="replace", timeout=1800)
    out = (r.stdout or "") + (r.stderr or "")
    for line in out.splitlines():
        if "passed:" in line:
            nums = line.replace("passed:", "").replace("failed:", "").split()
            return int(nums[1]), line.strip(), ""
    detail = next((ln.strip()[:170] for ln in out.splitlines() if "error CS" in ln), "")
    return None, None, (detail or "无输出/编译失败（可能是并发编辑导致）")


def revert(path):
    subprocess.run(["git", "checkout", "--", path], cwd=REPO, capture_output=True)


def apply_text_mutation(keyword):
    results = []
    for path, old, new, desc in TEXT_MUTATIONS:
        if keyword and keyword not in desc and keyword not in path:
            continue
        full = os.path.join(REPO, path)
        with open(full, "r", encoding="utf-8") as f:
            src = f.read()
        n = src.count(old)
        if n != 1:
            # 同构代码有 2 处匹配 → 改用行号型变异（见 LINE_MUTATIONS）
            results.append((desc, f"SKIP-匹配数={n}（同构代码，请用行号型变异）"))
            print(f"[SKIP] {desc}  匹配数={n}", flush=True)
            continue
        with open(full, "w", encoding="utf-8", newline="") as f:
            f.write(src.replace(old, new))

        t0 = time.time()
        failed, summary, err = run_tests()
        revert(path)

        if failed is None:
            v, d = "BUILD-FAIL", err
        else:
            v = "SURVIVED" if failed == 0 else "KILLED"
            d = summary
        results.append((desc, v))
        print(f"[{v}] {desc}  ({time.time() - t0:.0f}s)\n         {d}", flush=True)
    return results


def apply_line_mutation(keyword):
    results = []
    for path, lineno, expect, mutate, desc in LINE_MUTATIONS:
        if keyword and keyword not in desc and keyword not in path:
            continue
        full = os.path.join(REPO, path)
        with open(full, "r", encoding="utf-8") as f:
            lines = f.readlines()
        if lineno > len(lines) or expect not in lines[lineno - 1]:
            results.append((desc, "SKIP-行内容不符"))
            print(f"[SKIP] {desc}  第{lineno}行: "
                  f"{(lines[lineno - 1].strip()[:80] if lineno <= len(lines) else '<越界>')}",
                  flush=True)
            continue
        lines[lineno - 1] = lines[lineno - 1].replace(expect, mutate)
        with open(full, "w", encoding="utf-8", newline="") as f:
            f.writelines(lines)

        t0 = time.time()
        failed, summary, err = run_tests()
        revert(path)

        if failed is None:
            v, d = "BUILD-FAIL", err
        else:
            v = "SURVIVED" if failed == 0 else "KILLED"
            d = summary
        results.append((desc, v))
        print(f"[{v}] {desc}  ({time.time() - t0:.0f}s)\n         {d}", flush=True)
    return results


def main():
    ap = argparse.ArgumentParser(description="变异测试：检验单元测试是否真的咬得住实现")
    ap.add_argument("keyword", nargs="?", help="只跑路径或描述含该关键词的变异")
    args = ap.parse_args()

    if not os.path.isdir(RUNNER):
        print(f"找不到反射 runner：{RUNNER}\n"
              f"见 .workbuddy/memory/MEMORY.md「智能体跑不了 dotnet test」一节，"
              f"按其中步骤搭好后重试。", file=sys.stderr)
        return 2

    # 必须工作区干净：并发编辑会让结果失真（曾出现 BUILD-FAIL 误报）
    st = subprocess.run(["git", "status", "--porcelain"], cwd=REPO,
                        capture_output=True, text=True, encoding="utf-8")
    if st.stdout.strip():
        print("⚠️  工作区不干净，变异结果可能失真：\n" + st.stdout, file=sys.stderr)
        print("    本脚本只回滚自己改的文件，但并发编辑会干扰编译。\n", file=sys.stderr)

    print("=== 文本替换型变异 ===", flush=True)
    results = apply_text_mutation(args.keyword)
    print("\n=== 行号型变异 ===", flush=True)
    results += apply_line_mutation(args.keyword)

    killed = [d for d, v in results if v == "KILLED"]
    surv = [d for d, v in results if v == "SURVIVED"]
    other = [(d, v) for d, v in results if v not in ("KILLED", "SURVIVED")]

    print("\n" + "=" * 72)
    print(f"KILLED(有覆盖)={len(killed)}  SURVIVED(无覆盖)={len(surv)}  其他={len(other)}")
    if surv:
        print("\n--- 存活的变异（需逐条甄别，勿直接下结论）---")
        for d in surv:
            print(f"  * {d}")
    if other:
        print("\n--- 跳过 / 编译失败 ---")
        for d, v in other:
            print(f"  {v}  {d}")
    print("\n完整审计结论见 docs/UT-AUDIT.md")
    return 0


if __name__ == "__main__":
    sys.exit(main())
