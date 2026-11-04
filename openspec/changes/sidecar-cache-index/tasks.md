# Tasks

## 1. 工作区与读写器基座

- [ ] 1.1 创建本 change 专属 worktree：分支 `wt/sidecar-cache-index`（路径以 `git worktree list` 定位，此处只记标识），apply 全程在该 worktree 进行。验证：`git worktree list` 可见该分支。
- [ ] 1.2 实现 sidecar 读写器：头部（magic + 格式版本 + 8 段元素宽度 + 行数 + 各段期望长度与总长 + 身份摘要 + 三字典 + 崩溃事件表）写入与读取、8 段原始字节段顺序写读、magic/版本/长度不符即判定无效。测试：新增 `logcat.Tests/SidecarCacheTests`——`WriteRead_RoundTrip_PreservesAllColumns`、`Header_MagicOrVersionMismatch_Rejected`、`Segment_LengthMismatch_Rejected`。验证：`dotnet test --filter SidecarCacheTests` 全过。

## 2. 缓存身份与键

- [ ] 2.1 实现源路径派生名（哈希 + 可读前缀，不落本机路径明文）与身份摘要（`size` + `mtime` + 首尾各 64 KB 内容指纹）的计算、写入与比对；任一不匹配判失效。测试：`SidecarCacheTests`——`Key_DoesNotContainSourcePathText`、`Identity_AppendDetected`、`Identity_HeadOrTailRewriteDetected`、`Identity_CopyAtPath_IndependentEntry`。验证：`dotnet test --filter SidecarCacheTests` 全过。

## 3. 装配入口与等价性不变量

- [ ] 3.1 在 `LogDocument` 新增 internal 装载入口（仿 `BuildChunkedCore` internal static 范式）：接收 sidecar 读出的 8 段数组，调 private `SetColumns` 填列并恢复三字典与崩溃事件。测试：新增 `logcat.Tests/SidecarEquivalenceTests`——`SidecarLoad_MatchesFullBuild_RowForRow`（行数、8 列逐行一致）、`SidecarLoad_MatchesFullBuild_DictsAndCrashes`（三字典、崩溃事件一致）、`SidecarLoad_MatchesFullBuild_RecordBoundaries`（自然行边界一致，含跨段边界记录）。验证：`dotnet test --filter SidecarEquivalenceTests` 全过；该组用例即 spec「命中结果与全量解析完全等价」的可自动化断言。

## 4. 落盘集成（异步、边界、容量）

- [ ] 4.1 解析完成、界面可用后后台线程落盘：临时文件 + 原子替换（`File.Replace`/`File.Move` 覆盖目标不存在边界）、退出不等待、写失败静默并留启动日志。测试：`SidecarCacheTests`——`Write_TempThenAtomicRename_NoPartialFileVisible`、`Write_Failure_SilentAndOriginalCacheIntact`。验证：`dotnet test --filter SidecarCacheTests` 全过。
- [ ] 4.2 静态文件边界：解析前后各取一次 `size`+`mtime` 比对，不一致放弃落盘；代理 spool 与 adb 采集入口显式排除。测试：`SidecarCacheTests`——`StaticBoundary_FileChangedDuringBuild_NoCacheWritten`。验证：`dotnet test --filter SidecarCacheTests` 全过。
- [ ] 4.3 容量治理：按「行数 × 30 B」预判单份体积，超 1 GB 不落盘且不触发淘汰；目录超限按条目最近使用时间 LRU 淘汰（新条目必留，当前会话使用中的条目跳过；命中装载刷新条目访问时间）。测试：`SidecarCacheTests`——`Capacity_OversizedIndex_NotWritten_NoEvictionTriggered`、`Capacity_DirectoryOverLimit_EvictOldestFirst`、`Capacity_InUseEntry_NotEvicted`。验证：`dotnet test --filter SidecarCacheTests` 全过。

## 5. 命中路径接入 `StartIndex`

- [ ] 5.1 `StartIndex` 本地文件分支在分块编排启动前判定命中：命中 → 读 sidecar → internal 装载 → mmap 打开 + `IndexedSize` 对齐 → 按当前条件全量过滤，**绕过** `RunChunkedLoadAsync`；未命中走原路径。测试：`SidecarEquivalenceTests`——`Reopen_CachedFile_SkipsChunkedPipeline`（命中后不进入分块编排）、`Reopen_CachedFile_FilterAppliedImmediately`（装载完成即呈现按当前条件过滤的结果）。验证：`dotnet test --filter SidecarEquivalenceTests` 全过。
- [ ] 5.2 降级静默分支：缓存缺失 / 损坏 / 版本不识别 / 目录不可写 / 装载中途异常 → 捕获后静默走既有全量加载路径，不弹错误框、无开关，降级事件写启动日志。测试：`SidecarCacheTests`——`Degrade_CorruptCache_FallsBackToFullLoad`、`Degrade_HigherVersion_RebuildsCache`、`Degrade_DirectoryNotWritable_LoadSucceedsSilently`。验证：`dotnet test --filter SidecarCacheTests` 全过。

## 6. 集成验证

- [ ] 6.1 全量回归：`dotnet test` 全过且相对 `docs/TESTING.md` 基线无新增失败，报告口径数字（新增用例数计入）；异步用例以 dotnet test 口径为准（tstrun 不 await）。验证：命令输出与口径声明。
- [ ] 6.2 真实文件冒烟：用本地现成的 1 GB 量级大样本（不入库，仅本地验证）走「首次解析落盘 → 重开命中」全流程人工核验：重开装载 ≤ 600 ms（验收只约束装载段，不含其后全量过滤）、内容与首次一致、`cache/` 出现条目、`startup.log` 留有命中痕迹且无异常。验证：冒烟记录（结果数字附于交付说明）。
