# 工具栏过滤输入框星号收藏 + 收藏选择下拉（设计定稿）

> 日期：2026-10-05
> 关联需求：把 Tag / Message 工具栏输入框升级为「输入框 + 收藏入口 + 收藏下拉」组合
> 说明：本稿是实现依据，只记录最终结论；过程讨论与方案演变见 git 历史。

---

## 1. 方案定稿

采用**方案 C：复用 `ContextMenuStrip`**——在 `_tbTag` / `_tbMsg` 后追加 ▾ / ★ 两个 `ToolStripButton`，下拉复用面板侧 `ShowFilterFavMenu` 的数据源与交互。零新控件、零手写键盘导航、与面板侧视觉一致，改动约 60~90 行。

已确定的决策：

| 项                              | 结论                                                     |
| ------------------------------- | -------------------------------------------------------- |
| 交付形态                        | 按需唤出的收藏选择器，**不做实时联想**（方案 B 存档 §8.2） |
| 工具栏 Enter 键                 | 保持「应用过滤」既有行为；唤出菜单**只靠点 ▾**；不做焦点唤出 |
| 收藏满 60 条（每列表）          | **LRU 静默淘汰**，零改动，文档与文案写明即可              |
| IME 组合态（拼音未上屏）点 ★    | 暂不处理：组合串可被收藏，可再点 ★ 移除                   |
| 下拉混入未收藏的最近使用        | 不做（需独立第二份内存数据，属另一个功能）                |
| 工具栏宽度                      | +128px 可接受                                             |
| 工具栏 ▾ / ★ 的 Overflow 策略   | `★ = Never`（保可见）、`▾ = AsNeeded`；实测空间不足被裁切则回退为都 `AsNeeded`（§5.5） |
| 「清空收藏」                    | **加二次确认**（MessageBox YesNo），确认后才清空（§5.6）  |

---

## 2. 现状盘点（行号以 2026-10-05 源码为准）

### 2.1 已有能力

| 能力                                  | 位置                                                                                                                             |
| ------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------- |
| 收藏存储、去重、上限、落盘、`Changed` | `Services/FavoritesStore.cs`：`MaxPerList=60`(:12)、`Changed` 声明(:57)、`Changed?.Invoke` 在 `SaveTo`(:115)、过滤收藏区(:206-231) |
| 过滤收藏增删                          | `AddTagFilter`:214、`RemoveTagFilter`:216、`AddMsgFilter`:218、`RemoveMsgFilter`:220                                             |
| 面板侧 ▾ / ★ 按钮                     | `BuildTermControls`，`frmMain.cs:507-548`（▾:528、★:532，**均为局部变量**）                                                      |
| 从收藏选择的下拉                      | `ShowFilterFavMenu(Button anchor, TextBox ed, bool forTag)`，`frmMain.cs:554`                                                    |
| 收藏切换                              | `ToggleFilterFav(TextBox ed, bool forTag)`，`frmMain.cs:587`（返回 void）                                                        |
| 工具栏托管自定义控件先例              | `ToolStripControlHost(_chkToolbarMarkedOnly/_chkToolbarFollow)`，`frmMain.cs:214-215`                                            |
| 工具栏输入框                          | `_tbTag`(130px) / `_tbMsg`(180px)，`frmMain.cs:197-206`                                                                          |
| 双向同步 + `_syncingFilter`           | `SyncToolbarToPanel`:489 / `SyncPanelToToolbar`:499；字段 `_syncingFilter` 在 `:65`                                              |

### 2.2 已修复的既有 bug（无需再做）

面板/工具栏同步曾把 `_cbTagOp` 静默改写为 `or`：`frmMain.cs:201` / `:205` 已删除该副作用，工具栏输入永不改写面板 op；Message 面板 op 默认值已改为 `or`（`frmMain.cs:358-361`），保证工具栏快速过滤默认语义不变。

### 2.3 数据层大小写不对称（本需求核心动机）

| 操作     | 比较方式                      | 位置                                          |
| -------- | ----------------------------- | --------------------------------------------- |
| 新增去重 | `OrdinalIgnoreCase`           | `AddRecent`，`FavoritesStore.cs:227`          |
| 删除     | `p == text`（**大小写敏感**） | `RemoveTagFilter`:216 / `RemoveMsgFilter`:220 |

两条推论，缺任一都会出现「状态与提示不一致」：

1. **显示侧**：收藏里是 `activitymanager`，输入框里是 `ActivityManager`——用 Ordinal `Contains` 判 ★ 会显示未收藏，点 ★ 后状态栏却说「已收藏」。
2. **切换侧**：即使显示改用 IgnoreCase，点 ★ 取消时 `RemoveTagFilter("ActivityManager")` 大小写敏感 → false → 走新增分支，**取消静默失败**，且原条目被静默替换为用户输入的大小写。

