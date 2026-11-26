# 单元测试与覆盖率实操指南

> 最后更新：2026-11-26
> 本文是「怎么把测试跑起来、覆盖率怎么出」的**唯一操作入口**。
> 有效性审计（变异测试）见 [`UT-AUDIT.md`](UT-AUDIT.md)。
> **CI 语言环境陷阱**（本地全绿、推送就红）见 §3.4。

## 0. 当前基线（2026-11-14）

| 项         | 值                                                           |
| ---------- | ------------------------------------------------------------ |
| 用例数     | **923**（915 + `rust-core-query-handle` 新增 8：经 FFI 的 `LogcoreQueryIT`） |
| 行覆盖率   | 94.26% (2,677 / 2,840)——随结构重组于 2026-11-08 重新采样 |
| 分支覆盖率 | 88.64% (1,460 / 1,647)——同上，同一批采集                     |
| 方法覆盖率 | 92.77% (295 / 318，判据「方法内 ≥1 行命中」)——同上，同一批采集 |
| 统计口径   | 仅 Services + Models（UI 与设备/系统集成层已排除，见 §4）；主体已迁 `logcat-uicore/` |

> 2026-11-24 **rust-core-query-handle**：915 → **923**（+8：`LogcoreQueryIT` 经 FFI 的 IT——
> 过滤结果、按文档行起页、向上翻页、尾部跳转、定位、源关闭后读取、非法条件归因、页尾与空页）。
> 全量实测 **923/923**，无跳过。C# 覆盖率**未重采**（表内 94.26% / 88.64% / 92.77% 仍是
> 2026-11-08 口径）；Rust 侧用例与覆盖率见 §6。

> 2026-11-22 **修复过期 IT**：`SourceAcquireFailureIT.Direct_sniff_failure_falls_back_to_proxy_without_events`
> 断言的是 reject-unsupported-format 之前的旧契约（Auto 嗅探非文本转代理），已按现契约改写为
> `Direct_non_text_is_rejected_without_proxy`——同步返回 `UNSUPPORTED_FORMAT`、`format=Binary`、
> 不连代理。用例数不变（**915**），全量实测 **915/915**，无跳过。

> 2026-11-21 **rust-core-paging-read**：902 → **915**（+13：`LogcoreBindingTests` 新增
> `BindingPageLimitConstantTests` 1 条——`MAX_PAGE_ROWS` 与 core 常量对拍；新增
> `LogcorePageContentIT` / `LogcorePageGuardsIT` / `LogcorePagePathsIT` 3 个经 FFI 的 IT 类
> 共 12 条——页行切片与崩溃字段、尾页/空页、未知元数据哨兵、状态门禁与参数上限、
> 截断 `IO_ERROR`、缓存命中一致、源文件改名后仍读装载内容、重复取页稳定）。
> 全量实测 **914/915**（zh-CN 与 en-US 各复跑均同）：唯一失败为
> `SourceAcquireFailureIT.Direct_sniff_failure_falls_back_to_proxy_without_events`
> （master 上既有已知失败，本 change 未触碰该路径）；`SingleInstanceTests` 在整机高负载时
> 偶发失败（单跑该类 7/7 全过）。其余全过。覆盖率**未重采**。

> 2026-11-14 **source-acquire-contract**：852 → **902**（+50：`LogcoreBindingTests` 14 条绑定层
> UT——封送往返、错误码与 `INVALID_ARG.field` 映射、commit 受理描述形状、事件反序列化、
> 维护入口封送、缺失 dll 明确报错、`MAX_FEED_LINES` 与 core 常量对拍；19 个 `SourceAcquire*IT`
> 类共 36 条经 FFI 打真实 core 的 IT——commit 受理模型、本地直读、sidecar 二次打开与版本降级、
> 代理 spool 与实例复用、失败路径、取消、配置快照、停止排空、关闭作废、关闭并发、多源并存、
> 状态矩阵、超长路径、启动维护、推入与吞吐）。
> 全量实测 **897/902**：5 条 `SingleInstanceTests` / `SingleInstanceNameTests` 在**本机已有
> `logcat.exe` 运行**时必然失败（`Local\logcat-viewer-single-instance` 互斥量已被占用），
> 与本 change 无关（同一环境下在 master 上复跑同样 5 条也失败）；关停所有 logcat / andtools
> 实例后可全过。
> 吞吐观测：`SourceAcquireFeedThroughputIT` 以 10×1 万行推入，Debug 构建实测 10 万行
> **788 ms（约 12.6 万行/秒，含 D8 深拷贝）**，行数零丢失。该数字只作背压评估，不推导退出等待（D12）。
> 覆盖率**未重采**（表内 94.26% / 88.64% / 92.77% 仍是 2026-11-08 口径，不含本轮新增绑定层）；
> Rust 侧覆盖率口径独立，见 §6。

> 2026-11-08 **结构重组后的基线**：852 条（含 `LocTableTests` 的 30 条本地化一致性用例），
> 行覆盖率 94.26%、分支 88.64%、方法 92.77%，均为同批重采。

> 2026-11-06 **fix-devices-changed-race**：846 → **848**（+2：`DeviceSelectorTests`——
> 通知参数是快照（后续变更不影响已交付内容）、后台刷新与清空并发时枚举不抛）。
> 同批新增 `WinFormsCollection`（独占集合）：`frmMainTests` / `SidecarEquivalenceTests` /
> `AdbClearLogButtonTests` 并入，消除并行构造窗体时 `Loc.BindTip` → 共享 `ToolTip` 内部
> `Dictionary` 的并发破坏（实测 4 次全量挂 2 次）。`dotnet test` 实测 Debug **848/848** 全过。

> 2026-11-06 **sidecar-cache-index**：825 → **846**（+21：`SidecarCacheTests` 16 条——落盘格式
> 往返与 magic/版本/段长校验、缓存键与身份摘要、原子替换与写失败静默、静态文件边界、
> 容量上限与 LRU 淘汰、四类静默降级；`SidecarEquivalenceTests` 5 条——sidecar 装载与全量解析
> 逐列/字典/崩溃事件/行边界等价、命中不进入分块编排、装载后立即按当前条件过滤）。
> `dotnet test` 实测 Debug **846/846** 全过，无跳过。覆盖率未重采。

> 2026-11-02 **split-android-tools-exe**：791 → **825**（+34：配置三拆迁移与原子写 /
> legacy DTO 键面 / 语言存储 / 单实例注入名 / `DeviceSelector` 逻辑类 /
> `AndToolsWindowGeometry` 几何存取 / 工具箱启动器探测 / 唤醒前置兜底判定）。
> `dotnet test` 实测 Debug **825/825** 全过，无跳过。覆盖率未重采：Services/Models 主体
> 已随拆分迁 `logcat.UiCore/`，§4 排除口径需按三工程结构调整后另行重采。

