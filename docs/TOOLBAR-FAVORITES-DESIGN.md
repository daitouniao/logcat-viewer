# 工具栏过滤输入框星号收藏 + 收藏选择下拉

> 状态：重构稿。**仅保留已验证或理论可行的方案**
> 日期：2026-10-04
> 关联需求：把 Tag / Message 工具栏输入框升级为「输入框 + 收藏入口 + 收藏下拉」组合
> 来源：合并 `TOOLBAR-FAVORITES-REVIEW.md`、`ds_review.md` 的结论，剔除原「方案 A（AutoComplete）」与许可不相容的第三方资源。
> 保留方案：**C（推荐，已验证）**、**B（条件适用，理论可行）**。排除记录见 §7。

---

## 1. 结论

- **推荐方案 C**：在 `_tbTag` / `_tbMsg` 后追加 ▾ / ★ 两个 `ToolStripButton`，下拉复用现成的 `ContextMenuStrip`（`ShowFilterFavMenu`）。零新控件、零手写键盘导航、与面板侧视觉一致，改动约 60~90 行。
- **方案 B 仅在两种前提下启用**：(1)「实时联想（边打字边出候选）」是刚性需求；(2) 工具栏宽度是硬约束（C 额外占约 128px）。B 的内嵌 ★ 用成熟技术即可实现（§5.1），**不需要**从零写容器控件。

### 方案对比（仅剩两个可行方案）

|                     | C：复用菜单（推荐） | B：内嵌 ★                           |
| ------------------- | ------------------- | ----------------------------------- |
| 状态                | **已验证**          | 理论可行（需实测）                  |
| 改动量              | 60~90 行            | 内嵌 ★ ~50 行 +（可选）联想 ~100 行 |
| 新文件              | 无                  | 无（不再需要容器控件）              |
| 新增工具栏宽度      | +128px              | **0**（内嵌）                       |
| 与面板侧视觉一致    | ✔                   | ✔                                   |
| 下拉带 ★ / 子串匹配 | ✔                   | ✔                                   |
| 键盘导航            | **免**（菜单自带）  | 实时联想需手写                      |
| 实时联想（边打字）  | ✘（只能按需唤出）   | ✔（§5.2）                           |

### 真正的增量（三块）

| 增量                           | 说明                                        |
| ------------------------------ | ------------------------------------------- |
| 工具栏侧收藏入口               | ▾（从收藏选择）+ ★（收藏/移除）             |
| 收藏选择/联想下拉              | C=按需唤出选择器；B=可选实时联想            |
| `FavoritesStore` 查询/切换 API | **原稿漏算**，约 20 行 + 3~4 条测试（§2.3） |

### 与选型无关、必须先做的前置（§6）

1. `FavoritesStore` 新增 `Contains*` / `Toggle*`（IgnoreCase）——修掉 ★ 状态与提示不一致；
2. 修 `_cbTagOp` 被静默改写为 `or` 的既有 bug（本需求会放大它）；
3. `RefreshFavStars()` + `FavoritesStore.Changed` 统一刷新。

---

## 2. 现状盘点（已逐条核对源码）

### 2.1 已有能力

| 能力                                  | 位置                                                                                                                               |
| ------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------- |
| 收藏存储、去重、上限、落盘、`Changed` | `Services/FavoritesStore.cs`：`MaxPerList=60`(:12)、`Changed` 声明(:57)、`Changed?.Invoke` 在 `SaveTo`(:115)、过滤收藏区(:206-231) |
| 过滤收藏增删                          | `AddTagFilter`:214、`RemoveTagFilter`:216、`AddMsgFilter`:218、`RemoveMsgFilter`:220                                               |
| 面板侧 ▾ / ★ 按钮                     | `frmMain.cs:423-432`（`BuildTermControls`）                                                                                        |
| 从收藏选择的下拉                      | `ShowFilterFavMenu(Button anchor, TextBox ed, bool forTag)`，`frmMain.cs:454`                                                      |
| 收藏切换                              | `ToggleFilterFav(TextBox ed, bool forTag)`，`frmMain.cs:487`（**返回 void**）                                                      |
| 工具栏托管自定义控件先例              | `ToolStripControlHost(_chkToolbarMarkedOnly/_chkToolbarFollow)`，`frmMain.cs:212-213`                                              |
| 工具栏输入框                          | `_tbTag`(130px) / `_tbMsg`(180px)，`frmMain.cs:198-204`                                                                            |
| 双向同步 + `_syncingFilter`           | `frmMain.cs:391-407`；字段在 `:65`                                                                                                 |

