# 待重启卡 A3：跨 Windows 会话暂存目录互删

- 状态：已实施并归档 change，**手工验收四项开放（暂定放行，维护者暂无验证环境）**（重启卡：未结项条目一卡一条 `openspec/notes/BACKLOG-*.md`，结项后移入 `openspec/notes/archive/`）
- 日期：2026-10-18 立项；2026-10-21 登记；2026-10-29 从登记簿拆为独立卡片
- 关联提交：`5c74470`（change `fix-spool-multisession`，已归档：`openspec/changes/archive/2026-10-18-fix-spool-multisession/`）
- 验收结论：待多会话环境验收
- 来源：原 bug 报告已归档（`docs/archive/bug/2026-10-21-MULTISESSION-SPOOL-DELETION.md`，快照类不再维护）；本卡自包含需求要点

## 基础需求

**现象**：单实例互斥量用 `Local\` 前缀，作用域是**每个 Windows 会话**而非整机。远程桌面多会话并发、快速切换用户（不注销切换账户）两种场景下可各运行一个实例，互斥量互不感知；此时启动清理（暂存根目录子目录无阈值全删、只跳过本进程活跃集合）会把另一实例**正在使用**的暂存目录当崩溃残留删掉。

**危害窗口**：比「拉取进行中」更宽——spool 写盘期间暂存文件排他共享打开、删不掉，而「spool 完成 → 文档关闭」之间无任何保护，被删意味着代理文档索引构建读不到文件或读到残缺内容。

**根因**：

1. `Services/SingleInstance.cs` 互斥量 `Local\logcat-viewer-single-instance` 仅本会话范围（选 `Local\` 是刻意决策：规避跨会话 DACL 坑），缺陷在于把「暂存目录安全」建立在「互斥量覆盖所有并存实例」这个**并未成立的前提**上。
2. `Services/FileProxyService.cs` 的 `CleanupStaleSpoolDirs` 跳过判据只有本进程活跃集合（`activeDirs`），且启动路径传参恒为 null → 等价「见目录就删」。
3. 暂存根目录固定在应用目录下（`temp\logcat-proxy`），同安装路径的所有会话实例共享同一根。

**需求（已落地）**：暂存目录安全性与互斥量覆盖范围**解耦**——每个暂存子目录创建即持**活性锁**（目录内锁文件 + `FileShare.Delete` 句柄），覆盖「创建 → 删除」全生命周期；启动清理对每个候选目录先做活性探测，被任何存活实例持有（含本实例）即跳过。互斥量保持 `Local\` 不变，规范措辞收敛为「本会话范围」，跨会话并存明确为设计容忍状态；同时删除 `activeDirs` 双轨参数（活性探测对自持锁同样判有主）。

## 现状

- 已实施并归档（`5c74470`）。
- 自动化：`SpoolLivenessTests`（锁语义四边界）、`SpoolCleanupTests`（判据改锁持有）、`LogDocumentProxiedTests`（Close 后释放）、`FileProxyLoopbackTests`（取消/短读无泄漏）。

## 开放项（手工验收，暂定放行）

1. 真双进程互斥冒烟：同会话再启第二实例即时退出，且第一实例暂存目录不被启动清理误删。
2. 双 RDP 会话并存：两个 Windows 会话各运行一个实例、各开一个代理文档，互不删除对方暂存目录（核心缺陷场景）。
3. 启动清理计数日志核对：遗留目录启动时清理计数（deleted/skipped/failed）与磁盘实际一致，活跃目录计入 skipped。
4. 关闭文档后回收：`IsDirAlive` 判无主且磁盘目录已删（先删目录后释放锁语义）。

## 重启条件

维护者有双 RDP / 多会话环境时补测。