> 2026-11-01 **发版前复验（V0.1.7）**：`dotnet test` Release / Debug / en-US 三口径
> 实测 **791/791 全过，无跳过**，与基线一致。变异测试同批第五轮（chunked 锚点补挂 +
> remove-join-merge 删后对照补跑 + 2 条待甄别项收尾）：102 变异点
> **KILLED 92 / SURVIVED 10（全部等价/性能/纵深防御）/ SKIP 0 / BUILD-FAIL 0**，
> 详见 [`UT-AUDIT.md`](UT-AUDIT.md) §0f。覆盖率未重采（用例与代码自 chunked 重采后未变）。

> 2026-11-01 **chunked-progressive-load**：744 → **791**（+47：分块加载管线 / 增量块扫描与
> 加载期过滤 / 视图分段行号结构与发布节流 / frmMain 接线与取消清空，及 6 样本 × 3 块长
> 的参数化等价用例）。
> `dotnet test` 实测 Debug `791/791` 全过，无跳过。
> ⚠️ tstrun 口径注意：`ChunkedEquivalenceTests` 18 例为 `MemberData` 参数化，tstrun 会
> 静默跳过（§3.2）；本次口径为 `dotnet test`。
> 覆盖率同批重采（Debug + `--collect:"XPlat Code Coverage"` + `coverlet.runsettings`）：
> 行 97.42% → **97.31%**（3,663/3,760 → 3,828/3,934，新增管线/接线代码覆盖良好，新增
> 未覆盖 19 行多为防御与异常分支），分支 88.75% → **88.65%**（1,524/1,717 → 1,625/1,833），
> 方法 95.70% → **95.61%**（401/419 → 414/433）。

> 2026-11-01 **remove-join-merge**：756 → **744**（−12：删 20 条 join/单行化语义用例，
> 新增 8 条自然行口径真能力用例，另修 8 处编译/断言适配）。
> `dotnet test` 实测 Debug `744/744` 全过，无跳过。
> 覆盖率同批重采（Debug + `--collect:"XPlat Code Coverage"` + `coverlet.runsettings`）：
> 行 97.46% → **97.42%**（未覆盖行 98 → 97，删 join 已覆盖代码 ≈104 行致分母缩小，比率微降为分母效应），
> 分支 88.67% → **88.75%**（未覆盖分支 199 → 193），方法 95.77% → **95.70%**（未覆盖方法 18 → 18）。
> ⚠️ 方法覆盖率口径勘误：历次「方法覆盖 N/M」实为「方法内 ≥1 行命中」，此前 408/426 与
> 本表 401/419 均按此口径核对；按「全部行命中」口径则两轮同为 ≈88.5%。

> 2026-10-30 **filter-msgfilter-block-scan**：717 → **756**（+39，新增
> `FilterEngineBlockScanTests`：跨块命中 / 同行与重叠区去重 / 长行跨块 / 头部不误报 /
> 空正文 / CI 与 SIMD-标量互等 / 非 ASCII 行回退 / 组合语义 / 分派三条件 / 进度与取消）。
> `dotnet test` 实测 Debug `756/756`、Release `756/756` 全过，无跳过。
> 覆盖率同批重采（Debug + `--collect:"XPlat Code Coverage"` + `coverlet.runsettings`）：
> 行 97.46% → **97.46%**（分母 3,503 → 3,864，新增块扫描层与 `MessageSession.TryAcquireRaw`），
> 分支 88.43% → **88.67%**，方法 95.49% → **95.77%**。
>
> 2026-10-25 **device-scoped-file-browser-path**（分支口径）：673 → 706（+33：
> 按设备记忆目录的存取与旧配置平滑 7 条、起始目录候选链与首个可用者选取 11 条、
> 面板静默尝试与「用户最后导航目录」跟踪 8 条、窗体记忆写回与降级不覆盖 7 条）。
> 覆盖率同批重采（`--collect:"XPlat Code Coverage"` + `coverlet.runsettings`）：
> 行 97.43% → **97.46%**，分支 88.36% → **88.43%**，方法 95.40% → **95.49%**
> （三者均以同一命令在 master 与本分支各采一次，避免跨口径对比）。
> 新增代码全部在统计口径内：`Services/StartDirResolver.cs` 12/12 行、
> `Services/AppSettings.cs` 80/81 行、`Models/DeviceLastDir.cs` 2/2 行。

