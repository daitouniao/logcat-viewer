# 最近打开文档（MRU）设计

> 状态：待评审
> 日期：2026-10-11

## 1. 需求

- 记录用户**通过「打开…」对话框或拖拽**打开的日志文档，形成「最近打开」列表。
- 最多保存 **10** 条；超出时淘汰**最久没使用**的一条（LRU）。
- 同一文件重复打开时去重并刷新到最前。

## 2. 现状

- 打开文件只有两个用户入口，均汇聚到 [frmMain.OpenFile](file:///d:/01.0.Code/C%23/logcat/frmMain.cs#L848)：
  - 菜单「打开…」/ Ctrl+O → `OpenFileDialog` → `StartIndex(path)`
  - 主窗体拖拽 → `OnDragDrop` → `OpenFile(files[0])`
- `StartIndex` 成功后 `_doc.Path` 即最终文档路径。
- ADB 实时采集走独立的 `AdbAutoReload` + `_adbTempPath`，**不经过** `StartIndex`，天然不会被误记。

## 3. 方案

### 3.1 存储：FavoritesStore 增加 RecentFiles

复用 [FavoritesStore](file:///d:/01.0.Code/C%23/logcat/Services/FavoritesStore.cs)（favorites.json）而非 AppSettings：

- 该类已承载各类"最近使用"列表（RunAsPackages、ApkPaths、TagFilters…），且有**变更立即落盘**、`Changed` 事件与现成单测设施；AppSettings 仅在关窗时 `SaveSettings()` 落盘，崩溃会丢记录。
- `StoreData` 新增 `List<string> RecentFiles`（**最近在前**），加载时 `??= new()` 兜底。
- 新增专用上限常量 `MaxRecentFiles = 10`（与既有 `MaxPerList = 60` 区分）。
- 新增方法：
  ```csharp
  public bool AddRecentFile(string path)
  ```
  - 空白拒绝；用 `PathEquals(path, …, remote: false)` 做**大小写不敏感**去重（本机路径）；
  - 已存在 → 移除后插到最前（刷新使用时间）；不存在 → 直接插入最前；
  - 超过 10 条时从尾部淘汰；
  - 语义与既有 `AddRecent` 一致，但不复用它（上限不同）。

### 3.2 记录时机：StartIndex 成功路径

在 [frmMain.StartIndex](file:///d:/01.0.Code/C%23/logcat/frmMain.cs#L863) 成功构建文档处（`_doc = doc;` 之后）调用：

```csharp
FavoritesStore.Default.AddRecentFile(doc.Path);
```

- 只覆盖「打开对话框 / 拖拽 / 续行合并切换重开」三类调用，且**索引成功才算使用**（损坏或被锁文件不记录）。
- 续行合并切换（`StartIndex(_doc.Path)`）会让当前文档刷新到最前——同文件已在列表内，仅位置前移，符合"最近使用"语义。
- ADB 采集临时文件不经过此路径，不会被记录。

### 3.3 UI：文件菜单「最近打开」子菜单

在「文件」菜单的「打开…」之后插入 `ToolStripMenuItem`：

- 文本 `最近打开`，`Loc.Bind(mru, "最近打开")`，并在 [LocTable.MainUi.cs](file:///d:/01.0.Code/C%23/logcat/Services/LocTable.MainUi.cs) 增加英文翻译 `["最近打开"] = "Recent Files"`。
- **打开下拉时动态重建**（`DropDownOpening` 事件）：清空后按 `RecentFiles` 顺序逐条生成菜单项。
  - 项文本 = 完整路径，超过 80 字符时中间以「…」省略、保留首尾两端（`EllipsizeMiddle`）；`ToolTipText` = 完整路径（同名文件可区分）。
  - 存在性过滤**仅对本地路径**做：`File.Exists` 为 false 的条目跳过显示但不删除（U 盘未接入时保留记录，重连后仍可见；避免读路径触发写盘）。
  - **网络路径（UNC `\\` 开头，或盘符 `DriveType == Network`）跳过存在性检查，始终显示**。原因：对不可达的网络路径调用 `File.Exists` 会阻塞 UI 线程数秒（SMB 超时），下拉菜单直接卡死；且临时波动会让条目时隐时现。不可达时点击走 `StartIndex` 失败路径，弹「建立索引失败」提示，记录保留。
  - 全部缺失或列表为空 → 子菜单 `Enabled = false`（网络路径条目视为存在）。
- 点击项 → `OpenFile(path)`；`OpenFile` 内部已有非空路径直通逻辑，无需改动。
- 记录在 `StartIndex` 内完成，菜单回调 `OpenFile(path)` 后自动刷新排序（下一次下拉生效）。

## 4. 边界情况

| 场景                         | 行为                                                                                   |
| ---------------------------- | -------------------------------------------------------------------------------------- |
| 同一文件再次打开             | 移到最前，不产生重复项                                                                 |
| 第 11 个文件打开             | 淘汰列表尾部（最久未使用）                                                             |
| 文件被移动/删除              | 菜单中隐藏；记录保留                                                                   |
| 网络邻居文件临时不可达       | 菜单中仍显示（网络路径不探测）；点击弹「建立索引失败」；记录保留，网络恢复后可直接打开 |
| 打开菜单时构建网络路径条目   | 不调用 `File.Exists`，避免 SMB 超时阻塞 UI 数秒                                        |
| settings/favorites.json 损坏 | 沿用 FavoritesStore 既有重建逻辑，RecentFiles 置空                                     |
| 大小写不同的同一路径         | 视为同一条（Windows 路径不区分大小写）                                                 |
| 拖入多个文件                 | 仅取第一个（现状 `files[0]`，不变）                                                    |

## 5. 测试计划

- **单元测试**（tests/logcat.Tests/FavoritesStoreTests.cs，注入临时文件路径）：
  1. `AddRecentFile` 新增 → 列表头部，`Save` 后重新加载仍在（持久化）。
  2. 重复添加（含大小写变体）→ 去重且移到最前，列表长度不变。
  3. 添加 11 条 → 长度 10，最旧一条被淘汰。
  4. 空白路径 → 返回 false，列表不变。
- **手工验证**：
  - 拖拽打开 2 个文件 → 「最近打开」出现且最近打开的在前；
  - 打开第 11 个文件 → 最早的消失；
  - 重命名文件后 → 菜单中该条隐藏、其余正常；
  - 打开 UNC 路径日志（`\\server\share\x.log`）→ 出现在「最近打开」；断开网络后打开「文件」菜单**不卡顿**、该条仍显示；点击 → 「建立索引失败」提示；恢复网络后点击可正常打开；
  - 切换英文界面 → 菜单显示 "Recent Files"。
- 回归：`dotnet test` 全量通过（含既有 FavoritesStoreTests）。

## 6. 改动清单

| 文件                                      | 改动                                                                                                           |
| ----------------------------------------- | -------------------------------------------------------------------------------------------------------------- |
| Services/FavoritesStore.cs                | `StoreData.RecentFiles`、`MaxRecentFiles`、`AddRecentFile`                                                     |
| frmMain.cs                                | 「文件」菜单插入「最近打开」子菜单 + `RebuildRecentMenu()` + `IsNetworkPath()` 判断；`StartIndex` 成功路径记录 |
| Services/LocTable.MainUi.cs               | 增加翻译 `最近打开 → Recent Files`                                                                             |
| tests/logcat.Tests/FavoritesStoreTests.cs | 4 个新增单测                                                                                                   |
