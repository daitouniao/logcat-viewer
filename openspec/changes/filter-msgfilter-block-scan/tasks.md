# Tasks

- [x] 1.0 ~~创建独立 worktree：分支 `wt/filter-msgfilter-block-scan`~~ → **已废弃（2026-10-29）**：该 worktree 与分支已清理，apply 改在主树进行（原因：worktree 内产物副本滞后两轮决策，且实现未提交、tasks 未回填，维护两条线成本高于收益）。
  - 原记录：worktree 曾建于 91c037c；清理前其未提交改动已备份到仓库内 git-ignore 的 `.tmp-test/wt-block-scan-backup/`（`tracked-changes.patch` + `FilterEngineBlockScanTests.cs`），该备份不入库、不作为实现基线。

## 2. 块扫描核心

- [ ] 2.1 在过滤引擎内部实现块扫描骨架：`Parallel` 分块（块大小、`MaxDegreeOfParallelism = min(核数, BLOCK_SCAN_MAX_DOP)` 初值 16、重叠 = 最长关键词字节数 − 1）、CS 路径 `IndexOf` 扫描、命中点 → 行归属二分（`Offs`）+ message 段边界复核（`Moff`/`Lens`）、行首落块归属去重（重叠区不重复计行）。验证：新增 `FilterEngineBlockScanTests`——跨块边界命中不遗漏、重叠区命中恰好一次、头部（时间戳/tag）命中不误报、空正文记录不命中，用例构造小文档强制走块扫描路径。
- [ ] 2.2 CI 路径：块级首字节两态候选定位 + 逐字节验证（复用 `ContainsAsciiInsensitive` 算法口径），`Vector256` 候选提取按探针 CI-Simd 形态实现，保留无 SIMD 硬件回退（`Vector256.IsHardwareAccelerated` 分支）。验证：`FilterEngineBlockScanTests` 中 CI 用例——ASCII 过滤词在纯 ASCII / 混合中英文档上与逐行路径命中集合逐位一致；`Exception`/`error` 双向（needle 大写/小写）不漏。
- [ ] 2.3 非 ASCII 处理：CI 路径块扫描同轮对 `0x80` 单字节做一轮定位，把含 ≥0x80 字节的行交由既有逐行折叠路径判定；**过滤条件中任一关键词含非 ASCII 字节时整体回退逐行路径**（不做补齐）。验证：`FilterEngineBlockScanTests` 等价性用例——含 `İ`、`ß`/`ẞ`、中文正文的文档，ASCII 与非 ASCII 过滤词（区分/忽略大小写各一遍）命中集合与逐行路径完全一致（对应 spec「非 ASCII 行的判定结论不因扫描路径改变」场景），并断言非 ASCII 关键词场景走的是逐行路径。
- [ ] 2.4 结果合并层：多关键词 and/or 语义（各关键词命中集合交/并）、空 needle 短路（or 立即全命中、and 跳过）、exclude 取反，均与现状 `MatchAny`/`ok != exclude` 口径一致。验证：`FilterEngineBlockScanTests` 组合用例——空词 + 实词的 and/or、exclude 开/关、多关键词交/并，块扫描与逐行路径结论一致。

## 3. 分派与接线

- [ ] 3.1 分派接入全量 `MsgFilter`：行数、候选占比、关键词可用性（全 ASCII 且数 ≤ `BLOCK_SCAN_MAX_KEYS`）三条件（design D1 常量），不满足与 `FilterTail` 维持现状逐行路径；进度按已完成字节换算为「已完成行数估计」映射进 0.35–1.0 段并沿用现有键 `匹配 message {0}/{1}`（不新增本地化键）、取消检查逐块生效。验证：`FilterEngineTests` 既有全部用例不改断言仍过（小文档自动走逐行）；新增分派用例——同输入强制两种路径结果一致、关键词超限/含非 ASCII 词时走逐行；`ApplyFilter` 进度回调仍按序覆盖 0→1 且 x/y 单调。
- [ ] 3.2 全量回归：`dotnet test` 全过且无新增失败，报告口径数字（对照 `docs/TESTING.md` 基线）；涉及扫描/索引的类（`FilterEngineTests`、`FilterEngineBlockScanTests`、`LogDocumentTests`、`LogDocumentZeroCopyTests`）单列结果。验证：命令输出即证据。

## 4. 基准与收尾

- [ ] 4.1 基准：构造 ≥200 MB 含中文日志，A/B 对比「现状逐行」vs「块扫描」，记录热/冷缓存、CS/CI（CI 单列「含非 ASCII 回退轮」）、1/3/10 关键词各组数字，**另加高频短词组（`1` / `:` / `error` 类，具体清单自行选定并记录，决策 4）**；**同一大文档上手工执行一次双路径命中集合比对并记录结果**（不做集成测试，决策 2）；按 design 验收口径判定：仅本机多核档（≥16 逻辑核：1 GB ≥5×、200 MB ≥2×）实测，**普通档（4–8 核）标注「未验证」为终态、不排补测（决策 3=①）**；并标定 D1 常量与 `BLOCK_SCAN_MAX_DOP`（已获授权可直接调整，注明依据；若暴露需改结构的问题按决策 6=① 停下回评审）。验证：基准记录写入本 change 目录（数字进 tasks 完成注记），机器核数/档位、「冷缓存不劣化」、短词组结论、双路径比对结论、档位门槛结论均明确。
- [ ] 4.2 探索笔记收尾：在 `openspec/notes/UTF8-BYTE-SIMD-SEARCH.md` 标注「已转 change <name> 并落地」，保持既有决策记录不动（本轮 7+5 项决策日志已于 update 阶段补记，见该笔记「决策日志」段）。验证：笔记状态行更新。

## Workflow follow-up

（归档阶段，非本 change tracked 任务）

- 用户验收通过后按归档指令执行：`openspec archive` + 同批归档探索笔记。（该 change 已无 worktree，无需清除。）