> ✅ 空计数回归已查明并解决（2026-10-11），两个独立根因：
> 1. **PDB 没了**：10-10 的自包含发布改动（`1b17b79`）给主工程加了
>    `<DebugType>none</DebugType><DebugSymbols>false</DebugSymbols>`，且无条件生效——
>    Debug 构建也不再产 pdb，coverlet 直接拒绝插桩
>    （诊断日志：`Skipping module 'logcat.dll': not eligible for instrumentation. Module has no PDB`）。
>    **已修复（2026-10-11）**：csproj 里改为仅 Release 生效，Debug 保留默认 portable；
>    采集不再需要命令行覆盖属性。
> 2. **hits 文件写不进 AppData\Temp**：恢复 PDB 后插桩成功但命中仍为 0——
>    testhost 退出时 `Tracker...TryAcquireExclusiveLockOnFile` 抛
>    `UnauthorizedAccessException: ...\logcat_<guid>.lock`，testhost 崩溃、hits 未落盘。
>    把 `TMP`/`TEMP` 指到仓库内目录（如 `<仓库根>/.tmp-test/covtemp`）即正常。
>    成因疑为机器上安全软件盯防 AppData\Temp，未再深挖。
>
> 与 10-16 基线的差异（2026-10-18 重采）：fileproxy / root 检测 / i18n 等新代码使分母
> 2,971 → 3,557；本轮变异测试暴露 7 处覆盖缺口，补测试后用例 519 → 623（净 -22 条
> 被完全包含的冗余用例、+6 条缺口用例，外加 fileproxy 增量）。行覆盖率 98.85%→97.36%
> 的下滑全部来自新增代码的异常/拉起错误路径（见 §4 未覆盖清单），核心路径经变异
> 测试验证咬得住（103 个变异点 102 KILLED + 1 个确认等价变异）。
>
> 2026-10-24 增量：662 → 663（+1），新增 `强制界面语言已生效`——声明
> `LOGCAT_FORCE_UI_CULTURE` 时校验测试进程实际界面语言。区域强制机制见 §3.4：
> Windows 上 `LANG` 无效，此前「设环境变量即可」的结论已更正。
>
> 2026-10-25 **filter-msgfilter-zero-copy**（合入 master 口径）：706 → **717**（+11：零拷贝读取会话
> 5 条 + 过滤引擎零拷贝 6 条），`dotnet test -c Release` 实测 **717/717** 全过。
> 该 change 在独立 worktree 中实施时分支口径为 673 → 684，合入 master 后以全量为准。
> 覆盖率未随本次重采，表中覆盖率数字仍为 device-scoped-file-browser-path 口径。
>
> 2026-10-24 **minute-byte-column**（分支口径）：663 → 673（+10：分钟字段透传 / 分钟列 / 闰秒过滤 /
> 方括号 long 前缀 / 崩溃行序化与时间文本）→ **650**（−23：删除年份机制用例，
> 跨年回退 4、`TsToText` 2、`TsFromParts` 全部含 Theory、`NearestPos` 2、
> `HasYear`/`BaseYear` 2、混合年份锚点 1，Theory 展开后合计 23 条）。
>
> 2026-10-24 **合入 master 后**：minute-byte-column 与 file-transfer-progress（+23）均已合并，
> `dotnet test` 实测 **673/673** 全过——历史记录，现役基线表为上方 2026-10-25 口径。
> 分支期的 650 是单一 change 的分项，master 实际值以两者合并后的全量为准。
> **当时的覆盖率数字仍是 10-23 重采值；本 change 已随 filter-msgfilter-zero-copy 重采（见上）。**
>
> ⚠️ 若在智能体沙箱里看到 spool / 临时目录清理相关 7 条红，先读 §3.5（伪失败）。
>
> 2026-10-23 增量：655 → 662（+7 条），`fix-msgfilter-capacity-scan` 修复回归——
> 折叠函数 internal 直测 5 条（纯 ASCII 折叠 / 含非 ASCII 返回 false 且弃用缓冲 / 空切片 /
> 0x7F/0x80 边界 / 数组与切片重载结论一致）+ 行为等价回归 1 条（`MsgFilter_ResidualBytes_AsciiLineStillMatches`）
> + 「扫描总字节 == Σ 各行正文长度」计数器主钉 1 条（`MsgFilter_ScanBytes_EqualSumOfLineBytes`）。
> 新用例不涉及 `Loc.*` 输出，无需双语断言。
>
> 2026-10-23 **重采覆盖率**（Debug + coverlet collector，TMP 指向 `.tmp-test/covtemp`）：
> 行覆盖率 97.47% **持平**（3,504/3,595 → 3,507/3,598，分母 +3 行 = 切片重载与转发，全部被覆盖）、
> 分支 88.46% **持平**（1,411/1,595）、
> 方法 95.66% → **95.69%**（375/392 → 377/394，+2 个方法全部覆盖：切片重载与计数器属性）。
>
> 2026-10-22 增量：638 → 655（+17 条），三项内容——
> ① adb server 自愈提示本地化的双语断言（+3，另把 2 条既有断言改成双语）；
> ②「清空日志」按钮状态机与设备下拉框清理的覆盖（+12）；
> ③ CI 语言回归防线扫描器（+2）。
> **双环境验证**：zh-CN 与 en-US（CI 实际环境）各跑 655/0/0，en-US 连跑 3 次无偶发。
>
> 2026-10-22 **重采覆盖率**（Debug + coverlet collector，TMP 指向 `.tmp-test/covtemp`）：
> 行覆盖率 97.60% → **97.47%**（3,499/3,585 → 3,504/3,595）、分支 88.46% **持平**、
> 方法 95.65% → **95.66%**（374/391 → 375/392，净 +1 个方法且被覆盖）。
> **下滑归因（实测核实，勿凭印象改）**：分母 3,585 → 3,595（+10 行）来自**排除项之外**的
> 既有代码在上一轮重采之后又长过；本轮改动的三个生产文件 `AdbManager.cs` / `frmMain.cs` /
> `LocTable.RuntimeMsg.cs` 中，前两个在 coverlet 排除项内（实测零未覆盖行），
> 只有 `LocTable.RuntimeMsg.cs` 的 +5 行译文进分母且**全部被覆盖**。
> 换言之本次改动**未拉低覆盖率**，0.13 个百分点是分母自然增长所致。
>
> 2026-10-18 二次重采（`fix-spool-multisession` 落地后）：用例 623 → 628（+5 条暂存目录活性锁
> 用例，全部是 `[Fact]`）；行覆盖率 97.36% → 97.60%（分母 +28 行 = 活性锁原语，其中 3 行未覆盖：
> 两个公开无参重载 + 活性探测的外层防御 `catch`，仅由生产路径调用）；分支 88.44% → 88.46%（1,411 / 1,595）；
> 方法 95.32% → 95.65%（+7 个方法全部覆盖）。未覆盖行清单已按当前 HEAD 行号刷新（见 §4）。
>
> ⚠️ 采集命令见 §2.2：**该命令此前漏了 `Services/Loc.cs` 排除项**，照抄会得到一个不可比的
> 分母（3,861 行 / 92.25%）——已补齐（本次即按补齐后的命令采集）。

> 本轮口径变化：新增的 `Services/Loc.cs`（本地化运行时）**被排除**，理由同 `DpiFix`——
> 它一半的代码是控件树遍历、字体替换与文本像素测量，没有真实窗体与消息泵无从验证。
> 与它同目录的 `LocTable.*`（纯数据译文表）**不排除**，是 i18n 的核心验证面。
>
> `frmMain` 同样在排除范围内，但其中的**纯函数**（`IsNetworkPath` / `EllipsizeMiddle` /
> `HumanSize` / `CanStartCapture` / `CanClearLog` / `ClearDeviceCombo`）已改为
> `internal static` 并由 `frmMainHelperTests` / `AdbClearLogButtonTests` 直接覆盖——
> 判据是「能否在无窗体条件下被有意义地验证」，能验证就不该只靠排除项蒙混。
>
> 覆盖率是「代码被执行过」的证明，**不等于**「断言咬得住」。
> 本项目做过两轮变异测试审计，累计发现 13 处「覆盖率统计覆盖、却一个用例都抓不到」的代码，详见 [`UT-AUDIT.md`](UT-AUDIT.md)。

### 0.1 「通过」的口径

- **「全过」= 受影响类全过 + 全量无新增失败**，不是「跑了没红」。
- 用 tstrun 报告结果必须带口径声明：**tstrun 口径 N 条**（`[Fact]` 全部 + `[Theory]` 按 `[InlineData]` 展开），
  并声明已知跳过类别——`MemberData` / `ClassData` 驱动的 Theory 会被 runner **静默跳过**（§3.2 坑 3），
  汇总行里看不到它们。
