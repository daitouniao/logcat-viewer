# 新壳持久化：设置与索引缓存目录

- 状态：**已定稿**（探索结论；决策已全部落入在途 change `openspec/changes/avalonia-app-storage/`；未编码）
- 日期：2026-11-25
- 关联提交：无（未编码）
- 验收结论：不适用（未实施）

## 0. 一句话结论

新壳（Avalonia 宿主）的持久化收敛为**一个「可写根」解析器管三类数据**：设置（小而持久）、索引缓存（大而可清）、临时产物（用完即弃）。落盘位置、原子写与版本锚点是本轮要定的；缓存行为复用 core 既有实现，新壳只定目录。

## 1. 已定

**第一轮（方向）**

1. **设置落 exe 旁 + 可写回退**：exe 目录可写就用 exe 目录，否则回退用户目录，再否则仅会话内生效。
2. **语言保持独立**：新壳只读写自己的设置文件，不读写旧壳的 `language.json`。前提是**旧应用之后会废弃**，两套壳语言不一致是接受的代价。
3. **本轮切片连缓存目录一起定**：索引缓存从 `%TEMP%` 迁到持久目录，与设置共用可写根。

**第二轮（2026-11-25 确认）**

4. D1–D15 小项**全按建议**（见 §4）。
5. A2 诊断日志：**写 exe 同级**（随可写根），单文件追加；**SMB 路径是常态，完整路径入日志可接受**。
6. A3 缓存目录：**同意**——缓存是派生数据，不迁移、不兼容，替换 change 负责删除旧 `<exe>/cache`。
7. A4 旧设置：**不迁移**。
8. A5 并发写：**同意**——接受后写覆盖先写，单写入入口（内存模型整体落盘）。

9. A1 可写根解析器：**选 ②——放在 `Next.App/Storage` 命名空间，不新建工程**；候选目录与可写探测均注入，解析为纯函数（见 §5 A1）。

10. A6 多用户：**接受 exe 旁共享**——exe 可写时语言/主题可被同机多用户共享；装在 Program Files 等不可写位置时自然落到用户目录。
11. A7 缓存根：**与设置同根，一起回退**；请求里只多一项缓存目录。

## 2. 现状事实（代码取证）

| 事实 | 依据 |
| --- | --- |
| 旧壳三份配置文件，均在 exe 同目录，各带 `SchemaVersion=2` | `logcat-viewer/Services/LogcatSettings.cs`、`logcat-uicore/Services/LanguageSettings.cs` |
| 旧壳语言项单独存 `language.json`，两个 exe 共读共写，重启生效 | `logcat-uicore/Services/LanguageSettings.cs` 注释与 `Loc.SetLang` |
| 旧壳落盘原语：原子写（`.tmp` + `File.Move` 覆盖）、容错读、版本判定 | `logcat-uicore/Services/SettingsIO.cs` |
| 旧壳不用 `%LOCALAPPDATA%` 的理由：安全软件拒写且异常被吞导致设置静默丢失；`Properties.Settings` 的哈希目录随构建方式漂移 | `logcat-viewer/Services/LogcatSettings.cs` 类注释 |
| 旧壳索引缓存在 `<exe>/cache`，自带身份校验、容量上限与 LRU 淘汰 | `logcat-viewer/Services/SidecarCacheStore.cs` |
| 新壳设置只有 `Lang` / `Theme`，存 `<exe>/next-settings.json`；非原子写、无版本号、失败无留痕 | `logcat-next/Next.App/Localization/NextSettingsStore.cs` |
| 新壳语言与主题是两条独立的「读-改-写全文件」路径，无单一 owner | `Next.App/Localization/LocalizationService.cs`、`Next.App/Views/MainWindow.axaml.cs` |
| 新壳临时目录与索引缓存都在 `%TEMP%/logcat-next` | `Next.App/ViewModels/MainViewModel.cs` 的请求构造 |
| core 的 sidecar 默认单条目 1 GB、目录 4 GB；原子落盘（`.lcs` + `.tmp-`）、按 mtime LRU、跳过在用条目、损坏静默降级 | `crates/core/src/sidecar.rs`、`crates/core/src/request.rs` |
| sidecar 行为契约已由规范覆盖（身份校验、失效重建、容量与淘汰、静默降级、临时产物清理） | `openspec/specs/logcore/source-acquire/spec.md` |
| 新旧两套缓存格式互不兼容（旧壳为自身 C# 引擎的缓存，新壳为 core 的 `.lcs`） | 两侧实现；核心格式魔数 `LCSC` |
| `Next.Tests` 已同时引用 `Next.App` 与 `Next.Interop`；`Next.App` 为 `WinExe` 且只引用 `Next.Interop` | `logcat-next/Next.Tests/Next.Tests.csproj`、`logcat-next/Next.App/Next.App.csproj` |

