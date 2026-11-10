# Design：specs 按实现代际分层

## Context

动机见 `proposal.md` 的 Why。以下是决定「怎么切、怎么迁」的现状约束，均在 HEAD 上核对过：

| 约束 | 事实 |
| --- | --- |
| specs 现状 | 13 个 capability 平铺在 `openspec/specs/<capability>/spec.md`，共 846 行、82 条需求 |
| 上一轮的显式延后 | `restructure-project-layout` 置 `skip_specs: true`，design D-19「`openspec/specs/` 实测零处旧路径，不动」；D-38 更进一步：拒绝把工程布局写进 `openspec/config.yaml`，理由「布局在刀0 / 刀4 还要变，写进去就是会烂的镜像」 |
| 新宿主已立项 | `openspec/notes/RUST-CORE-REWRITE-SLICING.md` D1「log 文件操作与结果集全部归 Rust」；七刀（刀0 分页接缝 → 刀1 索引 → 刀2 dll 分页 → 刀3 过滤 → 刀4 MCP → 刀5 获取链 → 刀6 设备取流） |
| logcore 契约当前不可写 | 同笔记 §14：「C16 / C17 / C18 未拍板前，刀1～刀3 不得据此进入 propose」。§14.1 是「dll 接管分页但过滤留 C#」与 `open(filter)->handle` 自相矛盾；§14.2 是标记集合实为过滤输入却无承载原语 |
| C# 侧例外已钉死 | 笔记 §4.2「三处例外」：设备侧（枚举 / 连接 / monitor / server 自愈 / 截图 / 工具箱文件面板）、Windows 外壳、配置与文案（`SplitTerms` / `ParseMinutes` / `ParseInts` / `Loc` / 进度呈现 / 设置存储）留 C# |
| CLI 对多级 id 原生支持 | `dist/utils/spec-discovery.js` 递归遍历、`id = segments.join('/')`；`dist/core/specs-apply.js` 按 `path.join(mainSpecsDir, ...id.split('/'), 'spec.md')` 落位，注释明写 `nested layouts like specs/<area>/<capability>/spec.md` |
| CLI 无 capability 改名 / 迁移命令 | `openspec spec` 只有 `show` / `list` / `validate`（实测）；`archive` 时若某 capability 的最后一条需求被 REMOVED，会 `retireSpec` 删掉该 capability 目录，但**父目录留在原地** |
| 旧路径的引用面很小 | 在库且需改的只有 3 处（`openspec/notes/` 下），`tests/`、`scripts/`、`docs/` 实测零引用 `openspec/specs/` |

## Goals / Non-Goals

**Goals：**

- 13 个 capability 在磁盘上落到 `openspec/specs/old/<capability>/`，**需求文本零改动**
- 分层规则成为 `openspec/config.yaml` 的一部分，下次 propose 自动继承，不靠口口相传
- 给「某个 capability 换实现宿主」留一条明确的迁移路径，且迁移动作可被 tasks 与 delta 双重表达

**Non-Goals（设计层面的额外边界）：**

- 不做「按模块分层」（`specs/logcat-viewer/<cap>` 那套）。理由见 D-1
- 不预制 logcore 的分类骨架、不预写 Rust 侧契约（见 D-7）
- 不引入 capability 命名的批量重整（`adb-capture` 这类名字保持原样，前面只加分类段）
- 不改 CLI、不加脚本、不做自动化迁移工具

## Decisions

### D-1 分层轴取「实现代际」，不取「工程模块」

`openspec/specs/<分类>/` 的第一级 = 谁实现这段行为。当前只有 `old`（既有 C#），将来会有 `logcore`（Rust core）。

**为什么不按三个工程目录切**（`logcat-viewer/` / `logcat-uicore/` / `logcat-android-tools/`）：13 个 capability 里 6 个天然跨模块，切完是「5 / 2 / 空 / 6」——`logcat-android-tools/` 会长期空着，而跨模块层反而装着最大的一块。更致命的是 `logcat-uicore` 承载全部 `LocTable.*` 文案，按「文件在哪」判归属会把几乎所有带界面的能力拖进 uicore。归属判据若要是「行为由谁执行」，那它本来就不是模块这个维度。

**为什么代际轴不撞同一堵墙**：13 个 capability 全部落进 `old`，不存在跨分类的分配问题；分类只在「换宿主」这一刻变动，而那是低频、语义明确、由 change 驱动的事件。

