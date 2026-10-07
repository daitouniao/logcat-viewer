# 竞品追赶路线图（2026-10-06 定）

起点判断：项目已经是**优秀的「大文件阅读器」**（mmap + 列式索引 + 虚拟列表，千万行流畅），
但竞品（LogcatOn / rxmt007-log-filter / klogg）正在从"查看器"变成"诊断器"。
**差距不在打开文件的速度，在打开之后能帮用户做什么。**

本文档记录优先级、**本项目实际落地路径**、已知坑与验收口径。
与 `docs/UT-AUDIT.md`（测试纪律）、`docs/TESTING.md`（跑测试的绕法）配套使用。

---

## 优先级总览

| 序 | 事项 | 排期 | 与原建议的差异 |
|---|---|---|---|
| **P0** | UI 国际化底座 + 英文界面 | 第 1-2 周 | **提前**（原列第 4 位） |
| **P1** | 崩溃 / ANR / native fault 信号检测与高亮 | 第 2-4 周 | 不变（第 1 位） |
| **P2** | 过滤增强（排除语法、信号过滤） | 第 5-6 周 | 范围收窄，**不做正则** |
| **P3** | 可选 AI 分析（Ollama / OpenAI 兼容） | 第 2-3 月 | 不变 |
| **P4** | MCP / Agent 接口 | 第 3 月 | 收窄为手写 JSON-RPC，不引 SDK |
| 持续 | 公开可复现性能基准 | 长期 | 新增（原建议未提，但成本低） |

---

## P0 国际化（提前的理由）

### 为什么提前而不是并行
- `frmMain.Designer.cs` **只有 37 行**，整个主窗体 UI 是 `frmMain.cs`（1894 行）代码构建的 ——
  **没有 Designer 控件树可复用**，也没有现成 `.resx` 字符串表。
- 全项目中文串分布（`grep -oP '"[^"]*[\x{4e00}-\x{9fa5}][^"]*"'` 计数）：

  | 文件 | 数量 | 文件 | 数量 |
  |---|---|---|---|
  | frmMain.cs | 160 | Forms/ApkInstallDialog.cs | 41 |
  | Forms/CommandDialog.cs | 92 | Controls/LocalFilePane.cs | 39 |
  | Controls/FilePane.cs | 53 | Controls/DeviceFilePane.cs | 37 |
  | Forms/ApkUninstallDialog.cs | 44 | Forms/FileBrowserDialog.cs | 31 |
  | 其余 9 个窗体 | 54 | Controls/LogListView.cs | 1 |

  合计 **约 560 处**。如果 P1 先改 UI（加高亮列、gutter、issues 面板），
  这批文件要改两遍 —— **不如先把底座铺好，P1 在已翻译的框架上直接加**。

### 落地路径
1. 新增 `Services/Strings.cs`：`static class Strings` + `Resources.resx` / `Resources.en-US.resx`
   （用 `ResourceManager` 或直接生成静态属性）。zh-CN 为**默认资源**（`Resources.resx`），
   en-US 为卫星资源 —— 这样不改任何 `Culture` 默认行为时中文照常工作。
2. 语言选择存 `AppSettings`（已有 `LoadFrom/SaveTo` 可注入路径，天然可测）：
   `public string Language { get; set; } = "";`（空 = 跟随系统）。
3. 改语言需要**重启生效**还是即时生效？→ **建议即时**：所有窗体在 `Shown` 时读一次；
   已打开的窗体不刷新（可接受，文案型 UI 不重排）。
4. `AppInfo.cs`（标题栏 `V0.1.2`）与命令行解析文案也要抽。

### 坑
- **`FilterSpec.Describe()` 里硬编码了中文**（`"级别 "`、`"tag"`、`"（无过滤）"`）——
  这是给用户看的状态文本，必须一起抽，且**单测里可能有断言依赖它**，改之前先 grep 测试。
- `LogParser.LEVEL_NAME` 是 `{"?","V","D",...}`，**这是级别标识不是文案，不要翻**。
- `NewlineVis = "↵"`、`CommandStore` / `FavoritesStore` 里的默认名是**用户数据**，不是 UI 文案，别动。

