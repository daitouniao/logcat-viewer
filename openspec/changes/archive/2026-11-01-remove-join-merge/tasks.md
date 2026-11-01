# Tasks

## 1. 准备：worktree、脚本修正与删前基线

- [x] 1.1 记录本次 apply 的 worktree 标识：分支 `wt/remove-join-merge`，路径以 `git worktree list` 定位（建 worktree 需当轮授权）。验证：`git worktree list` 能看到该分支，本 tasks 首条含该标识
  - 已建：分支 `wt/remove-join-merge`，worktree 目录名 `logcat-wt-rjm`（确切路径以 `git worktree list` 为准）
- [x] 1.2 变异脚本修正（基线之前必做，否则基线全是既有 SKIP 噪声）：runner 路径改指 `.tmp-test/tstrun`（一行）；修回 8 条已失效的行号型锚点（pid/tid/分钟守卫 ×6、TagOf 上界、MAX_JOIN——后两条随删码自然处置）。验证：跑 `scripts/mutation-test.py`，行号型锚点不再因漂移产生 SKIP
  - 已完成：RUNNER 改指 `REPO/.tmp-test/tstrun`；8 条锚点修回（ApplyFilter 808/815/822、FilterTail 877/882/887、MAX_JOIN 21、TagOf 253）；worktree 内建本地 runner（相对 ProjectReference）；全锚点双重校验 0 mismatch；抽查 ApplyFilter(808) 实跑 KILLED（3 用例咬住）
- [ ] 1.3 删前基线：用 `scripts/gen_test_log.py`（固定种子）生成 1G 样本；新建专用探针 `.tmp-test/JoinBaselineProbe`（不入库）测「索引列数组字节量 + 进程 Private 峰值增量 + 索引耗时」各 3–5 轮取区间；跑全量 `mutation-test.py` 留基线报告（`.tmp-test/mutation-log-*.txt`）。验证：三类基线数字与 KILLED/SURVIVED/SKIP 计数回填 design.md §Decisions D8
  - 已完成：探针三类基线 + 变异基线 102/105 已回填 design.md §D8 删前基线实测；变异跑批按用户指示跳过（剩 3 条 + MsgExclude 锚点补验，移至 5.6 删后对照时一并重跑）

## 2. 索引层：拆合并机制（第 1 个 commit）

- [x] 2.1 删 `LogDocument` 合并机制本体：join 形参（`Build`/`BuildAsync`/`DoBuild`/`ScanRange`/`Reload`）、`Join` 属性、`MAX_JOIN`、`FLAG_MULTILINE`、flags 列整列、`joinCounts`、`prev`/`prevHead`/`joined`、`EndPrevHead`/`EndJoined`、`CarryLines`/`CarryEndOff`、`_prevHeadAtEnd`/`_joinedAtEnd`、`LastAppendCarried`、`IsMultiline(row)`、合并判定点；空行与横幅两段分支改为不依赖合并状态的重置逻辑（设计 D5 必留清单）。验证：编译通过；新用例 `LogDocumentTests.NaturalRowCount_EqualsPhysicalLinesMinusBlankAndBanner`（独立计数法结构等式）与 `LogDocumentTests.BlankAndBannerLines_NotIndexed` 通过
  - 已完成；空行/横幅分支改纯跳过并加锚点守护注释
- [x] 2.2 删崩溃 c 轨批末补扫整段（依赖多行标志的兜底路径）；a 轨不动。验证：CrashTests 既有 threadtime 用例全过；新用例 `CrashTests.HeadlessCrashHeader_BecomesStandaloneRecord_EventIdentified_RowEqualsHeaderLine` 通过
  - 已完成；原 c 轨形态用例替换为 D3 钉死用例
- [x] 2.3 删 `Append` 的 carry 回写分支（含崩溃 LineCount 补增与 tag 重映射特例）与「整批续行」早退分支；只删 join 相关，不统一两条路径（设计 D11/Non-goals）。验证：实时采集增量路径既有测试（去除依赖 join 的断言后）通过
  - 已完成；整批空行/横幅走 "unchanged" 并推进 IndexedSize；a 轨创建处 LineCount=1 过渡占位（4.1 删字段）
- [x] 2.4 删主窗体「carry 时回退全量过滤」分支（`ApplyIncremental` 调用方处）。验证：`dotnet test` 受影响类（LogDocumentTests / CrashTests / FilterEngineTests）全过、无新增失败；本段 commit（commit 需当轮授权）
  - 已完成；口径：受影响三类 135/135 全过，全量 741/741 全过（dotnet test，Debug）；段 commit 待授权

