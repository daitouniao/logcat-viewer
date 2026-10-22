# 待重启登记簿：docs 体系存量未结项与私有待评审设计

- 状态：常驻登记，滚动更新
- 日期：2026-10-21（初建）
- 关联提交：无（纯登记，未改动代码）
- 验收结论：不适用

## 用途与规则

- 收录 `docs/` 体系（公开 + 私有）中**未完成、未来可能重启**的 design / bug 条目：2026-10-21 `docs/design/`、`docs/bugfixes/` 停写，存量移入 `docs/archive/`；其中有开放验收项的条目登记于此，作为重启索引（防 `docs/private/` 换机器即丢、防归档快照被遗忘）。
- **重启方式**：补验收通过 → 在本文对应条目标注结项后移出；验收失败暴露新缺陷，或私有设计评审拍板 → 按现行流程新开 OpenSpec change（不再写 `docs/bugfixes/` 报告，分析写入 change 的 proposal / design）。
- 条目只做状态说明与索引，不复制原文；本簿即权威状态（原 `docs/IMPLEMENTATION-TRACKER.md` 跟踪表已于 2026-10-22 停用并归档至 `docs/archive/`，此后不再维护）。
- 归档快照不可回改；本簿只作状态说明，不写指向归档文件的链接。

## A. 存量移档未结项（`docs/archive/`，2026-10-21 移入）

### A1. ADB root 检测与初始化（design）

- 文档：`docs/archive/design/2026-10-21-ADB-ROOT-DETECTION.md`
- 状态：✅ 已编码（提交 2589f47，RootMode 三态 + 8s 超时探测）；🖐 真机手工验收开放
- 开放项（跟踪表第三节第 1 项）：真机验证 Adbd / Su / None 三态判定、授权框不再重弹、Q6 两处重试入口（手动刷新确认弹窗、设备操作窗 OnShown 重试）
- 重启条件：维护者有真机（含已 root / Magisk / 普通 user 构建各一）时补验收

### A2. adb server 自愈（bug）

- 文档：`docs/archive/bug/2026-10-21-ADB-SERVER-SELF-HEAL.md`
- 关联 change：已归档（`openspec/changes/archive/2026-10-21-fix-adb-server-self-heal/`，需求并入主规范 `openspec/specs/adb-server/`）
- 状态：✅ 已实施（tstrun 637 过 / 0 失败时点）；🖐 手工验收三项开放
- 开放项（跟踪表第三节第 6 项）：
  1. 真机自愈：`adb kill-server` 后点「刷新设备」→ server 自动拉起、设备恢复枚举（需 5555 无线设备重连配合）
  2. adb 不在 PATH（或临时改名模拟）：点刷新 → 状态栏出现手动处理提示、设备下拉清空、再点刷新可重试（不误报「无设备」）
  3. 正常态零变化：server 正常时刷新行为与改动前一致，自愈路径零副作用
- 重启条件：维护者有真机 + 可改 PATH 环境时补验收

### A3. 跨会话暂存目录互删（bug）

- 文档：`docs/archive/bug/2026-10-21-MULTISESSION-SPOOL-DELETION.md`
- 关联 change：已归档（`openspec/changes/archive/2026-10-18-fix-spool-multisession/`，提交 5c74470，活性锁方案）
- 状态：✅ 已实施；🖐 手工验收四项暂定放行、保持开放（维护者暂无验证环境）
- 开放项（跟踪表第三节第 5 项）：
  1. 真双进程互斥冒烟：同会话再启第二实例即时退出，且第一实例暂存目录不被启动清理误删
  2. 双 RDP 会话并存：两个 Windows 会话各运行一个实例、各开一个代理文档，互不删除对方暂存目录（核心缺陷场景）
  3. 启动清理计数日志核对：遗留目录启动时清理计数（deleted/skipped/failed）与磁盘实际一致，活跃目录计入 skipped
  4. 关闭文档后回收：`IsDirAlive` 判无主且磁盘目录已删（先删目录后释放锁语义）
- 重启条件：维护者有双 RDP / 多会话环境时补测

### A4. CI 本地化断言失败：fileproxy 越界消息（bug）