### 验收
- `README.md` 里那句 "the application UI is currently Chinese-only" 可以删掉。
- 切 en-US 后全窗体 walk 一遍（复用 `%TEMP%/lgayout` 布局审计器的遍历逻辑，
  英文普遍比中文长 30~50%，**必须重跑窄宽度档位**，见 `MEMORY.md` WinForms 布局坑）。

---

## P1 崩溃 / ANR 信号自动检测与高亮

### 定位
最高 ROI。用户打开 GB 级日志的核心目的往往就是"找到崩溃原因"，
现在还要手动搜 `FATAL EXCEPTION` / `ANR in`，大文件优势打折。

### 关键判断：信号扫描必须绑进索引阶段，不能事后扫
`LogDocument.BuildAsync` 已经是**字节级并行分块全文件扫描**（`ScanPart` + `PLogIndexPart`）。
在这条已有的字节循环里顺手多匹配几个 pattern，边际成本几乎为零；
**事后扫要重读一遍 mmap 的 GB 级文件**，还得再开一遍线程/进度条。

- 代价：新增 pattern 后旧文档的信号列失效 → 需要"仅重扫信号列"的轻量入口
  （`RescanSignals()`，只重跑匹配不重解析，可接受秒级）。
- **实时采集路径同理**：`LogcatStream` 追加时增量打信号标。

### 落点
1. **`LogDocument` 新增一列 `Sig`（byte[]）**，与 `Lvl`/`Flags` 并列。
   - `Flags` 是 byte，bit0 = `FLAG_MULTILINE`，**剩 7 bit**：
     `bit1 crash` / `bit2 anr` / `bit3 native fault` / `bit4 lowmemory` / `bit5 tombstone` / `bit6 lifecycle`。
   - 独立成列而不是挤进 `Flags`：`Flags` 已进 `EnsureCap`（`LogDocument.cs:99`）的 9 个
     `Array.Resize` 列表，加列要同步改这里 + `Assemble` 的 `Array.Copy`（`:95`）+ `ScanPart` 结构，
     **漏一处就是"Build 正常、追加时崩"**（`_cap` 满了才走扩容路径，小样本测不出来 ——
     用「1 行 Build + 一次追加 1100 行」触发，见 `MEMORY.md`）。
   - 1000 万行 = 10 MB 内存，可接受。
2. **匹配器用字节级手写，不用正则**（与 `LogParser` 快速路径一致；`CACHES` 已有先例）。
   首批 pattern（对齐 logcat 真实输出，逐条要用真机日志核对）：
   - crash：`FATAL EXCEPTION`、`Fatal signal`、`*** *** ***`（tombstone 头）、`beginning of crash`
   - ANR：`ANR in`、`Reason:`（配合 `Input dispatching timed out`）、`am_anr`
   - native：`signal 11 (SIGSEGV)` / `signal 6 (SIGABRT)`、`backtrace:`、`Abort message:`
   - memory：`OutOfMemoryError`、`lowmemorykiller`、`am_kill`、`GC overhead limit`
   - lifecycle：`Process .* has died`、`am_proc_died`
3. **F 级 ≠ 崩溃**：`Lvl == LVL_F` 只是候选（`FATAL` 关键字也用于非崩溃场景），
   **必须靠 pattern 命中**，不要拿等级当结论。
4. **多行合并下的信号归属**：崩溃行后面跟 stack trace，已被 `Join` 合并进**父记录**
   （`flags[prev] |= FLAG_MULTILINE`）→ 扫 `MessageBytes(row)` 拿到的就是合并后完整消息，
   **信号天然标在父行**，符合直觉。**不要试图标到 stack trace 的物理行上**。
