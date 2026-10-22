# Design：自愈提示本地化 + CI 语言回归防线

## 背景事实（2026-10-22 实测核实）

### 1. 语言探测的时机与依据

`Services/Loc.cs` 的**静态构造**执行 `DetectLang()`：

1. 先读 `AppSettings.Default.Language`（落盘在 exe 同目录 `settings.json`）；
2. 该值为空时按 `CultureInfo.CurrentUICulture` 推断；
3. `ParseLang` 规则：非 `zh` 开头一律英文。

仓库根**没有** `settings.json`，测试输出目录（`tests/logcat.Tests/bin/...`）也没有
（实测 `find` 无此文件），故界面语言**完全由系统区域决定**：

| 环境           | CurrentUICulture | Loc.Current |
| -------------- | ---------------- | ----------- |
| 本地开发机     | zh-CN            | ZhCn        |
| GitHub runner  | en-US            | En          |

> 关键推论：`Loc._lang` 是**进程级静态**，且**只在首次访问时初始化一次**。
> 因此测试进程启动那一刻的系统区域，把整轮测试的默认语言钉死了。

### 2. `Loc.F` 的格式化语义

`Loc.F(zh, args)` → `string.Format(CultureInfo.CurrentCulture, T(zh), args)`。

**实参不做格式解析**（实测：`string.Format("{0}", "路径 {0} 无效")` 输出 `路径 {0} 无效`，不抛异常），
故「原因」作为实参传入是安全的。真正会炸的是**把含花括号的串当作格式串再格式化一次** ——
这正是 `frmMain` 走 `ShowStatusRaw` 而非 `ShowStatus` 的原因（`ShowStatus` 会把整条提示当格式串）。

### 3. `ToolStripComboBox` 的类型层次

`ToolStripComboBox` 继承 `ToolStripItem`，**不是** `ComboBox`（两者无继承关系）。
故收参须按真实类型写 `ToolStripComboBox`——写成 `ComboBox` 编译直接报 CS1503。

### 4. `Items.Clear()` 不清 `Text`

实测：`combo.Items.Add("SERIAL123"); combo.SelectedIndex = 0; combo.Items.Clear();`
之后 `combo.Text` 仍是 `"SERIAL123"`。`SelectedIndex` 归 -1，但 `Text` 残留。
故清空设备列表时**必须显式赋空** `Text` —— 否则自愈失败时状态栏提示「adb 不可用」，
设备框里却还挂着旧序列号，用户会以为设备还连着。

## 关键决策

### D1：自愈提示拆成「本地化引导语 + 原文原因」两段

**决策**：`Loc.F("未能启动 adb server（请确认 adb 已加入 PATH，或手动执行 adb start-server）：{0}", reason)`
—— 引导语进译文表，`reason` 作为**实参**传入。

**理由**：
- 引导语是**产品自己的文案**（告诉用户怎么办），必须随界面语言变化；
- `reason` 是 **adb 上游抛的话**，翻译它等于替 adb 编造错误信息，用户拿英文原文去查 adb 文档才对得上。

**备选与否决**：
- ❌ 整句硬编码中文（现状）→ 英文界面显示中文，直接是缺陷。
- ❌ 整句进译文表（含 `{0}`）→ 原因被当格式串解析，含花括号时炸 `FormatException`；
  `Loc.F` 虽有兜底（catch 后返回未格式化串），但那会**丢掉原因**，比不译更糟。
- ❌ 原因也翻译 → 伪造上游信息。

### D2：调用方仍走 `ShowStatusRaw`

**决策**：`frmMain` 侧不改，仍用 `ShowStatusRaw(prompt)`。

**理由**：`ShowStatus` 会把整条提示当格式串再格式化一次（`Loc.F(_statusKey, _statusArgs)`），
而提示里含原因的原始花括号 → 会炸。即便本次的 `Loc.F` 已安全，多一层格式化没有收益。
`ShowStatusRaw` 的语义（运行时数据消息、不参与切语言重算）也正合此场景。

**已知取舍**：提示生成那一刻的语言会被固化，用户随后切语言不会重算这条状态栏文案。
这是**可接受的**——它是瞬时错误提示，不是常驻文案；且真要重算得把「原因」与「引导语」拆成
两个字段由 UI 层拼接，超出本次范围。

### D3：测试断言一律双语，且生成必须在语言作用域内

**决策**：`LangScope.InZh(...)` / `LangScope.InEn(...)` 各「生成 + 断言」一遍。

**理由**：
- 只断言中文 → 被测代码退回硬编码中文仍全绿，抓不住回归；
- 只断言中文且不锁语言 → 英文 runner 必红（历史 4 次翻车都是这个）；
- **生成必须在作用域内** —— 提示是生成那一刻按当时语言算出来的，事后切语言比对拿到的是旧语言的串。