- 文档：`docs/archive/bug/2026-10-21-CI-LOCALE-ASSERT-FILEPROXY.md`
- 状态：✅ 已实施（测试改 `LangScope` 双语生成 + 断言，产品代码零改动）；🖐 待用户验收
- 开放项：维护者验收（无真机环境依赖；下次 CI 实跑即间接复验）
- 重启条件：维护者验收；若验收不通过按 bug 流程开 change

## B. 私有待评审设计池（`docs/private/design/`，git-ignored，换机器即丢）

> 本池为「待评审」状态的私有设计；登记目的 = 防丢 + 重启索引。评审拍板转 OpenSpec change 后，从本簿移除并注出去向；长期不排期的条目维护者可决定关闭（关闭也注一笔，不静默消失）。

### B1. 大文件加载性能优化：单流水线渐进发布（方案 C）

- 文档：`docs/private/design/BIGFILE-LOAD-PERF.md`
- 状态：待评审；澄清项全部闭环（DEC-16..DEC-22，审计清单 Q1–Q8 已裁决回填）；与 fileproxy 的落地顺序已定（fileproxy 先行，本方案到来时负责「重接」`StartIndex` 分流段 / `SourcePath` 打开处 / spool 取消行为）
- 重启条件：维护者排期 → 转 OpenSpec change

### B2. 目录时间轴与快速切换（多文件工作区）

- 文档：`docs/private/design/DIRECTORY-TIMELINE-DESIGN.md`
- 状态：待评审（2026-10-13）；需求来自维护者口述使用画像（300–500MB 日志、SMB、同目录多文件高频）
- 关联：与 B1（M1 时间覆盖扫描 / YearTables 修复）、B3 有交叉
- 重启条件：维护者排期 → 转 OpenSpec change

### B3. MCP 服务与多格式日志支持（DLT 优先）

- 文档：`docs/private/design/MCP-AND-FORMATS-DESIGN.md`（主文档）+ `MCP-AND-FORMATS-DESIGN-DISCUSSION.md`（补充讨论：同仓库三工程 Core/GUI/Mcp 已决策）
- 状态：待评审（2026-10-13）；面向分发，新格式优先级 DLT
- 重启条件：维护者排期 → 转 OpenSpec change

### B4. 错误聚类（F-06 ~ F-08，候选编号见 `docs/private/FEATURES.md` §2.2）

- 文档：`docs/private/design/ANALYSIS-2.2-ERROR-CLUSTERING.md`
- 状态：待评审（2026-10-10）；F-06 前置（`Crashes` 事件列表）已随 58fbd8a 落地存在，F-08 无前置
- 重启条件：维护者排期 → 转 OpenSpec change

## C. 探索中（根因未定位，尚未建 change）

### C1. 网络路径文件过滤慢 → 网络文件本地缓存（方向待定）

- 文档：`openspec/notes/NETWORK-FILE-FILTER-PERF.md`（探索结论）
- 状态：**根因定位中，方案方向待重定**（2026-10-22 晚）。探索期间曾建 `network-file-local-cache` change，因动机论证被实测推翻、且与维护者新决策冲突，**已由维护者决定删除**；根因查清后再决定是否重建。
- 开放项：① **43 倍反差根因**（同款访问模式逐行读 274 ms vs 真实 logcat 12–19 s；嫌疑：真实 `moff` 使 `ReadArray` 跨度更长 → 跨页率放大；**若成立则块读是正解，与原结论相反**）② 缓存方案是否仍有价值待重评 ③ 维护者已拍板：文件关闭即清理、不做手动清理按钮、目录写死、不加开关、静态文件不需增量同步
- 重启条件：根因结论产出 → 重定方向 → 决定是否建 change

## 移出记录（已结项，不再重启）

- RECENT-FILES-DESIGN（`docs/archive/design/2026-10-21-RECENT-FILES-DESIGN.md`）：已落地（6f2983d），无开放项，未登记
- FILE-PROXY-DESIGN（`docs/private/design/`）：已定稿并落地，change 已归档（`openspec/changes/archive/2026-10-18-fileproxy/`），未登记
- ANALYSIS-2.1-CRASH-EVENTS（`docs/private/design/`）：已落地留档（58fbd8a），未登记
- 2026-10-16 批次（FILEBROWSER-BLANK-LIST / EMPTY-LINE-STACK-LOSS / DOC-COMPLIANCE-FIX / CRASH-SUMMARY / FILTER-REFACTOR-PLAN / TOOLBAR-FAVORITES）：已验收归档，无开放项，未登记
