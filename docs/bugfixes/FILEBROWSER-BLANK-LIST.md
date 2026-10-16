# Bug 修复报告：文件浏览两栏列表全部空白（列头未挂进 ListView）

> **日期**：2026-10-14
> **状态**：已验收（2026-10-14 手工验收 3 条全部通过；实施时 493 用例全过 = 492 基线 + 1 条新守卫）
> **严重度**：高——功能完全不可用；但纯显示层问题，无数据损坏风险
> **引入提交**：c574592「feat: UI 国际化底座落地，中英文运行时即时切换」（2026-10-07），V0.1.3 发布包已携带
> **关联提交**：36014aa「fix: 文件浏览列头未挂进 ListView，设备/本机两栏列表全部空白」

---

## 1. 现象

打开文件浏览后，左侧（Android 设备）与右侧（本机电脑）两栏的文件列表区一片空白。收藏栏、路径栏、操作栏、状态栏均正常；列目录实际已成功执行——状态栏会显示「N 项」，但列表区看不到任何条目。输入路径回车、点「刷新」、点「上级」都无效。

## 2. 影响范围

**受影响**：

- 文件浏览的设备面板与本机面板——所有条目不显示（文件夹、文件、盘符全部不可见）
- 列宽持久化连带失效：`ApplySavedColumnWidths` 回填与 `OnColumnWidthChanged` 记录都以列存在为前提，列数为 0 时两者静默不工作（无数据损坏，修复后自动恢复）

**不受影响**：

- 主日志列表 LogListView（自行构建并 `Columns.Add`，未走 `Loc.BindCol`）
- APK 卸载窗口、命令窗口、崩溃汇总窗口——同一提交里这三处用的都是 `Columns.Add(Loc.BindCol(...))` 正确形态
- 目录枚举、传输、重命名、删除、收藏、run-as 等全部非显示逻辑（从「状态栏显示 N 项」可知枚举与填充都在正常执行）

## 3. 根因

i18n 改造把 FilePane 三个 `CreateColumns` 里的列头写法从：

```csharp
list.Columns.Add("名称", 250);
```

改成了：

```csharp
Loc.BindCol(new ColumnHeader { Text = Loc.T("名称"), Width = 250 }, "名称");
```

但 `Loc.BindCol`（Services/Loc.cs）的职责只是**登记资源键 + 赋 Text**，让切换语言时列头文本能刷新——它**不负责把列挂进 ListView 的 Columns 集合**。新写法创建的 `ColumnHeader` 对象在赋完 Text 后就被丢弃了。

结果是文件浏览三个面板（FilePane 基类 3 列、DeviceFilePane 5 列、LocalFilePane 4 列，共 12 条列）全部没有列。WinForms 语义：`View = Details` 且 Columns 为空时，条目一律不渲染——于是两栏同时空白，且无任何异常、无任何报错。

**旁证**：同一提交中 ApkUninstallDialog、CommandDialog、CrashSummaryForm 的列头都是 `_lv.Columns.Add(Loc.BindCol(new ColumnHeader { … }, "键"))` 形态——先建列、绑定、再挂进集合。说明 `BindCol` 的正确用法明确，是文件浏览改写时的遗漏，而非 API 设计误导。

## 4. 修复方案

三处 `CreateColumns` 的每条列包回 `list.Columns.Add(...)`：

```csharp
list.Columns.Add(Loc.BindCol(new ColumnHeader { Text = Loc.T("名称"), Width = 220 }, "名称"));
```

共 12 行机械改动，不触碰任何其他逻辑。基类 `CreateColumns` 虽然当前被两个子类完全覆盖（实际不执行），同样修正，保持写法一致。

## 5. 修复后的行为变化

|              | 修复前                                 | 修复后                                                 |
| ------------ | -------------------------------------- | ------------------------------------------------------ |
| 文件列表     | 两栏空白（条目实际已填充，只是不可见） | 正常显示                                               |
| 列头中英切换 | 无列头可切                             | 列头文本跟随语言刷新（`BindCol` 登记的资源键一直有效） |
| 列宽持久化   | 静默失效                               | 恢复记录与回填                                         |

## 6. 测试计划

**新增守卫用例**（源码扫描，沿用 LocTableTests 既有的 `FindRepoRoot` / `EnumerateSourceFiles` / `BlankOutComments` 扫描机制）：

1. 所有 `BindCol(new ColumnHeader …)` 调用必须位于 `Columns.Add(` 调用之内；出现裸 `BindCol(new ColumnHeader` 即失败
2. 先断言扫描器至少扫到 1 个 `BindCol(new ColumnHeader` 调用点，防扫描器自身失效导致假绿

**回归**：既有全部用例执行，无新增失败（该改动在 UI 层，覆盖率口径不含；守卫用例的意义正是补上 UI 层无法用单测直接验证的空白）。

**手工验收**：

1. 文件浏览两栏条目正常显示，双击/拖拽/传输不受影响
2. 中英切换后列头文本跟随刷新
3. 调整列宽 → 关闭重开 → 列宽回填

## 7. 验收判据

1. 守卫用例通过；既有全部用例无新增失败。
2. 手工验收 3 条全部通过。