## 3. 目标模型

```text
可写根解析（启动一次，结果全局复用）
   |
   +-- <exe> 目录可写？ --是--> root = <exe>
   |                    --否--> root = %LOCALAPPDATA%\logcat-next
   |                             （仍不可写 --> 仅会话内，不持久化，留痕）
   |
   +--> <root>\next-settings.json   设置：Lang / Theme（+SchemaVersion）
   |       生命周期：永久；写入：原子写；失败：留痕 + 会话内生效
   |
   +--> <root>\cache\core\          派生缓存：core sidecar（*.lcs）
   |       生命周期：可随时被外部清理；容量：core 默认 1 GB/条目、4 GB/目录
   |       重建：静默降级为全量解析（core 已实现）
   |
   +--> <root>\next-startup.log     诊断日志：追加写，不轮转
   |
   +--> %TEMP%\logcat-next\         临时：spool / 采集落盘中转（不跟 root）
```

| 数据 | 现状 | 目标 | 责任方 |
| --- | --- | --- | --- |
| 设置 | `<exe>/next-settings.json`，非原子、无版本、双写路径 | `<root>` 解析 + 原子写 + 版本 + 单写入入口 | 新壳 |
| 语言 | 新壳独立 | 维持独立，不读 `language.json` | 新壳 |
| 索引缓存 | `%TEMP%/logcat-next/sidecar` | `<root>\cache\core` | 新壳定目录；行为归 core |
| 诊断日志 | 无 | `<root>\next-startup.log` | 新壳 |
| 临时产物 | `%TEMP%/logcat-next` | 不变 | core |

## 4. 可直接拍板的项（D1–D15，已于 2026-11-25 全部确认）

| # | 决策点 | 结论 | 理由 |
| --- | --- | --- | --- |
| D1 | 回退根目录名 | `%LOCALAPPDATA%\logcat-next` | 与产品名一致；不与旧壳目录混用，避免替换时互相干扰 |
| D2 | 设置文件名 | 保留 `next-settings.json` | 新壳未发布、无真实用户，改名无收益 |
| D3 | 设置 schema 版本 | 起始 `SchemaVersion = 1` | 旧壳的 `2` 属于另一文件，无需对齐；新文件从 1 起更清晰 |
| D4 | 缓存子目录 | `<root>\cache\core` | 与旧壳 `<exe>/cache` 的 C# 缓存格式不兼容，分目录避免混放；`core` 表明归属 |
| D5 | 缓存容量 | 沿用 core 默认（1 GB/条目、4 GB/目录），不显式传参 | 默认值即规范口径，显式传参只会多一处漂移点 |
| D6 | 临时目录 | 保持 `%TEMP%\logcat-next`，不跟 root | 临时产物按定义可丢；放持久目录反而制造垃圾 |
| D7 | 诊断日志落点 | `<root>\next-startup.log`，单文件追加、不轮转 | 与设置同一可写根，可写性已探测；新壳日志量极小 |
| D8 | 写失败处理 | 静默 + 写日志，不弹窗 | 沿用旧壳口径：设置失败不打断使用 |
| D9 | 原子写实现 | `.tmp` 同目录写入 + `File.Move` 覆盖 | 与旧壳同款，行为已被验证 |
| D10 | JSON 选项 | 缩进 + 不转义非 ASCII | 便于人读与 diff，与旧壳一致 |
| D11 | 设置写入时机 | 变更即写，不引入「退出时集中写」 | 退出写会丢异常退出场景；当前量级无性能压力 |
| D12 | 「清除缓存」入口 | 第一版不做 UI；目录可被用户手动删除 | core 已保证缺失/损坏静默降级，加 UI 属功能扩张 |
| D13 | 无设置时的语言 | 按系统区域（中文取中文，其余英文） | 沿用现有行为，且不依赖旧壳的 `language.json` |
| D14 | 旧 `next-settings.json` 迁移 | 不做 | 新壳未发布，无存量用户 |
| D15 | 日志与设置是否随便携包走 | 是，跟随应用目录 | 便携语义的自然结果；换机器时设置可用 |

