# 新 UI 技术栈与「多语言 / 昼夜色」重构方向

- 状态：**待评审**（探索结论；未编码）
- 日期：2026-11-24
- 关联提交：无（未编码）
- 验收结论：不适用（未实施）

## 0. 一句话结论

新 UI 工程从零建在独立目录（暂名 `logcat-next/`），前端框架选 **Avalonia**，继续消费既有 `logcore.dll`（C ABI 不变）；旧 `logcat-viewer` / `logcat-uicore` 原样不动、并行发布。多语言与昼夜色的难点靠**框架原生机制 + 两条硬约定**消除，而不是再写一套补丁：语义色 token 唯一出口、稳定键本地化目录。

## 1. 诉求

1. 新增一个 UI 工程，**不迁移**旧的 `logcat-viewer`。
2. 解决旧实现里「多语言难改、昼夜色难做」两件事。

## 2. 现状事实（代码取证）

| 事实 | 依据 |
| --- | --- |
| 本地化以「中文原文即键」，英文表分散在 8 个 `LocTable.*.cs` 分区 | `logcat-uicore/Services/Loc.cs` 的 `T()` / `BuildEn()` |
| 缺译时静默回退中文原文，漏译不会暴露 | `Loc.T` 的兜底分支 |
| 因绝对定位，额外长出「按语言给像素」的补丁 `W()` / `Sz()` / `FitWidth()` | `Loc.cs` 宽度区段 |
| 运行时切语言靠遍历控件树 + 注册刷新回调 + 字体钉住三套手工登记 | `Loc.SetLang` → `ApplyToAllForms` / `_refresh` / `PinFont` |
| 初始语言在静态构造期按 `CurrentUICulture` 探测 | `Loc.DetectLang`；即配置中记录的「本地绿 / CI 红」来源 |
| 项目**没有主题层**：级别色、标记底色为静态字面量 | `logcat-viewer/Controls/LogListView.cs` 的 `LEVEL_COLORS` / `MARK_BG` |
| 过滤框三维着色硬编码浅色十六进制，注释自认「深色主题需另做适配」 | `logcat-viewer/frmMain.cs` 的 `ApplyTermBoxStyle` |
| 部分颜色借 `SystemColors.*` 代理，主题切换只能「一半跟随」 | 同上 |
| C ABI 已被旧 viewer 端到端跑通（plan → commit → poll_events → row_count → page） | `logcat-viewer/Services/Logcore/LogcoreSource.cs` |
| 发布形态为「自包含 + 框架依赖」双包；单文件模式在本机实测启动失败 | `logcat-viewer/logcat.csproj` 注释 |

## 3. 两痛点根因

**多语言难改**——三个独立原因叠加，换框架只解决第三个：

1. 键策略：中文原文当键 + 缺译静默回退 → 改中文措辞可能连带丢英文，漏译不可见。
2. 布局：绝对定位导致必须按语言给像素 → 每加一处文案都要想宽度。
3. 刷新机制：WinForms 无绑定级联，运行时切换必须手工遍历控件树。

**昼夜色难做**——不是工作量问题，是框架能力问题：

1. 无颜色角色层，字面量散落各处，改主题等于全局搜索替换。
2. WinForms 无样式级联；即便 .NET 9+ 的实验性深色模式，也覆盖不到 TabControl 表头、ListView 表头、滚动条、自绘列表与系统对话框。
3. 领域色（级别 V/D/I/W/E/F/A、标记底、过滤框 or/and/大小写）只有浅色一套，深色下对比度需重新设计。

## 4. 框架取舍

| 维度 | WinForms（现状） | WPF | Avalonia | WinUI 3 |
| --- | --- | --- | --- | --- |
| 昼夜主题 | 无级联，逐控件手写 | 资源字典 + 动态资源，两套自维护 | 内置 `ThemeVariant` Light/Dark | `ThemeDictionaries` 原生 |
| 运行时切语言 | 手工遍历 | 绑定自动刷新 | 绑定自动刷新 | 绑定自动刷新 |
| 大列表 | 自绘成熟 | 虚拟化成熟 | 虚拟化 + 可自绘 | ItemsRepeater |
| 跨平台 | 无 | 无 | 有 | 无 |
| 自包含双包 | 已验证 | 可用 | 可用 | 依赖 WinAppSDK，最绕 |
| 主要代价 | 两笔债继续 | 无跨平台、主题自写 | 生态较小、学习成本 | 发布链最复杂 |

