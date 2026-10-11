# 计划实现的功能

<!--
本文件记录**未来计划实现**的功能，每加一项追加一行。

写法：
| 条目 | 说明 | 状态 |
|---|---|---|

状态：计划中 / 进行中 / 已完成
（已完成的移入 CHANGELOG，本文件只留未完成的）
-->

| 条目 | 说明 | 状态 |
|---|---|---|
| 崩溃 / ANR / native fault 信号检测与高亮 | 打开日志时在索引阶段顺带匹配崩溃信号，命中行高亮 | 计划中 |
| 「仅崩溃记录」复选框 | 按崩溃信号过滤出全部崩溃 / ANR / fault 记录（可能多条） | 计划中 |
| MCP 接口（供 AI 调用过滤） | 把 GUI 已有的过滤能力暴露为 MCP 工具，支持正则 | 计划中 |

---

## 1. 崩溃 / ANR / native fault 信号检测与高亮

打开 GB 级日志的核心目的往往是"找到崩溃原因"，现在还得手动搜
`FATAL EXCEPTION` / `ANR in`，大文件优势打折。

**范围**

- `LogDocument` 索引阶段顺带匹配信号 pattern（字节级手写，不用正则）
- 首批 pattern：崩溃（`FATAL EXCEPTION` / `Fatal signal` / tombstone 头）、
  ANR（`ANR in` / `Reason:` + `Input dispatching timed out` / `am_anr`）、
  native（`signal 11 (SIGSEGV)` / `signal 6 (SIGABRT)` / `backtrace:` / `Abort message:`）、
  内存（`OutOfMemoryError` / `lowmemorykiller` / `GC overhead limit`）、
  生命周期（`Process .* has died` / `am_proc_died`）
- UI 高亮分三档投入：行级底色（先做）→ 问题汇总托盘 → gutter / minimap 标记

**关键判断**

- 信号扫描必须绑进已有的字节级索引循环，**不能事后扫**（事后扫要重读 GB 级 mmap）
- `Lvl == LVL_F` 只是候选，`FATAL` 关键字也用于非崩溃场景，**必须靠 pattern 命中**
- 崩溃的 stack trace 已被 `Join` 合并进父记录 → 信号标在父行，符合直觉，
  **不要试图标到stack trace 的物理行上**

**已知坑**

- `Flags` 是byte，bit0 已被 `MULTILINE` 占用。**待定**：6 类信号是塞 `Flags` 剩余 7 bit，
  还是新开 `Sig` 列。新开列必须同步 `EnsureCap` 的 9 处 `Array.Resize` +
  `Assemble`的 `Array.Copy` + `ScanPart` 结构 —— **漏一处就是「Build 正常、追加时崩」**，
  小样本测不出来，要用「1 行 Build + 一次追加 1100 行」触发
- 多行日志在批次边界会被切成两条 → 复用现有 `prevHeadDoc` / `CarryLines` 续行状态
- 增量追加路径也要打信号标，否则实时采集时崩溃信号不亮

**排期**：ROADMAP P1（见 `docs/private/ROADMAP.md`，第 2-4 周）

---

## 2. 「仅崩溃记录」复选框

依赖第 1 项。日志里存在崩溃情报时复选框可用（否则置灰），
勾选后过滤出日志中**全部**崩溃记录 —— 崩溃情报通常是多行的，可能有多个。

- 过滤条件由信号位掩码（`SigKinds`）承载，`FilterEngine.ApplyFilter` 与
  `FilterTail` **两处都要加判定** —— 漏了增量路径会导致实时模式与全量过滤行为不一致
- 崩溃记录以「父行」为单位（多行已合并），因此一次命中 = 一条完整崩溃记录

**已知坑**

- 「复选框是否可用」需要在打开文档时就知道有没有命中信号 →
  依赖第 1 项的索引期扫描结果，不能靠过滤时才现算

**排期**：ROADMAP P2（见 `docs/private/ROADMAP.md`，第 5-6 周，依赖 P1）

---

## 3. MCP 接口（供 AI 调用过滤）

AI 编码助手能**直接查**日志，而不是让用户复制粘贴。
MCP 端点**内嵌在 GUI 进程内、复用 GUI 已有的过滤接口，不在窗口中显示任何入口**。

**范围**

- 手写 JSON-RPC，不引官方 C# SDK（SDK 会带进一批依赖 + 传输层抽象，
  与「单文件 exe 极简分发」冲突，而收益只是几十行 JSON-RPC）
- 首批工具：`search_log` / `get_record` / `list_tags` / `list_pids` /
  `get_signals`（依赖第 1 项）/ `tail`
- 结果**必须截断**（单次返回上限，如 64 KB）—— 日志内容可能含隐私，
  不该整文件往外送

**正则：仅 MCP 侧，GUI 侧不变**

这是对既有决策的**范围收窄**，不是推翻：
GUI 过滤保持字节级子串扫描不变（本项目核心优势），正则只在 MCP 工具里暴露。
理由：AI 单次调用本身就有 `limit` 截断，扫描成本可控；
而 GUI 侧一旦允许正则，十万行以上会从字节扫描退化成正则回退。

> 相关：`docs/private/FEATURES.md` §6「刻意不做」记录了 2026-10-02 有意移除正则过滤。
> 本项**不改**那条决策 —— 只是把正则的暴露面限定在 MCP 工具内。

**待定**

- **启动方式未定**：MCP 不在窗口中显示，那它怎么被唤起？
  候选：GUI 启动时自动起localhost HTTP 服务 / 命令行参数开启 / 环境变量开关
- 若走 HTTP：需处理跨线程读正在 mmap 中且可能正在追加的文档、防火墙弹窗、端口冲突

**已知坑**

- mmap 中的文档可能被增量追加，MCP 只读访问需明确一致性边界

**排期**：ROADMAP P4（见 `docs/private/ROADMAP.md`，第 3 月，收窄为手写 JSON-RPC）

---

## 备注

详细落地路径、已知坑与验收口径见 `docs/private/ROADMAP.md`。
未排期的候选功能见 `docs/private/FEATURES.md`。