**附带要求**：用到语言 helper 的测试类必须打 `[Collection(LangCollection.Name)]` ——
`Loc._lang` 是进程级静态，xUnit 默认按类并行，不串行会互相抢语言。

### D4：扫描器判据必须精确到「期望值位置 + 是资源键 + 未锁语言」

**决策**：`CiLocaleAssertionTests` 扫测试源码，命中以下**三个条件同时成立**才报错：

1. 断言的**期望值位置**（第一个实参）是中文字面量；
2. 该字面量**恰好是本地化资源键**（在 `Loc.En` 表里）——只有这种才说明被测输出会随语言变化；
3. 所在方法**未锁定语言**（无 `LangScope.InZh/InEn`、无 `Loc.SetLangGuard`，且所在类无类级锁）。

**理由与踩坑记录**：
- 只看「有没有中文字面量」→ 大量误报（筛选词、测试数据里的中文与界面语言无关）；
- 不看「是不是资源键」→ 同样误报（如 "别名"、"正文" 不是资源键）；
- 只看方法体 → 漏判**类级锁**（`FileProxyLaunchTests` 在 ctor 里 `SetLangGuard(ZhCn)` 锁整个类）。

**豁免机制**：确实安全的写法登记进 `KnownLocaleSensitiveAssertions`，**每条必须写理由**。
留白名单而不是放宽判据——白名单本身会被审阅。本期 3 条豁免全是 `CommandStore` 的
`Category` / `Remark`（存中文原文，本地化副本是 `CategoryZh` / `RemarkText`）。

### D5：防线自身要防「假绿」

**决策**：扫描器测试里加两条硬断言 —— 扫到的测试文件数 > 10、扫到的中文期望值数 > 20。

**理由**：`LocTableTests` 已有同类教训（§3.3 坑①）—— 扫描逻辑写错时会返回 0 条，
而"没发现问题"照样成立。**假绿比报错危险**。

**验证**：注入一个 CI 会挂的写法后该测试立刻变红；同一变异在 en-US 环境下确实失败
（`Expected "（无过滤）"` vs `Actual "(no filter)"`）—— 防线与 CI 真实故障**同源**。

### D6：本地复现 CI 环境靠环境变量（已实测有效）

**决策**：`scripts/run-tests-en-us.sh` 设 `LANG` / `LC_ALL` / `DOTNET_CLI_UI_LANGUAGE` 为 en-US
再跑 `dotnet test`。

**实测**（临时探针打印实际区域）：

| 环境         | CurrentUICulture | Loc.Current |
| ------------ | ---------------- | ----------- |
| 不设环境变量 | zh-CN            | ZhCn        |
| 设了环境变量 | en-US            | En          |

.NET 运行时在**进程启动时**读这些变量定默认区域，环境变量被 testhost 继承 → 生效。
（曾误以为 testhost 是独立进程会无视父进程环境变量，实测证明会继承。）

### D7：CI 里只声明 en-US，不做双跑

**决策**：`.github/workflows/ci.yml` 的 Test 步骤加 env 声明 en-US，**不**加第二次测试运行。

**理由**：`windows-latest` 本身就是 en-US，双跑只是把 CI 时长翻倍而无新增信息量。
显式声明的价值在于：把「隐式依赖 runner 区域」变成「写明要 en-US」——
将来换 runner 镜像或改系统语言时不会静默变中文而突然红一片。

### D8：纯函数提取用仓库既有模式

**决策**：`frmMain` 的 `CanStartCapture` / `CanClearLog` / `ClearDeviceCombo` 提为 `internal static`。

**理由**：仓库已有成熟先例（`IsNetworkPath` / `EllipsizeMiddle` / `HumanSize`），
判据一致 ——「能否在无窗体条件下被有意义地验证」。`frmMain` 整体在 coverlet 排除项内，
但这三者是**有明确契约的逻辑**（spec「清除入口的可用性状态机」明列三个场景），
不该只靠排除项蒙混。

**对外行为不变**：三个函数只是把原表达式原样搬过去，调用点行为完全一致。

## 覆盖率归因（实测，勿凭印象改）

重采结果：行 97.60% → 97.47%（3,499/3,585 → 3,504/3,595）、分支 88.46% 持平、
方法 95.65% → 95.66%（374/391 → 375/392）。

分母 +10 行的来源**不是**本次改动：本轮改的三个生产文件里 `AdbManager.cs` 与 `frmMain.cs`
都在 coverlet 排除项内（实测零未覆盖行），只有 `LocTable.RuntimeMsg.cs` 的 +5 行译文进分母
且**全部被覆盖**。0.13 个百分点是分母自然增长所致，**本次改动未拉低覆盖率**。