## 3. 入口与设置层（第 2 个 commit）

- [x] 3.1 删「续行合并」入口：主窗体 `_join`/`_actJoin` 及其与 `AppSettings.Join` 的双向同步、设置菜单项、`AppSettings.Join`、文案键（本地化表 MainUi 第 23 行附近）。验证：编译通过，设置保存/加载测试中相关断言同步删除后全过
  - 已完成
- [x] 3.2 删导出「单行化」：设置菜单项、`AppSettings.SingleLineExport`、导出行参数 `singleLine`、文案键（MainUi 第 24 行附近）。验证：导出测试改为一行一条口径后通过
  - 已完成；ExportRows/ExportRowsAsync 删 singleLine 形参与替换块
- [x] 3.3 删 `↵` 换行可视符：`AppSettings.NewlineVis`、列表控件的 `NewlineVis` 与正文换行替换、主窗体同步逻辑。验证：`ModelTests` 相关断言删除后 `dotnet test` 全过
  - 已完成；带格式复制的死代码换行替换一并删（4.2 只剩注释术语清理与验证）
- [x] 3.4 `LooksLikeNewRecord` 加说明注释（格式层能力；删 join 后生产侧无调用点，保留供外部/未来使用；不标 `[Obsolete]`）。验证：该 API 既有测试仍通过；本段 commit
  - 已完成；口径：全量 741/741 全过（dotnet test，Debug）；段 commit 待授权

## 4. 展示与崩溃（第 3 个 commit）

- [x] 4.1 `CrashEvent` 删 `LineCount` 字段（唯一消费方是「+N 行」显示分支），`Row` 注释改为「崩溃头所在行号」；删列表控件崩溃摘要的「+N 行」分支；改 `CrashTests` 约 6 处 `LineCount` 断言。验证：`dotnet test` 受影响类全过
  - 已完成；实际 LineCount 断言仅 2 处（其余已随 2.x 段删除）；全量 741/741
- [x] 4.2 带格式复制保留「摘要 → 原文」还原（`cells[^1] = raw`），只删其中变死代码的换行替换；清理散落的「续行/合并/Join」注释术语（解析器、文档、崩溃提取器、崩溃事件模型）。验证：带格式复制手工验证正常；注释里无 join 术语残留（grep 确认）；本段 commit
  - 已完成；死代码换行替换已随 3.3 删；grep 残留仅剩口径说明与 LooksLikeNewRecord 留档注释（有意保留）；带格式复制手工验证归入 5.7 UI 验收清单；段 commit 待授权

## 5. 测试重整、文档与全量验收（第 4 个 commit）

- [x] 5.1 删直接测 join 语义的约 22 条（LogDocumentTests 16 / CrashTests 4 / FilterEngineTests 2）与顺带依赖 join 的约 13 条（LogDocumentZeroCopyTests 5、StorePersistenceTests 3、ModelTests 2、LogDocumentTests 的 Flags 长度断言等）；按能力点不机械补（设计 D10）。验证：删除后 `dotnet test` 全过、口径数字记录
  - 已完成（删改随 2.x–4.x 段落地）：直接删 20 条（LogDocumentTests 14 / CrashTests 4 / FilterEngineTests 2）；顺带依赖项改为最小适配而非删除（ZeroCopy 样本 4→5 行保留零拷贝边界覆盖、AppSettings/StorePersistence 断言删 Join 相关）；`dotnet test` 744/744 全过
- [x] 5.2 补三条真能力 + 崩溃钉死用例（原地放现有按被测类分文件的测试文件）：`LogDocumentTests`（自然行计数 / 空行与横幅仍不索引，2.1 已建）、`FilterEngineTests.RecordsWithoutMetadata_NotHitByMetadataFilter`、`FilterEngineTests.RecordsWithoutMetadata_KeptInExcludeMode`、`FilterEngineTests.RecordsWithoutMetadata_VisibleWithoutMetadataFilter`、`CrashTests.HeadlessCrashHeader_…`（2.2 已建）。验证：新用例全过，对应 spec delta 的每个 Scenario 至少一条用例
  - 已完成：新增 8 条（LogDocumentTests 2、CrashTests 2、FilterEngineTests 4），spec delta 场景各有至少一条用例对应