### 2.2 收藏变更必须 `Save()` 才触发 `Changed`

`AddTagFilter` 等本身不触发事件；`Changed?.Invoke` 只在 `SaveTo` 末尾（`FavoritesStore.cs:115`）。
`SaveTo` 在磁盘不可写时 catch 后**仍会触发** `Changed`，因此 **`Changed` 处理器内不得抛异常**。

### 2.3 ★ 状态判定与切换：必须新增 IgnoreCase API

数据层的大小写比较**不对称**，且已被测试固化：

| 操作     | 比较方式                      | 位置                                          |
| -------- | ----------------------------- | --------------------------------------------- |
| 新增去重 | `OrdinalIgnoreCase`           | `AddRecent`，`FavoritesStore.cs:227`          |
| 删除     | `p == text`（**大小写敏感**） | `RemoveTagFilter`:216 / `RemoveMsgFilter`:220 |

两条推论，缺任一都会出现「状态与提示不一致」：

1. **显示侧**：收藏里是 `activitymanager`，输入框里是 `ActivityManager`——用 Ordinal `Contains` 判 ★ 会显示未收藏，点 ★ 后状态栏却说「已收藏」。
2. **切换侧**：即使显示改用 IgnoreCase，点 ★ 取消时 `RemoveTagFilter("ActivityManager")` 大小写敏感 → false → 走新增分支 → 状态栏「已收藏」、★ 仍亮，**取消静默失败**，且原条目被静默替换大小写。

**结论**：给 `FavoritesStore` 新增（逻辑进 Services，可进覆盖率口径）：

```csharp
public bool ContainsTagFilter(string text)   // OrdinalIgnoreCase，判定 ★ 显示
public bool ContainsMsgFilter(string text)
public bool ToggleTagFilter(string text)     // IgnoreCase 已存在→按定位到的存储条目删除→false；否则新增→true
public bool ToggleMsgFilter(string text)
```

`ToggleFilterFav` 一律改走 `Toggle*`，返回值直接决定状态栏提示，不再拼 `Remove`/`Add` 的返回值。

**不可行的捷径**：把 `RemoveTagFilter` 改成 IgnoreCase——会破坏已固化测试 `tag过滤词删除是大小写敏感的`（`FavoritesStoreTests.cs:219`），排除。

### 2.4 连带 bug：从面板选收藏会把 op 重置为 `or`

`frmMain.cs:199` / `:203`：

```csharp
_tbTag.TextChanged += (_, _) => { SyncToolbarToPanel(_tbTag, _edTag); _cbTagOp.SelectedItem = "or"; };
```

从面板选收藏：`ed.Text = text` → `SyncPanelToToolbar` → `dst.Text = src.Text` → **触发本 handler** → op 被改成 `or`。
该赋值与 `SyncToolbarToPanel` 是**并列语句**，`_syncingFilter` 拦不住它。

修复（一行，**删除副作用**）：

```csharp
_tbTag.TextChanged += (_, _) => SyncToolbarToPanel(_tbTag, _edTag);
```

（`_tbMsg` 同理。）

**与 `FILTER-REFACTOR-PLAN.md` 统一（v2 修订）**：改为**直接删除副作用**，不采用 `if (!_syncingFilter)` 守卫——工具栏输入**永不改写**面板 op，op 以面板控件为唯一来源，工具栏框仅用**背景色**提示（FILTER-REFACTOR-PLAN.md §3.3）。为保持工具栏快速过滤的默认语义仍为 `or`，Message 面板 op 默认值由 `and` 改为 `or`。此改动一并消除本节所述「选收藏重置 op」路径。

---

## 3. 待决策项（实现前需要拍板）

### 3.1 满 60 条时的行为

`AddRecent` 超限是**静默淘汰最旧**并返回 true（`FavoritesStore.cs:229`），故「已达上限」这个状态当前**不可能发生**。二选一：

