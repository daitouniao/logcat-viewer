# 单元测试有效性审计（变异测试）

> 审计日期：2026-10-03
> 审计对象：`tests/logcat.Tests/`（354 个用例，行覆盖率 98.56%）
> 方法：变异测试（mutation testing）——把实现逐点改坏，看测试是否变红
> 结论：**发现 6 处真实问题**（2 处高危、4 处中低），已在文末列出待办。**本轮未改任何测试代码。**

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

## 3. 审计结果：6 处真实问题

### 🔴 P1-1　单例测试会读真实用户配置

**位置**：`tests/logcat.Tests/ModelTests.cs:276` `AppSettings默认实例是进程内单例`

```csharp
// Default 只读用户目录下的 settings.json（测试不写入，无副作用），
var a = AppSettings.Default;
```

注释说「无副作用」——**它确实不写，但它读**。`AppSettings.Default` 指向真实的
`%LOCALAPPDATA%\logcat\settings.json`，测试结果受你本机的窗口几何、路径设置影响。

断言也偏弱：`Assert.Same(a, b)` 对 `??=` 单例恒真，`Assert.NotNull(a.LastRemotePath)` 几乎不可能失败。
同批的 `StorePersistenceTests` 全部走注入的临时路径，**只有它例外**。

**待办**：拆成两个用例——① 用 `LoadFrom(临时路径)` 验证「文件存在 → 正确反序列化」
与「文件损坏 → 回退默认值」；② 单例语义（`Assert.Same`）可以留，但它不该碰真实路径。

---

### 🔴 P1-2　取消粒度完全没被验证

**位置**：`Services/LogDocument.cs:447` 的 `lineInChunk % 5000`

变异：`% 5000` → `% 1000000`，**354 全绿**。

**为什么抓不到**：用例 `Build_长日志上报进度并支持中途取消` 用 `CancelOnFirstReport`
（首次收到进度上报就 cancel），而 `DoBuild:232` 的 `ct.ThrowIfCancellationRequested()`
位于 `ScanRange` **之后**——即使扫描期检查点稀到 100 万行，异常照样在 `DoBuild` 抛出。

用例注释写的「扫描应在下一个检查点（每 5000 行）抛出，而不是把整份索引跑完」并未被验证。

**待办**：需要一个能区分「扫描期抛出」和「收尾抛出」的用例。做法是让进度回调记录
取消时刻的 `pct`：扫描期取消时 `pct` 必然远小于 1；`DoBuild` 收尾抛出时 `pct` 已接近 1。

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

---

### 🟡 P2-2　上限淘汰方向没测

**位置**：`tests/logcat.Tests/FavoritesStoreTests.cs:244` `过滤词数量不超过上限`

```csharp
for (int i = 0; i < 65; i++) store.AddTagFilter($"tag{i}");
Assert.Equal(60, store.TagFilters.Count);   // 只验证数量
```

变异：`RemoveAt(Count - 1)`（丢最旧）→ `RemoveAt(0)`（丢最新），**354 全绿**。
而「刚收藏的别被挤掉」正是这个功能的全部意义。

**待办**：补断言 `Assert.Equal("tag64", store.TagFilters[0])`（最新在首位）且
`Assert.DoesNotContain("tag0", ...)`（最旧的被淘汰）。

> 同项目的 `CommandStoreTests.收藏数量不超过上限` **写法是对的**——它既断言了
> `Count == 300`，也断言了 `cmd304` 在首位。直接照抄这个模式。

---

### 🟡 P2-3　`Describe` 缺全集级别断言

`FilterSpec.IsEmpty` 有「全集级别 → 空条件」的用例，`Describe` 没有对应断言。
把 `Models/FilterSpec.cs:44` 的全集短路删掉，`Describe` 仍返回 `（无过滤）`，测试全绿。

**待办**：加一条
`Assert.Equal("（无过滤）", new FilterSpec { Levels = new[] { 7,1,5,3,2,6,4 } }.Describe())`。