**不可行的捷径**：把 `RemoveTagFilter` 改成 IgnoreCase——会破坏已固化测试 `tag过滤词删除是大小写敏感的`（`FavoritesStoreTests.cs:220`），排除。

---

## 3. `FavoritesStore` 新增 API（与选型无关，先做）

```csharp
public bool ContainsTagFilter(string text)   // OrdinalIgnoreCase，判定 ★ 显示
public bool ContainsMsgFilter(string text)
public bool ToggleTagFilter(string text)     // IgnoreCase 已存在→按定位到的存储条目删除→false；否则新增→true
public bool ToggleMsgFilter(string text)
```

参考实现（`ToggleMsgFilter` 对称）：

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

`ContainsTagFilter` 用 `FindIndex(OrdinalIgnoreCase) >= 0`。

`ToggleFilterFav` 一律改走 `Toggle*`，返回值直接决定状态栏提示，不再拼 `Remove`/`Add` 的返回值。

单元测试（进 `tests/logcat.Tests`，仅 Services / Models 进覆盖率口径）：

- `ContainsTagFilter` / `ContainsMsgFilter` 大小写不敏感（各 1 条）
- `ToggleTagFilter` / `ToggleMsgFilter`：大小写变体下能移除原存储条目并返回 false，`Count` 不变（1~2 条）
- `AddTagFilter` 连续收藏超过 60 条：最旧被淘汰、`Count == 60`（LRU，复用现有 `过滤词数量不超过上限` 用例语义）

---

## 4. 方案 C 实现规格

### 4.1 形态

- **anchor 拆成面板侧/工具栏侧两条路径**：`Control` 与 `ToolStripItem` 无公共基类，不能泛化为单一参数。面板侧 `Show(Control, Point)` 照旧；工具栏侧传 `_tbTag` / `_tbMsg`（`ToolStripTextBox` 继承自 `ToolStripItem`，走 `Show(ToolStripItem, Point)`）。推荐拆成两个方法。
- `ed` 参数传 `_tbTag.TextBox`——`ToolStripTextBox` 不是 `TextBox`，要用其**内层 TextBox**。
- **唤出时机：仅点 ▾**。Enter 保持「应用过滤」（`_tbTag.KeyDown` / `_tbMsg.KeyDown` 既有行为不变）；不做「获得焦点即唤出」（与 IME / Tab 导航冲突）。**不能挂在 `TextChanged` 上**——`ContextMenuStrip.Show()` 会激活菜单并接管键盘，TextBox 随即失焦。
- 唤出时按当前输入做一次**子串过滤**（`OrdinalIgnoreCase`）；无匹配显示禁用项；项文本前缀 `★ `；末尾保留「清空收藏」。
- 键盘导航（↑↓ / Enter / Esc / 长列表滚动 / 屏幕边界翻转）由 `ToolStripMenuItem` 集合**免费提供**。菜单展开时 Enter=选中当前项；菜单收起时工具栏输入框 Enter=应用过滤。

### 4.2 `ShowFilterFavMenu` 参考实现

```csharp
void ShowFilterFavMenu(Control anchor, TextBox ed, bool forTag)   // 面板侧重载；工具栏侧另拆 ToolStripItem 版本
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
            if (MessageBox.Show(this, $"确定清空{(forTag ? "Tag" : "Message")}收藏？", "清空收藏",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            FilterFavList(forTag).Clear();
            FavoritesStore.Default.Save();
            ShowStatus("已清空收藏");
        });
    }
    menu.Show(anchor, new Point(0, anchor.Height));
}
```

要点：

- 用 `IndexOf` + `OrdinalIgnoreCase` 做**子串**匹配，不是前缀匹配；
- 无匹配时显示禁用项，避免空菜单。

### 4.3 `ApplyFavText` 参考实现