- **(a) 保持 LRU 静默淘汰**（零改动），文档写明「超过 60 条时最旧的会被淘汰」；
- **(b) 满额拒绝并提示**：只能改 `AddTagFilter` / `AddMsgFilter`（先判 `Count >= MaxPerList`），**不能一刀切改 `AddRecent`**——它同时服务 `ApkPaths` / `Packages` 的「最近使用」语义。

现有测试 `过滤词数量不超过上限`（`FavoritesStoreTests.cs:243`）只断言 `Count == 60`，两种改法都能过；(b) 需补一条测试。

### 3.2 上限口径：每个列表各 60

`MaxPerList = 60` 对 `TagFilters` / `MsgFilters` 两条独立列表分别生效，**合计上限 120**，不是总共 60。文案要写准。

### 3.3 交付形态：实时联想 vs 按需选择器（决定选 C 还是 B）

- C 交付的是「**按需唤出的收藏选择器**」（点 ▾ / Enter 唤出），**不是边打字边联想**——`ContextMenuStrip.Show()` 会激活菜单并接管键盘，TextBox 随即失焦，无法挂 `TextChanged`。
- 若「实时联想」是刚性需求，只有方案 B（§5.2）能做到，成本显著上升。
- 若接受选择器形态，C 最优。

### 3.4 下拉里是否混入「未收藏的最近使用」

下拉的数据源就是收藏列表本身，每项必为 ★。要混入未收藏项需独立的第二份内存数据，属**另一个功能**。本设计默认**不做**。

---

## 4. 方案 C：复用 `ContextMenuStrip`（推荐，已验证）

### 4.1 形态

- 锚点参数 `Button` → 泛化为 `Control`（面板两个按钮照旧传，工具栏传 `_tbTag` / `_tbMsg`）。
- `ed` 参数传 `_tbTag.TextBox`——`ToolStripTextBox` 不是 `TextBox`，要用其**内层 TextBox**。
- **唤出时机**：点 ▾ / Enter / 获得焦点，**不能挂在 `TextChanged` 上**（见 §3.3）。
- 唤出时按当前输入做一次**子串过滤**（`OrdinalIgnoreCase`）；无匹配显示禁用项；项文本前缀 `★ `；末尾保留「清空收藏」。
- 键盘导航（↑↓ / Enter / Esc / 长列表滚动 / 屏幕边界翻转）由 `ToolStripMenuItem` 集合**免费提供**。

### 4.2 子串过滤实现

```csharp
void ShowFilterFavMenu(Control anchor, TextBox ed, bool forTag)
{
    var menu = new ContextMenuStrip();
    var list = FilterFavList(forTag);
    var key = ed.Text.Trim();

    if (list.Count == 0)
    {
        menu.Items.Add(new ToolStripMenuItem("（暂无收藏，点 ★ 收藏当前内容）") { Enabled = false });
    }
    else
    {
        var filtered = string.IsNullOrEmpty(key)
            ? list
            : list.Where(x => x.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

        foreach (var item in filtered)
        {
            var text = item;
            var mi = new ToolStripMenuItem($"★ {text}");
            mi.Click += (_, _) => ApplyFavText(ed, text, forTag);
            menu.Items.Add(mi);
        }
        if (menu.Items.Count == 0)
            menu.Items.Add(new ToolStripMenuItem("（无匹配收藏）") { Enabled = false });

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("清空收藏", null, (_, _) =>
        {
            FilterFavList(forTag).Clear();
            FavoritesStore.Default.Save();
            ShowStatus("已清空收藏");
        });
    }
    menu.Show(anchor, new Point(0, anchor.Height));
}
```

**要点**：

- 用 `IndexOf` + `OrdinalIgnoreCase` 做**子串**匹配，不是前缀匹配；
- 无匹配时显示禁用项，避免空菜单；
- 程序化写入文本要走 `ApplyFavText`；**工具栏侧**需用 `_syncingFilter` 包住赋值以避免 op 被重置（§2.4），并在解锁后手动补一次同步：

