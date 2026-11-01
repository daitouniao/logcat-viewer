# Tasks

## 0. Worktree

- [x] 0.1 建独立 worktree：分支 `wt/mutation-test-accel`，路径以 `git worktree list` 定位后回填此处（禁写本机绝对路径）；`git worktree add` 需当轮授权。**回填：已建于仓库同级目录 `logcat-wt/mutation-test-accel`（2026-10-31，基于 d01b383）**
- [x] 0.2 apply 期核对 runner ProjectReference 指向：脚本 REPO 取脚本所在目录（本 worktree），而 runner 硬编码指向主工作树——在本 worktree 验证脚本行为（2.3 / 4.1）之前，临时把 ProjectReference 改指本 worktree 的测试工程，验证结束后还原指向。验证：跑任一已知 KILLED 变异小样，确认抓到的是 worktree 源码的变异而非主树（还原后再次核对全量口径不变）。**已执行：指向本 worktree，CrashAggregator 变异小样抓到（2026-11-01）；还原指向的动作留待 4.1 结束后执行**

## 1. Runner：fail-fast 与逗号过滤（`.tmp-test/tstrun`，git-ignored）

- [x] 1.1 Runner.cs 加 `TSTRUN_FAIL_FAST=1` 开关：首个失败用例后停止调用后续用例，汇总行仍为 `passed: X failed: Y skipped: Z`；未设该变量时行为与现状逐字节等价。验证：① 不设变量跑全量，输出与改动前一致（tstrun 口径与 docs/TESTING.md 基线交叉核对，MemberData/ClassData 静默跳过类别照常声明）；② 设变量 + 构造一个必失败用例的临时验证（或选用例注入），确认提前退出且 failed=1。**已验证：①全量 756/0/0 与改动前基线一致；②变异下全量 120/1/0 提前退出（首败 CrashAggregatorTests.多组崩溃按首次行号排序）、子集 1/1 即停**
- [x] 1.2 Runner.cs 类名过滤参数支持逗号分隔多子串（含 `,` 时拆分、任一命中即入选；不含逗号语义不变）。验证：`dotnet run "LogDocument,FilterEngine"` 命中多类、单子串行为与改动前一致。**已验证：逗号 199/0/0（=FilterEngine 130 + LogDocument 69）、单子串 69/0/0 与改动前一致**
- [x] 1.3 将 1.1/1.2 的 Runner.cs 改动回写 `docs/TESTING.md` §3.1 模板（防 runner 重建丢改动），并核对 §3.2 坑清单是否需增补。验证：按 §3.1 模板重建 runner（或 diff 模板与实际文件）后全量用例数与基线一致。**已验证：模板与实际文件逐字一致（diff 脚本比对 OK）、全量 756/0/0；§3.2 无需增补（fail-fast 误用已由跑法注释覆盖）**

## 2. 脚本：两级调度（`scripts/mutation-test.py`）

