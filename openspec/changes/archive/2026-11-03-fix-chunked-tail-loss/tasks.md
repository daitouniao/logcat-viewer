# Tasks: fix-chunked-tail-loss

- [x] 0.1 **Worktree 标识**：本 change 在独立 worktree 中实施，分支 `wt/fix-chunked-tail-loss`（路径以 `git worktree list` 定位）。`git worktree add` 属 git 写操作，实施前需用户当轮授权。（已授权创建，2026-11-02）

## 1. 收尾强制消费接线（`frmMain.Load.cs`）

- [x] 1.1 提取「接管首次发布文档 → 按序应用发布操作 → `UpdateStat()` → 可选跟随滚动」为私有方法，`FlushLoadOps` 改为复用（取操作仍走 `TakeIfDue`，行为不变）。验证：`dotnet test` 现有相关类（`LogListViewTests`、`frmMainTests`、`frmMainHelperTests`）全过，无行为变化。
- [x] 1.2 `RunChunkedLoadAsync` 的 `finally` 中接入收尾强制消费：仅当 `completed && _workerGen == gen` 时执行——`TakeAll()` 无条件取走全部缓冲操作并走 1.1 的应用路径；缓冲为空时若文档已接管仍补一次 `UpdateStat()`（消除状态栏中途快照的最后时序窗口）。取消/失败分支不接入（spec「取消加载整份清空」）。验证：1.3 新用例由红转绿（先写测试后接线亦可，见 1.3）。

## 2. 生产收尾路径自动化断言

- [x] 2.1 新增收尾路径用例（测试工程专用文件，`frmMainTests.cs` 无完整加载基建、不在其上扩展）：**无泵直驱**——测试线程直接 `RunChunkedLoadAsync(...).Wait()`（无 SynchronizationContext，续体走线程池，无 STA 死锁），不泵消息使 WinForms 定时器全程不 tick，精确复现「≤ 首块小文件从未有过成功 flush」的缺陷场景；断言加载完成后——(a) 视图行数 == 文档行数且 > 0（钉死 100 % 空白档）；(b) 状态栏总数 == 文档行数（钉死中途快照/全空档）。用例名：`加载完成后视图行数与文档行数一致`、`加载完成后状态栏总数等于文档行数`。等待判据用「加载标志复位 + 索引推进到文件尾」，不依赖行数巧合。验证：两用例通过；若先于 1.2 编写，须在接线前呈红（复现缺陷）、接线后转绿。
- [x] 2.2 用例语言环境合规检查：新增用例若涉及 `Loc.*` 输出断言，须按 `LangScope.InZh/InEn` 双语各「生成+断言」（CI 为 en-US）；状态栏总数断言建议读数值或以 `UpdateStat` 的内部状态为准，避免文案比对。验证：`bash scripts/run-tests-en-us.sh` 下新用例通过（或明确记录不涉及语言断言）。

## 3. 全量回归与口径

- [x] 3.1 全量 `dotnet test`，报告口径数字，确认相对基线（docs/TESTING.md）无新增失败。验证：命令输出数字入验收记录；tstrun 交叉验证不适用 async 用例时如实声明口径。
- [x] 3.2 真实窗体人工冒烟：打开一份 ≤ 4 MB 的本地日志文件，确认内容立即完整显示、状态栏总数正确（探索实测此档位修复前 100 % 空白，是最直观的验收口径）。验证：人工确认结果记入验收材料。

## Workflow follow-up（归档阶段，非跟踪任务）

- 验收通过后等用户明确归档指令；归档时同批：`openspec archive fix-chunked-tail-loss` + 关联探索笔记 `CHUNKED-TAIL-LOSS.md` 移入 `openspec/notes/archive/`（归档日期前缀）+ 清除 worktree `git worktree remove`。