**代价（已知并接受）**：`old` 会长期存在——按 §4.2 的例外表，`adb-server` / `file-browser` / `device-file-transfer` / `android-toolbox` / `ui-language` / `settings-storage` / `app-shutdown` 这 7 项永久留在 `old`，加上 `fileproxy` 的 UI 侧。所以 `old` 不是「历史垃圾场」，它承载的是**当前有效契约**（8/13）。命名取舍已在探索阶段提过并被维护者定案为 `old`，本文不再改判。

### D-2 `openspec/config.yaml` 两处改动的确切措辞

**① `context` 新增一段**（插在「规范定位」之后，与之语义相邻）：

> 规范分层（2026-11-10 定）：`openspec/specs/` 按实现代际分层，落位形如 `specs/<分类>/<capability>/spec.md`；当前分类只有 `old`（既有 C# 实现承载的全部契约）。新宿主接管某能力时新建分类（如 `logcore` 承接 Rust 重写），分类名须先登记在本段，不得随手新建，也不得为「预留」建空目录（git 不跟踪空目录）。落位判据是「这段契约由谁的代码执行」，不是谁先写的。迁移规则「动谁迁谁」：一次 change 若要修改的能力已归或即将归新分类的宿主执行，须在同一 change 内把该 capability **整条**迁入新分类，不得在 `old/` 下原地 MODIFY；同一 capability 名不得在两个分类下并存；迁移完成后 `old/` 下不得残留旧副本。整条 capability 只有部分行为归新宿主时，迁移前须先按宿主边界把它拆成两条 capability（留在 `old` 的那条须改名以体现其边界），再把承接新宿主的那条整条迁出。目录搬运不在 openspec CLI 能力内（`openspec spec` 只有 show / list / validate），一律手工 `git mv` 并写进 change 的 tasks。

**② `rules.specs` 新增一条**（让写 spec 的人一定读到）：

> 落位形如 `specs/<分类>/<capability>/spec.md`；当前分类只有 `old`。分层与迁移规则见 `context` 的「规范分层」。

**为什么不把 13 个 capability 的清单写进 `context`**：那是会烂的镜像（同 `restructure-project-layout` D-38 的反面教训）。`context` 只写规则，不写现状快照；现状由 `openspec list --specs` 自己回答。

### D-3 迁移单位 = 整条 capability（维护者 2026-11-10 定案「甲」）

某 capability 换宿主时，**整条目录**迁入新分类，不保留 `old/<cap>` 与 `<新分类>/<cap>` 并存。

**为什么取整条而不是按需求切**：地址唯一，读者永远不必判断「哪份是权威」；归档后的历史 delta 也能一眼看出这条 capability 属于哪个代际。

**必须配套的处理（否则「甲」会撒谎）**：§4.2 已把 C# 侧写死（过滤参数解析、`Loc`、进度呈现、设置存储）。若某 capability 只有部分行为归新宿主，直接整条搬会让 `<新分类>/<cap>` 里躺着「不由新宿主执行」的需求。故 D-2 的规则里加了前置条件：**迁移前先按宿主边界拆成两条 capability，再整条迁出承接新宿主的那条**。例如 `message-filter` 迁移时，可能先拆出 `filter-input`（参数解析，留 `old`）与 `message-filter`（匹配语义，迁 `logcore`）。

**边界**：本次 change **不做任何拆分**——13 条全部原样放进 `old`，拆分只发生在将来对应刀次的 change 里。

### D-4 3 处在册笔记同步补前缀

本 change 直接让这些引用失效，同批收敛（`openspec/notes/` 是常驻参考，不归档，必须能照着找到文件）：

| 位置 | 现文 | 改为 |
| --- | --- | --- |
| `openspec/notes/2026-11-07-MESSAGE-INDEX-MECHANISM.md:6` | `openspec/specs/message-filter/`、`openspec/specs/time-column/`、`openspec/specs/sidecar-index-cache/`、`openspec/specs/chunked-file-load/` | 四个路径各加 `old/` |
| `openspec/notes/BACKLOG-APP-LIFECYCLE.md:42` | `openspec/specs/fileproxy/spec.md` | `openspec/specs/old/fileproxy/spec.md` |
| `openspec/notes/DLT-INTEGRATION.md:94` | `openspec/specs/message-filter/spec.md` | `openspec/specs/old/message-filter/spec.md` |
| `openspec/notes/2026-11-08-SOURCE-ACQUIRE-CONTRACT.md:44` / `:245` / `:369` | `openspec/specs/fileproxy/spec.md`（共 3 处）、`:369` 另有 `adb-capture/spec.md` 等 3 个简写路径 | 全部补 `old/`，简写路径写成完整路径 |

