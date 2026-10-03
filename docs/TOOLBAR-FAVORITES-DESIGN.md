# 评审：工具栏过滤输入框内嵌星号收藏 + 输入联想

> 状态：评审稿（未动代码）
> 日期：2026-10-03
> 关联需求：把 Tag / Message 工具栏输入框升级为「输入框 + 内嵌 ★ 按钮 + 输入联想下拉」组合控件

---

## 1. 结论（先说答案）

**可以简单实现，且比提案估的还简单** —— 因为提案假设"从零做"，而实际上数据层和大部分交互逻辑已存在：

| 提案中的部分 | 实际状态 |
|---|---|
| 收藏数据存储 | **已完成**。`FavoritesStore` 已有 `TagFilters` / `MsgFilters` 集合、去重、60 条上限、落盘 `favorites.json`、`Changed` 事件（`Services/FavoritesStore.cs:206-231`） |
| 点击星号增删收藏 | **逻辑已完成**。`ToggleFilterFav()`（`frmMain.cs:487`）可直接复用 |
| 从收藏选择 | **UI 已完成**。过滤面板 Tag/Message 行已有 ▾/★ 按钮，`ShowFilterFavMenu()`（`frmMain.cs:454`）就是现成的下拉菜单实现 |
| 工具栏托管自定义控件 | **有先例**。`_toolStrip2` 已用 `ToolStripControlHost` 承载 CheckBox（`frmMain.cs:213-214`） |

真正的增量只有两块：**工具栏侧的收藏入口** + **输入时联想下拉**。

---

## 2. 现状盘点（提案未提及的关键事实）

- 过滤面板（FilterDialog 非模态窗口）中 Tag/Message 行**已经有** ▾（选收藏）和 ★（收藏/移除）按钮，见 `BuildTermControls`（`frmMain.cs:423-432`）。
- 工具栏 `_tbTag`(130px) / `_tbMsg`(180px) 通过 `SyncToolbarToPanel` / `SyncPanelToToolbar` 与面板双向同步，`_syncingFilter` 防回环（`frmMain.cs:391-407`）。
- 收藏变更流程：`AddTagFilter()` 本身不触发事件，需随后调用 `Save()` 才触发 `Changed` —— 新代码必须遵循此约定。
- 分钟框 `_tbMin` 没有收藏数据，**不纳入本次改造**。

---

## 3. 两个实现方案

### 方案 A：轻量版（推荐先做）

不动 `ToolStripTextBox`，在 `_tbTag` / `_tbMsg` 后面各追加两个 `ToolStripButton`（▾ / ★），复刻面板按钮逻辑；联想用 TextBox 内置 AutoComplete。

```csharp
// 联想下拉只需 ~10 行：OS 原生建议列表
box.AutoCompleteMode = AutoCompleteMode.Suggest;
box.AutoCompleteSource = AutoCompleteSource.CustomSource;
box.AutoCompleteCustomSource.AddRange(favList.ToArray());
```

- 改动点：`frmMain.cs` 增加 ~80 行；`ShowFilterFavMenu` / `ToggleFilterFav` 参数从 `Button` 泛化为 `Control` 以便复用。
- 优点：零新控件、零 Popup 代码、风险极低。
- 限制：星号在框外（视觉与示意不完全一致）；下拉行无 ★ 图标；仅前缀匹配；**AutoCompleteCustomSource 在 ToolStrip 宿主的 TextBox 内的行为需实测验证**（已知存在个别环境 quirk）。

### 方案 B：提案完整版（自定义组合控件）

新建 `Controls/FilterBox.cs`，经 `ToolStripControlHost` 放入工具栏：

- 容器（Panel/UserControl）内含 TextBox（Dock=Fill，右侧 `Padding` 预留 20px）+ 内嵌 ★ 按钮（Anchor Right）。
- 下拉用 `ToolStripDropDown` 宿主无边框 ListBox：★/☆ 前缀区分已收藏项、键盘导航（↑↓/Enter/Esc）、`Show(control, offset)` 自带屏幕边界翻转。

**必须注意的坑**（提案"100~150 行"漏掉的部分）：

1. **内嵌按钮不能直接挂到 TextBox 里** —— 原生 EDIT 控件会覆盖子控件绘制，必须用容器布局（这也是组合控件存在的原因）。
2. **Enter 键语义冲突**：现有 KeyDown Enter=ApplyFilter，下拉展开时 Enter 应为选中项，事件需按下拉状态分发。
3. **星号状态一致性问题**（提案未提）：面板侧 ▾/★ 改动收藏后，工具栏星号要同步刷新 —— 需挂 `FavoritesStore.Changed` 或统一刷新钩子；且两侧输入框同步（`SyncPanelToToolbar`）时 TextChanged 会触发，星号刷新天然覆盖，但要防回环。
4. **收藏流程约定**：改完收藏必须调 `Save()` 才触发 `Changed`。
5. **DPI**：按钮尺寸沿用 `MinimumSize(28,25)` 模式，避免裁字。

- 改动量实际约 **200~300 行**（控件 ~120 + 下拉/键盘导航 ~80 + frmMain 接线 ~40 + 状态刷新 ~20）。

### 对比

| | 方案 A | 方案 B |
|---|---|---|
| 行数 | ~80 | ~200-300 |
| 新文件 | 无 | Controls/FilterBox.cs |
| 视觉还原度 | 部分（星号在框外） | 完全一致 |
| 下拉星标/键盘导航 | 无/无 | 有/有 |
| 风险 | 极低（一个待验证点） | 中（Popup 时序、事件分发） |

---

## 4. 建议

1. **先做方案 A**：不引入新控件，星号收藏 + 基础联想一次到位；顺带把 `ShowFilterFavMenu`/`ToggleFilterFav` 参数化，为 B 铺路。
2. 体验不满意（想要框内星号、下拉星标）再升级 B，且 A 的接线代码全部可回收。
3. 产品层面需确认两点：
   - 过滤面板现有的 ▾/★ 按钮**是否保留**（建议保留：面板是完整编辑区，但两处星号状态需统一刷新）；
   - 方案 A 下工具栏每个输入框新增两个按钮（约 56px），窄窗口下的空间压力是否可接受（可考虑只加 ★，联想靠 AutoComplete）。

---

## 5. 验收要点（实现后）

- [ ] 输入已有收藏的前缀 → 出现联想下拉，已收藏项带 ★
- [ ] 点星号：未收藏→收藏（☆→★），已收藏→移除（★→☆），状态栏有提示
- [ ] 面板侧改收藏 → 工具栏星号与下拉内容同步刷新
- [ ] 下拉展开时 Enter=选中；收起时 Enter=应用过滤；Esc 关闭下拉
- [ ] 输入为空时星号不可用（与面板行为一致）
- [ ] 收藏持久化：重启后仍在（favorites.json）