## 5. 架构 / 方向项

### A1. 可写根解析器放在哪一层

- 状态：**已定**（2026-11-25）：选 ②——`Next.App/Storage` 命名空间 + 注入探测，不新建工程。
- **背景**：解析器里有两类东西——**策略**（候选顺序、回退、会话内降级）与**机制**（目录可写探测）。设置、缓存请求构造、诊断日志三处都要消费它；并且必须能测「exe 可写 / exe 不可写 / 全部不可写」三种分支，而 `AppContext.BaseDirectory` 在测试进程里无法替换。
- **选项对比**：

| 选项 | 依赖方向 | 可测性 | 结构成本 | 结论 |
| --- | --- | --- | --- | --- |
| ① 新建 `Next.Storage` 工程 | 独立工程，App/Tests 都引用它 | 好 | 多一个 csproj、sln 条目、引用与测试接线 | 暂不 |
| ② `Next.App/Storage` 命名空间 + 注入探测 | App 内部，不新增工程 | 好（注入候选与探测函数） | 零 | **推荐** |
| ③ 放进 `Next.Interop` | 破坏「唯一懂 C ABI」的边界 | 好 | 低 | 否 |
| ④ `Next.App` 内静态类直接读 `AppContext.BaseDirectory` | 无 | 差（只读分支无法测） | 最低 | 否 |

- **无论选哪个都必须满足的约束**：
  1. 候选目录与探测函数**可注入**；解析是纯函数，输出不可变结果（设置目录 / 缓存目录 / 日志路径 / 是否仅会话内）；
  2. 解析只在启动跑一次，运行期不再探测；
  3. `Next.Interop` 不碰目录：缓存目录由 App 解析后写进请求 JSON（现状即如此，只改值）；
  4. 约定：只有 Storage 模块允许读 `AppContext.BaseDirectory`，其余代码使用注入结果。
- **升级为独立工程的触发条件**（任一满足再拆）：① 出现第二个 .NET 宿主需要复用；② 存储模块文件数增长到约 5 个以上，或测试需要独立的程序集隔离；③ 需要不同的目标框架 / 裁剪配置。
- **偏向 ② 的理由**：当前三工程语义是「互操作 / UI / 测试」；存储不属于 C ABI，也还不到独立成型；`Next.Tests` 已引用 `Next.App`（已核实），可测性不受影响；将来拆分只是搬命名空间，成本低且可逆。

### A2. 诊断日志体系

- 状态：**已定**（2026-11-25）：写 exe 同级（随可写根）单文件追加，不轮转；完整路径入日志（含 SMB 路径）**可接受**——SMB 是常态场景。
- 背景：新壳原本没有任何日志出口；设置 / 缓存的失败留痕都依赖它。
- 遗留：日志体积上限未实测；若将来日志点变多，再评估按大小截断。

### A3. 缓存目录与「替换旧壳」的衔接

