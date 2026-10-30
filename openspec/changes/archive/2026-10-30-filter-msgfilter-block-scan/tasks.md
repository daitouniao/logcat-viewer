# Tasks

- [x] 1.0 ~~创建独立 worktree：分支 `wt/filter-msgfilter-block-scan`~~ → **已废弃（2026-10-29）**：该 worktree 与分支已清理，apply 改在主树进行（原因：worktree 内产物副本滞后两轮决策，且实现未提交、tasks 未回填，维护两条线成本高于收益）。
  - 原记录：worktree 曾建于 91c037c；清理前其未提交改动已备份到仓库内 git-ignore 的 `.tmp-test/wt-block-scan-backup/`（`tracked-changes.patch` + `FilterEngineBlockScanTests.cs`），该备份不入库、不作为实现基线。

## 2. 块扫描核心

- [x] 2.1 在过滤引擎内部实现块扫描骨架：`Parallel` 分块（块大小、`MaxDegreeOfParallelism = min(核数, BLOCK_SCAN_MAX_DOP)` 初值 16、重叠 = 最长关键词字节数 − 1）、CS 路径 `IndexOf` 扫描、命中点 → 行归属二分（`Offs`）+ message 段边界复核（`Moff`/`Lens`）、行首落块归属去重（重叠区不重复计行）。验证：新增 `FilterEngineBlockScanTests`——跨块边界命中不遗漏、重叠区命中恰好一次、头部（时间戳/tag）命中不误报、空正文记录不命中，用例构造小文档强制走块扫描路径。
- [x] 2.2 CI 路径：块级首字节两态候选定位 + 逐字节验证（复用 `ContainsAsciiInsensitive` 算法口径），`Vector256` 候选提取按探针 CI-Simd 形态实现，保留无 SIMD 硬件回退（`Vector256.IsHardwareAccelerated` 分支）。验证：`FilterEngineBlockScanTests` 中 CI 用例——ASCII 过滤词在纯 ASCII / 混合中英文档上与逐行路径命中集合逐位一致；`Exception`/`error` 双向（needle 大写/小写）不漏。
- [x] 2.3 非 ASCII 处理：CI 路径块扫描同轮对 `0x80` 单字节做一轮定位，把含 ≥0x80 字节的行交由既有逐行折叠路径判定；**过滤条件中任一关键词含非 ASCII 字节时整体回退逐行路径**（不做补齐）。验证：`FilterEngineBlockScanTests` 等价性用例——含 `İ`、`ß`/`ẞ`、中文正文的文档，ASCII 与非 ASCII 过滤词（区分/忽略大小写各一遍）命中集合与逐行路径完全一致（对应 spec「非 ASCII 行的判定结论不因扫描路径改变」场景），并断言非 ASCII 关键词场景走的是逐行路径。
- [x] 2.4 结果合并层：多关键词 and/or 语义（各关键词命中集合交/并）、空 needle 短路（or 立即全命中、and 跳过）、exclude 取反，均与现状 `MatchAny`/`ok != exclude` 口径一致。验证：`FilterEngineBlockScanTests` 组合用例——空词 + 实词的 and/or、exclude 开/关、多关键词交/并，块扫描与逐行路径结论一致。

## 3. 分派与接线

- [x] 3.1 分派接入全量 `MsgFilter`：行数、候选占比、关键词可用性（全 ASCII 且数 ≤ `BLOCK_SCAN_MAX_KEYS`）三条件（design D1 常量），不满足与 `FilterTail` 维持现状逐行路径；进度按已完成字节换算为「已完成行数估计」映射进 0.35–1.0 段并沿用现有键 `匹配 message {0}/{1}`（不新增本地化键）、取消检查逐块生效。验证：`FilterEngineTests` 既有全部用例不改断言仍过（小文档自动走逐行）；新增分派用例——同输入强制两种路径结果一致、关键词超限/含非 ASCII 词时走逐行；`ApplyFilter` 进度回调仍按序覆盖 0→1 且 x/y 单调。
- [x] 3.2 全量回归：`dotnet test` 全过且无新增失败，报告口径数字（对照 `docs/TESTING.md` 基线）；涉及扫描/索引的类（`FilterEngineTests`、`FilterEngineBlockScanTests`、`LogDocumentTests`、`LogDocumentZeroCopyTests`）单列结果。验证：命令输出即证据。
  - 完成注记（2026-10-30）：`dotnet test` **Debug 756/756、Release 756/756 全过**（基线 717 + 新增 `FilterEngineBlockScanTests` 39 条），无失败、无跳过；`FilterEngineTests` 既有断言零改动仍全过。

## 4. 基准与收尾

