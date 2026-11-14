# 源获取契约（读入链）探索笔记

- 状态：**待评审**（探索记录，未编码、未 propose；阻塞项已清零——P1 已由 A14 定案，见 §4.1）
- 日期：2026-11-08（第十三轮 2026-11-11：P1 收口为 A14——`feed` 行数组异步入队（丙-1 无上限队列）、core 落盘、stop / close 排空后收尾且进度可查。第十二轮 2026-11-11：A12「S7 定案——Rust 不读配置，宿主传快照」+ A13「本轮不改 C# 现有代码，保持可用」。第十一轮 2026-11-11：A11 采集改为「宿主经 FFI 推入完整行、core 解析 / 索引 / 落盘」，A10 作废，P1 改写为推入接口语义。第十轮 2026-11-11：S19 提升为 P1「增长文件的可见性语义」，该问题已由 A11 消解。第九轮 2026-11-11：A9「采集只属 UI，MCP 不接采集」+ A10「采集输入取丙——宿主取流落临时文件、core 只读增长文件」，Q3 关闭、当轮阻塞项清零（第十轮另立 P1）。第八轮：第一步范围定案 A8（文件读入含代理 + 数据采集输入），Q2 收敛。第七轮：拍 A7「本轮写 Rust」，Q1 定案。第六轮：验证改 UT/IT 两层、按新需求从零设计。前五轮：初稿 / 复核补漏 / 采集纳入 / S2 定甲 + 取消超大文件确认 / 重构为事实记录）
- 关联提交：无（未编码）
- 验收结论：不适用（未实施）

> 本文件只写两类内容：**已核对的事实**（带 `file:line`）与**维护者已拍的决议**。判断与推测集中在 §6、§7 并显式标注。
> 第六轮口径：验证**不参考既有验证**，完全按新需求从零设计（A6）。§2 现状事实、§8 既有结论均降为背景，不作为新需求或验收的判定基准。
> 第七轮口径：**本轮产出 Rust core 可运行切片**（A7），契约与实现同轮交付；「先定契约、C# 当参照、后换语言」的三步走作废。

## 0. 结论

把源获取（文件 + ADB 采集）从窗体里的两条异源入口收成一份**两段式契约**：UI 传「目标（本地路径 / 代理暂存，或采集会话）+ 配置快照」，Rust 决定路由 / 格式 / 编码，返回**三轴拆开的源描述**；UI 只做两件事——显示渠道、按列声明重建表头。采集的取流与设备语义按 A11 留在宿主（仅 GUI，A9：MCP 不接采集），宿主把完整行经 FFI 推给 core；core 负责解析、索引与采集文件落盘，不再读并发写入的增长文件。

验证按 A6 分两层：**UT 验基本逻辑、尽可能多覆盖；IT 验接口能否拿到正确数据**。两层都是对新契约的正向声明，不证明与旧实现等价。

按 A7，判定 / 装载逻辑**本轮就落 Rust**：UT 主体跑在 Rust 侧（cargo test），IT 经 FFI 直接打 Rust 切片；C# 不再产出参照实现，只做 FFI 绑定层。

第八轮补正：第一步范围由维护者定为「文件读入（包括代理读入）+ 数据采集输入」（A8）；唯一阻塞项随之从切片范围（Q2）换成**采集输入以什么形态进 core**（Q3）。

第九轮定案：**采集只属 UI，MCP 不接采集**（A9）；**采集输入 = 宿主取流落临时文件、core 只读「增长文件」**（A10，Q3 取丙）。Q3 关闭。

第十轮补正（已由第十一轮消解）：A10 把增长检测从 UI 定时器移到 core，但「一次 `appended` 承诺什么」没定义——它同时决定事件语义、spec delta 的采集段与 IT 判据，且现状存在 stop 尾巴缺口。当轮把它提为 P1（§4.1）。

第十一轮定案：**采集输入改为「宿主经 FFI 推入完整行，core 解析 / 索引 / 落盘」**（A11），不再让 core 读宿主正在写的增长文件；A10 作废。增长文件的可见性、stop 尾巴一类问题随之消失，P1 改写为**推入接口的提交语义**（§4.1）。

第十三轮定案（P1 收口 = A14）：`feed` 行数组**异步入队**（丙-1：无上限队列、不丢行），core 消化线程负责「解析 → 索引追加 → 落盘 → 发事件」；`stop` 与 `close` 均**排空后收尾**——stop 排空 + flush + 发 `stopped`，close 排空 + 释放句柄，排空进度经 §3.7 事件轮询可查（UI 接线时显示进度条，走完流程再退出）；落盘失败清空队列、发 `error`、后续 `feed` 返回 `FAILED`。阻塞项清零。

## 1. 已定案（不再讨论）

| 编号 | 决议 | 事实依据 | 落地约束 |
| --- | --- | --- | --- |
| A1 | **入口判定归 Rust**：UI 只传配置与文件名 | 探测必须先于关文档（`frmMain.cs:954` 注释）；DLT 判定环（读魔数失败回退代理再判）在 UI 侧无法表达 | `TextProbe` / `FileProxyDecision` 的判定权外迁；UI 不再自行探测 |
| A2 | **描述符三轴拆开**：`channel` / `format` / `encoding` 互相正交 | DLT 只动 format；GBK 只动 encoding | 不用单一 `sourceType` 枚举 |
| A3（原 S5） | **采集（ADB）纳入同一契约** | 采集是独立入口、不经过 `StartIndex`（`frmMain.cs:2094`）；与文件双向互斥（`:974`、`:2101`） | `AcquireRequest.kind = File / Device`；`channel = AdbCapture`；`appendMode = Appendable`；取流留 C#（A11），推入与落盘归 core |
| A4（原 S2） | **列集由 Rust 下发**（`columns[]`），C# 只渲染 | 列集现写死在 `LogListView` 静态常量；DLT 要 5 列，两处硬编码会分叉 | 静态 `COLUMNS`/`COL_*` 变运行时数据；列宽 / 导出 / 标记按 `id` 而非位置；首版固定 logcat 7 列 |
| A5 | **取消超大文件确认**（删 1 GB 阈值）：经代理一律整体暂存 | 确认块只在 `frmMain.cs:1100`–`:1109`；`fileproxy` spec 27 条 Requirement 无此条款（§2.1 注） | 删常量/判定/确认块/文案/断言/变异项（§3.10）；`SourcePlan.needsConsent` 删除 |
| S1 | **plan → commit 两段式** | 判定先于关窗；UI 需在「路由已知、装载未启」时重置界面；DLT 判定环 | plan 副作用止于「读头部 / 拉起代理 / open 拿 size」；两阶段各有取消口径 |
| S3 | **探测参数留 Rust 常量 + 测试注入点** | 8 KB 窗口、10% 阈值现为写死常量（`TextProbe.cs:18`、`:19`）；D14 规定 spool 与探测均不加设置字段 | 注入点只服务窗口与阈值两项，不暴露到设置界面 |
| A6 | **验证分两层，按新需求从零设计** | 维护者 2026-11-11 拍板：UT 验基本逻辑、尽可能多覆盖；IT 验接口是否能获取到正确的数据；**不参考既有验证，完全按新需求来做** | §3.11 整段重写为 UT/IT 规格；旧「契约等价」判据（852 条基线 / 场景逐条过 / 16 格真值表）作废 |
| A7 | **本轮产出 Rust core 可运行切片，IT 直接打 Rust 实现** | 维护者 2026-11-11 拍板：需要写 Rust（原 Q1 按丙定案），推翻原「任何 Rust 代码」非目标 | 契约 + Rust 切片同轮交付；C# 参照实现概念作废（S8 / S10 随之改写）；UT 主体移到 Rust 侧 cargo test；切片范围边界见 §4 Q2（已由 A8 收敛）；工程落位见 §4「随 propose 定」 |
| A8 | **第一步范围 = 文件读入（含代理读入）+ 数据采集输入** | 维护者 2026-11-11 指令；与 `RUST-CORE-REWRITE-SLICING.md` §15.9 R3「输入先行」同向 | DLT 解码不进本轮（S9 维持）；采集取流按 A11 留 C#（D1 设备侧例外显式化）；Q2 据此收敛 |
| A9 | **采集只属 UI，MCP 不接采集** | MCP 规范「不暴露 adb、进程控制、文件删除 / 写入等能力」（`openspec/notes/MCP-FUNCTIONALITY.md:143`） | `AdbCapture` 源只在 GUI 路径出现；采集相关描述符字段不要求 Node 侧实现；「第二宿主也能采集」不再构成 Rust 自取流的理由 |
| A10（**已由 A11 推翻**） | **采集输入取丙：宿主取流落临时文件，core 只读「增长文件」**（Q3 定案） | 维护者 2026-11-11 拍板；A9 使「一个 dll 双宿主都采集」的收益消失；与现状同形（`frmMain.cs:2103`、`LogcatStream.cs:79`–`:83`） | 仅留决策历史：`capturePath` 入参、core 自检增长、stop final drain 等口径随 A11 作废 |
| A11 | **采集输入取戊：宿主经 FFI 推入完整行，core 解析 / 索引 / 落盘** | 维护者 2026-11-11 指令：不读变化的文件、数据从接口传给 core、文件操作由 core 完成；与 D1「文件操作完全归 Rust」同向（§8） | `AcquireRequest.kind=Device` 只带 `tempDir` / `serial?`；`feed(handle, lines[])` 推入（原「同步推入」已由 A14 改写为异步入队）；core 创建并追加采集文件；`stop` 刷盘后保留文件；A9 下仅 GUI 宿主实现 `feed` |
| A12 | **Rust 不读配置文件**：配置一律由宿主传快照（S7 定甲） | 维护者 2026-11-11 指令 | `AcquireRequest` 携带配置快照的全部面（`mode` / `proxy` / `options`）；Rust 不感知 `settings.json`、存储路径与 SchemaVersion 迁移，不复制解析与默认值规则 |
| A13 | **本轮不改 C# 现有代码，保持可用** | 维护者 2026-11-11 指令 | Rust 切片、FFI 绑定与 C# 侧绑定层均为**新增**；`frmMain*` / `Services/*` 既有链路行为不动；旧链切流（S12）与旧链清理（§3.10）不在本轮，顺延至后续切流 change |
| A14 | **P1 定案：`feed` 行数组异步入队（丙-1），core 落盘，stop / close 排空后收尾** | 维护者 2026-11-11 指令（§4.1 四子问题逐一拍定：形态甲 / 模型丙 / 背压丙-1 / 收尾排空 + 进度） | `feed(handle, lines[])` 入队即返，**无上限队列、不丢行**；core 消化线程执行「解析 → 索引追加 → 落盘 → 发事件」；`stop` 排空 + flush + 发 `stopped`；`close` 同样排空后释放，排空进度经 §3.7 轮询可查、UI 接线时显示进度条（走完流程再退出）；`stop` 后 `feed` 返回 `STOPPED`；落盘失败清空队列、发 `error`、后续 `feed` 返回 `FAILED`，已提交行保留 |