5. **UI 三处高亮**（按投入从低到高）：
   - a. 行级底色/左边框：`LogListView` 是 `VirtualMode = true` 的 `ListView`，
     已有 `MARK_BG` 标记行底色（`item.BackColor` / `item.SubItems[c].BackColor`）——
     **照抄这条路径**加信号色即可，代价最低，先做这个。
   - b. 问题汇总托盘：`FilterSpec` 加 `SigKinds`（byte 位掩码）+ `SigExclude`，
     `FilterEngine.ApplyFilter` / `FilterTail` 两处都要加判定
     （**`FilterTail` 是实时追尾路径，漏了会导致实时模式下过滤行为与全量不一致** —— 老坑同类）。
   - c. gutter strip / minimap dot：需要自绘（`OwnerDraw` 或 `DrawSubItem`），
     视觉收益大但要先解决 `VirtualMode` 下的绘制性能，**放最后**。
6. **跳转**：问题列表双击 → `LogListView` 定位到行（先确认现有 `EnsureVisible` 路径）。

### 坑（来自 `MEMORY.md`，直接相关）
- **多行日志在批次边界被切成两条**：信号扫描要复用现有 `prevHeadDoc` / `CarryLines` 续行状态，
  别新开一条读取路径。
- **`ScanRange` 的 start/end 行对齐**：判断依据是 `start - 1` / `end - 1` 处是不是 `\n`，
  不能看 `start` 自身首字节。
- **增量追加（`Append`）路径也要打信号标**，否则实时采集时崩溃信号不亮。
- **已被 mmap 打开的文件不能 `File.WriteAllBytes` 截断**（改 `MEMORY.md` 记的坑）。

### 验收
- 新增单测：每类 pattern 至少 1 正例 + 2 反例（尤其**不能误报**的：
  普通 `E` 级业务异常、`ANR` 出现在聊天文本里）。
- 跑 `docs/TESTING.md` 的 tstrun 绕法（`dotnet test` 在本机跑不起来）。
- 覆盖率口径保持 Services+Models；信号匹配器是纯函数，**行覆盖容易拉满，注意变异测试**
  （`scripts/mutation-test.py` 会 `git checkout --` 恢复，**跑前必须 commit**）。

---

## P2 过滤增强（范围收窄：不做正则）

### 明确**不做**正则 —— 这是 2026-10-02 有意移除的
`FilterSpec` 已无 `TagRegex`/`MsgRegex`，`FilterEngine` 无 `_reCache`/`CompileRegex`。
竞品 klogg 用 PCRE + hyperscan 是因为 Qt/C++ 生态有现成轮子；.NET 下正则回退会让
千万行过滤的耗时从"字节扫描"变成"正则回退"，**丢的是本项目的核心优势**。

> README 里 LogParser 那句 "regex fallback" 是**格式回退**（解析失败时按 threadtime 正则再试一次），
> 与过滤无关，别混为一谈。

### 实际要做的
1. **排除语法 `-keyword`**：`FilterEngine.SplitTerms` 里识别前缀 `-` → 转成已有的
   `TagExclude`/`MsgExclude`。
   > **更正一个常见误解**：排除功能**已经完整存在**，不是"字段有、入口没开"。
   > `frmMain.cs:58-59` 有 `_ckTagEx`/`_ckMsgEx` 两个 CheckBox，`:980-988` 写入 spec，
   > `FilterEngine` 的全量路径（`:184/:254/:261/:280`）与增量路径 `FilterTail`
   > （`:325/:330/:342`）**两侧都已实现**，单测也各有用例。
   > 所以这条只剩"`-keyword` 语法糖 + 面板上的一行提示"，成本远低于预期。
2. **信号过滤**（依赖 P1）：`SigKinds` 位掩码。
3. `?` 通配（若确认性能可接受）。

### 长期（不排期）
grep 模式 / 查询语言（`AND`/`OR`/`NOT`）—— 等 P1 的信号过滤验证了"多条件组合"的
交互形态再设计，否则容易做出一套没人用的查询语言。

---

## P3 AI 分析（可选、轻量、不自建模型）

### 定位
2026 年日志工具的明显趋势。**不自建模型**，只做"把选中的日志送出去"。

### 落地路径
- UI：工具栏"AI 分析"按钮 → 取当前**选中行**或**当前过滤结果**（超过 N 行截断并提示）
  → 侧边面板显示结构化结果。
