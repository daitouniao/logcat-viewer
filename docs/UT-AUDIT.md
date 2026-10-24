# 单元测试有效性审计（变异测试）

> 初版审计：2026-10-03　|　**复核并全部落实：2026-10-05**
> 审计对象：`tests/logcat.Tests/`
> 方法：变异测试（mutation testing）——把实现逐点改坏，看测试是否变红
>
> **2026-10-05 结论：§6 的 6 条待办已全部落实，另新发现并修掉 6 处覆盖缺口。**
> 用例数 358 → 372，变异结果从 **58 KILLED / 13 SURVIVED** 改善到
> **63 KILLED / 8 SURVIVED**（8 个存活项已逐一确认是等价变异或只影响性能，不再是缺口）。
> 本轮只改测试，未动任何产品代码。
>
> **2026-10-18 第二轮：fileproxy / 崩溃汇总 / root 检测首次纳入变异，再发现并修掉 7 处缺口。**
> 用例数 379 → 623，变异点 71 → 103，结果 **102 KILLED + 1 等价**（详见 §0b）。
>
> **2026-10-24 第三轮：minute-byte-column（分钟 byte 列替代绝对毫秒列、删除年份机制）。**
> 变异点 103 → 105（TEXT 95 + LINE 10）：下架 5 条年份机制锚点、新增 9 条分钟列/行序化锚点，
> 本 change 新增 9 条**全部 KILLED**。测试侧删 23 条年份机制用例、新增 10 条分钟列用例
> （663 → 673 → 650）。另修掉变异脚本 revert 机制缺陷，详见 §0c。
>
> 跑测试与出覆盖率的完整流程见 [`TESTING.md`](TESTING.md)。
>
> ⚠️ **口径说明**：下表（§0）是 **2026-10-05 审计时点**的数值，不是当前基线。现行基线（用例数 / 行覆盖率 / 分支覆盖率）以 [`TESTING.md`](TESTING.md) 为准。

---

## 0. 2026-10-05 复核速览

| 指标 | 审计时（10-03） | 落实后（10-05） |
|---|---|---|
| 用例数 | 354 | 379 |
| 变异点总数 | 60（未含行号型漂移失效的 6个） | 71 |
| KILLED（有覆盖） | 40 | **63** |
| SURVIVED（疑似无覆盖） | 20 | **8**（全部为等价变异 / 性能项） |
| 行覆盖率 | 98.56% | **99.29% (1,401/1,411)** |
| 方法覆盖率 | — | **100%** |

**修掉的 6 处覆盖缺口**（均由变异测试暴露，不在原审计清单里）：

| # | 位置 | 问题 | 现状 |
|---|---|---|---|
| N1 | `LogParser.IsBanner(string)` | TAB 分支从未被测（用例只调了 byte[] 重载） | KILLED |
| N2 | `FavoritesStore` 收藏目录上限 | 只断言数量，淘汰方向反了测不出 | KILLED |
| N3 | `LogDocument` 跨年阈值 `HALF_DAY` | 原用例都落在阈值远端，改成 30 天全绿 | KILLED（2026-10-24：随年份机制删除，变异点已下架） |
| N4 | `LooksLikeNewRecord` 9 连横线 | 用例的 13 横线横幅被前置 `IsBanner` 提前命中 | KILLED |
| N5 | `MatchAny` 空 needle | and 模式「跳过 vs 不命中」无人验证 | KILLED |
| N6 | `FavoritesStore` 冗余用例 | `过滤词数量不超过上限` 被另一条完全包含 | 已删 |

**按覆盖率报告补的 5 处**（变异测试全KILLED之后，仍有代码没被任何用例执行到）：

| # | 位置 | 问题 |
|---|---|---|
| C1 | `CommandStore.Default`、`FavoritesStore.Default` | 单例属性完全没测（`AppSettings` 至少还有一条），会读真实 json |
| C2 | `FavoritesStore.ToggleMsgFilter` | 「不存在则新增」分支未覆盖（`ToggleTagFilter` 有，msg 侧缺失） |
| C3 | `FilterEngine` scratch 缓冲区扩容 | 消息 > 1 KB 时才进入的分支；且要验**复用时不带残留** |
| C4 | `LogDocument.Reload` | 「追加全是空行」分支：`IndexedSize` 必须推进否则重复扫描 |
| C5 | `LogDocument.FinalizeTs` | 「首个已知年份之前的行」分支（混合年份格式）（2026-10-24：FinalizeTs 随年份机制删除，本条已作废） |