## 2. 现状事实（HEAD 核对，背景）

> 本节为约束来源的背景事实，**不作新需求或验收的判定基准**（A6）。

### 2.1 文件入口：四维交叉判定

```
StartIndex(path)                                    logcat-viewer/frmMain.cs:952
 +-- (1) 模式轴   settings.FileProxyMode -> off/on/auto      FileProxyModes.Parse
 +-- (2) 探测轴   auto 才跑 TextProbe.LooksLikeText(8KB)
 |                -> FileProxyDecision.ShouldUseProxy(九格)
 +-- (3) 生命周期轴 停采集 -> StopWorker -> 关旧文档 -> 新代 gen
 +-- (4) 通道轴   useProxy ? 租约 -> 自动拉起代理 -> open(size)
 |                        -> spool（A5 后一律暂存，无大小确认） -> Build(displayPath=原路径)
 |                       : sidecar 身份命中? -> LoadLocalFileAsync -> BuildChunked
 |                          (LoadLocalFileAsync 在 frmMain.Sidecar.cs：命中 -> 一次到位；
 |                           未命中 -> frmMain.Load.cs 的分块渐进装载，逐块 publish 上屏)
 +-- 出口: ProxyChannel 三态 + FileProxyException 失败分类 + DirectOpenError 附注
```

对应的既有条款（「装载入口分流」「代理使用判定」「分流时序」「失败分类与错误消息」「渠道状态区」「乱码配置回退关闭」「重载」）都在 `openspec/specs/old/fileproxy/spec.md`：**27 条 Requirement / 59 个场景**（HEAD 与迁移前 `a301168^` 一致；旧稿「15 条 / 27 场景」系把 Requirement 数误作场景数，本轮更正）。

> 更正：初稿把「超大文件确认」列为 spec 条款，实际 27 条 Requirement 标题中无此项——它只是实现细节，A5 删除它不需要改 spec。

另两份 spec 同样约束获取链，本轮未逐条核对：`chunked-file-load`（8 条 Requirement / 15 场景）、`sidecar-index-cache`（7 条 / 18 场景）。

### 2.2 配置与判定

| 事实 | 依据 |
| --- | --- |
| 模式只认小写精确值 `off`/`on`/`auto`，其余一律 `Off` | `logcat-viewer/Services/FileProxyMode.cs:26` |
| 代理配置 5 键：`FileProxyMode` / `FileProxyPipeName` / `FileProxyLaunchFile` / `FileProxyLaunchArgs`（`{pipe}` 占位）/ `FileProxyLaunchTimeoutMs`（默认 5000） | `logcat-viewer/Services/LogcatSettings.cs:104`–`:112` |
| 代理使用判定九格真值表：`On` 恒真；`Auto` = 直接打开失败 **或** 嗅探判非文本；`Off` 恒假 | `logcat-viewer/Services/FileProxyService.cs:496` |
| 文本嗅探：头部 8 KB，只数 ASCII 控制字符（排除 `09/0A/0C/0D`，加 `7F`），占比**严格大于** 10% 判非文本；空文件判文本；UTF-16 判非文本 | `logcat-viewer/Services/TextProbe.cs:18`–`:53` |
| **探测先于关窗**：嗅探在停当前工作、关旧文档之前完成 | `logcat-viewer/frmMain.cs:954` 注释；`:963`–`:968` |
| 超大文件阈值 1 GB 写死不进设置，确认框在 `open` 与 spool 之间（**A5 删除**） | `Services/FileProxyService.cs:25`–`:26`、`:284`；`frmMain.cs:1100`–`:1109` |
| 代理连接一次性：`OpenSpoolAsync` → `SpoolCoreAsync` → 释放连接 | `frmMain.cs:1097`–`:1142` |
| 错误文案双语表驱动（`Loc.F` + `LocTable.FileProxyUi.cs`），代理侧错误原文透出不翻译 | `logcat-uicore/Services/LocTable.FileProxyUi.cs` |

### 2.3 生命周期与状态

| 事实 | 依据 |
| --- | --- |
| spool 目录带租约（活性锁 `.alive.lock` 依赖 Windows 文件共享语义），租约挂到文档上随 `Close` 释放 | `frmMain.cs:1091`、`:1136` |
| 代理文档显示路径 = 用户原路径，源路径 = 暂存文件 | `frmMain.cs:1125`–`:1134` |
| 渠道三态互斥：ADB 在途 > spool 在途或代理文档 > 直接读 > 空 | `logcat-viewer/Services/ProxyChannel.cs:29` |
| 渠道文案随语言切换重算，不缓存静态文案 | `ProxyChannel.cs:36` |
| UI 侧另有临时状态 `_proxySpooling` 参与渠道判定 | `frmMain.cs:1092`、`:1154` |
| 失败分类带附注：转代理前的直接打开异常挂在 `DirectOpenError` | `frmMain.cs:1060`–`:1068` |
| Reload 分岔：代理文档重走完整装载（含 spool），本地文档走增量 `Reload` | `frmMain.cs:1166` |
| 本地路径先试 sidecar 身份命中，命中则跳过解析 | `frmMain.cs:1035`–`:1057` |