---

### 🟡 P2-4　`TryParseInt` 缺脏数据用例

`Services/LogParser.cs:415` 的 `TryParseInt` 在括号内出现非数字时返回 `-1`。
现有用例只覆盖了「纯空格」（`I/Tag(    ):`）和正常数字，`I/Tag(abc):` 这类脏数据没测。
把 `if (b < ZERO || b > NINE) return -1;` 改成 `if (false) return -1;`，**354 全绿**。

---

## 4. 误报记录：SURVIVED 不等于测试无效

20 个 SURVIVED 里**多数不是问题**。这部分同样重要——避免下次重复怀疑。

### 4.1 我自己写的等价变异

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

### 4.3 顺带确认不算缺陷的

- **`EnsureCap` 扩容步长、`SetColumns` 初始容量**（改成 1 都 SURVIVED）
  —— 这两处**只影响性能、不影响正确性**，没有断言是合理的。要测得断性能阈值，收益低。
- **`FilterTail` 的 `n <= start` → `n < start`** —— 实时采集里 `start == RowCount` 确实会发生
  （`frmMain.cs:713` 传 `_filterDocRows`），但那条路径本就返回空数组，属等价变异。

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

### 5.2 并发编辑会污染审计结果

审计期间在 IDE 里编辑 `docs/TOOLBAR-FAVORITES-*`，导致变异后仍 `BUILD-FAIL`
（报 `PasteUploadDialog` 找不到，而该类型在仓库里确实不存在）。

**纪律**：

1. 变异前 `git status` 确认工作区干净（把自己的改动先提交或 stash）
2. 回滚时**只 `git checkout -- <被改的那一个文件>`**，绝不用整体 checkout（会丢自己的改动）
3. 每次变异后核对基线用例数没变（本次全程 354）

### 5.3 自我矛盾时以实测为准

审计中途一度认定「阈值递进与 `pos` 语义不匹配是实现 bug」。探针实测证明：
把两个阈值都改成 1 TB 反而产生 20001 次上报，但**原样代码是 3 次**——那是人为改坏造成的，
不是既存缺陷。**下结论前先量一下。**

---

## 6. 待办清单

| # | 优先级 | 位置 | 动作 | 状态 |
|---|---|---|---|---|
| 1 | 🔴 P1 | `ModelTests.cs:276` | 单例测试改走临时路径，拆成「反序列化」+「单例」两条 | 待改 |
| 2 | 🔴 P1 | `LogDocumentTests.cs:830` | 补「扫描期取消」的 pct 断言，区分扫描期/收尾抛出 | 待改 |
| 3 | 🟡 P2 | `CommandStoreTests.cs:213` | 常量改字面量 `"shell"` | 待改 |
| 4 | 🟡 P2 | `FavoritesStoreTests.cs:244` | 补淘汰方向断言（照抄 `CommandStoreTests` 写法） | 待改 |
| 5 | 🟡 P2 | `ModelTests.cs` | 补 `Describe` 全集级别用例 | 待改 |
| 6 | 🟡 P2 | `LogParserTests.cs` | 补 `TryParseInt` 脏数据用例 | 待改 |

**验证方式**：改完重跑变异测试，§3 的 6 个变异点应从 SURVIVED 变为 KILLED。

---

## 7. 什么时候该做变异测试

不必每次都做。建议在这几个时机跑：

- **改了核心解析/过滤逻辑**（`LogParser` / `LogDocument` / `FilterEngine`）之后——
  这几处是本项目的正确性核心，且分支多，肉眼很难确认测到了没有
- **覆盖率报告好看但心里没底**的时候
- **新增一批测试之后**，确认新用例是真的有效而不是空转

日常改 UI / 改文案不需要。本项目覆盖率已 98.56%，**再堆用例的边际收益很低，
不如先修上面这 6 条断言质量问题**。