**`2026-11-08-SOURCE-ACQUIRE-CONTRACT.md` 是实施阶段补入的第 4 份**（apply 时发现）：规划时它还是未跟踪文件（`git grep` 只扫已跟踪文件，故 D-4 初稿漏了它），在 `a5fb2d4` 入库后才进入扫描面。它不是「任务外夹带」，而是同一类「本 change 直接让引用失效」的补齐。

**不动的**：`openspec/notes/archive/**`（冻结档）、`openspec/changes/archive/**`（当时的事实）、`docs/archive/**`。`2026-11-08-SOURCE-ACQUIRE-CONTRACT.md:282` 的「`openspec/specs/` 现 13 个 capability」保持原样——13 这个数字仍然成立，只是现在嵌在 `old/` 之下，且它不是可点击的路径。

**订正（apply 时实测）**：初稿写「`docs/**` 实测零引用」是错的。`docs/archive/2026-10-22-IMPLEMENTATION-TRACKER.md` 有 **3 处**旧路径（`:4` / `:25` / `:49`）。按 AGENTS §3.2，`docs/archive/` 属「不再维护（之后统一删除）」的快照类，存量仅作历史参考、不作为依据，故**不回填**——与「历史记录不改」同口径。初稿误判的原因是规划时的扫描命令带 `:(exclude)docs/archive` 路径排除项却没生效，把零命中的结论写进了文档。

### D-5 「迁移靠 git mv」，delta 只表达需求变更

openspec 没有 capability 改名 / 搬迁命令（D 段 Context 已实测）。因此：

- **本次迁移用 13 条 `git mv`**，并由 `skip_specs: true` 显式声明不产出 delta——需求文本一字未动，用 REMOVED + ADDED 重写 846 行是纯粹的噪声
- **将来的迁移**（含拆分）用「同 change 内两份 delta」表达：`specs/old/<cap>/spec.md` 写 REMOVED、`specs/<新分类>/<cap>/spec.md` 写 ADDED；同时 tasks 里必须有对应的 `git mv`。整条迁完时 archive 会 `retireSpec` 自动删掉空的 `old/<cap>/` 目录，**但父目录 `old/` 会留下**——这是已知残留，不清理、不做判据

### D-6 `AGENTS.md` §3.1 同批修改（维护者 2026-11-10 批准）

`AGENTS.md` §3.1「规范类」行现文把 `openspec/specs/` 描述为「主规范，按 capability」，与本 change 落定的层级冲突。§3.7 规定本文件修订需用户批准，现已获批准，**同批改这一行**。

精确改动（只替换该单元格里的第一个括号说明，行内其余文字与其后的「更新时机」列一字不动）：

| | 文本 |
| --- | --- |
| 现文 | `` `openspec/specs/`（主规范，按 capability） `` |
| 改为 | `` `openspec/specs/`（主规范，按实现代际分层：`specs/<分类>/<capability>/`，当前分类只有 `old`） `` |

**为什么必须同批而不能留给以后**：`AGENTS.md` 是执行规范唯一入口（「§3.1 文档分类」表是活文档清单的权威源）。它说「按 capability」而实际是「按分类 + capability」，下一个 AI 会话或维护者会按错的层级写 delta 路径，且这种错在 archive 合并时才暴露。同类的「指向来源的镜像」风险正是 §3.3 要消掉的东西。

**边界**：只改这一个单元格。不动 §2.3 的 worktree 约定（本 change 的豁免见 D-8）、不动 §3.2 归档口径、不动「更新时机」列。

### D-7 不建 `openspec/specs/logcore/`

两个独立理由：

1. git 不跟踪空目录。要么写 `.gitkeep`（`discoverSpecFiles` 跳过点文件，无害但丑），要么什么都不留。后者更干净。
2. logcore 的契约当前受 §14.1 / §14.2 阻塞，写进去只能是编造。分类目录应由「第一个 logcore 契约落地」的那次 change 自然创建。

`context` 里的规则已覆盖这一点（「不得为预留建空目录」），不需要额外占位。

### D-8 不使用 worktree，直接在 `master` 上实施（维护者 2026-11-10 定）

`AGENTS.md` §2.3 要求 apply 在 change 专属的独立 worktree（`wt/<change-name>`）中实施。**本 change 豁免该约定**，不建 `wt/specs-layering`，直接在 `master` 上改。

**理由（按权重）：**

