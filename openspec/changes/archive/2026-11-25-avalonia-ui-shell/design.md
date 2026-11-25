# Design

## Context

- 动机见 `proposal.md`；行为契约见 `specs/next/{ui-theme,ui-localization,log-view-rendering}/spec.md`。
- 既有 C ABI 已被旧查看器端到端使用：`logcore_plan` / `commit` / `feed` / `poll_events` / `get_progress` / `row_count` / `page` / `free_page` / `free_handle` / `take_last_error`，错误以 `i32` 码返回、参数经线程本地 `take_last_error` 取走（`crates/cabi/src/lib.rs`）。
- `logcore_page` 返回裸缓冲（指针 + 长度），分配在 Rust、释放必须回调 `logcore_free_page`（谁分配谁释放）。
- 旧 UI 的本地化与配色形态见探索笔记 `openspec/notes/AVALONIA-UI-APPLICATION-STACK.md` §2；本设计不沿用其实现。
- 约束：`crates/`、`logcat-viewer/`、`logcat-uicore/` 本 change 零改动；不改发布链；目标框架沿用 `.NET 10`。

## Goals / Non-Goals

**Goals:**

- 一个可运行的最小壳，能证明「千万行自绘列表 + 明暗主题 + 中英切换」在 Avalonia 上成立。
- 把主题与本地化做成**唯一出口**的机制，后续新增界面默认不会重新长出硬编码颜色与中文原文即键的债。
- 互操作层与 UI 框架解耦，将来替换视图层不需要重写 C ABI 绑定。

**Non-Goals:**

- 不追求零拷贝：第一版允许一次行模型拷贝。
- 不做过滤、标记、导出、ADB、崩溃聚合、设置迁移。
- 不做自动化视觉回归（截图比对）；第一版以可测的逻辑与手测清单验收。
- 不决定旧查看器何时退役。

## Decisions

### D1：前端框架选 Avalonia

- **为什么**：明暗主题是框架内建能力（`ThemeVariant` + 内置 Fluent 主题），而非自研补丁；运行时切换语言可完全走绑定；单 NuGet 依赖、自包含发布可行；自绘视口可行；将来跨平台不需要另起炉灶。
- **备选**：WPF（无内置深色主题、无跨平台）；WinUI 3（依赖 Windows App SDK，发布与裁剪链最复杂，而既有发布链已因单文件模式踩坑）；继续 WinForms（本 change 的两项目标正是它做不到的）。
- 目标框架用 `net10.0`（不带 `-windows`），只在运行时依赖 Windows 的 `logcore.dll`，为跨平台保留余地。

### D2：新目录、三工程、不引用旧工程

```text
logcat-next/
  Next.Interop/   唯一懂 C ABI：导出表、句柄包装、错误码与 JSON 封送
  Next.App/       Avalonia 壳：DesignTokens、LocCatalog、自绘列表、最小窗口
  Next.Tests/     键集合、占位符、token 完整性、行解码
```

- **为什么不引用 `logcat-uicore`**：它的本地化与配色正是本 change 要替换的形态；复用会把旧假设带进来。互操作契约参照旧 `LogcoreSource` 的**行为**重写，不复制工程引用。
- **为什么第一版不拆 Presentation 层**：验证阶段多一层只会空转；真实功能进来时再拆，视图模型天然可迁移。
- 备选：单工程。否——互操作与 UI 的依赖方向需要靠工程边界强制，放在一个工程里迟早互相引用。

### D3：主题用「语义 token + ThemeDictionaries」，不定义控件级色值

- `DesignTokens.axaml` 只放语义角色：`Surface.Canvas` / `Surface.Panel` / `Text.Primary` / `Text.Muted` / `Accent` / `Level.Verbose..Assert` / `Mark.Background` / `Filter.Or` / `Filter.And` / `Filter.CaseOn`。
- `ResourceDictionary.ThemeDictionaries` 为 `Light` / `Dark` 各给一份；控件样式与自绘代码只引用 `{DynamicResource ...}`，不出现颜色字面量。
- 切换 = 设置 `RequestedThemeVariant`；`Default` 表示跟随系统。
- 自绘列表不是样式系统的一部分，订阅 `ActualThemeVariantChanged` 后 `InvalidateVisual()`。
- **为什么不用控件级色值**：语义角色才能保证「同一含义在两套主题下同时改」；控件级色值会让主题覆盖随控件数量线性增长。
- 备选：把所有颜色集中到一个静态类。否——静态类拿不到资源字典的系统主题跟随，也无法在运行时替换。

### D4：本地化用稳定键 + 可观察索引器

