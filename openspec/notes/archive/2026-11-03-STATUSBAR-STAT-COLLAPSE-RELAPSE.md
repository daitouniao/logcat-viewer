# 状态栏统计塌缩复发（2026-10-28 修复实机验收失败，回炉）

状态：已修复，待实机验收
日期：2026-11-03
关联：`openspec/notes/archive/2026-10-28-STATUSBAR-STAT-COLLAPSE.md`（首修 + 笔记，已冻结）

## 复发证据（实机）

- 复现路径：启动 → 拖入约 100M 日志（走建索引进度条 + `_lblMsg` 高频刷新路径）→ **不滚动列表** → 过滤 → 右下角统计不显示。
- 拖滚动条（触发 `OnScrolled` 改 `_lblPos`）→ 统计立即恢复。符合首修笔记的验收判据：「能恢复说明塌缩入口未被堵住，需回炉」。

## 首修为何无效（时序假设 + 探针佐证）

首修在 `UpdateStat()` 尾部加 `_statusStrip.PerformLayout()`，但 `ApplyFilter` 完成段的实际顺序是：

```
UpdateStat()（写 _lblStat + PerformLayout）   ← 重排跑在这里
finally: ShowStatusRaw("") / _pbar 藏起       ← 尾次状态栏搅动在其后
```

统计重排之后还有清理触发的布局，塌缩仍可在尾次布局里发生。

本地探针（net10.0-windows 运行时实测，已删）证实四个事实：

1. `ToolStripItem.Text` 同值赋值**不触发** TextChanged（短路成立）→ `finally` 的重复清理是真 no-op，时序归位可靠；
2. `AutoSize=false` + 显式 Size 的项，`PerformLayout` 后宽度原样保留；
3. `AutoSize=true` 对照：清空 → 0 宽（塌缩形态确认）；
4. 相邻项文本搅动触发的后续布局**不会**清掉显式尺寸项的宽度。

## 修复（§2.1 短流程，frmMain.cs 四处）

双保险：

1. **时序归位**：`ApplyFilter` 完成段先清本代进度反馈（`ShowStatusRaw("")` / `_pbar.Visible=false`，同值短路不重复布局），再 `UpdateStat()`——保证统计回写是对状态栏的最后一个布局动作。外层 `StartIndex` 的 `finally` 重复清理因短路无需改。
2. **项级确定性尺寸**：`_lblStat` 改 `AutoSize=false`，写文本后 `_lblStat.Size = GetPreferredSize(Size.Empty)` 显式定宽（含 `OpenFile` 清空处收回零宽）。项宽不再走 AutoSize 重测路径，任何后续布局都清不掉它。

## 验证

- dotnet test 全量 791/791 通过、零跳过（WinForms 布局无法单测覆盖，未新增用例；塌缩本身偶发，探针也无法确定性复现竞态）。
- 实机验收（待办）：曾复现机器上拖入 100M 级日志、**不滚动**、连续过滤换条件，观察统计是否再消失；若再现，拖滚动条确认是否仍能恢复（能恢复 = 仍未堵住，需第三次回炉）。