**剩余 10 行未覆盖，已逐一确认不该补**（不要为了数字去构造）：

| 行 | 内容 | 为什么不补 |
|---|---|---|
| 127-132 | `OpenMap` 的 `catch` + 资源清理 | mmap 创建失败的资源异常路径，构造代价远大于收益 |
| 518-519 | `FinalizeTs` 的 `n == 0` 短路 | **防御性死代码**——`Assemble` 在 `part.Offs.Length == 0` 时提前 return，永远到不了这里（2026-10-24：FinalizeTs 已随年份机制删除，本条作废） |
| 662-663 | `Reload` 的「头部变化则重建」 | 需在 mmap 期间改写文件头，单测无法稳定构造（见 §4.3） |
| 769 | `catch { }` 吞异常 | 纯防御 |

> **「未覆盖」不等于「该补」**。判断标准是：这个分支**能否被稳定构造**、**构造代价是否匹配它的风险**。
> 死代码和资源异常路径留在未覆盖里是正确状态，硬凑数字反而会写出脆用例。

---

## 0b. 2026-10-18 第二轮速览

| 指标 | 10-05 落实后 | 第二轮（10-18 补测试后） |
|---|---|---|
| 用例数 | 379 | **623** |
| 变异点总数 | 71 | **103**（TEXT 93 + LINE 10） |
| KILLED | 63 | **102** |
| SURVIVED | 8（全部等价/性能） | **1**（确认等价，见下） |

本轮纳入范围：fileproxy（代理判定 / 启动命令构造 / spool / 协议客户端）、`CrashExtractor`
误报防线、`AdbManager` root 检测纯函数——都是首轮清单建立之后落地、从未被变异验证的代码。
**先跑变异、后补测试**：初跑 FileProxy 19 点 14K/5S、Crash 7 点 4K/3S、Root 3 点 3K/0S，
暴露 7 处真实缺口（FileProxy 存活 5 中 4 处 + Crash 存活 3 中 3 处，另 1 处为等价）。

**修掉的 7 处覆盖缺口**（净 -22 条冗余用例、+6 条缺口用例，全部转 KILLED）：

| # | 位置 | 问题 | 修法 |
|---|---|---|---|
| N7 | `SpoolCoreAsync` EOF 后完整性判定 | 代理干净收尾但实收不足（截短回包）从无用例 | 新增 ShortRead 干净收尾用例（`ServeMaxBytes` 注入截短） |
| N8 | `ReadAsync` 负载越界拒绝 | n 超 `MaxReadLen` 或为负时必须在收负载前拒绝，无用例 | 新增越界用例（`FakeReadN` 注入，改为 `long?` 以支持负值） |
| N9 | `SendAsync` 响应 ID 校验 | 响应 ID 错位（帧错位回归）无断言 | 新增 ID 不匹配用例（`IdOffset` 注入） |
| N10 | `SpoolProgress` 追逐态边界 | 实收恰等于期望时误入追逐态分支，无边界断言 | 进度用例补 `received==expected` 与 `(0,0)` 两条断言 |
| N11 | `TryJava` 的 `, PID` 防线 | 无 PID 标识的业务日志会误报为崩溃，无反例 | 新增 Process 行缺 PID 用例 |
| N12 | `TryAnr` 前缀收紧 | 裸 `ANR` 前缀（非 `ANR in `）会误报，无反例 | 新增行首 ANR 但非 `ANR in ` 前缀用例 |
| N13 | `IsPackageName` 小写开头约束 | 大写开头也当包名，Java/ANR 两路无反例 | 新增大写开头不认用例（两路一起断） |

**1 处等价变异**：`SpoolProgress` 去掉 `Math.Min(0.5, …)` 封顶——正常分支数学上
`pct ≤ 0.5`（`received ≤ expected` 是进入该分支的前提），`Min` 永不生效，属防御性写法，
存活是正确信号。与 §4.4 的 `TagIds` 空短路同类。

**顺带修掉的 5 处行号漂移**：`ScanRange` 参数改名（`Path`→`SourcePath`）×2、
`CommandStore.RenameCategory` 281→336、`LogDocument.TagOf` 217→243——双重校验把漂移
拦成 SKIP 而非误改（§5.1 的设计奏效），但 SKIP 本身会**静默漏掉变异点**；本轮修正行号/
文本后复验全部 KILLED。