- 语言目录为每语言一份 JSON（`Strings.zh-CN.json` / `Strings.en.json`），键为稳定 ID（`Main.Reload`、`Error.IO_ERROR`、`Level.Warn`）。
- 落地注记：JSON 以文件随输出复制（csproj `Link` 到输出 `Strings\`），**不用 EmbeddedResource**——Avalonia 构建任务会把 EmbeddedResource 吸进 `!AvaloniaResources` 大包，运行时清单里取不到（实测）。
- `LocalizationService` 实现 `INotifyPropertyChanged`，暴露索引器；XAML 用 `{loc:Loc Key=...}` 标记扩展（Avalonia 语法要求 `前缀:类型名`，无冒号时解析器在默认命名空间找类型，实测确认）绑到索引器。切语言 = 改当前语言 + 触发一次变更通知。
- 落地注记（实测）：Avalonia 11.3 的索引器绑定只认 `PropertyChanged` 名 **`Item`**；发空属性名或 `Item[]` 都不会刷新绑定（同一应用内三种通知名逐一实测对比）。切语言固定发 `Item`，消费方（状态栏重算）也按该名判断。
- 启动语言：上次保存的选择 → 否则系统区域（中文取中文，其余取英文）。
- 缺键呈现中文原文；**缺键本身由测试判定为失败**，不靠运行时观察。
- **为什么不用中文原文当键**：改中文措辞会连带丢英文，且同一中文在不同语境需要不同英文时无解——这正是旧实现改不动的根因。
- **为什么不用 resx**：可读性与 diff 友好度差；第一版只需两份平面目录，JSON 足够且易审阅。
- 错误码本地化：Rust 只回 `code` + `params`，UI 按 `Error.<CODE>` 取文案，不在 core 里带任何界面语言。

### D5：大列表自绘 + 视口物料化

- 用一个自绘控件承担列表：维护滚动偏移与行高，`Render` 中只取视口内的行；不创建逐行控件对象。
- 列标题、滚动条用 Avalonia 标准控件或轻量自绘，同样只引用 token。
- 取页策略：按视口需要向互操作层请求「起始行 + 行数」，返回已解析的行模型。
- **为什么自绘而不是 ItemsControl 虚拟化**：日志行是定高、多色分段、千万行的密集文本，自绘的物料规模与分配次数可控；旧查看器的自绘路径也证明可行，迁移的是思路而非代码。
- 备选：`ItemsRepeater` + 模板。否——千万行下模板对象的创建与回收成本更高，且分段着色仍需自定义绘制。

### D6：原生缓冲在后台线程解码后立即释放

- 取页调用放在后台线程：拿到 `ptr + len` → 立即解析为托管行模型（序号、时间、级别、tag、正文片段）→ 调用 `logcore_free_page` → 把行模型交回 UI 线程绘制。
- **为什么不在 UI 线程持有原生缓冲**：Avalonia 的合成可能落到后续帧，「取页 → 立即释放」会读到已释放内存；而「持有到渲染结束」要把生命周期跨线程拉到合成层，第一版不值得。
- **备选（后续优化）**：pinned 缓冲 + 引用计数，渲染帧结束后归还。需要时再引入，不改变互操作契约。
- 代价：每页一次拷贝。第一版以正确性优先，性能由验收清单度量后再决定是否优化。

### D7：互操作手写绑定，不引入代码生成

- `Next.Interop` 手写导出表（与旧实现同样的「按名解析、缺失即明确报错」），并在启动时校验所需导出全部存在。
- 请求形状注记（core request.rs 契约）：File 源的 `proxy` 节必填（`pipeName` / `launchFile` / `launchTimeoutMs`），且 proxy 存在时 `options.sidecar.dir` 必填；壳以占位 `launchFile` 满足形状，本地直开路由不启动代理。
- **为什么不引入绑定生成器**：本 change 约定不改 `crates/`，而常用生成方案需要在 Rust 侧加构建步骤；导出表只有十余个函数且已稳定，手写成本低。
- **备选（后续）**：若 ABI 继续扩张，在允许改 `crates/` 的 change 中引入生成器，消除签名漂移风险。
- 原生库定位沿用旧做法：允许显式路径覆盖（便于测试与部署），默认在应用目录旁查找。

### D8：测试不依赖界面语言与渲染文案

- 断言对象：两语言键集合一致、格式占位符一致、每条键都有非空译文；两套主题的语义 token 集合一致；行模型解码与分页边界。
- 不断言渲染出的文本，从机制上避免「本地中文绿、CI 英文红」。
- 视觉与交互（主题覆盖、DPI、IME、滚动跟手）进手测清单，结果记入验收。

### D9：第一版不接发布链

- 新工程独立构建运行，`logcore.dll` 以构建项复制到输出目录；不加入现有双包发布脚本。
- **为什么**：发布形态（自包含 / 框架依赖、图标、许可文件、版本号来源）牵涉 `Directory.Build.props` 与脚本，属于范围外；等壳验证通过再单独一刀接入。

## Risks / Trade-offs

- **中文字体度量与旧实现不同** → 显式字体族与回退链，三档 DPI 手测；发现裁字优先调整布局而非加像素常量。
- **中文 IME 候选窗行为** → 过滤输入框虽不在第一版功能内，但壳里保留一个文本输入用于验证；不通过则记录并评估替代输入控件。
- **千万行自绘帧率不达标** → 先量取页与绘制两段耗时，再决定是优化解码（避免逐行分配）还是引入零拷贝。
- **两套 UI 并行导致认知与维护成本** → 第一版不发布、不承诺功能等价；替换决策另立 change。
- **Avalonia 版本演进** → 锁定到当前稳定主版本，不跟随预览版。
- **主题覆盖不全（系统对话框等）** → 验收清单显式列出「无浅色残留」的检查范围；系统级对话框不在第一版范围。

## Migration Plan

- 不迁移任何代码与数据；新工程与旧工程并存，旧工程零改动。
- 回滚 = 删除新目录，旧查看器不受影响。
- 第一版不进入发布产物，不改变用户可见的下载内容。

## Open Questions

- 语言选择是否与旧查看器共享同一份持久化文件：影响多应用一致性，但不影响本 change 的契约与任务；先各自持久化。
- 设置持久化的整体方案（文件格式、迁移、主题与语言之外的项）：等功能扩张时另立 change。