- [x] 4.1 基准：构造 ≥200 MB 含中文日志，A/B 对比「现状逐行」vs「块扫描」，记录热/冷缓存、CS/CI（CI 单列「含非 ASCII 回退轮」）、1/3/10 关键词各组数字，**另加高频短词组（`1` / `:` / `error` 类，具体清单自行选定并记录，决策 4）**；**同一大文档上手工执行一次双路径命中集合比对并记录结果**（不做集成测试，决策 2）；按 design 验收口径判定：仅本机多核档（≥16 逻辑核：1 GB ≥5×、200 MB ≥2×）实测，**普通档（4–8 核）标注「未验证」为终态、不排补测（决策 3=①）**；并标定 D1 常量与 `BLOCK_SCAN_MAX_DOP`（已获授权可直接调整，注明依据；若暴露需改结构的问题按决策 6=① 停下回评审）。验证：基准记录写入本 change 目录（数字进 tasks 完成注记），机器核数/档位、「冷缓存不劣化」、短词组结论、双路径比对结论、档位门槛结论均明确。
  - **进行中（2026-10-30，因未达 design 门槛而暂停，待评审）**。环境：Intel Xeon E5-2696 v3 ×2（18 核 / 36 线程 ×2，共 72 逻辑核），63 GB 可用内存；沙箱下进程可见核数在 8/64 间波动，故多数轮次用 `DOTNET_PROCESSOR_COUNT=16` 固定并行度。基准工具 = `.tmp-test/BlockScanBench`（不入库），`cand` = 全部行。
  - 200 MB（2,744,000 行 / 中文行 8% / DOP 16 / 热缓存 3 轮取最小，单位 ms）：
    | 用例 | 逐条 | 块扫描 | 倍数 |
    | --- | ---: | ---: | ---: |
    | CS 1 词 Exception | 57 | 21 | 2.67× |
    | CS 3 词 and | 79 | 50 | 1.59× |
    | CS exclude error | 69 | 27 | 2.54× |
    | CI 1 词 exception | 219 | 116 | 1.88× |
    | CI 3 词 or | 263 | 156 | 1.68× |
    | CI 3 词 and | 194 | 134 | 1.44× |
    | CI 10 词 or | 421 | 261 | 1.62× |
    | CI 短词 `1` | 114 | 116 | 0.98× |
    | CI 短词 `:` | 128 | 129 | 0.99× |
    | CI 短词 `error` | 180 | 128 | 1.41× |
  - 1 GB（14,044,000 行 / 中文行 8% / DOP 16 / 热缓存 2 轮取最小，单位 ms）：CS 1 词 313→108（2.91×）、CS 3 词 and 323→215（1.50×）、CI 1 词 751→505（1.49×）、CI 3 词 or 1334→708（1.89×）、CI 3 词 and 865→660（1.31×）、CI 10 词 or 2130→1112（1.92×）、CI 短词 `1` 636→498（1.28×）、CI 短词 `:` 615→557（1.10×）、CI 短词 `error` 768→526（1.46×）。
  - 双路径命中集合：上述 10 组在 200 MB 与 1 GB 上均**逐位一致**（含 3.7 M / 12.2 M 行的大命中集合）；冷缓存（`NtSetSystemInformation` 清待机列表后各跑一次）两条路径同步劣化（200 MB 基本持平，1 GB +15%~27%），**冷缓存不劣化成立**。
  - 常量标定：`BLOCK_SCAN_MAX_DOP` 16→64 无收益（CI 1 词 44→49 ms、CS 1 词 26→27 ms，内存带宽墙坐实），保留 16；块大小 32 MB 未改。
  - **门槛判定：未达成**——design 要求多核档 1 GB CI ≥5×、200 MB CI ≥2×，实测 1 GB CI 1.49×、200 MB CI 1.88×。主因：① 非 ASCII 行折叠回退按 D3 红线必付，8% 中文行时 1 GB 约 1.1 M 行 × ~0.3 µs ≈ 340 ms，压过扫描本身（CI 无命中对照：CS 21 ms vs CI 118 ms，200 MB）；② 短同义词（`1`/`:`）逐条路径本就便宜（候选定位 + 单字节判定），块扫描无收益；③ 多关键词 and 场景逐条路径提前退出，块扫描每词一轮全量扫。
  - **结论（2026-10-30 用户拍板「接受现状」）**：不改口径、不改结构，如实记录实测结论并交付。由此确定的口径要点：
    - **不承诺倍数**：发版说明 / README 不写「1 GB ≥5×」之类的收益数字（design 已要求 4–8 核不承诺，此处再扩到全部档位）。
    - **正确性优先**：双路径命中集合逐位一致是本 change 的唯一硬门槛，已达成（含 1 GB / 12.2 M 行的大命中集合）。
    - **收益形态**：CS 全场景 1.5–2.9× 稳定正收益；CI 在 ASCII 行占绝对多数时 1.7–1.9×（200 MB）/ 1.3–1.9×（1 GB），短同义词（`1`/`:`）与多词 `and` 场景与现状持平（0.98–1.01×）——块扫描在最坏情形**不劣化**（实测最差 0.92–0.98×，落在测量噪声内）。
    - **普通档（4–8 核）**：未验证、为终态，不排补测（design 决策 3=①）；本次沙箱进程可见核数在 8/64 间波动，凡 DOP=8 的轮次与 DOP=16 轮次结论同向。
    - 基准工具：`.tmp-test/BlockScanBench`（不入库），A/B 直调 `MsgFilterCore` 强制两条路径，命中集合逐位比对后再报耗时；冷缓存用 `NtSetSystemInformation(MemoryPurgeStandbyList)` 清待机列表制造。
- [x] 4.2 探索笔记收尾：在 `openspec/notes/UTF8-BYTE-SIMD-SEARCH.md` 标注「已转 change <name> 并落地」，保持既有决策记录不动（本轮 7+5 项决策日志已于 update 阶段补记，见该笔记「决策日志」段）。验证：笔记状态行更新。
  - 完成注记（2026-10-30）：状态行追加「已转 change `filter-msgfilter-block-scan` 并落地」+ 交付结论（正确性达标、性能未达探针预期，按「接受现状」处理）。

## Workflow follow-up

（归档阶段，非本 change tracked 任务）

- 用户验收通过后按归档指令执行：`openspec archive` + 同批归档探索笔记。（该 change 已无 worktree，无需清除。）