```csharp
void ApplyFavText(TextBox ed, string text, bool forTag)
{
    var tb = forTag ? _tbTag : _tbMsg;

    if (ed == tb.TextBox)
    {
        // 工具栏侧：抑制重复同步（op 已不再被工具栏改写，见 §2.4），随后手动补一次工具栏→面板同步
        _syncingFilter = true;
        try { ed.Text = text; } finally { _syncingFilter = false; }
        ed.SelectionStart = ed.TextLength;
        SyncToolbarToPanel(tb, forTag ? _edTag : _edMsg);
    }
    else
    {
        // 面板侧：走既有「面板 TextChanged → SyncPanelToToolbar」链路，勿加 _syncingFilter（会阻断同步）
        ed.Text = text;
        ed.SelectionStart = ed.TextLength;
        OnFilterChanged();
    }
    RefreshFavStars();
}
```

- 工具栏侧接线时，锚点传 `_tbTag` / `_tbMsg`，`ed` 传 `_tbTag.TextBox`。

### 4.3 优点 / 限制

- 优点：零新控件、零手写键盘导航、与面板侧视觉一致、子串匹配、可动态增删项。
- 限制：只能**按需唤出**（非实时联想）；`AutoClose = true`，点输入框会先关闭菜单。

---

## 5. 方案 B：内嵌 ★（条件适用，理论可行）

### 5.1 内嵌 ★ 的正确做法（纠正旧稿错误结论）

旧稿「内嵌按钮不能直接挂到 TextBox 里——原生 EDIT 会覆盖子控件绘制」**不成立**：`Button` 是独立子窗口，原生 EDIT 不会覆盖它。真正要做的是用 `EM_SETMARGINS` 给文本预留右侧空间，避免文本被按钮压住：

```csharp
[DllImport("user32.dll")]
static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

const int EM_SETMARGINS = 0x00D3;
const int ECM_RIGHTMARGIN = 0x0002;

void AttachStar(TextBox box, Button star)
{
    star.Size = new Size(box.ClientSize.Height, box.ClientSize.Height);
    star.Location = new Point(box.ClientSize.Width - star.Width, 0);
    box.Controls.Add(star);
    // lParam 低字=左边距，高字=右边距：给右侧留出按钮宽度
    SendMessage(box.Handle, EM_SETMARGINS, (IntPtr)ECM_RIGHTMARGIN, (IntPtr)(star.Width << 16));
    box.SizeChanged += (_, _) => star.Location = new Point(box.ClientSize.Width - star.Width, 0);
}
```

接入工具栏时用内层 `TextBox`（`_tbTag.TextBox`）。要点：

- 必须在句柄创建后调用（`HandleCreated` / `OnLoad`）；
- 按钮与文本之间留 1~2px 可点击缝隙，否则按钮会吞掉右端的光标定位点击；
- 尺寸 / DPI 变化时重定位（`SizeChanged`，可复用 `DpiFix` 的思路）。

采用本做法后，旧稿「容器 `AutoSize` 撑爆工具栏」的坑**不再存在**（不再需要容器控件）。

### 5.2 实时联想下拉（仅当 §3.3 判定为刚性需求）

`ContextMenuStrip.Show()` 会激活菜单导致 TextBox 失焦，无法边打字边弹。实时联想需要：

- `ToolStripDropDown` + `SWP_NOACTIVATE`（`ShowWithoutActivation` / `SetWindowPos`）**非激活显示**；
- 手动路由 ↑↓ / Enter / Esc；
- `TextChanged` 上做子串过滤并刷新下拉项。

这是方案 B 唯一需要手写键盘导航的部分，也是其成本所在。

### 5.3 适用条件

仅当「实时联想刚性」（§5.2）或「工具栏宽度是硬约束」（C 额外约 +128px）时启用；否则用方案 C。

---

## 6. 共用实现清单（与选型无关）

### 6.1 `FavoritesStore` 新增 API

见 §2.3，配 3~4 条单元测试（§9）。

### 6.2 `RefreshFavStars()` 的调用时机

```csharp
_fav.Changed += (_, _) =>
{
    // 必须用 BeginInvoke：Changed 在 Save() 调用栈内触发，而 Save() 可能由 UI 事件调用，
    // 同步调用会形成「UI 事件 → Save → Changed → UI 刷新」的重入链
    if (IsHandleCreated) BeginInvoke(new Action(RefreshFavStars));
};
```