- [x] 5.3 清理变异脚本：删 9 个 join/singleLine 锚点（含导出 singleLine 一条，整条删除而非改写）；补 2 个新锚点（空行分支恒不跳过 / 横幅分支恒不跳过）；删码后按新行号重核全部行号型锚点（TagOf 上界必改）。验证：脚本跑批无匹配数 = 0 的静默跳过
  - 已完成：随码下架 8 条（7 文本 + MAX_JOIN 行号型）并整条删除 singleLine 锚点；新增 2 条「不索引」锚点；TagOf 253→240、MsgExclude 文本锚点修回块扫描形态；无副作用 dry-check 98 OK / 0 BAD / 0 SKIP（跑批对照按用户指示跳过，见 5.6）
- [x] 5.4 更新 `docs/UT-AUDIT.md`：随锚点清理 / 新增 / 行号修回同步（唯一订正代码行号引用的活文档）。验证：文档与脚本锚点清单一致
  - 已完成：§0d 第四轮记录（锚点增删改、测试删改、新坑 2 条、性能基线）
- [x] 5.5 README ×2 删「续行合并」特性行、「单行化」提及与数据存储段相关表述；README 测试数字 / 徽章与 `docs/TESTING.md` 即时联动。验证：两 README 无 join / 单行化残留，数字一致
  - 已完成：README×2 删特性行/单行化/设置清单表述、增量重载表述修正；徽章与 Tests 段 744 / 97.42% / 88.75% 与 TESTING.md 同批联动
- [x] 5.6 全量验收：`dotnet test` 全过并报口径数字；覆盖率 ≥ 基线（行 97.46% / 分支 88.67% / 方法 95.77%，以 `docs/TESTING.md` 为准，下降必须归因并补）；`mutation-test.py` 删后对照基线判「无新增 SKIP / 无新增 SURVIVED」；1G 手测复核阈值（索引列数组总量 ≤ 1.0 GB、索引耗时 ≤ 删前基线上界 × 1.15）。验证：design.md D8 全表数字落档
  - 已完成（变异对照除外）：dotnet test Debug 744/744；覆盖率三项未覆盖量全部不增（行 98→97、分支 199→193、方法 18→18），百分比微降为删除已覆盖代码的分母效应（归因已记 design §D8 与 TESTING.md）；1G 复测 colBytes 637.8 MB ≤ 1.0 GB ✓、热轮 11.6–11.9 s ≤ 15.3 s ✓；**变异删后对照未跑（用户指示跳过，记待办）**，锚点 dry-check 98 OK / 0 BAD / 0 SKIP
- [ ] 5.7 UI 手工验收清单（验收阶段照着点）：① 设置菜单少两项（续行合并、导出时单行化）；② Message 列不再出现 ↵；③ 无头续行形态的崩溃行详情只剩崩溃头一行；④ 导出文件一行一条、无换行折行；⑤ 崩溃摘要无「+N 行」字样。验证：用户逐项确认
  - Release 构建已产出（worktree `bin/Release/net10.0-windows/logcat.exe`，0 警告），清单待用户逐项确认
- [x] 5.8 本段 commit（commit 需当轮授权）
  - 已提交 `a28c593`（test(suite): 删 join 语义用例补自然行真能力用例，变异锚点/文档/README 同步；修正过一次——剔除了探针遗留的 `scripts/__pycache__` 并补 `.gitignore` 模式）。分支 `wt/remove-join-merge` 共 4 个提交：ff8ad88 / a8f3699 / 7a84b38 / a28c593
- **待办（交付后补）**：`scripts/mutation-test.py` 删后对照全量跑批（98 点，约 35 分钟）——判据：无新增 SKIP / SURVIVED，SURVIVED 集 ⊆ 删前已知 9 条等价/性能项（含删前未跑完的 3 条与 MsgExclude 锚点补验）

## Workflow follow-up

- 用户验收通过后，等明确归档指令：`openspec archive remove-join-merge`——delta 并入主规范（归档时手工给 `message-filter` 的 Purpose 补一句「本能力同时覆盖元数据类过滤下的记录可见性口径」）、同批将 `openspec/notes/REMOVE-JOIN-MERGE.md` 移入 `openspec/notes/archive/`（git mv + 日期前缀）、同批清除 worktree `wt/remove-join-merge`。
- 归档后按既定顺序：修订在途 `chunked-progressive-load` 规划产物（整块重构）→ 其 apply → sidecar 缓存 change。