- 交叉验证：tstrun 口径条数应与上表基线（**915**）一致；不一致先怀疑 runner 漏扫，
  **不要改数字迁就 runner**。
- 两条路径都跑不了时（如沙箱故障），如实写「**未验证**」——禁止声称「通过」。

---

## 1. 两条路径，先选对再往下看

|          | 路径 A：`dotnet test`                    | 路径 B：tstrun 反射 runner              |
| -------- | --------------------------------------- | --------------------------------------- |
| 跑测试   | `dotnet test`（**首选**）               | `dotnet .tmp-test/tstrun/bin/Debug/net10.0-windows/tstrun.dll` |
| 出覆盖率 | `dotnet test --collect`（§2.1）         | `coverlet.console` 驱动 tstrun（§2.2）  |
| 适合谁   | 人终端 + **智能体沙箱（2026-10-22 起可用）** | 覆盖率采集的沙箱兜底 / 定制过滤         |

### 1.1 两条路径的关系（2026-10-22 更新）

**历史**：`dotnet test` 会拉起 `testhost.exe`（apphost），它在当时的沙箱里加载 `hostfxr.dll`
时报 **`0x80070005`**（拒绝访问）。沙箱开关、`TEMP` 指向、进程位数、目录 ACL、是否管理员
—— 全部试过并排除，于是改用反射 runner（§3）绕开 testhost。

**现状（2026-10-30 实测）**：本机 `dotnet test` **已可直接运行**，minute-byte-column、
file-transfer-progress、device-scoped-file-browser-path、filter-msgfilter-zero-copy 与
filter-msgfilter-block-scan 相继合入后 **756 条全过（756/756）**（2026-11-01 remove-join-merge 后为 **744/744**）；
此前 663 条中 656 条通过、7 条为沙箱伪失败（§3.5），2026-10-23 首测时 662/662 全过：

```bash
dotnet test tests/logcat.Tests/logcat.Tests.csproj -c Release --no-build -v minimal
```

且**多语言用例在 `dotnet test` 下会被真正执行**——这与 tstrun 有个关键差异：
tstrun 反射调用不 `await` 返回 `Task` 的方法，仓库既有 3 个 `async Task` 用例
（`FilterEngineTests` 2 个 + `LogDocumentTests` 1 个）在 tstrun 下**从未真正跑过**（假绿），
而 `dotnet test` 会 `await` 它们。**故现在优先用路径 A。**

tstrun 仍保留的价值：

1. **覆盖率采集**：`dotnet test --collect` 走同一个 testhost，若某台机器上仍起不来，
   改用 §2.2 的 coverlet console 驱动 tstrun；
2. **类名过滤**：`tstrun <类名子串>` 快速只跑一类（`dotnet test` 需 `--filter`）；
3. **控制台输出**更干净，排查单条用例时好用。

> ⚠️ tstrun 口径与 `dotnet test` 口径**可能不一致**：tstrun 不支持 `MemberData`/`ClassData`
> 驱动的 Theory（会静默跳过），也不 `await` async 用例。**报告结果时务必声明用的是哪条路径**。

---

## 2. 出覆盖率

### 2.1 路径 A：终端 collector 模式

> 2026-10-11 起 `TMP`/`TEMP` 必须指到仓库内目录（见 §0 根因 2），否则 hits 文件写不进
> AppData\Temp，产出空报告。PDB 问题已在 csproj 修复，无需再传 `-p:DebugType`。

```powershell
$env:TMP = $env:TEMP = "<仓库根>\.tmp-test\covtemp"   # 目录需存在，先 mkdir
dotnet test tests/logcat.Tests/logcat.Tests.csproj `
  --collect:"XPlat Code Coverage" `
  --settings tests/logcat.Tests/coverlet.runsettings
python tests/coverage-report.py
```

产物在 `tests/logcat.Tests/TestResults/<guid>/coverage.cobertura.xml`，
`coverage-report.py` 会自动取**最新**那个 xml，输出 `tests/coverage-report.html`。

> 注意区分：这里的 `TMP`/`TEMP` 只影响 **coverlet 写 hits 文件**（testhost 进程级）；
> 测试自身的临时文件早已迁到 `tests/logcat.Tests/tmp/`（`TempRoot`），与环境变量无关。

### 2.2 路径 B：coverlet console 驱动 tstrun（绕开 testhost）

用 `coverlet.console` 全局工具，把 tstrun 当 runner 注入 Profiler。

```bash
dotnet tool install -g coverlet.console        # 需 ≥ 10.1.0，见 §5.3
```

```bash
cd .tmp-test/tstrun/bin/Debug/net10.0-windows
coverlet logcat.Tests.dll \
  --target tstrun.exe --targetargs "" \
  --include "[logcat]*" \
  --exclude "[logcat]logcat.ApplicationConfiguration" \
  --exclude "[logcat]logcat.Forms.*" \
  --exclude "[logcat]logcat.Controls.*" \
  --exclude "[logcat]logcat.frmMain" \
  --exclude "[logcat]logcat.frmMain.*" \
  --exclude "[logcat]logcat.Program" \
  --exclude "[logcat]logcat.Services.AdbManager" \
  --exclude "[logcat]logcat.Services.AdbManager.*" \
  --exclude "[logcat]logcat.Services.LogcatStream" \
  --exclude "[logcat]logcat.Services.LogcatStream.*" \
  --exclude "[logcat]logcat.Services.ClipboardHelper" \
  --exclude "[logcat]logcat.Services.StartupLog" \
  --exclude "[logcat]logcat.Services.DpiFix" \
  --exclude "[logcat]logcat.Services.DpiDiag" \
  --exclude "[logcat]logcat.Services.Loc" \
  --exclude-byfile "**/Forms/*.cs" \
  --exclude-byfile "**/Controls/*.cs" \
  --exclude-byfile "**/frmMain.cs" \
  --exclude-byfile "**/frmMain.Designer.cs" \
  --exclude-byfile "**/Program.cs" \
  --exclude-byfile "**/Services/DpiFix.cs" \
  --exclude-byfile "**/Services/DpiDiag.cs" \
  --exclude-byfile "**/Services/Loc.cs" \
  --format cobertura \
  --output <仓库>/tests/logcat.Tests/TestResults/coverlet-tstrun/coverage.cobertura.xml
```

然后同样跑 `python tests/coverage-report.py` 出 HTML（它按 mtime 取最新 xml，不关心是哪个 collector 产的）。