另在 `OnFilterChanged` 末尾补调一次，覆盖「文本变化导致星号状态变化」的场景。

**硬契约**：`RefreshFavStars()` 只改按钮显示状态，**绝不碰任何 `TextBox.Text`**。

### 6.3 `_syncingFilter` 是全局单标志

一个 bool 覆盖全部输入框（`frmMain.cs:65`）。若顺手在刷新里写 `ed.Text = ed.Text.Trim()`（收藏时是 Trim 过的，很自然）会立刻死循环。
开头 `if (_syncingFilter) return;` 是**可选优化**、不是硬约束，取决于调用点：挂在 `TextChanged` 上时会被同步期跳过，星号滞后到下一次刷新。

### 6.4 IME 组合态

组合过程中（拼音已上屏、汉字未上屏）`Text` 拿到的是**组合串**，点 ★ 会收藏一个拼音。二选一：

- 组合态下 ★ `Enabled = false`（保守）；
- 或接受现状，但在 `ToggleFilterFav` 里拦掉含非 ASCII 的组合串并提示「请先上屏」。

**严格方案需 IMM interop**（`ImmGetContext` / `ImmGetOpenStatus` / `ImmGetCompositionString`，或子类化 WndProc 收 `WM_IME_*`）。
「文本含 CJK 且长度 ≤2」的启发式**不可靠**，只能作降级，不能作为正式方案。

### 6.5 `ToolStrip` 窄宽时进 overflow，不是裁切

`ToolStrip.WrapContents` 默认 `true`，放不下的项收进 overflow 下拉（MEMORY 里「裁切」那条是 `FlowLayoutPanel` / `ToolStripPanel` 的，**套不到 `ToolStrip`**）。
若要保 ★ 可见：

```csharp
_btnFavTag.Overflow = ToolStripItemOverflow.Never;      // ★ 永不进 overflow
_btnFavMenuTag.Overflow = ToolStripItemOverflow.AsNeeded;
```

`Never` 会在工具栏内换行显示；空间不足时仍可能被裁切，需实测。

### 6.6 「清空收藏」路径最易被漏掉

`ShowFilterFavMenu` 里的「清空收藏」是直接 `Clear()` + `Save()`（`frmMain.cs:477-482`），**不走 `Add`/`Remove`**，靠 `Save()` 触发 `Changed` 兜底，要单列用例。

---

## 7. 已排除方案与资源（决策记录）

### 7.1 方案 A（AutoComplete）——已排除

- 未经实测；且**理论上不满足需求**：原生 listbox 无 owner-draw 入口，**不可能带 ★**（验收项直接不成立）；
- 仅前缀匹配且按**整串文本**比对，过滤框允许多词（空格分隔短语），`ActivityManager foo` 基本必然匹配不上；
- 下拉展开时 Enter 被原生 listbox 吃掉去接受建议，与 `_tbTag.KeyDown → ApplyFilter`（`frmMain.cs:200/204`）冲突。

### 7.2 第三方资源——许可已核对，以下**不得拷入本仓库**（本项目 Apache-2.0）

| 资源                         | 许可                                  | 结论                                                                           |
| ---------------------------- | ------------------------------------- | ------------------------------------------------------------------------------ |
| StackOverflow 代码片段       | CC BY-SA                              | 与 Apache-2.0 冲突。§5.1 的做法属通用 Win32 技术，**自行重写**即可，勿逐行照抄 |
| CustomCompleteTextBox        | **GPL**（NuGet 声明；仓库无 LICENSE） | 不可用                                                                         |
| WinForms.AutoComplete        | **LGPLv3**                            | 仅可作未修改的独立 DLL 依赖，不可拷贝源码                                      |
| SunnyUI                      | 个人免费 / 商用需授权                 | 不可用于本项目分发                                                             |
| Lucid / AntdUI / ReaLTaiizor | MIT / Apache-2.0 / MIT                | 许可相容，可作参考；引用代码须登记到 `THIRD-PARTY-NOTICES.md`                  |

---

## 8. 推进顺序

