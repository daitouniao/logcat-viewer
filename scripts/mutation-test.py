#!/usr/bin/env python3
"""
变异测试（mutation testing）——检验单元测试是否真的咬得住实现。

原理：把实现里的某处逻辑改坏（变异），重跑全量测试。
    KILLED   = 有用例抓到 → 该处有有效断言
    SURVIVED = 全绿       → 该处没有有效断言（可能是测试空洞，也可能是等价变异）
    BUILD-FAIL = 编译失败 → 变异本身写错了

为什么需要它：行覆盖率只证明「代码被执行过」，不证明「断言咬得住」。
本项目实测三处被覆盖率统计覆盖、却一个都测不出来的代码。

依赖：`dotnet test` 在本机跑不起来（testhost 加载 hostfxr.dll 报 0x80070005），
需先准备反射 runner `%TEMP%/tstrun`（ProjectReference → logcat.Tests.csproj，
反射扫 [Fact] / [Theory] 按 [InlineData] 展开后 m.Invoke）。

用法：
    python scripts/mutation-test.py                # 跑全部
    python scripts/mutation-test.py FilterEngine   # 只跑路径/描述含该关键词的

⚠️ 纪律（踩过的坑）：
  1. 运行前确认 `git status` 干净——本脚本只回滚自己改的那一个文件，
     但如果你正在编辑仓库，并发写入会让结果失真（曾出现 BUILD-FAIL 误报）。
  2. 本脚本对每个变异执行 `git checkout -- <单个文件>`，不会碰你的其他改动。
  3. SURVIVED 必须逐条甄别，别直接下结论——先确认「变异本身真的改了行为」。
     用探针打印中间状态，别只静态推理。
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
     "                prev = -1;\n                prevHead = false;\n                joined = 0;\n                pos = nl >= 0 ? nl + 1 : data.Length;\n                continue;",
     "                prev = 0;\n                pos = nl >= 0 ? nl + 1 : data.Length;\n                continue;",
     "空行不再重置合并状态（回退修法 A）"),
    ("Services/LogDocument.cs",
     "        var part = ScanRange(SourcePath, IndexedSize, newSize, Join, _prevHeadAtEnd, _joinedAtEnd, progress, ct);",
     "        var part = ScanRange(SourcePath, IndexedSize, newSize, Join, true, _joinedAtEnd, progress, ct);",
     "Append：prevHeadDoc 改回恒 true（批界合并缓存失效）"),
    ("Services/LogDocument.cs",
     "        var part = ScanRange(SourcePath, IndexedSize, newSize, Join, _prevHeadAtEnd, _joinedAtEnd, progress, ct);",
     "        var part = ScanRange(SourcePath, IndexedSize, newSize, Join, _prevHeadAtEnd, 0, progress, ct);",
     "Append：joinedDoc 改回每批清零（MAX_JOIN 退化为每批预算）"),
    ("Services/LogDocument.cs",
     "        // 批末状态必须在任何早退分支之前入缓存：整批空行走 \"unchanged\" 但合并状态已变\n        _prevHeadAtEnd = part.EndPrevHead;",
     "        // 批末状态必须在任何早退分支之前入缓存：整批空行走 \"unchanged\" 但合并状态已变\n        _prevHeadAtEnd = _prevHeadAtEnd;",
     "Append：批末合并状态缓存自赋值（永不更新）"),
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
     "扫描期取消检查从每 5000 行改成每 100 万行"),
    ("Services/LogDocument.cs",
     "        long nextReport = 1 << 22;",
     "        long nextReport = 1 << 40;",
     "扫描进度上报阈值 4MB → 1TB（等价变异会误报）"),
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
     "TagIds：去掉 doc.Tags 为空的短路（等价变异）"),
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
     "FallbackCategory 改成 \"Shell\"（断言自证，抓不到）"),

    # ── FavoritesStore ──
    ("Services/FavoritesStore.cs",
     "        list.Insert(0, value);\n        while (list.Count > MaxPerList) list.RemoveAt(list.Count - 1);\n        return true;",
     "        list.Insert(0, value);\n        while (list.Count > MaxPerList) list.RemoveAt(0);\n        return true;",
     "AddRecent：超上限丢最早而非最后（没测淘汰方向）"),
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
     "FilterSpec.Describe：全集级别也输出「级别 ...」（缺用例）"),

    # ══ 2026-10-18 第二轮：fileproxy（6973681/27190e2）/ 崩溃汇总（58fbd8a）/ root 检测（2589f47）══
    # 这些代码在首轮变异清单建立之后落地，此前从未被变异测试验证。

    # ── FileProxyService：代理判定 / 启动命令构造 ──
    ("Services/FileProxyService.cs",
     "            FileProxyMode.On => true,",
     "            FileProxyMode.On => false,",
     "FileProxyDecision：On 模式恒走代理失效"),
    ("Services/FileProxyService.cs",
     "            FileProxyMode.Auto => !directOpenSucceeded || !looksLikeText,",
     "            FileProxyMode.Auto => !directOpenSucceeded && !looksLikeText,",
     "FileProxyDecision：Auto 的 || 退化为 &&（真值表应抓）"),
    ("Services/FileProxyService.cs",
     "        if (name.Contains('\\\\') || name.Contains('/')) return \"管道名不能包含路径分隔符\";",
     "        if (name.Contains('\\\\')) return \"管道名不能包含路径分隔符\";",
     "ValidatePipeName：不再拒绝正斜杠分隔符"),
    ("Services/FileProxyService.cs",
     "    public static bool NeedsHugeFileConfirm(long openSize) => openSize > HugeFileThreshold;",
     "    public static bool NeedsHugeFileConfirm(long openSize) => openSize >= HugeFileThreshold;",
     "NeedsHugeFileConfirm：> 改 >=（边界探测）"),
    ("Services/FileProxyService.cs",
     "        => (argsTemplate ?? \"\").Contains(\"{pipe}\", StringComparison.Ordinal)",
     "        => (argsTemplate ?? \"\").Contains(\"{PIPE}\", StringComparison.Ordinal)",
     "BuildLaunchArgs：{pipe} 占位符判定失效（旧形态全走追加分支）"),
    ("Services/FileProxyService.cs",
     "        if (s.EndsWith(\"--pipe {pipe}\", StringComparison.OrdinalIgnoreCase))",
     "        if (false)",
     "NormalizeLegacyArgs：不再剥尾部 --pipe {pipe}"),
    ("Services/FileProxyService.cs",
     "        => args.Length > 0 ? args + \" --pipe \" + QuoteIfNeeded(pipeName) : \"--pipe \" + QuoteIfNeeded(pipeName);",
     "        => args.Length > 0 ? args + \"--pipe \" + QuoteIfNeeded(pipeName) : \"--pipe \" + QuoteIfNeeded(pipeName);",
     "AppendPipeArg：丢参数间空格"),
    ("Services/FileProxyService.cs",
     "        => s.Length > 0 && (s.Contains(' ') || s.Contains('\\t')) ? \"\\\"\" + s + \"\\\"\" : s;",
     "        => s;",
     "QuoteIfNeeded：不再加引号（含空格路径）"),

    # ── FileProxyService：spool 进度 / 落盘 / 清理 ──
    ("Services/FileProxyService.cs",
     "            var pct = Math.Min(0.5, 0.5 * received / expected);",
     "            var pct = 0.5 * received / expected;",
     "SpoolProgress：去掉 50% 封顶"),
    ("Services/FileProxyService.cs",
     "        if (expected > 0 && received <= expected)",
     "        if (expected > 0 && received < expected)",
     "SpoolProgress：实收恰等于期望时误入追逐态分支（边界）"),
    ("Services/FileProxyService.cs",
     "        if (name.Length == 0) return \"proxy.log\";",
     "        if (false) return \"proxy.log\";",
     "SpoolFileNameFor：空文件名不再回退 proxy.log"),
    ("Services/FileProxyService.cs",
     "        return string.Concat(name.AsSpan(0, 48), name.AsSpan(name.Length - 16));",
     "        return string.Concat(name.AsSpan(0, 48), name.AsSpan(name.Length - 15));",
     "SpoolFileNameFor：截断尾段 16 → 15"),
    # ⚠️ 不能变异 `received += chunk.Length;`（如 -1）：received 同时是读偏移，
    # 会让读位置渐进逼近文件尾却永不到达 EOF → 死循环，runner 30 分钟超时。
    # 改为直接关掉 EOF 后的完整性判定。
    ("Services/FileProxyService.cs",
     "            if (received != expected) throw ShortRead(received, expected);",
     "            if (false) throw ShortRead(received, expected);",
     "SpoolCoreAsync：EOF 后不再校验实收==期望（完整性判定失效）"),
    ("Services/FileProxyService.cs",
     "            if (SpoolPaths.IsDirAlive(dir)) { skipped++; continue; }",
     "            if (false) { skipped++; continue; }",
     "CleanupStaleSpoolDirs：不再跳过活性目录"),

    # ── FileProxyClient：协议握手 / 帧 ──
    ("Services/FileProxyClient.cs",
     "        if (resp.Proto != ProtoVersion)",
     "        if (false)",
     "HelloAsync：协议版本校验失效"),
    ("Services/FileProxyClient.cs",
     "        if (n < 0 || n > MaxReadLen)",
     "        if (n < 0)",
     "ReadAsync：不再拒绝超上限负载长度"),
    ("Services/FileProxyClient.cs",
     "    internal static int ClampLen(int len) => Math.Clamp(len, 0, MaxReadLen);",
     "    internal static int ClampLen(int len) => len;",
     "ClampLen：不再截断到 16MB 上限"),
    ("Services/FileProxyClient.cs",
     "        if (resp.Id != id)",
     "        if (false)",
     "SendAsync：响应 ID 匹配校验失效"),
    ("Services/FileProxyClient.cs",
     "            filled = take;",
     "            filled = 0;",
     "ReadExactAsync：leftover 字节不再计入（帧错位回归）"),

    # ── CrashExtractor：误报防线 ──
    ("Services/CrashExtractor.cs",
     "        for (int j = head + 1; j <= head + 3 && j < lines.Length; j++)",
     "        for (int j = head + 1; j <= head + 1 && j < lines.Length; j++)",
     "TryJava：Process 行窗口 3 行 → 1 行（窗口边界探测）"),
    ("Services/CrashExtractor.cs",
     "            if (!t.Contains(\", PID\")) continue;",
     "            if (false) continue;",
     "TryJava：不再要求 , PID（业务日志误报防线失效）"),
    ("Services/CrashExtractor.cs",
     "                if (string.Equals(name, s, StringComparison.Ordinal)) return name;",
     "                if (name.StartsWith(\"SIG\", StringComparison.Ordinal)) return name;",
     "FindSignal：信号白名单退化为 SIG 前缀（SIGKILL 应被抓）"),
    ("Services/CrashExtractor.cs",
     "        if (s[0] < 'a' || s[0] > 'z') return false;",
     "        if (false) return false;",
     "IsPackageName：不再要求小写字母开头"),
    ("Services/CrashExtractor.cs",
     "            if (!tombstoneHead && t.StartsWith(\"*** *** ***\", StringComparison.Ordinal))\n                tombstoneHead = true;",
     "            if (false)\n                tombstoneHead = true;",
     "TryNative：tombstone 头不再识别（tombstone 全归 native）"),
    ("Services/CrashExtractor.cs",
     "            if (!t.StartsWith(\"ANR in \", StringComparison.Ordinal)) continue;",
     "            if (!t.StartsWith(\"ANR\", StringComparison.Ordinal)) continue;",
     "TryAnr：前缀放宽为 ANR（业务日志误报防线失效）"),
    ("Services/CrashExtractor.cs",
     "    internal static readonly string[] FastKeywords = { \"FATAL EXCEPTION\", \"SIG\", \"ANR in \", \"*** ***\" };",
     "    internal static readonly string[] FastKeywords = { \"FATAL EXCEPTION\", \"ANR in \", \"*** ***\" };",
     "FastKeywords：快筛丢 SIG 关键字（扫描零分配路径覆盖探测）"),

    # ── AdbManager：root 检测纯函数 ──
    ("Services/AdbManager.cs",
     "        if (suIdOut == null)\n            return new RootDetectEntry(RootMode.None, SuFailed: true);",
     "        if (false)\n            return new RootDetectEntry(RootMode.None, SuFailed: true);",
     "ClassifyRootOutput：su 无输出不再判 SuFailed"),
    ("Services/AdbManager.cs",
     "        cached is null || cached.Mode == RootMode.Adbd;",
     "        cached is null;",
     "RootDetect ShouldRedetect：Adbd 型不再自动重测"),
    ("Services/AdbManager.cs",
     "        mode == RootMode.Su ? $\"su -c {ShellQuote(cmd)}\" : cmd;",
     "        cmd;",
     "BuildRootShellCmd：Su 型不再加 su 包裹"),
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
    # 2026-10-18 核对：RenameCategory 空名校验从 280 漂到 336（X-A8 后多次增删）。
    ("Services/CommandStore.cs", 336,
     "if (oldName.Length == 0 || newName.Length == 0) return false;",
     "if (oldName.Length == 0) return false;",
     "RenameCategory：不再拒绝空的新名"),
    ("Services/CommandStore.cs", 336,
     "if (oldName.Length == 0 || newName.Length == 0) return false;",
     "if (false) return false;",
     "RenameCategory：不再拒绝任一空名"),
    ("Services/LogDocument.cs", 14,
     "const int MAX_JOIN = 200;",
     "const int MAX_JOIN = 1;",
     "MAX_JOIN 200 → 1（续行合并上限）"),
    ("Services/LogDocument.cs", 243,
     'if (row < 0 || row >= _n) return "";',
     'if (row < 0) return "";',
     "TagOf：去掉上界检查（只挡负数）"),
]


def run_tests():
    """跑全量测试，解析反射 runner 的输出。返回 (失败数, 摘要行, 错误详情)。"""
    try:
        r = subprocess.run(
            ["dotnet", "run", "-c", "Release", "--no-restore"],
            cwd=RUNNER, capture_output=True, text=True,
            encoding="utf-8", errors="replace", timeout=1800)
    except subprocess.TimeoutExpired:
        # 变异可能造成死循环；超时必须如实报，不能拖死整轮
        return None, None, f"测试超时（>1800s，疑似变异导致死循环）"
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
              f"需先搭建反射 runner（ProjectReference → logcat.Tests.csproj，"
              f"反射扫 [Fact]/[Theory] 按 [InlineData] 展开后调用）再重试。",
              file=sys.stderr)
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
    return 0


if __name__ == "__main__":
    sys.exit(main())