> **⚠️ 头号坑：`--exclude` 不接受逗号分隔。**
> 上面写成了 **15 个 `--exclude` + 8 个 `--exclude-byfile`**，这不是啰嗦，是**必须**这么写。
> 把 `coverlet.runsettings` 里 `<Exclude>` 的逗号串原样塞进单个 `--exclude`，coverlet console
> 会把它当成**一个**无效表达式，然后**全部静默失效** —— 不报错、不警告，只是 UI / 集成层全被算进
> valid，行覆盖率假降到 **23.85%**。详见 §5.1。

---

## 3. 智能体沙箱怎么跑测试：tstrun 反射 runner

`.tmp-test/tstrun` 是仓库内的 runner 工程（`.tmp-test/` 已被 `.gitignore` 忽略；`%TEMP%` 会被清、且被沙箱/安全软件盯防，故不放那里）。
它是控制台工程，通过 `ProjectReference` 引用 `logcat.Tests.csproj`，
反射扫出所有 `[Fact]` / `[Theory]` 方法直接调用，绕开 testhost。

### 3.1 重建方法

`tstrun.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseWindowsForms>true</UseWindowsForms>
    <AssemblyName>tstrun</AssemblyName>
    <NoWarn>$(NoWarn);CS8600;CS8602;CS8604;CS8625;CS0618;xUnit1004</NoWarn>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Runner.cs" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="<仓库绝对路径>/tests/logcat.Tests/logcat.Tests.csproj" />
  </ItemGroup>
</Project>
```

`Runner.cs`（顶栏语句，**不要**写 `static int Main`）：

```csharp
using System.Reflection;
using System.Text;

// 用反射直接跑 xUnit 测试方法，绕开 testhost 加载 hostfxr.dll 失败（0x80070005）的问题。
// 同时处理 [Fact]（无参）与 [Theory] + [InlineData]（按数据行展开）。
// 注意：早期版本只匹配 FactAttribute，导致 20 个 Theory 全部被跳过、用例数对不上，
//       排查时务必用「用例总数」交叉验证 runner 本身没漏。
// 用法：tstrun [类名过滤子串，可逗号分隔多个，任一命中即入选]   不带参数则跑全部测试类。
// 环境变量 TSTRUN_FAIL_FAST=1：首个失败用例后立即停止（变异测试 KILLED 快速判定用）；
//       未设置时行为与旧版完全一致。

var asm = typeof(logcat.Tests.FilterEngineTests).Assembly;
var filter = args.Length > 0 ? args[0] : null;
var filters = filter?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
bool failFast = Environment.GetEnvironmentVariable("TSTRUN_FAIL_FAST") == "1";

int pass = 0, fail = 0, skipped = 0;
var failures = new List<string>();
bool aborted = false;

foreach (var type in asm.GetTypes().Where(t => t.IsClass && !t.IsAbstract).OrderBy(t => t.Name))
{
    if (aborted) break;
    if (filters != null && !filters.Any(f => type.Name.Contains(f, StringComparison.OrdinalIgnoreCase))) continue;

    var tests = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Where(m => m.GetCustomAttributes().Any(a =>
               a.GetType().Name is "FactAttribute" or "TheoryAttribute"))
        .OrderBy(m => m.Name).ToList();
    if (tests.Count == 0) continue;

    foreach (var m in tests)
    {
        if (aborted) break;
        bool isTheory = m.GetCustomAttributes().Any(a => a.GetType().Name == "TheoryAttribute");

        // InlineData 展开
        var datas = new List<object[]>();
        foreach (var a in m.GetCustomAttributes())
        {
            if (a.GetType().Name != "InlineDataAttribute") continue;
            if (a.GetType().GetMethod("GetData")?.Invoke(a, new object[] { m }) is IEnumerable<object[]> rows)
                datas.AddRange(rows);
        }
        if (datas.Count == 0)
        {
            if (isTheory) { skipped++; continue; }   // MemberData/ClassData 等未支持的情形
            datas.Add(Array.Empty<object>());
        }

        foreach (var data in datas)
        {
            if (aborted) break;
            var argText = string.Join(", ", (object[])data);          // 坑②：先提到变量
            string label = $"{type.Name}.{m.Name}({argText})";
            object inst = null;
            try
            {
                inst = Activator.CreateInstance(type);
                m.Invoke(inst, (object[])data);
                pass++;
            }
            catch (Exception ex)
            {
                var real = ex is TargetInvocationException tie ? tie.InnerException : ex;
                fail++;
                var msg = real is Xunit.Sdk.XunitException xe
                    ? xe.Message
                    : real.GetType().Name + ": " + real.Message;
                failures.Add(label + "\n    " + msg.Replace("\r\n", "\n    "));
                if (failFast) aborted = true;
            }
            finally
            {
                (inst as IDisposable)?.Dispose();
            }
        }
    }
}

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine($"passed: {pass}   failed: {fail}   skipped(theory w/o InlineData): {skipped}");
if (fail > 0)
{
    Console.WriteLine("\n================ FAILURES ================");
    foreach (var f in failures) Console.WriteLine("  " + f);
    Environment.Exit(1);
}
```

跑法：

```bash
cd .tmp-test/tstrun
dotnet run                 # 全量
dotnet run FilterEngine    # 只跑类名含 FilterEngine 的
dotnet run "LogDocument,FilterEngine"   # 逗号分隔多个子串，任一命中即入选
TSTRUN_FAIL_FAST=1 dotnet run           # 首败即停（变异测试脚本用；日常勿开，会看不到失败总数）
```

### 3.2 三个必须知道的坑

1. **顶栏语句文件里不能写 `static int Main`** —— 报 CS7022，Main 被忽略，程序**静默什么都不跑**，
   表现为「跑完没输出、看起来像全绿」。**假绿**比报错危险得多。
2. **插值表达式里内嵌 `"null"` 会提前闭合字符串**：`$"...{string.Join(", ", data)}..."` 在某些参数下
   会被解析成 `string.Join(", ", data) + "..."` 之类，`string.Join` 的结果要**先提到变量**再插值。
3. **只匹配 `FactAttribute` 会漏掉全部 Theory** —— 所以要么同时匹配 `TheoryAttribute` 并按
   `[InlineData]` 展开（上面的代码已做），要么**必须**拿「用例总数」与已知基线（当前 **852**）交叉验证
   runner 本身没漏。这个交叉验证是每次改 runner 后的必做动作。

### 3.3 本地化相关的两个特殊坑（2026-10-07）

**① 源码扫描类测试必须先断言「扫到了东西」**