1. `FavoritesStore` 加 `Contains*` / `Toggle*`（§2.3）+ 测试——与选型无关，先做。
2. 修 §2.4 的 op 重置 bug（一行）。
3. 落地方案 C（§4）：锚点 `Button → Control` 泛化、`ed` 传 `_tbTag.TextBox`、子串过滤、工具栏 ▾ / ★ 接线。
4. 接 `RefreshFavStars()` + `FavoritesStore.Changed`（注意 §6.2 / §6.6）。
5. 面板侧 ▾ / ★ 保留，两处星号共用 `RefreshFavStars()` 刷新。
6. 若 §3.3 判定需要实时联想，再按 §5.2 增补。

---

## 9. 验收要点

功能：

- [ ] 点工具栏 ▾ → 按当前输入**子串**过滤的收藏菜单；无匹配显示禁用项
- [ ] 菜单项带 ★，↑↓ / Enter / Esc 可用，屏幕边界自动翻转
- [ ] 点 ★：未收藏→收藏（☆→★），已收藏→移除（★→☆），状态栏有对应提示
- [ ] 大小写变体（§2.3）：★ 亮 ⇔ IgnoreCase 已存在；**点亮 ★ 再点击必须移除原条目并提示「已移除」**；不得出现「提示已收藏但 ★ 未亮」或「点 ★ 取消却提示已收藏」
- [ ] 从面板 / 工具栏选收藏后，Tag / Message 的 op 不被静默改写（§2.4）
- [ ] 面板侧改收藏 → 工具栏 ★ 与菜单内容同步刷新
- [ ] 「清空收藏」后两侧同步清空（§6.6）
- [ ] 输入为空时 ★ 不可用（与面板行为一致）
- [ ] 收藏持久化：重启后仍在（`favorites.json`）
- [ ] 下拉展开时 Enter=选中；收起时 Enter=应用过滤；Esc 关闭下拉
- [ ] `FilterDialog` 与主窗体同时打开时，两侧文本与 ★ 双向一致

布局与边界：

- [ ] 窗口缩窄时新增按钮的行为符合 §6.5 的决策（进 overflow / 换行可见），不是被裁掉
- [ ] 2K 高缩放屏下工具栏不溢出、输入框与按钮不被裁切
- [ ] 若采用方案 B：内嵌 ★ 随尺寸 / DPI 正确重定位，文本不被遮挡（§5.1）

上限与容错：

- [ ] 满 60 条（每列表）时的行为符合 §3.1 的决策；不能无任何反馈
- [ ] 磁盘不可写时收藏仅在当前会话生效，不抛异常（`SaveTo` 已有 try/catch，确认 UI 不崩）
- [ ] IME 组合态下点 ★ 的行为符合 §6.4 的决策

单元测试（进 `tests/logcat.Tests`，仅 Services / Models 进覆盖率口径）：

- [ ] `ContainsTagFilter` / `ContainsMsgFilter` 大小写不敏感（各 1 条）
- [ ] `ToggleTagFilter` / `ToggleMsgFilter`：大小写变体下能移除原存储条目并返回 false，`Count` 不变（1~2 条）
- [ ] 若 §3.1 选 (b)：`AddTagFilter` 满 60 条后返回 false 且 `Count` 仍为 60（1 条）

> 控件 UI 行为按项目现有约定不进覆盖率口径，用 harness 目视 + 截图验证。

---

## 10. 审核补充（2026-10-04）

> 对照源码逐条核对原文所有行号、方法签名、测试名，**零偏差**；以下为原文未提及、但实现前需要处理的问题。

### 10.1 `ShowFilterFavMenu` 的 anchor 参数不能简单泛化为 `Control`

原文 §4.1 写"锚点参数 `Button` → 泛化为 `Control`"，但工具栏侧要传的 `_tbTag` / `_tbMsg` 是 `ToolStripTextBox`，**它继承自 `ToolStripItem`，不是 `Control`**。`ContextMenuStrip.Show()` 有两个不同重载：

- `Show(Control, Point)`——面板侧 `btnFavPick`（Button）走这个；
- `Show(ToolStripItem, Point)`——工具栏侧 `_tbTag` 走这个。

`Control` 和 `ToolStripItem` 没有公共基类（除 `object`）。二选一：

- (a) anchor 参数改为 `object`，内部 `is Control` / `is ToolStripItem` 分派；
- (b) 拆成两个方法，分别处理面板侧和工具栏侧（更清晰，推荐）。

