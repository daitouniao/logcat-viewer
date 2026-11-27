# Proposal

## Why

新壳（`logcat-next`）当前的日志视图只能滚动查看，无法选中任意行，也没有复制入口——用户在排查问题时无法把若干条记录原样贴到别处。旧查看器已有「选中行 + Ctrl+C 复制原始文本」的完整习惯，新壳缺这一段，属于日常使用的最低交互闭环。

## What Changes

- 日志视图增加行选择：单击选中一行；`Ctrl+单击` 切换该行选中状态；`Shift+单击` 以最近一次普通/Ctrl 单击的行（或已选范围边界）为锚点，按行区间选择。
- 增加 `Ctrl+C` 复制：把当前选中行按结果集行序、用换行符连接后写入剪贴板；无选中时不动作。
- 无修饰键双击任意一行打开「完整记录」窗口：只读展示该行的原始文本（保留原始换行），便于查看异常堆栈。
- 复制后状态栏给出「已复制 N 行」反馈；文案走既有本地化目录。
- 选择状态只存在于界面层，不进入 Rust core、不改 C ABI、不影响分页取数。
- **BREAKING**：无。

**范围（in scope）**

- 新壳日志画布的行点击命中、选中集合维护（锚点 + 选中集合）、选中行视觉呈现。
- 键盘 `Ctrl+C`；复制内容与旧查看器「复制原始文本」同口径（每行原文、行序、`\n` 连接）。
- 文件切换 / 重新装载时清空选择；主题、语言、窗口尺寸变化不改变选择。

**非目标（out of scope）**

- 框选 / 拖动选择、键盘选择（Shift+方向键等）、右键菜单、`Ctrl+A` 全选、复制为表格行（制表符分隔版）。
- 详情窗口不做结构化字段拆分视图、不做「把字面的 \n 拆成真实换行」开关、不加独立复制按钮（文本框自身支持选择与 Ctrl+C）；不做标记。
- 标记（mark）与标记持久化、导出、过滤/搜索、ADB 采集。
- 大段复制的后台化或进度提示：第一版在 UI 线程按页缓存逐行取原文，选中规模按视口与 Shift 区间自然受限。
- 不改 `crates/`、不改 `Next.Interop` 的导出面、不改旧 `logcat-viewer`。

## Capabilities

### New Capabilities

无。

### Modified Capabilities

- `next/log-view-rendering`: 新增「行选择」与「复制选中行」两条行为契约——选择手势（单击 / Ctrl / Shift）、选择在重载与外观变化下的生命周期、`Ctrl+C` 的复制内容与反馈。

## Impact

- 代码：`logcat-next/Next.App/Controls/LogCanvas.cs`（命中行、绘制选中态）、`logcat-next/Next.App/Views/MainWindow.axaml.cs`（指针与键盘事件、剪贴板、双击打开详情）、新增详情窗口（`logcat-next/Next.App/Views/`）与选择状态类型；`logcat-next/Next.Tests` 增选择与详情用例。
- 依赖：使用 Avalonia 内置剪贴板（`TopLevel.Clipboard`），不新增包。
- 规范：仅修改 `openspec/specs/next/log-view-rendering/spec.md`，分类与目录不变。
- 测试口径：`Next.Tests` 增用例，主仓 923 基线不受影响；`docs/TESTING.md` 的数字在 apply 阶段按联动更新。
- 文档：不需要新增探索笔记；旧查看器的复制口径在本 proposal 与本 change 的 design 内自包含写明。README 不更新（新壳未发布，README 仍描述旧版），`docs/IMPLEMENTATION-TRACKER.md` 不更新（不在 AGENTS 活文档清单内）。