### 2.4 采集入口（ADB）

| 事实 | 依据 |
| --- | --- |
| 独立入口，不走 `StartIndex`；前置 `StopWorker()` 并等待 `_docBusy` 后关旧文档 | `frmMain.cs:2094`–`:2115` |
| 与「打开文件」双向互斥：打开前先 `AdbStopCaptureAsync()`；开始采集前先 `StopWorker()` | `frmMain.cs:974`–`:977`、`:2101` |
| 临时文件 `<exe 目录>/Temp/logcat_live_<yyyyMMdd_HHmmss>.log`，`FileMode.Append` + UTF-8 无 BOM（带 BOM 会让首行解析失败） | `frmMain.cs:2103`；`Services/LogcatStream.cs:79`–`:83` |
| 取流命令固定 `logcat -v threadtime`；逐行读取，丢弃空行与横幅行（`IsBanner` / `beginning of` / `switch to`）；按 500 行或 150 ms 批量 flush | `Services/LogcatStream.cs:65`、`:92`–`:107` |
| 取流经 `AdbClient.ExecuteRemoteEnumerableAsync`（AdvancedSharpAdbClient 的 adb server 5037 通道），**不是**自拉 `adb.exe` 子进程 | `Services/LogcatStream.cs:70`、`:89`；`logcat-uicore/Services/AdbManager.cs:28`–`:29`、`:89`（`AdbSocket` 构造即连 5037） |
| 取流事件三件：`LinesReceived`（UI 只做计数）、`ErrorOccurred`（状态栏原文）、`Stopped`（复位按钮与定时器） | `Services/LogcatStream.cs:25`–`:27`；`frmMain.cs:2118`–`:2132` |
| 首次索引是**整份 `BuildAsync`**（非分块渐进），`_adbGen = -1` 保证首次走首次加载 | `frmMain.cs:2136`、`:2262` |
| 增量靠 **1 秒 WinForms 定时器** → `AdbAutoReload()` → `doc.Reload()`（长度比较 + 64 KB 头比较）→ `unchanged` / `appended` / `rebuilt` | `frmMain.cs:2244`–`:2300` |
| 索引与追加互斥：`_adbReloading` / `_adbIndexing` + `_docBusy` + `_workerGen` 代际守卫 | `frmMain.cs:41`–`:42`、`:2246`、`:2275` |
| 渠道优先级最高：`adbActive = _adbCapturing \|\| _adbIndexing` | `ProxyChannel.cs:29`；`frmMain.cs:1729` |
| 停止采集后文档仍在屏：可继续过滤/导出/「保存日志」，故 `stop` ≠ 关闭文档 | `frmMain.cs:2173`、`:2196`–`:2211` |
| 临时文件在停止后保留（「保存日志」就是复制它），仓库内无清理逻辑 | `frmMain.cs:2210` |
| 标题显示 `Loc.F("[实时采集] {0}", _adbTempPath)`，临时路径进用户可见位置 | `frmMain.cs:2267` |
| 「清空日志」（`logcat -c`）与采集互斥，共用同一条可用性状态机 | `frmMain.cs:2194`、`:2229` |
| 关窗时同步等待采集退出（上限 2 s） | `frmMain.cs:1788`–`:1792` |

### 2.5 格式侧（DLT 会给契约加什么）

| 事实 | 依据 |
| --- | --- |
| DLT 判定必须先于代理分派，且 DLT 恒本地解码 | `openspec/notes/DLT-FORMAT-AND-RUST.md` §4.1 |
| 读魔数失败倾向「回退代理 spool 取副本再判」 | 同上 K4 |
| 列集按格式切换：logcat 7 列 / DLT 固定 5 列；PID/TID 恒空 | 同上 §3.2、§3.5 |
| 中间个别记录损坏 → 跳过 + 计数继续；首条解析失败 → 不打开 | 同上 §3.4 |
| DLT 禁用自动重载，`Reload` 只重读临时文本 | 同上 §3.4 |
| 转换产物沿用 spool 目录与活性锁，关闭即删 | 同上 §3.4 |
| 编码探测**未实现**，现状硬编码 UTF-8；`TextProbe` 只做二进制/文本嗅探 | `Services/TextProbe.cs`；`RUST-CORE-REWRITE-SLICING.md` §3.1 |

### 2.6 仓库与测试现状

| 事实 | 依据 |
| --- | --- |
| 获取链代码全在 `logcat-viewer`（`frmMain` partial 三文件 + `Services/FileProxy*` + `ProxyChannel.cs`）；`logcat-uicore` 只出 `Loc` / `LocTable.*` / `SettingsIO` | 目录清单；`frmMain` / `frmMain.Load` / `frmMain.Sidecar` 三文件 |
| 本地路径两条子分支：① sidecar 命中 → 一次到位不过渐进编排；② 未命中 → 分块渐进装载 | `frmMain.Sidecar.cs:25`–`:57`、`frmMain.Load.cs:58` |
| sidecar 身份是 size + mtime + 首尾各 64 KB 的 FNV-1a 指纹（四要素）；取不到返回 `null`，放弃缓存不影响打开 | `Services/SidecarCache.cs:39`–`:85` |
| sidecar 命中失败全部静默降级回全量加载：不弹提示、不加开关，只写启动日志 | `frmMain.Sidecar.cs:36`–`:40`、`:69`–`:76` |
| sidecar 格式钉死 `FormatVersion = 1`、列宽 `{8,4,4,1,4,4,1,4}`；单份 1 GB、目录 4 GB 上限 + LRU | `Services/SidecarCache.cs:155`、`:163`；`SidecarCacheStore.cs:142` |
| 契约等价测试现成基座：`FakeProxyServer` / `FakeAdbSocket` / `TestHelpers` | `tests/logcat.Tests/`（`FileProxyLoopbackTests`、`FileProxyLaunchTests`、`FileProxyProtocolTests` 已在用） |
| 现有测试基线 **852 条**全过，行覆盖 94.26%、分支 88.64%、方法 92.77% | `docs/TESTING.md` §0（A6 后仅作背景，不作判据） |

## 3. 契约形状（当前草案）

> 草案按 A6 定位为**新需求声明**：字段取舍不再以「能否表达现状」为判据，改以 §3.11 的 UT/IT 可验性为准。表中「现状对应」列仅为约束来源，不是必须复刻的行为。

### 3.1 两段式

```
UI                                   Rust
+-------------------+               +------------------------------+
| 收集配置 + 文件名 | --plan----->   | 判定：探测 / 拉起代理 / open |
| (settings 快照)   |               | （不写盘、不建索引、不改状态）|
|                   | <---plan----   | 返回 SourcePlan              |
| 关旧文档/停采集   |               +------------------------------+
|                   | --commit---->   spool -> 解码 -> 建索引
|                   | <--事件流----    stage/progress/warning
|                   | <--desc------    SourceDescriptor
+-------------------+               +------------------------------+
```

### 3.2 `AcquireRequest`（UI → Rust）