1. 本 change 的改动面是 13 条纯 rename + 4 处文本（`config.yaml` ×2、`AGENTS.md` ×1、笔记 ×3）——零代码、零构建、零测试运行，worktree 买到的是「主工作树可并行做别的」，而维护者当前正好要在主工作树上完成其他在途修改，另开 worktree 反而把两件事拆到两个目录里
2. 维护者当轮明确指令：用户指令效力高于 `AGENTS.md`（该文件首行即如此规定）

**后果（必须一并接受）：**

- **tasks 首条的 worktree 标识形同虚设**：`AGENTS.md` §2.3 要求「worktree 标识记入 tasks.md 首条防断链」与「tasks 须含『wt 合入目标分支』任务」，本 change 改为记录「不使用 worktree，工作分支 = `master`」，归档门的「wt 已合入」自动满足，无 merge commit 需要
- **归档门禁不变**：tasks 全部关闭仍是硬条件
- **主工作树的在途改动会混进 diff**：apply 开始前必须先确认 `openspec/notes/RUST-CORE-REWRITE-SLICING.md` 的未提交修改与未跟踪的 `2026-11-08-SOURCE-ACQUIRE-CONTRACT.md` 已由维护者处置，否则「只改了预期文件」这条验收判据（tasks 5.5）无法机械判定
- **回滚只剩 `git revert`**：没有可丢弃的分支，改错了按 commit 粒度 `git revert`，不得 `reset --hard`（`master` 是共享历史）
- **不修改 `AGENTS.md` §2.3**：豁免只针对本 change，通用规则保持原样。若将来这类纯文档 change 频繁出现、每次都豁免，再单独立 change 讨论是否放宽 §2.3 —— 现在不预判

## Risks / Trade-offs

- **[`old` 这个名字会长期撒谎——8/13 是当前有效契约，不是历史]** → 已在探索阶段提出，维护者定案保留 `old`；缓解措施是把语义写进 `context`（「`old` = 既有 C# 实现承载的全部契约」），让读者读到规则时不会误解。若将来维护者改主意，改名成本随归档 change 累积，越早越便宜
- **[本 change 与 `restructure-project-layout` D-38 的口径相反]** → 不冲突：D-38 拒的是「把会频繁变的工程布局写进 `context`」，本 change 引入的是「换宿主」这个低频、语义化、由 change 驱动的分类轴，且规则里不写现状快照（D-2 末段）。刀0 / 刀4 增删工程目录时，本规则一个字都不用改
- **[13 个 id 全变，归档 change 里的旧 delta 路径成为历史]** → 接受。历史 delta 表达的是「当时改了什么」，回填等于伪造；`openspec show` 只认现行地址，archive 目录本就不参与解析
- **[迁移期「整条搬」可能把不由新宿主执行的需求搬走]** → 已由 D-2 的前置条件（先拆后搬）+ D-3 的边界（本次不拆）约束；真正的执行在将来各刀次的 change 里 review
- **[全部迁完后 `old/` 残留空目录]** → `retireSpec` 只删 capability 目录不删父目录。接受，不清理：`old/` 长期都有 7 项在里面，不会真的空
- **[`old/` 下的 H1 标题与 id 不再一致]**（文件里仍是 `# message-filter Specification`）→ 接受。H1 不参与校验（parser 按路径取 id）；改 13 个 H1 是纯噪声，且会让「需求文本零改动」这条验收判据失去机械抓手

## Migration Plan

1. 直接在 `master` 上执行（不使用 worktree，见 D-8）；开工前确认主工作树的在途改动已由维护者处置
2. 13 条 `git mv openspec/specs/<cap> openspec/specs/old/<cap>`，此时**不改被移动文件的内容**
3. 改 `openspec/config.yaml` 两处（D-2）与 `AGENTS.md` §3.1 一行（D-6）
4. 改 3 处在册笔记（D-4）
5. 验证（判据见 `tasks.md`）：`openspec list --specs --json` 13 条全形如 `old/<cap>` 且 `requirementCount` 与迁移前逐项一致（合计 82）；`openspec validate --specs --json` 全过；`git status` 显示成对 rename
6. **回滚**：没有可丢弃的 worktree 分支，按 commit 粒度 `git revert`，不得 `reset --hard`（`master` 是共享历史）
7. 无运行时依赖、无发布步骤、不发版

## Open Questions

- **刀0 的「分页查询窗口」契约落哪个分类**：它是跨代接缝（刀0 由 C# 进程内适配实现，刀2 起由 Rust 实现，契约同一份）。候选是 `logcore/query-window`（最终归宿在 Rust）或另立 `contract/` 分类。**不影响本 change 的 tasks**，须在刀0 立项前拍板