```csharp
void ApplyFavText(TextBox ed, string text, bool forTag)
{
    var tb = forTag ? _tbTag : _tbMsg;

    if (ed == tb.TextBox)
    {
        // 工具栏侧：抑制重复同步，随后手动补一次工具栏→面板同步
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

### 4.4 优点 / 限制

- 优点：零新控件、零手写键盘导航、与面板侧视觉一致、子串匹配、可动态增删项。
- 限制：只能**按需唤出**（非实时联想）；`AutoClose = true`，点输入框会先关闭菜单。

---

## 5. 共用实现要点

### 5.1 `RefreshFavStars()` 与 `FavoritesStore.Changed` 订阅

`RefreshFavStars()` 是新增方法，同时刷新面板侧 + 工具栏侧共 4 个 ★ 按钮（当前 `frmMain.cs` 没有 `_fav.Changed` 订阅者）。

```csharp
_fav.Changed += (_, _) =>
{
    // 必须用 BeginInvoke：Changed 在 Save() 调用栈内触发，而 Save() 可能由 UI 事件调用，
    // 同步调用会形成「UI 事件 → Save → Changed → UI 刷新」的重入链
    if (IsHandleCreated) BeginInvoke(new Action(RefreshFavStars));
};
```

另在 `OnFilterChanged` 末尾补调一次，覆盖「文本变化导致星号状态变化」的场景。

**硬契约**：`RefreshFavStars()` 只改按钮的 `Text` / `Enabled`，**绝不碰任何 `TextBox.Text`**，否则死循环。

### 5.2 面板侧 ★ 按钮提为可访问

`frmMain.cs:532` 的 `btnFavToggle`（以及 `:528` 的 `btnFavPick`）目前是 `BuildTermControls` 局部变量，`RefreshFavStars()` 无法访问。推荐给 `BuildTermControls` 加 `out Button favToggle` 参数（改动最小）；或把两个按钮提为 `frmMain` 字段。工具栏侧新增的 `_btnFavToggleTag` / `_btnFavToggleMsg` 本就是字段。

### 5.3 `_syncingFilter` 是全局单标志

一个 bool 覆盖全部输入框（`frmMain.cs:65`）。若顺手在刷新里写 `ed.Text = ed.Text.Trim()`（收藏时是 Trim 过的，很自然）会立刻死循环。开头 `if (_syncingFilter) return;` 是**可选优化**、不是硬约束，取决于调用点：挂在 `TextChanged` 上时会被同步期跳过，星号滞后到下一次刷新。

### 5.4 IME 组合态：暂不处理

组合过程中（拼音已上屏、汉字未上屏）`Text` 拿到的是**组合串**，点 ★ 会收藏一个拼音。已拍板接受现状：组合串被收藏无大碍（可再点 ★ 移除，或被 LRU 淘汰）。不做 IMM interop（`ImmGetContext` 等，成本不匹配收益），不加非 ASCII 拦截（中文过滤词是合法输入，会误伤）。「文本含 CJK 且长度 ≤2」的启发式不可靠，任何情况下不作为方案。

### 5.5 工具栏按钮的 Overflow 策略

`ToolStrip.WrapContents` 默认 `true`，窄窗口时放不下的项收进 overflow 下拉（不是裁切；MEMORY 里「裁切」是 `FlowLayoutPanel` / `ToolStripPanel` 的问题，套不到 `ToolStrip`）。若要保 ★ 可见：

```csharp
_btnFavTag.Overflow = ToolStripItemOverflow.Never;      // ★ 永不进 overflow
_btnFavMenuTag.Overflow = ToolStripItemOverflow.AsNeeded;
```

`Never` 会在工具栏内换行显示；空间不足时仍可能被裁切。**已定**：默认 `★ = Never、▾ = AsNeeded`；若实测（含 2K 高缩放屏）`Never` 在空间不足时被裁切，回退为两个都 `AsNeeded`。

### 5.6 「清空收藏」路径

`ShowFilterFavMenu` 里的「清空收藏」是直接 `Clear()` + `Save()`（`frmMain.cs:577-582`），不走 `Add`/`Remove`，靠 `Save()` 触发 `Changed` 兜底，要单列用例。**已定：加二次确认**——点击后先弹 `MessageBoxButtons.YesNo` 确认（§4.2 参考实现已含），确认后才清空。

---

## 6. 推进顺序

1. `FavoritesStore` 加 `Contains*` / `Toggle*`（§3）+ 测试。
2. `ShowFilterFavMenu` 拆面板侧/工具栏侧两条路径（§4.1），子串过滤，工具栏 ▾ / ★ 接线，`ToggleFilterFav` 改走 `Toggle*`。
3. 接 `RefreshFavStars()` + `FavoritesStore.Changed`（§5.1 / §5.2 / §5.6）。
4. 面板侧 ▾ / ★ 保留，两侧星号共用 `RefreshFavStars()` 刷新。

每步跑回归（`dotnet test`）。

---

## 7. 验收清单

### 7.1 功能

- [ ] 点工具栏 ▾ → 按当前输入**子串**过滤的收藏菜单；无匹配显示禁用项
- [ ] 菜单项带 ★，↑↓ / Enter / Esc 可用，屏幕边界自动翻转
- [ ] 点 ★：未收藏→收藏（☆→★），已收藏→移除（★→☆），状态栏有对应提示
- [ ] 大小写变体（§2.3）：★ 亮 ⇔ IgnoreCase 已存在；**点亮 ★ 再点击必须移除原条目并提示「已移除」**；不得出现「提示已收藏但 ★ 未亮」或「点 ★ 取消却提示已收藏」
- [ ] 从面板 / 工具栏选收藏后，Tag / Message 的 op 不被改写（§2.2）
- [ ] 面板侧改收藏 → 工具栏 ★ 与菜单内容同步刷新
- [ ] 「清空收藏」先弹 YesNo 二次确认，取消则不清空；确认后两侧同步清空（§5.6）
- [ ] 输入为空时 ★ 不可用（与面板行为一致）
- [ ] 收藏持久化：重启后仍在（`favorites.json`）
- [ ] 下拉展开时 Enter=选中当前项；菜单收起时工具栏输入框 Enter=应用过滤（既有行为不变）；Esc 关闭下拉
- [ ] `FilterDialog` 与主窗体同时打开时，两侧文本与 ★ 双向一致
- [ ] 面板侧 ▾ / ★ 与工具栏侧 ▾ / ★ 同时存在，同一输入框两侧的 ★ 状态完全一致
- [ ] 面板侧和工具栏侧的 ▾ 弹出的菜单内容、顺序、子串过滤行为完全一致（同一份 `FilterFavList` 数据源）

### 7.2 布局与边界

- [ ] 窗口缩窄时新增按钮的行为符合 §5.5 的最终决策（进 overflow / 换行可见），不是被裁掉
- [ ] 2K 高缩放屏下工具栏不溢出、输入框与按钮不被裁切

### 7.3 上限与容错

- [ ] 满 60 条（每列表）后继续收藏：最旧条目被淘汰、`Count` 保持 60（LRU 静默淘汰，**无提示是预期行为**）
- [ ] 磁盘不可写时收藏仅在当前会话生效，不抛异常（`SaveTo` 已有 try/catch，确认 UI 不崩）
- [ ] IME 组合态下点 ★：组合串可被收藏（接受现状），且该条目可再点 ★ 正常移除

> 控件 UI 行为按项目现有约定不进覆盖率口径，用 harness 目视 + 截图验证。基线测试数以 `dotnet test` 实测为准。

---

## 8. 附录：已排除方案与许可（决策存档）

### 8.1 方案 A（AutoComplete）——排除

- 原生 listbox 无 owner-draw 入口，不可能带 ★；
- 仅前缀匹配且按整串文本比对，`ActivityManager foo` 这类多词输入必然匹配不上；
- 下拉展开时 Enter 被原生 listbox 吃掉，与 `_tbTag.KeyDown → ApplyFilter` 冲突。

### 8.2 方案 B（内嵌 ★ + 实时联想）——未采用，技术路径存档

仅当未来「实时联想」升级为刚需时启用。关键技术事实：

- 内嵌按钮**可行**：`Button` 是独立子窗口，原生 EDIT 不会覆盖它；用 `EM_SETMARGINS`（`0x00D3`，`ECM_RIGHTMARGIN=0x0002`，lParam 高字=右边距）给文本预留右侧空间，`SizeChanged` 时重定位，须在句柄创建后调用；
- 实时联想需 `ToolStripDropDown` + `SWP_NOACTIVATE` 非激活显示（`ContextMenuStrip.Show()` 会激活菜单导致 TextBox 失焦），并手写 ↑↓ / Enter / Esc 路由 + `TextChanged` 过滤。

### 8.3 第三方资源许可（本项目 Apache-2.0）

| 资源                         | 许可                                  | 结论                                                                           |
| ---------------------------- | ------------------------------------- | ------------------------------------------------------------------------------ |
| StackOverflow 代码片段       | CC BY-SA                              | 与 Apache-2.0 冲突。通用 Win32 技术须**自行重写**，勿逐行照抄                   |
| CustomCompleteTextBox        | **GPL**（NuGet 声明；仓库无 LICENSE） | 不可用                                                                         |
| WinForms.AutoComplete        | **LGPLv3**                            | 仅可作未修改的独立 DLL 依赖，不可拷贝源码                                      |
| SunnyUI                      | 个人免费 / 商用需授权                 | 不可用于本项目分发                                                             |
| Lucid / AntdUI / ReaLTaiizor | MIT / Apache-2.0 / MIT                | 许可相容，可作参考；引用代码须登记到 `THIRD-PARTY-NOTICES.md`                  |