`LocTableTests` 要扫源码找漏翻的中文键。扫描器的成员访问解析一旦写错（`Loc.T` 后面是 `.` 不是 `(`），会返回 **0 条**，而"没有漏翻"照样成立——**假绿比报错危险**。所以必须配一条硬断言：

```csharp
int scanned = 0;
foreach (var _ in EnumerateLocCallKeys(repoRoot)) scanned++;
Assert.True(scanned > 300, $"只找到 {scanned} 个调用点，解析逻辑大概率坏了");
```

改扫描逻辑后，**务必跑一份独立 Python 脚本交叉验证条数**，别信"全绿"。

仓库路径用 csproj 的 `AssemblyMetadata("RepoRoot")` **编译期注入**，不要靠 `AppContext.BaseDirectory` 往上找 `logcat-viewer/logcat.csproj`——tstrun 跑在 `.tmp-test/` 下，bin 输出目录与仓库根的相对层级会随 runner 重建变化，靠往上找不稳。

**② `LocTableTests` 只扫 `Loc.*` 调用点，不扫所有字面量**

诊断日志（`StartupLog` / `DpiDiag` / `_logger`）与 `ArgumentException` 消息按约定**保持中文不翻译**。全量扫字面量会把它们误判成漏翻，测试与设计直接冲突。

### 3.4 CI 语言环境：本地全绿、推送就红 ⚠️

**这是本项目最坑的一类 CI 失败，已经翻车 4 次**（2026-10-09 ×2、10-10、10-18）。

#### 机制

界面语言由 `logcat-uicore/Services/Loc.cs` 的**静态构造期**按 `CultureInfo.CurrentUICulture` 探测；
仓库根没有 `settings.json` 时完全由**系统区域**决定：

| 环境       | CurrentUICulture | 界面语言 |
| ---------- | ---------------- | -------- |
| 本地开发机 | zh-CN            | 中文     |
| GitHub runner | en-US         | 英文     |

于是：**被测输出走 `Loc.T`/`Loc.F` 的用例，若把期望值硬编码成中文，本地全绿、CI 必红。**
本地反复跑也复现不出（因为本机就是中文），只有推送后才炸。

历史记录：

- 10-09 ×2、10-10：412 条里挂 9 条（7 条是语言），典型是 `CommandStore` 内置分类
- 10-18：623 条里挂 1 条，fileproxy 越界消息（异常消息走 `Loc.F`，断言找中文「越界」，
  实际消息是英文 "out-of-range"）

#### 三道防线（2026-10-22 起）

**① 写用例时锁语言**（治本）

用 `LangScope.InZh` / `LangScope.InEn` **双语各「生成 + 断言」一遍**：

```csharp
LangScope.InZh(() => Assert.Contains("越界", ex.Message));
LangScope.InEn(() => Assert.Contains("out-of-range", ex.Message));
```

三条硬要求：

1. **必须双语**——只测一种语言的话，被测代码退回硬编码中文仍然全绿，抓不住回归；
2. **生成必须在语言作用域内**——消息是抛出那一刻按当时语言算出来的，事后切语言拿到的是旧语言的串；
3. **用到语言 helper 的测试类必须打 `[Collection(LangCollection.Name)]`**——
   `Loc._lang` 是进程级静态，xUnit 默认按类并行，不串行就会互相抢语言。

**② 扫描器测试**（预防，已入库）

`tests/logcat.Tests/CiLocaleAssertionTests.cs` 扫测试源码，找出
「硬编码中文断言 + 该字面量**恰好是本地化资源键** + 所在方法未锁语言」的高危组合并报错。
已实测：注入一个 CI 会挂的写法后，该测试立刻变红；同一个变异在英文界面下也确实失败
（`Expected "（无过滤）"` vs `Actual "(no filter)"`）——**防线与 CI 真实故障同源**。
（区域切换方式已于 2026-10-24 更正，见 ③。）

> 判据里的「恰好是资源键」很关键：普通数据字面量（如 "别名"、"正文"）与界面语言无关，
> 不该被拦。豁免清单在 `KnownLocaleSensitiveAssertions`，每条都要写理由。

**③ 本地用 en-US 跑一遍**（兜底）

```bash
bash scripts/run-tests-en-us.sh
```

> ⚠️ 本机若 `bash` 解析到未安装的 WSL（报「适用于 Linux 的 Windows 子系统…」），
> 可在 PowerShell 里直接等价执行——脚本的核心就是下面第一行：
>
> ```powershell
> $env:LOGCAT_FORCE_UI_CULTURE='en-US'; dotnet test tests/logcat.Tests/logcat.Tests.csproj -c Release --no-build -v minimal
> ```

脚本设 `LOGCAT_FORCE_UI_CULTURE=en-US`，由测试程序集 `TestCultureBootstrap`
（`[ModuleInitializer]`）在**测试进程内**把 `CurrentCulture` / `CurrentUICulture`
钉成 en-US，再跑 `dotnet test`。

> ⚠️ **不要靠 `LANG` / `LC_ALL` / `DOTNET_CLI_UI_LANGUAGE`**（2026-10-24 实测更正）：
> Windows 上这三个变量**都不改变** `CultureInfo.CurrentUICulture`——不设是 zh-CN，
> 设了仍是 zh-CN；它们只在 Unix 生效。此前「设了环境变量 → en-US」的结论是错的，
> 会让 Windows 上的英文复现实际跑中文却显示全绿。
>
> 硬断言：`CiLocaleAssertionTests.强制界面语言已生效` 校验测试进程的实际区域；
> 初始化器失效时立即红，不会静默退回中文。
>
> CI 的 Test 步骤同时声明 `LOGCAT_FORCE_UI_CULTURE`（真正生效）与 `LANG` 等
> （仅供 Unix runner），把「隐式依赖 runner 区域」变成「可验证的显式契约」。

### 3.5 智能体沙箱：`Directory.Delete` 静默失效（2026-10-24 实测）

在部分 IDE 托管的执行沙箱里，.NET 子进程的目录删除会**返回成功但目录仍在**：

- `Directory.Delete(dir, recursive: true)` 不抛异常，随后 `Directory.Exists(dir)` 仍为 true，
  进程退出后目录依然存在；
- 同一路径用 PowerShell `Remove-Item` 能删掉；往 `%TEMP%` 创建目录则直接抛
  `UnauthorizedAccessException`（沙箱限制）；
- 后果：spool / 临时目录清理相关 **7 条**「删完断言目录不存在」的用例会伪失败
  （`SpoolCleanupTests` / `SpoolLivenessTests` / `FileProxyLoopbackTests` /
  `LogDocumentProxiedTests` 等）。

