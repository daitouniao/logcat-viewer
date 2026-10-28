# 已结项留档：待重启登记簿（2026-10-29 拆分时点）

- 状态：已归档（结项留档，不再维护；同批拆出的未结项条目为 `openspec/notes/BACKLOG-*.md` 重启卡）
- 日期：2026-10-29
- 关联提交：无（纯登记整理，未改动产品代码）
- 验收结论：不适用

本文件承接 2026-10-29 登记簿拆分时**已结项**的条目与历史「移出记录」，与在办事项（`openspec/notes/BACKLOG-*.md` 重启卡）分开存放以免混读。

## 一、2026-10-29 结项条目

### A4. CI 本地化断言失败：fileproxy 越界消息（bug）

- 原报告：`docs/archive/bug/2026-10-21-CI-LOCALE-ASSERT-FILEPROXY.md`（快照类，不再维护）
- 原状态：已实施（测试改 `LangScope` 双语生成 + 断言，产品代码零改动）；待用户验收
- **结项依据**：后续 change `2026-10-24-fix-locale-prompt-and-ci-guard`（已归档）把「就事论事改双语」升级为系统性防线——`CiLocaleAssertionTests` 扫描器自动拦截硬编码中文断言、CI 显式声明 en-US、新增本地一键复现 CI 语言环境脚本、`docs/TESTING.md`「CI 语言环境」章节记录机制与历史翻车。该条目原先唯一开放项「待维护者验收 / 下次 CI 实跑间接复验」已由上述防线覆盖，`tests/logcat.Tests/FileProxyLoopbackTests.cs` 现为双语生成 + 双语断言。
- 备注：若日后再次出现「本地全绿、推送就红」，按 defect 走 bug 流程，不再回到本条目。

### C1. 网络路径文件过滤慢 → 网络文件本地缓存（方向已作废）

- 原状态：根因定位中、方案方向待重定（曾被建 `network-file-local-cache` change，因动机被实测推翻且与维护者决策冲突而删除）
- **结项依据（2026-10-23 根因闭环）**：慢与网络无关，真凶是 `MsgFilter` 按缓冲容量（8 KB）逐行折叠扫描的 CPU 侧缺陷（次因是残留字节污染把 Unicode 回退率从 10.8% 抬到 27.4%）。修复面为 `FoldLowerAsciiInPlace` 增 `Span<byte>` 重载、调用点按 `msgBuf.Length` 切片折叠；实测 17.4 s → 0.5 s。
- 修复落地：change `2026-10-23-fix-msgfilter-capacity-scan`（已归档）→ 后续 change `2026-10-28-filter-msgfilter-zero-copy`（已归档，新建主规范 `openspec/specs/message-filter/`）。
- **连带作废**：「网络文件本地缓存」整套方向（瓶颈从来不是传输）、「块读 / 跨页率」重评（实测跨页率仅 1.1%）。
- **仍有效的维护者决策（2026-10-22 拍板，原文保留）**：缓存若做，则「文件关闭即清理、不做手动清理按钮、目录写死、不加开关、静态文件不需增量同步」。该决策对后续任何缓存方案（如旁车索引缓存）仍有约束力。
- 探索笔记归档位置：`openspec/notes/archive/2026-10-23-NETWORK-FILE-FILTER-PERF.md`。

## 二、历史「移出记录」（2026-10-29 前已结项，未登记过）

- `RECENT-FILES-DESIGN`：已落地（`6f2983d`），无开放项，未登记。
- `FILE-PROXY-DESIGN`：已定稿并落地，change 已归档（`openspec/changes/archive/2026-10-18-fileproxy/`），未登记。
- `ANALYSIS-2.1-CRASH-EVENTS`：已落地留档（`58fbd8a`），未登记。
- 2026-10-16 批次（`FILEBROWSER-BLANK-LIST` / `EMPTY-LINE-STACK-LOSS` / `DOC-COMPLIANCE-FIX` / `CRASH-SUMMARY` / `FILTER-REFACTOR-PLAN` / `TOOLBAR-FAVORITES`）：已验收归档，无开放项，未登记。
- 原 `docs/IMPLEMENTATION-TRACKER.md` 跟踪表：2026-10-22 停用并归档至 `docs/archive/`，此后不再维护。
