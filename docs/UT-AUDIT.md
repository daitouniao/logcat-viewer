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
> 跑测试与出覆盖率的完整流程见 [`TESTING.md`](TESTING.md)。

---

## 0. 2026-10-05 复核速览

| 指标 | 审计时（10-03） | 落实后（10-05） |
|---|---|---|
| 用例数 | 354 | 372 |
| 变异点总数 | 60（未含行号型漂移失效的 6个） | 71 |
| KILLED（有覆盖） | 40 | **63** |
| SURVIVED（疑似无覆盖） | 20 | **8**（全部为等价变异 / 性能项） |

**修掉的 6 处覆盖缺口**（均由变异测试暴露，不在原审计清单里）：

| # | 位置 | 问题 | 现状 |
|---|---|---|---|
| N1 | `LogParser.IsBanner(string)` | TAB 分支从未被测（用例只调了 byte[] 重载） | KILLED |
| N2 | `FavoritesStore` 收藏目录上限 | 只断言数量，淘汰方向反了测不出 | KILLED |
| N3 | `LogDocument` 跨年阈值 `HALF_DAY` | 原用例都落在阈值远端，改成 30 天全绿 | KILLED |
| N4 | `LooksLikeNewRecord` 9 连横线 | 用例的 13 横线横幅被前置 `IsBanner` 提前命中 | KILLED |
| N5 | `MatchAny` 空 needle | and 模式「跳过 vs 不命中」无人验证 | KILLED |
| N6 | `FavoritesStore` 冗余用例 | `过滤词数量不超过上限` 被另一条完全包含 | 已删 |

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

`dotnet test` 在本机环境跑不起来（testhost 加载 `hostfxr.dll` 报 `0x80070005`），
但可以用反射 runner 绕过：`%TEMP%/tstrun` 控制台工程反射调用 `[Fact]` / `[Theory]`。
完整做法见 `.workbuddy/memory/MEMORY.md`。本次的变异脚本已入库：

| 脚本 | 用途 |
|---|---|
| `scripts/mutation-test.py` | 变异测试主脚本（变异清单在 `MUTATIONS` 列表里） |

用法：

```bash
python scripts/mutation-test.py            # 跑全部变异
python scripts/mutation-test.py FilterEngine # 只跑匹配到该关键词的变异
```

脚本对每个变异点独立执行：改文件 → 编译 → 跑全量 → `git checkout -- <该文件>` 回滚。
判定：

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

---

## 5. 变异测试踩坑

### 5.1 同构代码导致 `str.replace` 匹配歧义

`FilterEngine` 里 `ApplyFilter`（254/261/268）与 `FilterTail`（325/330/335）的
pid/tid 排除、分钟守卫是**逐字符相同**的代码块：

```csharp
bool hit = pidSet.Contains(doc.Pid[i]);
if (spec.PidExclude ? hit : !hit) continue;
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

### 5.2 并发编辑会污染审计结果

审计期间在 IDE 里编辑 `docs/TOOLBAR-FAVORITES-*`，导致变异后仍 `BUILD-FAIL`
（报 `PasteUploadDialog` 找不到，而该类型在仓库里确实不存在）。

**纪律**：

1. 变异前 `git status` 确认工作区干净（把自己的改动先提交或 stash）
2. 回滚时**只 `git checkout -- <被改的那一个文件>`**，绝不用整体 checkout（会丢自己的改动）
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
git checkout -- Services/LogDocument.cs # 逐个还原
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