| 字段 | 内容 | 现状对应 |
| --- | --- | --- |
| `kind` | `File` / `Device`（A3） | `StartIndex(path)` / `AdbStartCapture()` |
| `path` | 用户意图路径原样（含 UNC / `\\?\` 长路径） | `StartIndex(path)` |
| `device` | `{serial?}`：仅来源标注 / 排障，不参与判定；采集会话由 `kind=Device` 开启，core 在 `options.tempDir` 下创建采集文件（A11）。取流、设备枚举与选择全留 C# | `frmMain.cs:2094`；`SelectedSerial()` |
| `mode` | `off` / `on` / `auto` | `FileProxyModes.Parse` |
| `proxy` | `pipeName`、`launchFile`、`launchArgs`、`launchTimeoutMs` | 设置 5 键中的 4 键 |
| `options` | 嗅探窗口 / 阈值；`tempDir`（S17；采集文件也落此） | 现为写死常量（S3） |
| `cancel` | 原子取消标志 | `CancellationTokenSource` |

`feed` 是 commit 之后的独立调用（`feed(handle, lines[])`），不属于 `AcquireRequest`；宿主在采集后台线程调用，入队即返（A14），见 §3.6。

配置一律来自宿主快照（A12）：`mode` / `proxy` / `options` 即快照的全部面；Rust 不读设置文件，不感知存储格式与默认值规则。

### 3.3 `SourcePlan`（Rust → UI，无装载副作用）

| 字段 | 内容 |
| --- | --- |
| `displayPath` | 标题/MRU 用路径（DLT 时为源 `.dlt` 路径） |
| `route` | `LocalDirect` / `ProxySpool` / `LocalDecode` / `DeviceStream` |
| `estimatedSize` | 代理路径来自 `open.size`；本地来自文件信息；采集为**未知**（不参与任何大小判定） |
| `probeEvidence` | 探测字节数、非文本占比、命中分支（排障用，UI 不展示） |

无 `needsConsent`：A5 后 plan 不返回宿主裁决项。将来若出现新裁决需求，以独立字段加入，本阶段不预留。

`kind=Device` 的采集文件路径在 commit 时生成，plan 的 `displayPath` 为空；标题在拿到描述符（`sourcePath`）后再设置。

### 3.4 `SourceDescriptor`（Rust → UI，commit 成功后）

| 轴/字段 | 取值 | 消费方 |
| --- | --- | --- |
| `channel` | `Local` / `FileProxy` / `AdbCapture` / `DltDecoded`（互斥） | 状态栏渠道区 |
| `format` | `Logcat` / `Dlt` / … | 列声明与时间语义 |
| `encoding` | `Utf8` / `Gbk` / … | 字节→文本（首版仅 Utf8） |
| `columns[]` | `{id, 语义键, 建议宽度}` | 表头/导出/标记全部按 `id` 索引（A4） |
| `identity` | `size` + `mtime` + 首尾指纹（可空） | sidecar 命中与缓存失效 |
| `sidecarHit` | bool | 状态文案、跳过重复落盘 |
| `appendMode` | `Appendable` / `ReloadOnly` / `None` | 增量追加（采集靠 `feed` 触发，不轮询文件） |
| `leaseKind` | `Ephemeral`（关闭即删）/ `Retained`（保留供保存） | 生命周期口径 |
| `timeSemantics` | `Original` / `Relative` | 时间列口径 |
| `leaseId` | 暂存/解码目录标识：`Ephemeral` 关闭即删，`Retained` 只解除句柄 | 生命周期与启动清理 |
| `warnings[]` | `{category, count}` | 跳过 N 条的提示（现状无此通道） |

`kind=Device` 的 `identity` 为空、`sidecarHit=false`（没有 sidecar）；`sourcePath` 指向 core 创建的采集文件，`leaseKind=Retained`。

### 3.5 顺序不变量

1. 判定顺序在 Rust 内固定：存在性/权限 → 格式（魔数/首行，含回退再判）→ 文本嗅探（仅 auto）→ 代理决策 → spool/解码 → 索引。
2. `format` 与 `encoding` **只在 open 时确定，运行期不变**。
3. 租约随句柄走，关闭顺序固定「停扫描 → 释放句柄 → 释放租约/删目录」。

### 3.6 采集在契约里的形状（A11：宿主推入完整行，core 解析 / 索引 / 落盘）

```
宿主（C#，仅 GUI）                          Rust core
+-- 选设备 + LogcatStream 取流 ------------> plan(kind=Device) -> SourcePlan{route: DeviceStream,
|   （取流 / adb / 设备语义全在 C#）        |      estimatedSize: 未知}
|                                           | commit -> 建采集文件（tempDir）+ 空文档
|                                           | descriptor{channel: AdbCapture, appendMode: Appendable,
|                                           |            leaseKind: Retained, loadMode: OneShot,
|                                           |            sourcePath: <core 建的临时文件>}
| -- feed(handle, lines[]) 异步入队 ------->  入队即返；core 消化线程：解析 -> 追加索引 -> append 落盘 -> 发 appended
| <-- appended(totalRows, delta) -----------  （UI 侧经事件轮询取，§3.7）
+-- stop：停流后把残余批次入队 ----------->  排空队列 -> flush 文件 -> 发 stopped；句柄与文件保留
+-- close -------------------------------->  排空（进度经 §3.7 可轮询）-> 释放句柄；文件按 S16 保留
```

不变量：

1. **取流不在 core**：core 不拉起 adb、不接触设备；`channel = AdbCapture` 是宿主声明的**来源事实**。
2. **`kind=Device` 跳过代理与嗅探**：来源已知为 logcat 文本，不做格式魔数 / 8 KB 嗅探 / 代理决策。
3. **core 不读并发写入的文件**：采集数据只经 `feed` 进入；采集文件由 core 创建、追加、flush，仅作保留物。
4. **同一时刻最多一个活动源**：采集与文件双向互斥；Rust 对第二个活动句柄返回 `BUSY`。
5. **`stop` 不关句柄、不删文件**：宿主停流、残余批次入队后调用；core 排空队列后 flush 并发 `stopped`；`leaseKind = Retained`。
6. **`feed` 只收完整行**：`ExecuteRemoteEnumerableAsync` 逐行产出、`LogcatStream` 已滤空行 / 横幅；core 不维护跨批残行。
7. **MCP 不接采集（A9）**：`feed` 只由 GUI 宿主实现，Node 侧不要求实现；事件面仍共用。

口径：

- `displayPath` 只给路径，`[实时采集]` 这层文案留 C#（B1 同源）。
- 落盘格式按现状复刻：UTF-8 无 BOM（`LogcatStream.cs:83`）、每行 CRLF（`StreamWriter.WriteLine` 默认 `Environment.NewLine`）；否则保存出来的日志字节与现状不同。
- 采集源的 `encoding` 由宿主解码确定（现状 `Encoding.UTF8`，`LogcatStream.cs:89`），描述符如实声明 `Utf8`；文件源的编码判定仍归 core（S6）。将来设备侧要 GBK 时改走字节推入（P1 乙）。
- `feed` 异步入队即返（A14，丙-1）：**无上限队列、不丢行**；core 消化线程执行「解析 → 索引追加 → 落盘 → 发事件」。宿主在采集后台线程调用，不碰 UI 线程；拥堵体现为队列积压，宿主永不阻塞。
- 落盘失败（磁盘满 / 权限）→ 发 `error` 并**清空队列**、停止跟随；后续 `feed` 返回 `FAILED`（不入队）；已提交（已落盘）行保留；不得静默丢数据。
- `stop` 之后 `feed` 返回 `STOPPED`（不入队）；`close` 排空后释放（进度经 §3.7 轮询可查，UI 接线时显示进度条），与 `feed` 竞争由 core 串行化。
- 「保存日志」在 stop 后由 UI 复制 `sourcePath`（无并发读写）；不新增 core 导出接口。

### 3.7 事件通道（轮询，不用回调）

| 事件 | 内容 |
| --- | --- |
| `stage` + `progress` | 阶段枚举 + 原子计数（spool 0–50%，索引 50–100%；stop / close 的排空阶段复用此通道上报排水进度） |
| `channelChanged` | spool 在途升为 `FileProxy`，替代 UI 侧 `_proxySpooling` |
| `appended(totalRows, delta)` | `feed` 批次**消化完成**后的增量，与提交一一对应（非与入队调用一一对应，A14）；只在行数增长时发 |
| `warning` | 跳过/容错计数增量 |
| `done` / `error(code, params)` | 错误码表统一，两宿主共用；保留「直接打开失败」附注位 |

硬约束：**不用回调**（进度用原子计数、取消用原子标志），Node 侧 koffi 不需要 threadsafe function；C# 侧 P/Invoke 轮询同样成立。

### 3.8 复核补正（契约字段不足以表达现状的四条）

| 编号 | 缺口 | 现状事实 | 需补 |
| --- | --- | --- | --- |
| G1 | 本地两条子分支未区分 | sidecar 命中 → 一次到位；未命中 → 分块渐进（`publish` 逐块上屏） | `SourceDescriptor.loadMode: OneShot / Progressive`；`stage` 语义随其分叉 |
| G2 | sidecar 身份只有 size+mtime 不够 | 四要素含首尾各 64 KB 的 FNV-1a | `identity` 补 `headHash` / `tailHash`；空 identity 需明确表达 |
| G3 | `rowCount` / `size` 缺失 | UI 直接消费 `loaded.RowCount`、`loaded.Size`、`loaded.LoadedFromSidecar` | 补 `rowCount`、`docSize` |
| G4 | 源路径 vs 显示路径只写了一半 | `doc.Path` = 显示路径，暂存路径是内部字段且不得外泄到标题/MRU/导出名 | 描述符区分 `displayPath` 与 `sourcePath` |

`identity` 为空 + `sidecarHit = false` 是**合法组合**（文件临时不可读），不得表达成失败。

### 3.9 契约边界未决（B1–B3，与 S 编号并列）

| 编号 | 议题 | 事实 |
| --- | --- | --- |
| B1 | 描述符的本地化文案归谁 | 现状错误与状态文案双语表驱动；Rust 出文案需复制译文表 |
| B2 | 描述符字段布局与两宿主 | Node（MCP）若读同一份描述符，需与 `RUST-CORE-REWRITE-SLICING.md` §4.4 的布局口径对齐；A7 后 C# 宿主同用一套布局；A9 后采集相关字段只要求 GUI 可解 |
| B3 | `column id` 稳定性 | `columns[]` 下发后标记/导出/缓存按 id 索引，id 必须跨版本稳定（不能是序号） |

### 3.10 A5 落地清单（去掉超大文件确认）

| 位置 | 动作 |
| --- | --- |
| `Services/FileProxyService.cs:25`–`:26` | 删 `HugeFileThreshold` |
| `Services/FileProxyService.cs:284` | 删 `NeedsHugeFileConfirm` |
| `frmMain.cs:1100`–`:1109` | 删确认块（含 `OperationCanceledException("用户在超大文件确认框取消")`）及 `LoadViaProxyAsync` 注释中的相关描述 |
| `logcat-uicore/Services/LocTable.FileProxyUi.cs:73`–`:75` | 删两条文案（「目标文件大小 {0}…是否继续？」与「文件代理」——后者仅 `frmMain.cs:1106` 引用） |
| `tests/logcat.Tests/FileProxyLoopbackTests.cs:517`–`:520` | 删阈值断言 4 条 |
| `scripts/mutation-test.py:293`–`:296` | 删 `NeedsHugeFileConfirm` 的 `>`/`>=` 变异项 |
| `openspec/specs/old/fileproxy/spec.md` | **不改**：27 条 Requirement 无此条款 |

行为差异仅一处：经代理打开 >1 GB 的文件不再询问，直接 spool；磁盘不足仍按既有口径透出 I/O 错误原文。

> A6 后：本清单属**旧链清理动作**，在切流（S12）时随 apply 执行，**不作为本轮验证依据**；删断言 / 变异项也不再牵动 §3.11 判据。基线数字变动按 `docs/TESTING.md` 与两份 README 的联动口径同批处理即可。

### 3.11 验证规格（UT / IT 两层，按新需求从零设计）

**口径**（A6）：不沿用 852 条基线、不逐条过 `fileproxy` / `adb-capture` 既有场景、不以 `ProxyChannelStates` 16 格真值表为判据。验证对象是**新契约声明的行为**；「通过」= 各层用例全过，与旧实现输出无关。两层按「是否触碰外部资源」划界：**UT 不碰文件 / 管道 / 进程 / adb，IT 必须真实走一遍源**。

**被测体**（A7）：**Rust core 最小切片**，经 FFI 调用，范围边界见 §4 Q2（已由 A8 收敛）。C# 侧不再产出参照实现，只做绑定层。

**UT —— 验基本逻辑，尽可能多覆盖**

Rust 侧（cargo test，主体，判定逻辑本轮即在此层）：

| 面 | 覆盖点 |
| --- | --- |
| 模式判定 | `mode` 三态（off/on/auto）与非法值回退 `Off`；判定九格（模式 × 直接打开结果 × 嗅探结果）全组合 |
| 文本嗅探 | 8 KB 窗口边界与截断；控制字符白名单（09/0A/0C/0D 排除、7F 计入）；占比**严格大于** 10%；空文件、UTF-16、全控制字符、临界的 10% 占比 |
| 路由决策 | `route` 四值（LocalDirect / ProxySpool / LocalDecode / DeviceStream）的进入条件、互斥与优先级；DLT 判定环（读魔数失败回退代理再判）各环节 |
| plan 无副作用 | 不写盘、不建索引、不改状态；`estimatedSize` 三来源（代理 `open.size` / 文件信息 / 采集未知）与「未知不参与大小判定」 |
| 描述符组装 | 三轴正交（`channel` × `format` × `encoding`）；`columns[]` 按 `id` 索引；`identity` 四要素（size / mtime / headHash / tailHash）与「空 identity + `sidecarHit=false`」合法组合；`appendMode` / `leaseKind` / `timeSemantics` / `loadMode` 取值约束；`displayPath` 与 `sourcePath` 分离 |
| 事件序列 | `stage` / `progress` 单调不回退；`channelChanged` 时机；`appended` 与**提交**批次一一对应且单调（A14：非与入队调用对应）；`warning` 计数累加；`done` 与 `error(code, params)` 互斥且终态唯一 |
| 推入语义（A11 / A14） | 队列不丢行（无上限）；批次消化完成后读回顺序与入队一致；空批不发事件；`stop` 排空后发 `stopped`、之后 `feed` 返回 `STOPPED`；落盘失败清空队列 + 发 `error` + 后续 `feed` 返回 `FAILED`、已提交行保留；`close` 排空后释放、排空进度可经 `progress` 轮询 |
| 取消与不变量 | plan 阶段取消、commit 阶段取消各自的资源释放；判定顺序固定；`format` / `encoding` 运行期不变；关闭顺序「停扫描 → 释放句柄 → 释放租约/删目录」；同一时刻最多一个活动源（第二个返回 `BUSY`） |

C# 侧（FFI 绑定层，随切片同轮交付）：

| 面 | 覆盖点 |
| --- | --- |
| FFI 封送 | 请求序列化 / 描述符反序列化 / 行数组封送（`feed`）/ 错误码映射 / 字符串与句柄生命周期（分配与释放配对） |
| 事件泵 | 轮询事件队列的取用、顺序与终态语义 |

手段：参数化 + 真值表 + 边界值 + 事件序列不变量断言。**不设条数上限**，以分支 / 组合覆盖为准；注入点沿用 S3（嗅探窗口与阈值）。

**IT —— 验接口能否拿到正确的数据**（经 FFI 调 Rust core；A9：MCP 不接采集，采集用例只在 GUI 侧）

| 用例 | 预置数据源 | 断言「正确数据」 |
| --- | --- | --- |
| 本地直读 | 已知内容的临时文件 | `route=LocalDirect`；commit 后按 `columns[]` 逐行逐列读回，值与预置相等；`rowCount` / `docSize` / `channel` 一致 |
| 代理 spool | `FakeProxyServer` 给定字节流 | 走 `ProxySpool`；读回内容与源字节逐字节一致；`displayPath` 为原路径、`sourcePath` 不外泄到标题/MRU/导出名 |
| DLT 解码 | 含首条 / 中间损坏记录的 `.dlt` | 记录数、`columns[]` 5 列、`warnings[]` 跳过计数与预置一致；首条损坏则不打开（A8：不进本轮，用例挂起） |
| ADB 采集 | 宿主经 FFI 推入 2–3 批已知行（模拟 `LogcatStream` 的批次），**待排空后**读回 | 行数与顺序和预置一致，每批 `appended` 的 delta 与批内行数一致；`stop` 排空后文档仍可读且行数完整；`sourcePath` 文件存在、UTF-8 无 BOM / CRLF、可被复制；`close` 排空后句柄释放、`leaseKind=Retained` 成立；core 全程未并发读该文件 |
| sidecar 命中 | 同一文件二次打开 | `identity` 一致、`sidecarHit=true`、读回数据与首次逐项相同 |
| 失败路径 | 拉起超时 / 中途断线 / 直接打开失败 / 第二个活动源 | `error(code, params)` 正确、保留「直接打开失败」附注；无残留句柄与租约 |
| 列集切换 | logcat 7 列 / DLT 5 列 | `columns[]` 与读回列数一致，按 `id` 取列不错位（依赖 B3 的 id 规则随切片定死） |

**判据**：读回的**行数、每列值、顺序**，以及描述符声明的 `rowCount` / `docSize` / `identity` / `warnings[]` / `channel` / `columns[]` 与**预置期望数据逐项相等**；不是「与旧实现输出一致」。数据源一律可复现（临时文件 / Fake 服务 / Fake socket / 推入行批次），不依赖真机 adb 与用户机器上的代理程序。

**两层分工**：UT 保证判定与组装逻辑无死角，IT 保证「按契约调一次，拿得回正确数据」。UT 失败不靠 IT 兜底，IT 失败不靠旧用例背书。

## 4. 待澄清（阻塞 propose）—— 0 个（P1 已由 A14 定案）

> Q1（写 Rust）由 A7 定案；Q2（切片范围）由 A8 收敛；Q3（采集输入形态）经 A10 定案、后由 A11 改写为「宿主推入完整行」；P1（推入接口的提交语义）已由 A14 定案（第十三轮，见 §4.1）。**阻塞项清零，可走 `/opsx:propose`**。

### P1：采集推入接口的「提交语义」（已定案 = A14，第十三轮）

> 原标题「P1（第十一轮改写）：……未定」；原 P0（增长文件可见性）随 A11 作废的历史见本节末尾的方案表「丁」行。

**定案（2026-11-11，A14）**：数据形态 = **行数组**（甲）；调用模型 = **异步入队**（丙）；背压 = **丙-1 无上限队列、不丢行**；落盘归属 = **core 写**（UTF-8 无 BOM / CRLF，复刻现状命名，路径经 `sourcePath`）；收尾 = **stop 排空 + flush + 发 `stopped`**，**close 同样排空后释放、排空进度经 §3.7 轮询可查**（UI 接线时显示进度条，走完流程再退出），`stop` 后 `feed` 返回 `STOPPED`；落盘失败清空队列 + 发 `error` + 后续 `feed` 返回 `FAILED`。以下原始论证保留作决策历史；其中「倾向甲（同步）」一段已被丙取代。

> 原 P1（增长文件可见性）随 A11 作废：core 不再读并发写入的文件。但推入是 FFI 新增的反向数据流，`feed` 的批次、同步性、背压、错误与 stop 收尾仍决定契约与 IT。

**问题一句话**：宿主把一批行交给 core 时，core 承诺什么——本批何时可读、失败怎么报、`stop` 收到哪、采集文件由谁写和何时可见。

**为什么它仍是第一顺位**

| 理由 | 事实 / 依据 |
| --- | --- |
| 推入是 FFI 的新方向 | A10 曾以「与文件入口相反、要定背压与生命周期」否掉字节推流；A11 改走完整行推入，这两件事仍未定 |
| 契约与验证被它钉死 | `feed` 的返回、`appended` 的口径、IT「推 N 行读回 N 行」、`stop` 后文档仍可读都取决于它 |
| 文件归属换了 | A11 要 core 建文件 / 追加 / flush；路径经描述符给 UI，「保存日志」依赖它 |
| 边界条件换了一批 | 批内多行、空批、stop 后 feed、落盘失败、feed 与 close 竞争；现状没有对应实现 |
| 跨宿主面 | A9 后只有 GUI 推入，但 C ABI 必须写清 `feed` 属 GUI-only，避免 Node 侧误实现 |

**待定的四个子问题**

1. **数据形态**：完整行数组，还是字节块（残行缓冲归 core）？
2. **调用模型**：同步返回提交结果，还是异步入队（需背压 / 溢出策略）？
3. **落盘归属**：core 写采集文件（A11 已定方向），路径 / 命名 / 失败口径怎么定？
4. **收尾**：`stop` 是否要求宿主先推完最后一批？stop 后 `feed` / `close` 的返回码？

**方案**

| 方案 | 数据形态 | 调用模型 | 背压 | 评价 |
| --- | --- | --- | --- | --- |
| 甲 行数组同步（**倾向**） | `string[]` | `feed` 同步返回结果 | 天然 | 与现状批次同形；无残行模型；编码由宿主定（现状 UTF-8） |
| 乙 字节块同步 | bytes | `feed` 同步返回 | 天然 | 保真最强、给将来 GBK / 二进制留路；core 要缓冲跨批残行并解码 |
| 丙 异步队列 | lines / bytes | `feed` 入队即返 | 需上限 + 丢弃策略 | 宿主不阻塞；多线程、溢出与生命周期复杂，v1 无必要 |
| 丁 读增长文件（A10） | 文件 | core 轮询 | 无 | 已被 A11 否；原 P1 的可见性 / 尾巴问题即由此而来 |

**倾向：甲**（历史，同步部分已被 A14 改为异步入队；以下口径中数据形态、落盘、错误与收尾框架维持，调用模型见定案）

1. **`feed` 同步**：调用返回本批提交结果（行数 / 错误码）；执行在宿主采集后台线程，天然背压，不碰 UI 线程。
2. **只收完整行**：`ExecuteRemoteEnumerableAsync` 逐行产出、`LogcatStream` 已滤空行 / 横幅（`LogcatStream.cs:92`–`:107`）；core 不维护残行，空批不发事件。
3. **本批立即可读**：`feed` 内完成「解析 → 索引追加 → 落盘 → 事件入队」；UI 下次轮询即可见，不需要额外的 flush 可见性约定。
4. **文件由 core 写**：在 `options.tempDir` 下建 `logcat_live_<时间戳>.log`，UTF-8 无 BOM、CRLF（复刻 `LogcatStream.cs:83` 与 `StreamWriter.WriteLine` 默认换行）；`sourcePath` 随描述符返回。
5. **错误可见**：落盘失败返回错误码并发 `error`，已提交行保留，停止跟随；不静默丢。
6. **收尾**：`stop` 前宿主停流并推完最后一批；`stop` flush 文件、发 `stopped`，文件 `Retained`；stop 后 `feed` 返回 `STOPPED`，`close` 与 `feed` 竞争返回 `CLOSED`。
7. **不加导出接口**：「保存日志」在 stop 后由 UI 复制 `sourcePath`（无并发读写）。

理由：与现状批次同形（500 行 / 150 ms，`LogcatStream.cs:14`–`:15`），不用在 core 里重建残行与解码模型；同步调用让背压免费；A9 下只有 GUI 实现，跨宿主成本已消失。

**选乙需接受的代价**：core 要维护跨批残行与 UTF-8 边界，等于把原 P1 的一半问题搬回来；若将来设备侧要 GBK 或原始字节，再升级为乙。

**仍未定、但不阻塞 propose**：`feed` 单批最大行数与 FFI 行数组布局（B2）；异步入队下消化线程的吞吐（500 行 / 批的解析 + 落盘）需在首个切片压测。

### Q2 已收敛（A8，2026-11-11）

维护者指令：第一步实现**文件读入（包括代理读入）**与**数据采集输入**。据此：

| 面 | 结论 |
| --- | --- |
| 进本轮 | 文件入口全链（存在性 / 格式判定 / 嗅探 → 代理决策 → spool → 直读 + `identity` 四要素）；采集输入（A11：宿主取流后经 `feed` 推入完整行，core 解析 / 索引 / 落盘） |
| 不进本轮 | DLT 解码（S9 维持，随 DLT change） |
| 不再适用 | 原 Q2 的甲 / 乙 / 丙三选一作废，范围以本表为准 |

### Q3 已定案，后由 A11 改写（第十一轮）：采集 = 宿主推入完整行，core 落盘

> 本节保留 A10 的决策历史。第十一轮 A11 推翻 A10：core 不再读增长文件，改为 `feed(handle, lines[])`；下表的「决议」「最小取证」与「遗留 design」均以 A11 / P1（§4.1）为准。

| 项 | 内容 |
| --- | --- |
| 决议（已被 A11 取代） | **丙**：C# `LogcatStream` 继续取流并写临时文件（`frmMain.cs:2103`、`LogcatStream.cs:79`–`:83`）；core 以 `kind=Device` + `capturePath` 打开该文件，当 `appendMode=Appendable` 的源，自检增长并推 `appended` |
| 理由（A10 口径，留史） | ① A9 后 MCP 不接采集，「一个 dll 双宿主都能采集」不再成立；② 与现状同形，字节保真天然维持（C21 不阻塞本轮，另立 change）；③ IT 用可控写入的本地增长文件，不需要 fake adb；④ 将来若改取流归属，只换生产者，描述符与事件面不变 |
| 被否 · 甲 | Rust 自取流（直连 5037 或拉 `adb.exe` 子进程）：要背设备语义、进程树、断连 / 重连，却只能拿到半套能力（枚举 / 自愈 / 监控仍在 `AdbManager`） |
| 被否 · 乙（第十一轮部分恢复） | 字节推流（FFI `feed(handle, bytes)`）：当时因「与文件入口相反、要定背压与生命周期、第二宿主用不到」被否；A9 后第二条消失，A11 改按**完整行**同步推入（§3.6）——否的是字节形态与异步模型，不是推入方向 |
| 遗留 design（已作废） | S19 随 A11 消解；新开问题见 P1（§4.1）；crate 落位与 FFI 形式见「随 propose 定」 |
| 最小取证（已作废） | A11 下改为：宿主经 FFI 推一批已知行 → 读回行数与顺序一致、`sourcePath` 文件可复制；不依赖真机 adb |

### 默认解清单（不再逐项拍）

| 编号 | 默认解 | 依据 |
| --- | --- | --- |
| S4 | 甲 首版留 `warnings[]` 空数组 | §6 |
| S9 | 甲 本契约先走，DLT 基于契约实现 `format = Dlt`（A8 维持：DLT 不进本轮） | §6 / A8 |
| S11 | 甲 新开 `source-acquire` capability（specs delta 落此处） | §6 |
| S15 | 乙 Rust 推增量（A8 后进本轮；A11 后由 `feed` 触发，不轮询文件） | §6 / A8 / A11 |
| S17 | 甲 UI 传 `options.tempDir` | §6 |
| S8 / S10 | **已由 A7 定案**（Rust 切片取代 C# 参照实现） | §1 |
| S6 / S7 / S12 / S13 / S16 / S18 | 按 §5 顺延 design 阶段 | §5 / §6 |
| B1 | 甲 C# 写文案（Rust 只出语义键与错误码） | §3.9 / S13 同源 |
| B2 | 随 FFI 形式在 design.md 定稿 | §4 随 propose 定 |
| B3 | 随 Rust 切片定死 `column id` 命名规则（IT「列集切换」用例的前置） | §3.11 |

### 随 propose 定（工程落位，不逐项请示）

| 项 | 默认方向 |
| --- | --- |
| Rust 工程落位 | 仓库内新增 crate（目录与命名 propose 时定），与 `logcat-viewer` 现有工程解耦 |
| FFI 形式 | C ABI 导出（`plan` / `commit` / `feed` / `stop` / `close` / 事件轮询 / 原子取消），与 §3.7「不用回调」一致；`feed` 为 GUI-only；C# P/Invoke 消费，Node 侧复用其余 C ABI |
| UT 覆盖率口径 | Rust 侧另立口径（cargo llvm-cov 等），不并入 C# 的 94.26% 口径；`docs/TESTING.md` 落地时新增 Rust 段 |
| 构建链 | cargo 产出 cdylib / staticlib，`docs/BUILDING.md` 增补构建步骤 |
| 描述符跨宿主布局（B2） | 随 FFI 形式在 design.md 定稿 |

## 5. design 阶段定（不阻塞 propose）

> 验证分层已由 A6 定为 propose 范围；S8 / S10 已由 A7 定案；S7 已由 A12 定案；S14 因 A7 产生连锁调整；S19 已随 A11 作废，推入语义见 P1（§4.1）。

| 编号 | 事实 | 可选 |
| --- | --- | --- |
| S6 编码判定位置 | 未实现，现状硬编码 UTF-8 | 沿用 C7：独立 change 立 spec，用 `utf8_cn_hits` / `gbk_cn_hits`；本契约只留 `encoding` 字段 |
| S7 UI 是否允许 Rust 读 `settings.json` | 设置存储与 SchemaVersion 迁移在 UI 侧 | **已由 A12 定案**：甲 UI 传快照，Rust 不读配置文件 |
| S12 旧判定链去留 | `frmMain.StartIndex` 是唯一实现（§2.1）；A7 后切流对象为 Rust 链路 | 甲 切流后立即删；乙 并存一个版本；丙 永久保留作对照。**本轮不切流（A13），去留顺延至切流 change 拍板** |
| S13 `startup.log` 归属 | 获取链每次分流/探测/拉起都写 `StartupLog`，是排障主依据 | 甲 Rust 出诊断事件、C# 写日志；乙 Rust 直接写文件；丙 分级（结构化事件 + C# 转写） |
| S14 sidecar 归属 | 读写含 LRU / 容量上限 / 原子替换 / 静默降级，格式钉死 logcat 列宽；**A7 连锁：`identity` 四要素计算随判定链进 Rust，存储（读写 / LRU / 原子替换）仍可留 C#** | 甲 sidecar 存储留 C#，契约只给 `identity` 与可否走缓存；乙 读写全部随获取链入 Rust |
| S16 采集临时文件生命周期 | 停止后保留，供「保存日志」复制；无清理逻辑 | 甲 `Retained`，关文档也不删；乙 关文档即删；丙 保留 + 启动老化清理 |
| S18 采集首次索引是否分块 | 现为整份 `BuildAsync`（§2.4） | 甲 维持；乙 改分块渐进（需处理边建索引边推入的竞态） |
| S19 增长文件源的语义（A10 的落点） | 现状由 UI 1 秒定时器 `AdbAutoReload` → `doc.Reload()`（长度比较 + 64 KB 头比较）驱动（§2.4）；`LogcatStream` 每批 `FlushAsync`（`LogcatStream.cs:131`–`:138`） | **已随 A11 作废**：core 不再读增长文件；推入接口语义见 P1（§4.1） |

## 6. 倾向汇总（判断，非事实）

> S8 / S10 已由 A7 定案、S7 已由 A12 定案，不再列为倾向。

| 编号 | 倾向 | 理由要点 |
| --- | --- | --- |
| S4 | 甲（首版留空数组） | 现在留空数组的成本，远低于 DLT change 时改描述符结构（C# 与 Node 两侧同步） |
| S9 | 甲（本契约先走） | DLT 的判定链依赖本契约；反序要白写一次 |
| S11 | 甲（新开 `source-acquire`） | 代理协议与源获取是两件事，混在一起后续 DLT/编码都要改代理 spec |
| S15 | 乙（Rust 推） | 增量判定本在文档层；A11 后由 `feed` 触发，UI 定时器退休 |
| S17 | 甲（UI 传 `tempDir`） | 与 S7 同源：Rust 不该知道 exe 布局与设置（S7 本体已由 A12 定案） |
| S6 | 沿用 C7 | 启发式已定，本契约只留字段位 |
| S12 | 甲（切流即删旧链） | 并存期的偏差只被测试发现，切流点必须显式 |
| S13 | 甲（C# 写日志） | 与 B1 同源；Rust 不应知道 `%LOCALAPPDATA%` 与日志格式 |
| S14 | 甲（sidecar 存储留 C#，identity 计算随判定进 Rust） | 缓存的是 logcat 8 段列宽的索引，与 DLT 5 列格式不兼容；计算归判定链、存储归宿主各得其所 |
| S16 | 甲（Retained） | 零行为变化，清理另立 backlog |
| S18 | 甲（维持整份） | 本阶段目标是契约正确，不是体验改进 |
| A11 / A14（原 S19 / P1） | 形态甲（行数组推入 + core 落盘）；调用模型定**丙**（异步入队、无上限队列），取代原「同步即背压」倾向 | 见 §4.1 定案：与现状批次同形、无残行；A9 下仅 GUI 实现 `feed`；背压以队列积压体现，宿主不阻塞 |

## 7. 显式猜测（非事实）

| 内容 | 标注 |
| --- | --- |
| 两段式能表达新需求里的全部分支（尤其「Auto 探测失败但拉起代理也失败」的二级失败） | **推断**，需在 Rust 切片里逐条核对 |
| `FakeProxyServer` 能跨语言复用：走命名管道、协议无关宿主，可用 | **推断** |
| Rust 切片能在一轮 change 内达到 IT 可跑的完成度（crate 骨架 + C ABI + 封送 + 构建链打通） | **未实测**，本轮最大的工程未知 |
| Rust 侧 UT 覆盖率工具链（llvm-cov 等）与 CI 集成的工作量 | **未评估** |
| 切片「探测 + 拉起代理」耗时可接受 | **未实测** |
| 列声明下发不会显著增加 `LogListView` 改造量 | **未实测**，取决于列 id 耦合程度（B3） |
| 描述符字段集对 MCP 侧够用 | **未验证**，MCP 工具面两套口径未拍板；A9 后采集相关字段只在 GUI 路径要求 |
| `columns[]` 下发后 sidecar 与导出仍按 id 稳定索引 | **推断**，现状列宽是位置而非 id，列集可切后会错位（B3） |
| 采集取流归属不影响描述符面 | **已按 A11 收口**：取流留宿主，推入与落盘归 core；描述符只多 `sourcePath` |
| 采集期改推入驱动后跟随尾部与状态栏口径不变 | **未实测**；`_adbReloadTimer` 随 A11 退休，`_adbStatTimer` 仍由宿主维护，两者职责不同 |
| 异步入队（丙-1）下消化线程吞吐能跟上 adb 输出速率、无上限队列的积压可控（峰值内存） | **未实测**，需在首个切片压测（取代原「同步 feed 吞吐」猜测项） |
| FFI 行数组封送（每批 ≤500 行）开销可接受 | **推断**，B2 布局定稿时核对 |
| core 消化线程落盘不与 UI 线程争锁 | **推断**：`feed` 仅入队，落盘在 core 消化线程执行 |
| 「保存日志」在 stop / close 后复制 `sourcePath` 无竞态 | **推断**：stop / close 均排空后 flush；需确认 UI 不并发读 |
| 宿主 flush 后写盘内容对读者立即可见（A10 的增长文件前提） | **已随 A11 作废**：core 不再并发读采集文件 |
| A10 下用「可控写入的本地文件」替代 fake adb 做 IT | **已随 A11 作废**：IT 直接经 FFI `feed` 推入行 |

> A6 后删除的等价类猜测（「契约层能覆盖 chunked / sidecar 行为」「27 个 fileproxy 场景可在纯契约层验证」「参照实现放新目录不影响覆盖率口径」等）：不再以既有验证为参照，这些不再构成风险项。其中「覆盖率统计口径只含 Services + Models」是 `docs/TESTING.md` §0 明写的**事实**，非猜测；A7 后 Rust crate 不在此口径内，另立口径见 §4「随 propose 定」。

## 8. 与既有结论的关系

> 本节记录历史结论的演进关系，A6 / A7 后**不作为新需求的依据**。

| 既有结论 | 出处 | 本笔记的处理 |
| --- | --- | --- |
| 刀5「获取链入 Rust」 | `RUST-CORE-REWRITE-SLICING.md` §5 | **被 A7 改写**：不再是「先定契约、C# 当参照、后换语言」三步走，契约与 Rust core 最小切片（边界由 A8 定）同轮交付；旧链切流（S12）在切片通过 IT 后执行 |
| 七刀顺序（刀0 / 刀1 谁先） | 同上 §8.1 C5 | 不受影响，仍待拍 |
| D1「文件操作完全归 Rust」 | 同上 §1 | 成立；本契约是其对外可见面，且本轮即开始落地 |
| 「ADB 采集走 Reload 增长分支，故不进获取链」 | 同上 §3.3 | **被 A3 改写**：采集进入契约；取流留 C#，数据经 `feed` 推入、core 解析并落盘（A11） |
| C3「Windows 外壳边界粒度」 | 同上 §8.1 | 本契约不暴露这些细节给 UI，只给 `channel` 与 `leaseId`；C3 选择仍待拍 |
| DLT 解码器落 `Services/Dlt*/` | `DLT-FORMAT-AND-RUST.md` §6.2 | **已被改写**：解码归 Rust core，C# 不再有 DLT 解码器；具体落点待拍板（该文 K1） |
| DLT「读魔数失败回退代理再判」 | 同上 K4 | 由本契约吸收（判定环在 Rust 内） |
| DLT「静态 COLUMNS 改为按格式重建」 | 同上 §3.5 | **升级为 A4**：按 `descriptor.columns[]` 重建，C# 不持有格式→列集映射 |
| 状态栏渠道三态在 UI 侧判定 | `ProxyChannel.cs` | 入参从「UI 自算」改为「描述符 + 事件」；文案与互斥规则不变 |
| 编码判定口径待定 | `RUST-CORE-REWRITE-SLICING.md` §8.2 C7 | 不变，独立 change 立 spec |
| DLT 与本契约同改 `frmMain.StartIndex` | `DLT-FORMAT-AND-RUST.md` §4.1 | 冲突面显式化：按 S9 定先后；A7 后 UI 侧不再有新判定代码，冲突面缩小为「切流点」 |

## 9. 下一步

1. **阻塞项已清零**：P1 已定案为 A14（§4.1）；待维护者发话走 `/opsx:propose`（需用户指令，本笔记不编码）。其余默认解清单与工程落位表按表执行，不再逐项请示。
2. propose 范围 = §3 契约 + §3.11 UT/IT 验证规格 + A14（P1 定案：feed 异步入队、core 落盘、stop / close 排空收尾）+ A7（Rust core 切片）+ A8（文件读入含代理 + 采集输入）+ A9（采集只属 UI，MCP 不接）+ A11（宿主推入完整行，core 解析 / 索引 / 落盘）+ A12（Rust 不读配置，宿主传快照）+ A13（本轮不改 C# 现有代码，保持可用）+ §4 工程落位表；非目标 = DLT 解码实现、采集取流实现（留 C#）、采集字节保真改造（C21）、UI 交互改动（stop / close 进度条属接线期 UI，随切流 change 落地）、sidecar 存储治理、MCP 侧采集、C# 现有链路的切流与清理（§3.10 / S12 顺延至切流 change）。
3. 验证从零设计，不引入 852 条基线 / 既有 spec 场景 / 16 格真值表作为判据；旧链清理（§3.10）在切流（S12）时随 apply 执行。
4. 实现从该 change 的 tasks 走；本笔记不编码。
5. change 归档时，本笔记按 AGENTS.md §3.2 同批移入 `openspec/notes/archive/`。

## 10. 关联

- `RUST-CORE-REWRITE-SLICING.md`：七刀与 FFI 契约面（§4.3 分页原语、§4.5 panic 与并发硬约束、§7 分发实账）。A7 后本契约与刀5 首片合并推进。
- `RUST-CORE-EVALUATION.md`：语言决策与性能基线。
- `DLT-FORMAT-AND-RUST.md`：格式侧设计与 Rust 侧落点（本契约改写其解码归属与列集口径；旧 C# 期落地改动已整条删除）。
- `openspec/specs/old/fileproxy/spec.md`（27 条 / 59 场景）、`openspec/specs/old/adb-capture/spec.md`（4 条 / 10 场景）、`openspec/specs/old/chunked-file-load/spec.md`、`openspec/specs/old/sidecar-index-cache/spec.md`：**背景参考**，不作验收语料（A6）。
- `BACKLOG-MCP-AND-FORMATS.md`：MCP 侧对源描述的需求口径（两套未拍板）。
- `MCP-FUNCTIONALITY.md` §5.5（`openspec/notes/MCP-FUNCTIONALITY.md:143`）：「不暴露 adb、进程控制、文件删除 / 写入等能力」——A9（采集只属 UI）的事实依据。