**清理的 22 条冗余用例**：真值表子集（`FileProxyChannelState` 3 条被全集表完全包含）、
解析表重复（`FileProxyMode` 9 行并入既有 Theory，补 `ON` / ` on` / `开启` 三个反例）、
管道名校验两处重复（`AppSettings` 4 条并入 `FileProxyLaunchTests`）等。
删完后**复跑变异**确认未丢任何 KILLED——这是删冗余的唯一安全判据。

---

## 0c. 2026-10-24 第三轮：minute-byte-column

| 指标 | 第二轮（10-18） | 第三轮（10-24） |
|---|---|---|
| 用例数 | 623 | **650** |
| 变异点总数 | 103 | **105**（TEXT 95 + LINE 10） |
| 本 change 新增变异点 | — | **9，全部 KILLED** |
| 年份机制变异点 | 5 | **0（随机制删除下架）** |

本 change 用 byte 分钟列（0-59，哨兵 255）替代 8B/row 绝对毫秒列，删除整套年份机制
（`FinalizeTs` 跨年检测 / `GuessYear` / `YearTables` / `TsToText` / `TsFromParts` / `NearestPos`）。
变异清单同步：

- **下架 5 条失效锚点**：`HALF_DAY` 跨年阈值、`NearestPos`、`GuessYear`、`TsFromParts-adj、
  `YearTables-adj`（对应代码已删）。
- **改写 6 条 LINE 锚点**：分钟守卫从 `if (ts < 0) continue;` 改为
  `if (!minSet.Contains(doc.Minute[i])) continue;`，行号 268/335 → 277/342；
  pid/tid 同构块随之下移（254/261/325/330 → 263/270/332/337），`TagOf` 243 → 246。
- **新增 9 条锚点**（全部 KILLED，每条抓住 1~3 个用例）：

| # | 变异 | 抓住它的用例 |
|---|---|---|
| M1 | 分钟哨兵 `MINUTE_NONE` 255 → 0 | 分钟过滤跳过无时间戳的行（主路 + 增量路） |
| M2 | ScanRange 无时间戳行分钟列存 0 而非哨兵 | 同上 |
| M3 | `ParseSlow` 慢路分钟恒 0 | `ParseLine_透传分钟字段` |
| M4 | `ParseSlow` 解析失败行分钟 0 化 | 同上（越界行 → -1 断言） |
| M5 | `HeaderBytes` 不再识别 `[ ` 前缀 | `HeaderBytes_方括号long格式跳过前缀` |
| M6 | 崩溃分组排序去掉首次行号升序 | `多组崩溃按首次行号排序` |
| M7 | 崩溃组内末次行号改取最小 | `组内时间乱序时末次取行序最后` 等 3 条 |
| M8 | ApplyFilter 分钟守卫放行哨兵行 | 分钟过滤跳过无时间戳的行 |
| M9 | FilterTail 分钟守卫放行哨兵行 | 分钟过滤跳过无时间戳的行_增量路径同样成立 |

**测试侧删改**：删除年份机制用例 23 条（`HasYear`/`BaseYear` 2、跨年回退 4、
`TsToText` 2、`TsFromParts` 全部含 Theory、`NearestPos` 2、混合年份锚点 1，
Theory 展开后合计 23），新增 10 条（`ParseLine_透传分钟字段`、`Build_分钟列各值正确`、
`Append_分钟列增量对齐`、`分钟过滤_闰秒行按59命中`、`HeaderBytes_方括号long格式跳过前缀`、
崩溃行序化/时间文本 5 条）。净变化 663 → 673 → **650**，`dotnet test` 全绿。

### 0c.1 本轮踩的两个新坑（都已修）

1. **tstrun runner 的 ProjectReference 指错仓库 → 全部假 SURVIVED**。
   runner 在 `%TEMP%/tstrun`，`ProjectReference` 硬编码指向**主仓** `logcat/tests/...`。
   在 worktree 里跑变异脚本，变异打在 worktree 文件上，runner 编译运行的却是主仓旧代码——
   本 change 新增变异一度 3/9 假存活。**在 worktree 跑变异前，先核对 runner 的指向**；
   指错时改 csproj + `dotnet restore`，跑完改回。
2. **脚本 revert 用 `git checkout -- <file>`，在未提交改动的树上会打回 HEAD**。
   docstring 原话「不会碰你的其他改动」是错的：未提交时 checkout 恢复的是 HEAD 而非当前内容，
   worktree 上实测两次丢失改动（靠 tar 备份救回）。连带原实现的读写还会把 CRLF 文件写成 LF
   （只是被 checkout 掩盖）。已改为「内存保存变异前内容 → 跑完原地写回」+ 读写统一
   「CRLF 归一化 LF → 替换 → 写回转 CRLF」，并在 docstring 纪律里写明。
   修后复跑「分钟」6 条全部 KILLED、零 SKIP/BUILD-FAIL，工作树无损。

---

## 1. 为什么不能用覆盖率判断测试有效性

行覆盖率 98.56% 只证明「代码被执行过」，不证明「断言咬得住」。本次审计实测：

- 把扫描期取消检查点从每 5000 行改成每 100 万行 → **354 用例全绿**
- 把 `AddRecent` 超上限的淘汰方向从「丢最旧」改成「丢最新」→ **354 用例全绿**
- 把 `CommandStore.FallbackCategory` 从 `"shell"` 改成 `"Shell"` → **354 用例全绿**

这三处代码**都被覆盖率统计覆盖到了**，但一个都测不出来。

> 覆盖率是「代码被执行过」的证明，「测试有效性」得靠变异测试。

---

## 2. 变异测试怎么跑

日常测试用 `dotnet test`（2026-10-22 起本机已可直接跑，见 [`TESTING.md`](TESTING.md)）；
变异脚本 historically 用反射 runner `%TEMP%/tstrun`（`ProjectReference` 指向被测测试工程，
反射调用 `[Fact]` / `[Theory]`）。**注意 runner 的指向决定测哪个仓库的代码**——
在 worktree 里跑前必须核对（§0c.1 第 1 条）。本次的变异脚本已入库：

| 脚本 | 用途 |
|---|---|
| `scripts/mutation-test.py` | 变异测试主脚本（变异清单在 `MUTATIONS` 列表里） |

用法：

```bash
python scripts/mutation-test.py            # 跑全部变异
python scripts/mutation-test.py FilterEngine # 只跑匹配到该关键词的变异
```

脚本对每个变异点独立执行：改文件 → 编译 → 跑全量 → **把该文件写回变异前内容**
（2026-10-24 前用 `git checkout -- <该文件>`；那条路在未提交改动的树上会打回 HEAD，
已改为内存内容写回，见 §0c.1）。判定：

- **KILLED** —— 有用例抓到，测试有效
- **SURVIVED** —— 全绿，该处无有效断言。**但需逐条甄别**，见 §4
- **BUILD-FAIL** —— 编译失败，变异本身写错了

---

## 3. 审计结果：6 处真实问题（✅ 已全部修复）

> 下面保留**发现时的问题描述**以便追溯，末尾的「✅ 处理」是 10-05 的实际修法。
> 复现时的基线是 354 个用例。

### 🔴 P1-1　单例测试会读真实用户配置

**位置**：`tests/logcat.Tests/ModelTests.cs:276` `AppSettings默认实例是进程内单例`

```csharp
// Default 只读用户目录下的 settings.json（测试不写入，无副作用），
var a = AppSettings.Default;
```

注释说「无副作用」——**它确实不写，但它读**。`AppSettings.Default` 指向真实的
`settings.json`（程序目录），测试结果受你本机的窗口几何、路径设置影响。

断言也偏弱：`Assert.Same(a, b)` 对 `??=` 单例恒真，`Assert.NotNull(a.LastRemotePath)` 几乎不可能失败。
同批的 `StorePersistenceTests` 全部走注入的临时路径，**只有它例外**。

> **✅ 处理（10-05）**：查证后发现「反序列化 / 文件损坏回退」这两条语义
> **已被 `StorePersistenceTests.AppSettings_*` 覆盖**（走 `TempStoreFile`），
> 不必重写——只需把单例用例里两个恒真/环境相关的断言删掉，剩`Assert.Same`。
> 原「拆成两个用例」的建议属于重复劳动。

---

### 🔴 P1-2　取消粒度完全没被验证

**位置**：`Services/LogDocument.cs:447` 的 `lineInChunk % 5000`

变异：`% 5000` → `% 1000000`，**354 全绿**。

**为什么抓不到**：用例 `Build_长日志上报进度并支持中途取消` 用 `CancelOnFirstReport`
（首次收到进度上报就 cancel），而 `DoBuild:232` 的 `ct.ThrowIfCancellationRequested()`
位于 `ScanRange` **之后**——即使扫描期检查点稀到 100 万行，异常照样在 `DoBuild` 抛出。

用例注释写的「扫描应在下一个检查点（每 5000 行）抛出，而不是把整份索引跑完」并未被验证。

> **✅ 处理（10-05）**：新增 `Build_取消发生在扫描途中而非扫完后` + helper
> `CancelOnFirstMidScanReport`。判据不是 pct 本身，而是**「取消后是否还有后续上报」**。
> 前后改了三版才对，过程见 §5.5——第一版算错了样本规模与阈值的关系。
> 现已 KILLED。

---

### 🟡 P2-1　拿被测常量当期望值（断言自证）

**位置**：`tests/logcat.Tests/CommandStoreTests.cs:213`

```csharp
Assert.Equal(CommandStore.FallbackCategory, entry!.Category);
```

左右两边都是同一个常量。把它从 `"shell"` 改成 `"Shell"`，测试**依然通过**。

**通则**：凡「拿被测代码里的常量当期望值」的断言都是假断言，必须写死字面量。

```csharp
Assert.Equal("shell", entry!.Category);   // 改法
```

> **✅ 处理（10-05）**：已改字面量，并在注释里写明为什么不能回退成常量引用
> （`"shell"` 是 UI 依赖的分类名，与 `CommandKinds.ShellText` 同值）。现已 KILLED。

---

### 🟡 P2-2　上限淘汰方向没测

**位置**：`tests/logcat.Tests/FavoritesStoreTests.cs:244` `过滤词数量不超过上限`

```csharp
for (int i = 0; i < 65; i++) store.AddTagFilter($"tag{i}");
Assert.Equal(60, store.TagFilters.Count);   // 只验证数量
```

变异：`RemoveAt(Count - 1)`（丢最旧）→ `RemoveAt(0)`（丢最新），**354 全绿**。
而「刚收藏的别被挤掉」正是这个功能的全部意义。

> **✅ 处理（10-05）**：查出**三个**「数量不超过上限」用例都是这个模式，
> 且`过滤词数量不超过上限` 与 `AddTagFilter超过60条后最旧被淘汰`
> （写法本就正确、10-05 之前刚加的）**输入完全相同、前半段断言完全重复**——
> 后者被前者完全包含，属纯冗余，已删。收藏目录与 run-as 两处补上淘汰方向断言。
> 现已 KILLED。

> 同项目的 `CommandStoreTests.收藏数量不超过上限` **写法是对的**——它既断言了
> `Count == 300`，也断言了 `cmd304` 在首位。已照抄该模式。

---

### 🟡 P2-3　`Describe` 缺全集级别断言

`FilterSpec.IsEmpty` 有「全集级别 → 空条件」的用例，`Describe` 没有对应断言。
把 `Models/FilterSpec.cs:44` 的全集短路删掉，`Describe` 仍返回 `（无过滤）`，测试全绿。

> **✅ 处理（10-05）**：新增 `Describe传全集级别视为无过滤`，除全集短路外还加了
> 「乱序全集」与「少一个级别必须输出」两条边界。现已 KILLED。

---

### 🟡 P2-4　`TryParseInt` 缺脏数据用例

`Services/LogParser.cs:415` 的 `TryParseInt` 在括号内出现非数字时返回 `-1`。
现有用例只覆盖了「纯空格」（`I/Tag(    ):`）和正常数字，`I/Tag(abc):` 这类脏数据没测。
把 `if (b < ZERO || b > NINE) return -1;` 改成 `if (false) return -1;`，**354 全绿**。

> **✅ 处理（10-05）**：新增 Theory 5 组脏数据（纯字母/数字后跟字母/字母后跟数字/
> 带符号/中间空格）。断言只锁`-1` 与「其余字段仍可解析」，**不锁 msg 起点**——
> 脏 pid 时 `MsgOffset` 落点由实现决定，锁死会让用例变脆。现已 KILLED（5 个用例同时红）。

---

## 4. 误报记录：SURVIVED 不等于测试无效

SURVIVED 里**多数不是问题**。这部分同样重要——避免下次重复怀疑。

### 4.1 审计时（10-03）确认的等价变异

- **`TagIds` 去掉 `doc.Tags.Count == 0` 短路** —— `doc.Tags` 为空时循环体本来就不执行，
  删掉这个分支行为完全不变。这是**等价变异**，不是覆盖缺口。
- **`ApplyFilter` 等价变异（改注释）** —— 设计如此，存活是正确信号，可当 runner 自检。

### 4.2 探针推翻的误判：进度上报阈值

变异 `nextReport = 1 << 22` → `1 << 40`，测试判 SURVIVED，**但这是误报**。
写了个探针工程 `%TEMP%/probe` 直接观察 `IProgress` 回调序列，实测：

| 场景 | 上报次数 | 断言 `Contains(扫描行 && pct>0)` |
|---|---|---|
| 原样（阈值 4 MB） | 3（`pct=0.0` / `0.698` / `0.95`） | True |
| 阈值改 1 TB | 4 | True ← 初看仍是"存活" |
| **把 445 行整个 `if` 关掉** | 2（`pct=0.0` / `0.95`） | **False → 测试会红** |

误报原因：只改了 `nextReport` 初值，但 444 行 `nextReport = pos + (1 << 22)` 会在同轮重设它。
**断言本身是有效的。**

> **教训**：变异「存活」时，先确认变异本身真的改变了行为，再判定测试有无覆盖。
> 探针打印中间状态，比只做静态推理可靠得多。

### 4.3 只影响性能 / 无法构造的（不算缺陷）

- **`EnsureCap` 扩容步长、`SetColumns` 初始容量**（改成 1 都 SURVIVED）
  —— 这两处**只影响性能、不影响正确性**，没有断言是合理的。要测得断性能阈值，收益低。
- **`FilterTail` 的 `n <= start` → `n < start`** —— 实时采集里 `start == RowCount` 确实会发生
  （`frmMain.cs:713` 传 `_filterDocRows`），但那条路径本就返回空数组，属等价变异。
- **`Reload` 去掉「头部变化则重建」判定** —— 需要在 mmap 期间改写文件头才能构造，
  单测无法稳定复现，用例注释已说明。

### 4.4 10-05 复核时新确认的等价变异

这四条是本轮 SURVIVED 里逐条读实现、算清数据流后确认**不用补**的，
写下来是为了下次别再怀疑：

| 变异点 | 为什么等价 |
|---|---|
| `FracToMs` 不足三位改 `*100` | 循环条件 `scale > 0` 恰好只让 3 位进入，`scale` 是 100/10/1 且**全部 > 0**，所以变异体的三元表达式永远取不到 `*100` 分支。4 位以上早已被 `scale > 0` 截断 |
| `LooksLikeNewRecord` 的 TAB 缩进 | 去掉 `c == TAB` 的`return false` 后，会继续往下走到 `n >= 3 && line[1] == SLASH && _lc.ContainsKey(c)`——`c` 是 TAB 而 `_lc` 只含级别字母，`ContainsKey` 为false，最终仍落到末尾 `return false` |
| `MessageBytes` 的 `mlen <= 0` → `mlen < 0` | `mlen == 0` 时原样返回 `Array.Empty<byte>()`，变异返回 `new byte[0]`——长度都是 0，行为等价 |
| 空行不再中断合并 `prev = -1` → `prev = 0` | `prev` 只是「上一条已索引记录的行下标」，而真正决定是否合并的 `prevHead` 由 435 行独立维护（`pr.WithinMs >= 0 || pr.Level > 0`），不受 `prev` 影响 |

> **判等价的快捷法**：先问「这个变量/条件在**下游**还有没有被读」。
> 被读了才有区分度；像 `prev` 这样只用于记录位置而不参与控制流的，改它天然等价。

### 4.5 （已移除）探针实测：`roll` 与 `within` 是两套编码，不能混算

> **2026-10-24 移除说明**：本节原记录 `FinalizeTs` 跨年推算的探针实测（回退 1 天
> 差值 +364 天等四类场景）。minute-byte-column 删除整套年份机制后，`roll`/`within`
> 两套编码已不存在，对应探针与方法论失去对象，整段移除。
> 仍有效的教训并入 §5.5 通法：**跨年/时间戳这类编码换算逻辑，凭直觉推断几乎必错，
> 先写探针打印中间值，再决定断言什么**——本轮分钟列等价性论证
> （`(ts/60000)%60 ≡ (within/60000)%60`）即是先证后删的同类操作。

---

## 5. 变异测试踩坑

### 5.1 同构代码导致 `str.replace` 匹配歧义

`FilterEngine` 里 `ApplyFilter`（263/270/277）与 `FilterTail`（332/337/342）的
pid/tid 排除、分钟守卫是**逐字符相同**的代码块：

```csharp
bool hit = pidSet.Contains(doc.Pid[i]);
if (spec.PidExclude ? hit : !hit) continue;
```

分钟守卫则从 2026-10-24 起是同一形态的两份：

```csharp
if (!minSet.Contains(doc.Minute[i])) continue;
```

`src.count(old) != 1` 的保护会直接跳过（`匹配数=2`），静默漏掉一半变异点。
**解法**：用「行号 + 该行内容片段」双重校验定位（脚本里的 `LINE_MUTATIONS` 写法）。

> **行号会漂移**：2026-10-04 移除正则后，`FilterEngine.cs` 少了约 47 行，这 6 个行号整体下移。
> 双重校验让漂移变成**安全失败**（报 `SKIP-行内容不符` 而不会误改代码），但仍会静默漏掉
> 全部 6 个变异点 —— 改完 `FilterEngine.cs` 务必回来核对 `LINE_MUTATIONS` 里的行号。
> 当初的 301/308/315/372/377/382 就是这么全部失效的。
>
> **2026-10-05 复核**：`FilterEngine` 那 6 个行号（254/261/268/325/330/335）**未漂移**，
> 全部 KILLED。但 `CommandStore` 的两个变异点从 **281 漂到了 280**
> （`RenameCategory` 上方多了两行 `Trim` 防御），一度被静默跳过。已修正。
>
> **2026-10-24 核对（minute-byte-column）**：分钟守卫改写为集合成员判定后，6 个行号
> 整体变为 263/270/277/332/337/342（pid/tid 各下移 9/7 行），`LogDocument.TagOf` 243 → 246。
> 已同步修正并复验全部 KILLED。**教训复述**：改 `FilterEngine.cs` 后不核行号 =
> 静默丢失一半变异点。

### 5.2 并发编辑会污染审计结果

审计期间在 IDE 里编辑 `docs/TOOLBAR-FAVORITES-*`，导致变异后仍 `BUILD-FAIL`
（报 `PasteUploadDialog` 找不到，而该类型在仓库里确实不存在）。

**纪律**：

1. 变异前 `git status` 确认工作区干净（把自己的改动先提交或 stash）
2. 回滚由脚本「内存保存变异前内容 → 写回」完成（2026-10-24 起）；**若要手动还原，
   绝不要对未提交改动的文件用 `git checkout --`**——它恢复的是 HEAD 而非你的改动
3. 每次变异后核对基线用例数没变（10-03 全程 354，10-05 全程 366~372）

### 5.3 ⚠️ 变异运行期间不能碰仓库任何文件（10-05 连续踩了两次）

10-05 那轮变异跑了 15 分钟，其间我**两次**去改测试文件，后果分两层：

1. **后续变异全部 `BUILD-FAIL`** —— 编译报的是我编辑到一半的中间态
   （`error CS0103: 当前上下文中不存在名称"pct"`），脚本会如实记成 BUILD-FAIL，
   但那是假的，真实结论被淹没。
2. **kill 脚本会残留变异** —— 脚本只在跑完一个变异后`git checkout --` 回滚，
   中途 kill 的话**当次改的代码留在工作区**。本轮两次都残留在
   `Services/LogDocument.cs` 的 `f.Position = start;`（`start` 行对齐那个经典吞行 bug），
   差点把变异体当成真实代码提交进去。

**正确顺序**：先把所有测试改动 **commit** → 再跑变异 → **期间只读不改**（连 `git status` 都别做）。

**kill 后的自检**（务必做）：

```bash
git diff --stat Services/ Models/       # 看有没有残留
# 还原残留变异：从备份/编辑器历史恢复该文件，不要 git checkout --（未提交时会被打回 HEAD）
grep -n "if (false)" Services/*.cs Models/*.cs   # 找残留的变异特征
```

### 5.4 自我矛盾时以实测为准

审计中途一度认定「阈值递进与 `pos` 语义不匹配是实现 bug」。探针实测证明：
把两个阈值都改成 1 TB 反而产生 20001 次上报，但**原样代码是 3 次**——那是人为改坏造成的，
不是既存缺陷。**下结论前先量一下。**

### 5.5 写「能区分两条路径」的断言前，先把数据流算清楚

P1-2（取消检查点）这条前后改了三版才做对，根因都是**没算样本规模与阈值的比例**：

- 12 万行 / 5.84 MB 样本，进度阈值 4 MB → 只越过**一次**阈值 → 只有 1 次 `pct>0` 上报 →
  「取消后是否再收到上报」两种情况完全一样 → 判据无效（第一版就栽在这）。
- 改成 17 万行 / 8.27 MB → 上报序列 `0 → 0.484 → 0.968`，第 2 次上报成了区分信号。
- 约束必须同时满足：**> 8 MB**（否则没有第 2 次扫描期上报）且
  **< 48.6 MB**（`100 万行 × 51 字节`，否则变异后的稀疏检查点也能被碰到）。

还有个陷阱：**`DoBuild` 在扫描开始前就先 `Report((0.0, "扫描行…"))`**（`DoBuild:230`）。
「首次上报就 cancel」切出来的仍是收尾那条路径——和原用例同一个失败点。
必须等第一次 `pct > 0` 才cancel。

> **通法**：设计区分性断言前，先把「样本规模 / 各阈值 / 每次变异改变了什么」写成具体数字表，
> 再决定断言什么。别写完用例再靠变异结果猜。

### 5.6 死循环变异：`received` 既是累加器又是读偏移（10-18）

`SpoolCoreAsync` 里 `received += chunk.Length;` 同时承担「实收累计」与「下一次读偏移」
两个职责。变异时把它减 1（或 `+= 0`），读位置会渐进逼近 EOF 却永远到不了——
**runner 卡满 30 分钟超时**。这不是测试没咬住，是变异体本身不可终止。

**解法**：完整性判定改关 `if (false) throw ShortRead(...)`（同样能验证断言有效性），
并在清单里给同行写 ⚠️ 注释防再犯；脚本已加 `subprocess.TimeoutExpired` 捕获（1800s）。

**附带教训**：kill 卡死的脚本时，当次变异会**残留在工作区**（回滚发生在 run 返回之后）——
先按 §5.3 的自检命令 `git diff --stat` + 找 `if (false)` 特征，再继续。

---

## 6. 待办清单（已全部完成）

| # | 优先级 | 位置 | 动作 | 状态 | 验证 |
|---|---|---|---|---|---|
| 1 | 🔴 P1 | `ModelTests.cs` | 单例测试不再断言字段值（会读真实 `settings.json`），反序列化语义交给 `StorePersistenceTests` 的临时路径用例 | ✅ 已改 | — |
| 2 | 🔴 P1 | `LogDocumentTests.cs` | 新增「取消发生在扫描途中」用例，用 pct + 后续上报次数区分扫描期/收尾 | ✅ 已改 | KILLED |
| 3 | 🟡 P2 | `CommandStoreTests.cs` | `FallbackCategory` 常量改字面量 `"shell"` | ✅ 已改 | KILLED |
| 4 | 🟡 P2 | `FavoritesStoreTests.cs` | 补淘汰方向断言；删掉被完全包含的冗余用例 | ✅ 已改 | KILLED |
| 5 | 🟡 P2 | `ModelTests.cs` | 补 `Describe` 全集级别短路用例 | ✅ 已改 | KILLED |
| 6 | 🟡 P2 | `LogParserTests.cs` | 补 `TryParseInt` 脏数据用例（5 组） | ✅ 已改 | KILLED |

本轮**新发现并修掉**的 6 处见 §0 表（N1~N6），也全部转为 KILLED。

**结论**：清单已清空，剩余 8 个 SURVIVED 全部确认为等价变异或只影响性能（§4.3/§4.4）。

---

## 7. 什么时候该做变异测试

不必每次都做。建议在这几个时机跑：

- **改了核心解析/过滤逻辑**（`LogParser` / `LogDocument` / `FilterEngine`）之后——
  这几处是本项目的正确性核心，且分支多，肉眼很难确认测到了没有
- **覆盖率报告好看但心里没底**的时候
- **新增一批测试之后**，确认新用例是真的有效而不是空转

日常改 UI / 改文案不需要。

> **⚠️ 修正本节旧结论**：原文写「覆盖率已 98.56%，再堆用例的边际收益很低」——
> 10-05 的复核证明这个判断是**错的**。在 98% 覆盖率之下仍然藏着 6 处真实覆盖缺口
> （§0 的 N1~N6），其中 4 处连「覆盖率统计显示已覆盖」都成立。
> **高覆盖率下该修的是断言质量，不是继续加用例数量。**
>
> 真正的经验是：**别用覆盖率决定要不要测，用变异测试决定**。
> 覆盖率回答「这行代码跑过吗」，变异测试回答「这行代码写错了，测得出来吗」。