- 状态：**已定**（2026-11-25）：缓存不迁移、不兼容；替换 change 负责删除旧 `<exe>/cache`，新壳从空缓存起步。
- 依据：缓存是派生数据，可重建；两套格式互不兼容（旧 C# 缓存 vs core `.lcs`）。

### A4. 设置键面与旧设置迁移

- 状态：**已定**（2026-11-25）：**不迁移**旧壳设置；本轮只立骨架，键面仍只有 Lang / Theme。设置文件必须带 `SchemaVersion`，为将来加键留迁移入口。
- 影响：旧壳的窗口几何 / 字号 / 过滤偏好不会带过来；替换后用户首次打开新壳按默认布局。

### A5. 多实例与并发写

- 状态：**已定**（2026-11-25）：接受「后写覆盖先写」；设置层做单写入入口（内存模型 → 变更后整体落盘），不做文件锁或跨进程协商。
- 事实：旧壳同样没有跨进程互斥，只保证两应用各写自己的文件不互相覆盖。

### A6. 便携语义与多用户共享

- 状态：**已定**（2026-11-25）：接受共享。
- 背景：exe 目录可写但被多用户共享时（例如放在公共目录），语言与主题会被所有用户共享。
- 偏向：接受。安装在 `Program Files` 时通常不可写，会落到用户目录，问题自然消失；便携包按单用户使用理解。
- 需要你确认的原因：这是产品语义选择。若要求「永远按用户隔离」，就必须放弃 exe 旁落盘（与已定决策 1 冲突）。

### A7. 缓存目录与设置根是否必须同一个

- 状态：**已定**（2026-11-25）：同根，一起回退。
- 背景：若分开，只读 exe 时需要两套回退逻辑；若同一个，缓存也会跟着设置回退到用户目录。
- 偏向：同一个根，一个解析器。缓存体积大，回退到用户目录比「exe 不可写则放弃缓存」更符合「重开亚秒」的目标。
- 需要你确认的原因：影响请求里是否要区分两套目录参数，以及回退后的容量预期。

## 6. 待澄清

- 旧壳废弃的时间表：仅影响替换 change 的缓存清理与用户引导，不影响本轮骨架。
- 「重开亚秒」在新壳是否仍是硬指标：若是，缓存回退到用户目录后需实测 SMB 源文件的冷/热重开耗时（当前无数据）。

## 7. 建议切片（待 propose 时展开为 tasks）

1. 可写根解析器 + 三种分支（exe 可写 / 回退用户目录 / 仅会话内），含单元测试；
2. 设置层骨架：版本号 + 原子写 + 容错读 + 单写入入口，合并现有语言/主题两条写路径；
3. 诊断日志最小实现（D7/D8）；
4. 请求构造改接 `<root>\cache\core`，`tempDir` 保持不变；
5. 测试：只读目录回退、损坏设置回默认、版本不识别回默认、`.tmp` 残留不生效、语言与主题互不覆盖、请求 JSON 的 sidecar 路径正确；
6. 手测：可写目录与只读目录两种部署下，设置与缓存各自表现；重开同一大文件验证缓存命中。

前置：`avalonia-ui-shell` 的 7.1（合入 master）完成后，本 change 从 master 起 worktree。

## 8. 事实出处（仓库相对路径）

- `logcat-viewer/Services/LogcatSettings.cs`
- `logcat-uicore/Services/SettingsIO.cs`
- `logcat-uicore/Services/LanguageSettings.cs`
- `logcat-viewer/Services/SidecarCacheStore.cs`
- `logcat-next/Next.App/Localization/NextSettingsStore.cs`
- `logcat-next/Next.App/Localization/LocalizationService.cs`
- `logcat-next/Next.App/Views/MainWindow.axaml.cs`
- `logcat-next/Next.App/ViewModels/MainViewModel.cs`
- `logcat-next/Next.Tests/Next.Tests.csproj`、`logcat-next/Next.App/Next.App.csproj`
- `crates/core/src/sidecar.rs`、`crates/core/src/request.rs`
- `openspec/specs/logcore/source-acquire/spec.md`
