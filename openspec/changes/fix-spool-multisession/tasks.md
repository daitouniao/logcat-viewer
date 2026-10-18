# Tasks

> 前置说明：本 change 为缺陷修复（满足仓库「较大改动」清单：跨 ≥3 文件），按标准流程执行并携带 bug 报告（`docs/bugfixes/MULTISESSION-SPOOL-DELETION.md`，任务 4.1）。测试口径统一为 tstrun 反射 runner（`dotnet test` 本机不可用），每项任务的验证以对应测试类全绿为准；所有「测试通过」声明必须带口径数字。真双进程 / 双 RDP 会话冒烟属维护者手工项（先例：SingleInstanceTests 同进程语义自动化 + 真双进程手工）。

## 1. 活性锁原语

- [x] 1.1 实现 session 目录活性锁：`SpoolPaths.NewSessionDir` 创建目录后立即在目录内创建锁文件并持锁（`FileShare.Delete` 句柄：放行自删、拒绝其余访问；句柄生命周期 = 目录生命周期，返回 `SpoolDirLease` 租约）；提供「尝试排他打开探测」判据助手（打开成功 = 无主，失败 = 有主）。验证 `SpoolLivenessTests`（新增）通过：持有期间探测判定有主、释放后判定无主、无锁文件目录判定无主、本进程对自己持有的锁探测同样失败（Windows 按句柄校验、不按进程——双轨制删除的关键依据）
- [x] 1.2 建目录-建锁竞态收窄：锁文件创建失败（目录被并发清理删掉的微秒窗口）时重建目录重试一次，再失败才抛 I/O 错由打开路径报告。验证 `SpoolLivenessTests` 通过：构造「目录在建锁前被删」场景（经内部重载注入），重试后创建成功且句柄可持有
- [x] 1.3 复核探测判据的异常面：锁文件不存在 / 存在但被占 / 存在且空闲三种输入均有确定结论（无主 / 有主 / 无主），不抛未处理异常。验证 `SpoolLivenessTests` 对应三条边界用例通过

## 2. 启动清理改造

- [x] 2.1 `FileProxyService.CleanupStaleSpoolDirs` 跳过判据改为活性探测：枚举候选子目录 → 探测锁 → 有主跳过 / 无主删除；删除 `activeDirs` 参数（公开重载 + internal 重载同步），删除 / 跳过 / 失败计数口径不变。验证 `SpoolCleanupTests` 更新后全类通过：崩溃残留全删（无锁目录）、他实例活跃目录跳过（测试内持锁模拟）、删不掉只留痕不抛、混合场景计数正确——全部改为锁持有语义
- [x] 2.2 `Program.cs` 启动清理调用点适配新签名（行为不变：守卫成功后、主界面启动前起后台线程）。验证手工启动一次，StartupLog「启动清理：删 X / 跳过 Y / 失败 Z」口径与改造前一致（空暂存根时为 0/0/0）
- [x] 2.3 `scripts/mutation-test.py` 中 `CleanupStaleSpoolDirs：不再跳过活跃目录` 变异体随新判据更新或确认失效。验证变异测试清单与实际代码判据一致

## 3. 暂存目录全生命周期持锁

- [x] 3.1 `LogDocument` 暂存目录属性改持 `SpoolDirLease` 租约；`Close` 保持「先删目录、后释放锁」顺序（不变式：目录存在 ⇒ 锁仍持有）；非代理文档路径零变化。验证 `LogDocumentProxiedTests` 通过：Close 后探测判定无主且目录可被清理删除、非代理文档 Close 零行为变化锚不回退
- [x] 3.2 `frmMain` spool 分流段：session 目录创建即持锁；取消 / 抢占 / 短读失败路径删目录后释放锁（与 `DeleteDirBestEffort` 同点，先删后释）。验证 `FileProxyLoopbackTests` 取消与短读用例通过，且用例结束后探测判定无主（无锁句柄泄漏）

## 4. 文档与收尾

- [x] 4.1 bug 报告 `docs/bugfixes/MULTISESSION-SPOOL-DELETION.md` 已随本 change 撰写（状态待评审，§3.7 文档头四字段齐全）；实施提交后回填其「关联提交」字段。验证文档头字段回填到位、文件名大写连字符不变
  - 2026-10-18：状态已转「已实施待验收」、头部四字段齐全；**「关联提交」仍待实施提交后回填**（当前未获 commit 授权）。
- [x] 4.2 跑 tstrun 全量并报告口径数字（相对 `docs/TESTING.md` 基线无新增失败；已知环境性失败类别单独声明——如应用运行中互斥体 / 目录占用导致的既有失败）。验证输出含用例总数与通过数
- [x] 4.3 登记维护者手工验收清单至 `docs/IMPLEMENTATION-TRACKER.md`：真双进程互斥冒烟（同会话第二实例仍即退）、双 RDP 会话并存各开一个代理文档互不删除暂存目录、启动清理计数日志核对。验证 tracker 出现本 change 行与待办清单

## Workflow follow-up

- 维护者验收通过后，如其明确指示归档：执行 `openspec archive fix-spool-multisession`（delta 并入 specs、change 入 archive），同批更新 `docs/FEATURES.md` 受影响条目与 README 单实例相关描述（活文档打 tag 时联动口径）
- 归档后验证：主规范「启动清理暂存残留」「单实例守卫」两条为更新后文本、archive 目录结构正确、FEATURES 与主规范无漂移
- 后续多开功能立项时，本 change 的活性锁为其地基；设置文件并发合并等遗留项（proposal out of scope）移交该立项处理