- [x] 2.1 抽出 `run_tests` 为可传过滤子串与 fail-fast 开关的通用入口；子集轮超时 600s、全量轮维持 1800s。验证：对无变异基线分别跑一次子集轮（含逗号过滤）与全量轮，两轮汇总行均能正确解析。**已验证：子集轮 135/0/0、全量轮 756/0/0 均正确解析**
- [x] 2.2 实现 D4 映射：被变异文件 → 测试类名的约定推导（`Services/X.cs` → 类名含 `X`）+ 显式覆盖表 dict（消费方补挂与手工修正）；推导为空且覆盖表无条目 → 标记 `[FULL]` 直接全量。验证：对现有 105 个变异点打印映射结果人工核对一遍，确认无「空子集被当子集跑」的路径。**已验证：12 个文件映射全覆盖、无空映射（覆盖表按测试类依赖梳理：FilterSpec→ModelTests、FileProxy*→FileProxy/Spool、Crash*→Crash、AppSettings→StorePersistence 等）**
- [x] 2.3 实现两级主流程：子集轮（fail-fast 开）有失败 → KILLED（子集口径，附首个失败详情）；全过 → 全量轮（不 fail-fast）→ KILLED（全量口径）或 SURVIVED。输出每轮 tier 标记与耗时，汇总输出 SUBSET-HIT / FALLBACK / FULL 占比。验证：选 2~3 条已知 KILLED 与 1 条已知等价变异（历史 UT-AUDIT 记录）小样复跑，判定结果与历史一致。**已验证：MatchAny×2 与 HeaderBytes、崩溃分组排序 → KILLED|SUBSET-HIT（4~5s）；TagIds 等价变异 → SURVIVED|FALLBACK（全量 756/0/0），全部与历史一致**
- [x] 2.4 加 `--full` 开关：强制每变异全量、不 fail-fast（等价现行为）。验证：`--full` 下任选 3 条变异，输出与改动前脚本逐字段一致。**已验证：3 条 KILLED，passed/failed 与历史逐字段一致（747/9、754/2、755/1）；唯一增量是判定标记附 `|FULL`（属本次设计内的输出扩展）**
- [x] 2.5 docstring 纪律区同步：两级口径、`--full` 用途、映射表维护提醒（改 FilterEngine/测试类名后核对覆盖表，与 LINE_MUTATIONS 行号核对同批）。验证：人工读一遍 docstring，与实际行为一致。**已写入：用法含 `--full` 与两级语义（含 SURVIVED 只出自全量轮的不变量）；纪律区新增映射表维护条目**

## 3. 文档

- [x] 3.1 `docs/UT-AUDIT.md` 补 A+B 流程与双口径声明（KILLED=子集口径、SURVIVED=全量口径）、tier 占比的解读方式、`--full` 仲裁口径；更新 §5 运行纪律中受影响的条目。验证：对照脚本实际行为逐条核对文档描述。**已写入 §0d（A+B 流程、双口径、tier 解读、`--full` 仲裁、实测数字）+ §5.6 补子集轮 600s 上限说明**
- [x] 3.2 `docs/TESTING.md` 核对：§3.1 模板已在 1.3 更新；其余涉及 tstrun 用法/口径的段落按需微调，不引入与 UT-AUDIT 重复的内容（只说明不内联）。验证：通读相关段落无口径冲突。**已核对无冲突；顺手修正 §5.4 过时描述（变异脚本 2026-10-24 起已改内存写回，原文仍写 git checkout）**

## 4. 一致性校验（验收标准）

- [x] 4.1 一致性校验轮：`--full` 口径跑全部 105 个变异点一遍，与两级模式结果对照，要求 KILLED/SURVIVED 判定完全一致（SURVIVED 本就出自全量，重点核对子集 KILLED 的变异在全量下同样失败）。产出对照结论记录到 UT-AUDIT.md。验证：两边结果集合相等，无一致但耗时异常的离群点。**已完成（2026-11-01）：96 判定点逐条对照 0 不一致（KILLED 87 / SURVIVED 9 / SKIP 9 两轮相同）；耗时 27m47s → 11m22s（2.4×）；tier SUBSET-HIT=87（90.6%）。结论已记 UT-AUDIT.md §0d.1；另发现存量问题 2 项（9 锚点漂移、SURVIVED 1→9 待甄别），已记录、属后续审计**

- [x] 4.2 全量测试回归：本 change 不改产品代码与测试用例，跑 `dotnet test tests/logcat.Tests/logcat.Tests.csproj -c Release` 确认基线无新增失败。验证：口径数字与 docs/TESTING.md 基线一致（全过）。**已验证（2026-11-01）：756/756 全过（Release，worktree），与基线一致**

## Workflow follow-up

- 验收通过后等用户明确指示归档：`openspec archive mutation-test-accel`，同批将 `openspec/notes/MUTATION-TEST-ACCEL.md` 移入 `openspec/notes/archive/`（git mv + 日期前缀），并清除本 change 的 worktree（`git worktree remove`）