- 后端：抽 `Services/IAiBackend` + `OllamaBackend`（`/api/chat`，本地 REST）
  + `OpenAiCompatBackend`（OpenAI / Claude / 各家 OpenAI 兼容网关）。
  `HttpClient` 在 .NET 内置，**零新依赖**，不破坏单文件发布。
- 提示词里**显式要求返回结构化小标题**（根因 / 证据行 / 建议动作），
  侧栏按纯文本或轻 Markdown 渲染即可，别引 Markdown 库。

### 必须有的护栏（缺了就是负功能）
- **默认关闭**，设置里显式开启；每次发送前显示"将向外部发送 N 行日志"。
- **API Key 不明文落盘**：`AppSettings` 是 JSON 明文（见 `StorePath`），
  key 单独存并至少做 DPAPI 或干脆**只从环境变量读**。
- **发送内容做脱敏**（包名/token/IMEI 常见），至少给一个开关。
- **超时 + 取消**，遵循 `BuildAsync` 已有的 `CancellationToken` / `IProgress` 模式。

---

## P4 MCP / Agent 接口（收窄：不引 SDK）

竞品 LogcatOn 有 "Agent Port"、FadCat 有 MCP server 模式。价值真实：
AI 编码助手能**直接查**日志，而不是让用户复制粘贴。

### 为什么不引官方 C# SDK
MCP SDK 会带进一批依赖 + 传输层抽象，与"单文件 exe 极简分发"（见 `PUBLISHING.md`）冲突。
**收益只是几十行 JSON-RPC**。

### 落地路径
在 `Services/` 加 `McpEndpoint.cs`：
- **stdio JSON-RPC**（MCP 标准做法，零端口零冲突）或 localhost HTTP（需注意防火墙弹窗）。
- 暴露的工具集（先少后多）：
  - `search_log(pattern, level?, pid?, limit?)` → 返回行号 + 截断文本
  - `get_record(row)` → 完整原始记录（含多行合并后的全文）
  - `list_tags()` / `list_pids()`
  - `get_signals()`（依赖 P1）→ 崩溃/ANR 清单
  - `tail(n)` → 实时尾部
- 结果**必须截断**（单次返回上限，如 64 KB）——日志内容可能含隐私，不该整文件往外送。
- 注意：**MCP 端点要能只读访问正在打开的文档**，而文档可能在 mmap 中被增量追加。

---

## 持续：公开性能基准

本项目最大的差异化是"大文件"。**公开可复现的基准能把护城河变成可信证据**。

- 现状：README 只说"千万行流畅"，**没有可复现的数字**。
- 竞品 rxmt007/log-filter 宣称 10GiB / 7115 万行 —— 需要一个同口径的对照。
- 做法：`bench/` 目录 + 生成器（合成 logcat 格式文本，**明确标注是合成数据**）+ 计时脚本，
  README 附实测环境（CPU/内存/磁盘/文件大小/行数/打开耗时/滚动帧率）。

---

## 明确不做的（避免范围膨胀）

| 项 | 理由 |
|---|---|
| 正则过滤 | 2026-10-02 有意移除，破坏性能优势 |
| 深色主题 | 已知的未适配项（过滤输入框背景色硬编码浅色），属独立工作量，不搭车 |
| 自建 / 部署模型 | 与项目定位不符 |
| 移动端 / 跨平台 | WinForms + ADB 深度绑定，跨平台要重写 |
| 完整查询语言 | 等 P1 验证交互形态再设计 |

---

## 参考：竞品核实结论

2026-10-06 已逐条 WebSearch 核实（详见 `.workbuddy/memory/2026-10-06.md`）：
- **LogcatOn** 真实但**源码私有**（只是 release 分发页），32+ signal / Agent Port 均为自述无法验证。
- **LogFox 功能描述准确，但作者已声明停止维护**（最后提交 2026-06）——
  **它的能力不应作为追赶基准**。
- 架构最接近的竞品是 **rxmt007/log-filter**（Tauri v2，mmap + checkpoint 索引，10GiB/7115万行）
  和 **klogg**（Qt5，PCRE + hyperscan，>2147483647 行 int32 溢出是其卖点）。
- 被广泛误传的 LogSleuth / "2GB→61MB 峰值内存"性能声明：**查无实据**。