**不是仓库缺陷**：普通终端与 CI 不受影响。看到这批红先确认是否在沙箱里跑，
必要时换本机普通终端复跑；不要据沙箱结果改代码。

---

## 4. 统计口径（为什么不是 100%）

覆盖率**只统计可单测的业务逻辑层**，规则集中定义在 `tests/logcat.Tests/coverlet.runsettings`，
排除两类代码：

| 排除类别              | 成员                                                                                                         | 理由                                                                                                                                                                                           |
| --------------------- | ------------------------------------------------------------------------------------------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **UI 层**             | `Forms.*` / `Controls.*` / `frmMain` / `Program` / `DpiFix` / `DpiDiag` / `Loc` / `ApplicationConfiguration` | WinForms 依赖消息泵与 STA 线程，构造/布局/事件绑定单测成本极高、收益极低；DPI 兜底要真实窗口与缩放链路才有意义；`Loc` 的控件树遍历、字体替换与像素测量同理。行业惯例交给 UI 自动化或人工验收。 |
| **系统 / 设备集成层** | `AdbManager`（只测了两个纯函数）/ `LogcatStream` / `ClipboardHelper` / `StartupLog`                          | 分别需要真实 adb+设备、设备流、Windows 剪贴板（共享资源+UI 线程）、固定写用户目录（会污染真实数据）。                                                                                          |

> `LocTable.*`（纯数据译文表）**刻意留在口径内** —— 它没有控件依赖，且是 i18n 的核心验证面。
> 判断标准是「**这条代码能不能在无窗体条件下被有意义地验证**」，不是「这个文件新不新」。

所以报告里的 ~100% 指的是「**该测的都测到了**」，不是「整个程序集都测到了」。
看覆盖率变化要**按类对比**，别只看总数字 —— 总数字持平完全可能掩盖了某个类的塌方。

> 2026-10-18 二次重采后的 86 行未覆盖清单（行号按当前 HEAD，按行号去重）：
> `AppSettings.cs:104`；`CommandStore.cs:80,306,311-315`；`CrashExtractor.cs:127,130,214,223`
> （Java 异常类型「第一个非空行不是已知命名空间」与包名校验的死支路）；
> `FavoritesStore.cs:91`；`FileProxyClient.cs:106,115,147,162,199-202,206,216,324,327-330`
> （连接/握手/收尾的异常与超时路径）；`FileProxyException.cs:55`；
> `FileProxyService.cs:151,204,207-210,212-215,217,260,304,352,363-367,374-380,393,400,450,461`
> （自动拉起编排的异常留痕、进程句柄观测与杀进程树的防御分支；`304`/`400` = 仅生产路径
> 调用的公开无参重载，`393` = `DefaultRoot` 初始化，`450` = 活性探测的外层防御 `catch`，
> `461` = `SpoolFileNameFor` 的非法路径分支）；
> `FilterEngine.cs:208`；`LogDocument.cs:160-165,630,647,738-740,882-884,1051`；
> `LogParser.cs:155,163-165`；`SingleInstance.cs:45`。
> fileproxy 未覆盖集中在「需要真实子进程/网络态失败」的拉起与连接错误路径，
> 构造代价与风险不匹配，与既往口径一致不硬凑；其余多为 10-07 旧基线就有的死支路。

---

## 5. 踩坑清单

### 5.1 coverlet console 的 `--exclude` 不接受逗号分隔 ⚠️

**症状**：把 runsettings 的逗号串塞进单个 `--exclude`，覆盖率从 ~98% 掉到 **23.85%**，且**无任何报错**。

**根因**：coverlet **collector** 的 `<Exclude>` 配置支持逗号分隔；
但 **console 命令行**的 `--exclude` / `--exclude-byfile` 每个参数只吃**一个**表达式。
逗号串被当成单个表达式、匹配不到任何东西，于是**一条都没排除** —— 静默失效。

**正解**：拆成 14 个 `--exclude` + 7 个 `--exclude-byfile`（见 §2.2）。

**自查**：出报告后先确认分母。`lines-valid` 应该是 ~1700 量级；
若是几千，说明排除规则没生效（UI 层的 ~9400 行灌进来了）。

### 5.2 cobertura XML 里同一行号会输出两次（行数与分支数都会翻倍）

同一行号的 `<line>` 条目在 coverlet 的 cobertura 输出里**重复出现两次**。后果有两条：

- 按类直接累加**行数**会翻倍；
- 累加每个 `<line>` 的 `condition-coverage` 里的分支数，**分支数同样翻倍**
  （实测：直接累加得 1,984 / 2,244，按行号去重后是真实的 **992 / 1,122**）。

行数翻倍曾把 `1,716/1,741` 显示成 `3,432/3,482`，**百分比恰好不变**，极具迷惑性
（`README` 的「Current status」行一度长期挂着这个错数，2026-10-04 才修正）。

`coverage-report.py` 已按行号去重（2026-10-03），**直接用它的输出**。
自己解析 xml 时务必按行号去重（`line_hits[num] = max(...)`），分支同理。

### 5.3 coverlet 必须 ≥ 10.1.0

6.0.4 在 .NET 10 上覆盖率**全 0**。当前 `logcat.Tests.csproj` 引用 `coverlet.collector` 10.1.0。
若换回旧版，会看到「测试全过但覆盖率 0%」的现象。

### 5.4 跑 `scripts/mutation-test.py` 前先确认工作区干净 ⚠️

该脚本按「内存保存变异前内容 → 跑完原地写回」恢复被变异的文件（2026-10-24 起不再用
`git checkout`——那条路会打回 HEAD、抹掉未提交改动，曾实测两次丢失）。但仍要求
工作区干净：并发编辑会污染编译与判定结果。
细节与完整审计见 [`UT-AUDIT.md`](UT-AUDIT.md)。

---

## 6. Rust core（`logcore`）

Rust core 的测试分两层，**口径与上表的 C# 数字互不相加**：

| 层 | 入口 | 说明 |
| --- | --- | --- |
| UT | `cargo test --workspace -- --test-threads=1` | crate 内 `#[cfg(test)]` 模块：路由九格、嗅探边界、sidecar 键与命中/降级、缓存维护、描述符、事件不变量、推入/排空/关闭、状态矩阵、FFI 导出 |
| IT | `dotnet test` 中的 `SourceAcquire*IT` | xUnit 经 C ABI 打真实 core：本地直读、sidecar、FakeProxy / `logcore-test-proxy.exe` spool、失败路径、取消、多源、启动维护、吞吐 |