`ed` 参数传 `_tbTag.TextBox`（`ToolStripTextBox` 的 `.TextBox` 属性返回内层 `TextBox`），这点原文提过，确认可用。

### 10.2 面板侧 `btnFavToggle` 当前是局部变量，`RefreshFavStars()` 找不到它

[frmMain.cs:429](file:///d:/01.0.Code/C%23/logcat/frmMain.cs#L429) 的 `btnFavToggle` 在 `BuildTermControls` 内部构造，未保存到字段。`RefreshFavStars()` 需要同时刷新面板侧和工具栏侧共 4 个 ★ 按钮，因此必须：

- 把 `btnFavToggleTag` / `btnFavToggleMsg` 提为 `frmMain` 字段；或
- 给 `BuildTermControls` 加 `out Button favToggle` 参数。

工具栏侧新增的 `_btnFavToggleTag` / `_btnFavToggleMsg` 本就是字段，天然可访问。

### 10.3 `RefreshFavStars()` 是全新方法，当前源码里不存在

面板侧 ★ 按钮目前**没有任何刷新机制**——点击后靠 `ToggleFilterFav` 内部的 `Save()` 触发 `Changed`，但 `Changed` 当前**没有订阅者**（确认过 `frmMain.cs` 全文没有 `_fav.Changed +=`）。因此 `RefreshFavStars()` 是本需求新增的方法，且需要订阅 `FavoritesStore.Changed`。

**硬契约**（与原文 §6.2 一致）：`RefreshFavStars()` 只改按钮的 `Text` / `Enabled`，绝不碰任何 `TextBox.Text`，否则会死循环。

### 10.4 当前 `ToggleFilterFav` 的"静默替换大小写"路径

原文 §2.3 推论 2 提到"原条目被静默替换大小写"，完整路径如下（当前代码 [frmMain.cs:487-504](file:///d:/01.0.Code/C%23/logcat/frmMain.cs#L487-L504)）：

```
存了 activitymanager → 输入 ActivityManager → ToggleFilterFav:
  RemoveTagFilter("ActivityManager")  // 大小写敏感 → false
  → 走 AddTagFilter("ActivityManager")
    → AddRecent 内 RemoveAll(OrdinalIgnoreCase) 先清掉 activitymanager
    → 重新插入 ActivityManager
  → 状态栏"已收藏"，★ 仍亮，Count 不变，存储值被替换为用户输入的大小写
```

用 `Toggle*` API（§2.3）可以彻底消除这条路径，返回值直接决定状态栏提示，不再拼 `Remove` / `Add` 的返回值。

### 10.5 `Toggle*` 参考实现

```csharp
public bool ToggleTagFilter(string text)
{
    text = (text ?? "").Trim();
    if (text.Length == 0) return false;
    var list = _data.TagFilters;
    int idx = list.FindIndex(p => string.Equals(p, text, StringComparison.OrdinalIgnoreCase));
    if (idx >= 0) { list.RemoveAt(idx); return false; }   // 已存在 → 移除，返回 false
    list.Insert(0, text);
    while (list.Count > MaxPerList) list.RemoveAt(list.Count - 1);
    return true;                                            // 不存在 → 新增，返回 true
}
```

`ContainsTagFilter` 同理用 `FindIndex(OrdinalIgnoreCase) >= 0`。`ToggleMsgFilter` 对称实现。

### 10.6 验收清单补充

原文 §9 的 11 条之外，再补 2 条：

- [ ] 面板侧 ▾ / ★ **与** 工具栏侧 ▾ / ★ **同时存在**，且同一输入框两侧的 ★ 状态完全一致（同一份 `RefreshFavStars()` 刷新）
- [ ] 面板侧和工具栏侧的 ▾ 弹出的菜单内容、顺序、子串过滤行为完全一致（同一份 `FilterFavList` 数据源）

---

### 审核结论

原文可直接按 §8 推进实现。需在落地前修正 §10.1（anchor 类型）、§10.2（面板侧按钮提字段），其余按原文执行。基线测试 354 条全部通过，改造时优先落 §8.1（`FavoritesStore` API + 测试），每步跑回归。