**决策：Avalonia**。理由：昼夜色是框架内建能力（`ThemeVariant` + 内置 Fluent 主题）；运行时切语言可完全走绑定；自绘视口可行；自包含发布可行；未来跨平台无需重写。

## 5. 与框架无关的两条硬约定

```
View (XAML)  只写语义引用：{loc Key=...} / {DynamicResource Surface.Panel}
     |
     +--> DesignTokens：Surface / Text / Accent / Level.* / Mark.* / Filter.*
     |        两套 ThemeDictionaries，切换 = 换字典 + 一次通知
     |
     +--> LocCatalog：稳定键 -> zh / en，缺键即测试失败，切换 = 变更通知
     |
     +--> Interop：logcore.dll（JSON 控制面 + 裸缓冲数据面）
              Rust 只回错误码与参数，本地化在 UI 侧按 Error.<CODE> 映射
```

1. **颜色唯一出口是 token**：UI 层禁止出现颜色字面量；自绘控件订阅主题变更后重绘。
2. **本地化用稳定键**：不用中文原文当键；键集合与格式占位符由测试断言，渲染文案不参与断言（从机制上消除 CI 语言环境问题）。
3. **禁止按语言给像素**：一律布局自适应，不再需要 `W()` / `Sz()` / `FitWidth()` 这类补丁。

## 6. 最小验证壳的范围

第一版只做一个窗口，验证三件事，不做真实业务功能。

验收清单：

- [ ] 打开大文件只经 `logcore.dll`，不碰旧 C# `LogDocument`
- [ ] 自绘列表只画视口行，10M 行滚动帧时间不劣于旧版
- [ ] 主题三态（Light / Dark / 跟随系统）切换不重启、不重载文件
- [ ] 中英运行时切换，全部文案即时刷新；缺键、缺 token 由测试报红
- [ ] 100% / 125% / 150% DPI 下中英混排不裁字；中文 IME 输入正常

明确不在第一版：过滤、标记、导出、ADB、格式识别（已由 core 承担）、设置持久化、打包脚本。

工程切分（第一版 3 个，验证后再拆 Presentation）：

- `Next.Interop`：唯一懂 C ABI 的层，契约可参照旧 `LogcoreSource` 重写，不引用旧工程。
- `Next.App`：Avalonia 壳 + DesignTokens + LocCatalog + 自绘画布。
- `Next.Tests`：键集合、占位符、token 完整性、行解码。

## 7. 风险

1. **中文字体度量**：旧代码为 GDI 「测量宽度 ≠ 绘制宽度」专门做过规避；Avalonia 走 DirectWrite/Skia，度量路径不同，需在三档 DPI 实测。
2. **中文 IME**：过滤框高频输入中文，Avalonia 在 Windows 上的候选窗定位需实测。
3. **自绘帧率**：10M 行下「只画视口」是否跟手，是方案成立与否的硬指标。
4. **原生缓冲生命周期**：`logcore_page` 返回「谁分配谁释放」的裸缓冲；Avalonia 合成可能延迟到后续帧，不能取页后立即释放。两条路径二选一：取页即解码成行模型，或 pinned 缓冲 + 引用计数。

## 8. 待澄清

- 新分类名与规范落位：新 UI 宿主的行为契约（主题、语言、大列表渲染）需要新的 `openspec/specs/` 分类，分类名须先登记进 `openspec/config.yaml`。
  - 为什么需要澄清：现有分类只有 `old`（C# 旧实现）与 `logcore`（Rust core），新 UI 不属于二者。
  - 方案：登记 `next`；或本 change 先不产出行为契约（`skip_specs`）。
  - 当前偏向：登记 `next`，与「新宿主接管能力即新建分类」的既定规则一致。
- 中文输入法、字体度量、自绘帧率三项均需探针实测后才能定稿，当前结论属设计判断，未实测。