**必须串行**：代理实例注册表与 `shutdown` 是进程全局状态，默认并行跑会出现互踩的伪失败
（实测默认并行 6 条代理用例失败，`--test-threads=1` 后全过）。报告 Rust 结果时写明
「串行口径 N 条」。本机实测：Rust UT **425/425**（串行，2026-11-24；`--workspace` 口径：core 392 + cabi 33）；
C# 侧绑定 UT + 经 FFI 的 IT 合计 **71/71**。

> 2026-11-24 **rust-core-query-handle**：Rust UT 373 → **425**（+52：core +45——查询句柄开启/定位、
> 双向分页、生命周期与一致性 38 条，结果集定位原语 6 条，状态矩阵 1 条；cabi +7——查询导出
> 开/读/定位/双向取页/释放与失败清空）；C# 915 → **923**（+8 经 FFI 的 `LogcoreQueryIT`）。
>
> 2026-11-23 **rust-core-filter-kernel**：Rust UT 285 → **373**（+88：过滤模型与校验 21 条、
> 结构化/tag 结果集 19 条、正文回读与大小写/空词 28 条、错误与分页/只读 20 条）；C# 侧未改，仍 **915/915**。
>
> 2026-11-22 **rust-core-result-set**：Rust UT 270 → **285**（+15：结果集模型与 `resolve`
> 12 条、页装配恒等/非恒等与页头断言 3 条），用例总数变化仅此一处；C# 侧未改，仍 **915/915**。

**前置**：C# IT 要求先 `cargo build`（产出 `logcore/target/debug/logcore.dll`
与 `logcore-test-proxy.exe`，探测顺序见 [`BUILDING.md`](BUILDING.md)）。缺 dll 时 IT 会以
`LogcoreUnavailableException` 明确失败，而不是静默跳过。

**覆盖率**：Rust 覆盖率是独立口径（`cargo llvm-cov`），不并入 C# 的 94.26% 统计。
本机实测（2026-11-24，串行，`rust-core-query-handle` 同批重采）：行 **92.78%**（8,773 / 9,456）、
区域 92.90%、函数 91.50%；新增 `query.rs` 行覆盖 99.52%。
`crates/cabi/src/bin/test_proxy.rs`（测试代理入口，0%）计入分母，剔除后库本体行覆盖 **93.10%**（8,773 / 9,423）；
库内最低是 `winserver.rs` 78.93%（监听线程的异常分支多由代理子进程侧触发，进程内测不到）。
采集命令：`cargo llvm-cov -- --test-threads=1`（需先 `cargo install cargo-llvm-cov` 并
`rustup component add llvm-tools-preview`）。插桩构建把测试代理建在 cargo 的 target 目录，
`test_proxy_exe()` 在测试可执行目录旁找不到时按 `cargo metadata` 的 `target_directory` 回退。

---

## 7. MCP 服务端（Node，`mcp/`）

MCP 服务端工程 `mcp/`（OpenSpec change mcp-node-shell）是独立的 Node 进程，
用例数与覆盖率**不并入**上方 C# / Rust 的合计口径：

| 入口 | 说明 |
| --- | --- |
| `npm --prefix mcp ci` | 按锁文件安装依赖（Node ≥ 20） |
| `npm --prefix mcp run build` | TypeScript 编译，产出在 `mcp/dist/`（已忽略，不入库） |
| `npm --prefix mcp test` | 编译后跑 `node --test dist/test/`（Node 内置测试运行器，无第三方 runner） |
| `npm --prefix mcp run test:coverage` | 同上并输出 Node 内置覆盖率（`--experimental-test-coverage`） |
| `npm --prefix mcp run harness` | 手工验收台：起一个网页把 stdio 服务端桥成 HTTP，可看工具清单、调工具、看原始 JSON-RPC 流量（需先 `npm --prefix mcp run build`） |

覆盖内容：契约信封与错误码（信封同源、错误码取值与命名族、区域无关性）、
握手与能力声明、工具注册与列举、未注册工具与未握手调用的拒绝路径、
自述工具五项字段、stdout 纯净性、端到端链路（子进程拉起）、客户端断开后的进程退出、
引擎无关性与依赖清单检查。

本机实测（2026-11-26）：**41 条用例，40 通过 / 0 失败 / 1 跳过**。
跳过项为「收到终止信号后退出」——Windows 不向子进程投递 POSIX 信号，
该用例仅在类 Unix 平台执行；断开标准输入触发退出是另一条用例，已在本机通过。

> 端到端用例以子进程方式拉起服务端并观测真实的标准输出、标准错误与退出码，
> 不做进程内替换；标准输出的每一行都逐行断言为合法协议消息。

> **Node 版本口径**：测试运行器参数用 glob（`dist/test/*.test.js`）而非目录——
> Node 22 的 `--test` 不接受目录参数（会当模块加载并报 `MODULE_NOT_FOUND`），
> Node 26 才支持。CI 钉 Node 22，故必须用 glob。
> 本机实测：**Node 22.23.5 与 Node 26.8.1 均为 41 通过 0 失败 1 跳过**。

---

## 8. 新 UI 宿主（`logcat-next/`）

新 UI 宿主（OpenSpec change avalonia-ui-shell）带独立测试工程 `Next.Tests`，数字与上表互不相加：

| 入口 | 说明 |
| --- | --- |
| `dotnet test logcat-next/Next.Tests/Next.Tests.csproj` | 互操作绑定与导出缺失、页布局 v1 解码与释放恰好一次、真实 core 端到端（plan/commit/轮询/行数/分页边界/空文件）、双语目录键集合与占位符、语义 token 完整性、视口取页范围 |

**前置**：同样要求先 `cargo build`（装载层按旧查看器同一探测顺序回退到
`logcore/target/debug/logcore.dll`，另支持 `LOGCORE_DLL` 与应用目录显式定位）。
双语目录以 JSON 文件随输出复制（`Strings\`），不用 EmbeddedResource——Avalonia
构建任务会把 EmbeddedResource 吸进 `!AvaloniaResources`，清单不可见（实测）。

本机实测（2026-11-24）：**Next.Tests 30/30**；主仓 `logcat.slnx` 全量 **915/915** 未受影响
（新工程与旧 viewer 完全并存，互不引用）。

---

## 9. 一句话速查

- 我在终端 → §2.1（`dotnet test --collect` + `python tests/coverage-report.py`）。
- 智能体沙箱 → §3 建 tstrun 跑测试、§2.2 用 coverlet console 出覆盖率。
- 数字看着不对 → §5，先查分母（§5.1）。
- 数字好看但心里没底 → [`UT-AUDIT.md`](UT-AUDIT.md) 跑变异测试。
- MCP 服务端（Node）→ §7，`npm --prefix mcp test`。
