# Proposal：specs 按实现代际分层（old / logcore）

## Why

13 个 capability 平铺在 `openspec/specs/<capability>/`。这在 2026-11-08 的三块工程重组时是有意保留的（该 change 置 `skip_specs: true`，design D-19 明写「`openspec/specs/` 实测零处旧路径，不动」），因为当时只有一个实现宿主，分层没有对象。

现在 Rust core 重写已在细化（`openspec/notes/RUST-CORE-REWRITE-SLICING.md`，D1：log 文件操作与结果集全部归 Rust）。同一批 capability 从此会出现两种归属：「今天由 C# 实现」与「将来由 Rust `logcat-core` 实现」。扁平命名没有任何位置表达这件事，只能靠给 capability 名硬加前缀堆叠（`rust-filter-…`、`legacy-filter-…`），越堆越乱。

**为什么现在**：刀0 / 刀1 的 change 一旦立项就要知道新契约往哪写。先定地址，后续每个刀次各自落位；不先定，就要在每个刀次重新吵一次同样的问题，而地址一旦被归档 change 引用就很难再改。

## What Changes

- **13 条 `git mv`**：`openspec/specs/<capability>/` → `openspec/specs/old/<capability>/`。需求文本、场景、Purpose **一字不动**，只改路径。
- **`openspec/config.yaml` 两处**：`context` 新增「规范分层」一段（分层判据、迁移规则、分类登记要求）；`rules.specs` 新增一条落位规则。措辞见 `design.md` D-2。
- **不建 `openspec/specs/logcore/`**：git 不跟踪空目录；且 logcore 的契约当前受两条未拍板的缺口阻塞（笔记 §14.1 / §14.2，C16 分页与过滤的归属自相矛盾、C17 标记集合无承载原语），现在写只能是编造。第一个 logcore 契约由对应刀次的 change 自己创建目录。
- **迁移单位 = 整条 capability**（本轮定案「甲」）：将来某条 capability 被新宿主接管时，整条目录迁入新分类，**不允许同一 capability 名在两个分类下并存**。
- **3 处在册笔记补 `old/` 前缀**：本 change 直接让这些引用失效，同批收敛（清单见 `design.md` D-4）。
- **`AGENTS.md` §3.1「规范类」行同批修改**（维护者 2026-11-10 批准）：该行现描述 `openspec/specs/` 为「主规范，按 capability」，与新层级冲突。替换措辞见 `design.md` D-6。
- **BREAKING（就规范地址而言）**：13 个 capability id 全部加 `old/` 前缀。`openspec show "old/message-filter" --type spec` 成为唯一地址，`openspec/specs/message-filter/` 不再存在。

## 范围

- `openspec/specs/` 下 13 个 capability 目录整体下移一层
- `openspec/config.yaml` 的 `context` 与 `rules.specs`
- `AGENTS.md` §3.1「规范类」一行
- `openspec/notes/` 中 3 处指向旧 spec 路径的引用

## 非目标（out of scope）

- **不改任何需求内容**：不合并、不拆分、不重写任何 Requirement 与 Scenario；不新增、不删除需求。specs 描述行为，行为不变则 spec 内容不变
- **不预写 logcore 契约**：分页查询窗口、索引字节格式、过滤器语义等 Rust 侧契约一律不在本 change 内成文（受 C16 / C17 / C18 阻塞）
- **`AGENTS.md` 只改 §3.1 那一行**：不动 §2.3 的 worktree 约定、不动 §3.7 的批准口径、不动其余章节。本 change 不使用 worktree 是维护者对**本次**的豁免，写进 `design.md` D-8，不修改通用规则
- **不使用 worktree**：维护者 2026-11-10 明定本 change 不走 `wt/specs-layering`，直接在 `master` 上实施（详见 `design.md` D-8）
- **不回填历史**：`openspec/changes/archive/**` 里的旧 delta 路径、`openspec/notes/archive/**` 里的旧引用保持原样，它们是当时的事实
- **不定刀0 接缝落位**：「分页查询窗口」是跨代契约（刀0 由 C# 进程内实现、刀2 起由 Rust 实现、契约同一份），落 `logcore/` 还是另立分类，留待刀0 立项前拍板（`design.md` D-5）
- **不改代码、不改构建**：`tests/`、`scripts/` 实测零引用 `openspec/specs/`，本 change 不触碰任何 `.cs` / `.csproj` / 脚本
- **不动 `docs/`**：`docs/` 下无 `openspec/specs/` 引用（实测）

## Capabilities

### New Capabilities

无。

### Modified Capabilities

无。

本 change 是纯路径重组：13 个 capability 的**行为契约一字不变**，只是地址从 `<capability>` 变成 `old/<capability>`。故在 `.openspec.yaml` 置 `skip_specs: true`，不产出 spec delta，也不为凑校验而虚构需求。

## Impact

### 规范工具链

- `openspec list --specs` / `show` / `validate` / `archive` 的 id 输出全部带 `old/` 前缀
- 两级 capability id 由 CLI 原生支持，无需改造工具链：`dist/utils/spec-discovery.js` 递归遍历并以 `segments.join('/')` 生成 id，`dist/core/specs-apply.js` 按 `path.join(mainSpecsDir, ...id.split('/'), 'spec.md')` 落位（注释明确 `nested layouts like specs/<area>/<capability>/spec.md`）
- **CLI 无 capability 改名 / 迁移命令**：`openspec spec` 只有 `show` / `list` / `validate`（实测）。目录迁移一律手工 `git mv`，故迁移动作必须写进 tasks，不能只靠 delta 表达

### 文档与笔记

- `AGENTS.md` §3.1「规范类」行改为「按实现代际分层」，该文件是执行规范唯一入口，不改会让下一个 AI 会话按错的层级写 delta
- `openspec/notes/` 3 处引用需同批补前缀（`design.md` D-4 给了逐处清单）
- `openspec/changes/archive/**` 的旧路径不回填

### 后续刀次

- 刀0 / 刀1 起，新契约写进各自分类目录，按 `design.md` D-2 的规则落位
- 每条 capability 何时迁出 `old/`，由「该能力的实现是否换了宿主」决定，不由本 change 预设

### 代码、构建、测试

- 零影响：不触碰源码、工程文件、构建脚本、测试
- 覆盖率口径、用例数字、发布产物一律不变
