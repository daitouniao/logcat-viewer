# 源获取契约（读入链）探索笔记

- 状态：**待评审**（探索记录，未编码、未 propose）
- 日期：2026-11-08（第五轮：重构为事实记录。前四轮：初稿 / 复核补漏 / 采集纳入 / S2 定甲 + 取消超大文件确认）
- 关联提交：无（未编码）
- 验收结论：不适用（未实施）

> 本文件只写两类内容：**已核对的事实**（带 `file:line`）与**维护者已拍的决议**。判断与推测集中在 §6、§7 并显式标注。

## 0. 结论

把源获取（文件 + ADB 采集）从窗体里的两条异源入口收成一份**两段式契约**：UI 传「目标（路径或设备序列号）+ 配置快照」，Rust 决定路由 / 格式 / 编码，返回**三轴拆开的源描述**；UI 只做两件事——显示渠道、按列声明重建表头。

## 1. 已定案（不再讨论）

| 编号 | 决议 | 事实依据 | 落地约束 |
| --- | --- | --- | --- |
| A1 | **入口判定归 Rust**：UI 只传配置与文件名 | 探测必须先于关文档（`frmMain.cs:954` 注释）；DLT 判定环（读魔数失败回退代理再判）在 UI 侧无法表达 | `TextProbe` / `FileProxyDecision` 的判定权外迁；UI 不再自行探测 |
| A2 | **描述符三轴拆开**：`channel` / `format` / `encoding` 互相正交 | DLT 只动 format；GBK 只动 encoding | 不用单一 `sourceType` 枚举 |
| A3（原 S5） | **采集（ADB）纳入同一契约** | 采集是独立入口、不经过 `StartIndex`（`frmMain.cs:2094`）；与文件双向互斥（`:974`、`:2101`） | `AcquireRequest.kind = File / Device`；`channel = AdbCapture`；`appendMode = Appendable`；**取流实现归属 C2 不因此定** |
| A4（原 S2） | **列集由 Rust 下发**（`columns[]`），C# 只渲染 | 列集现写死在 `LogListView` 静态常量；DLT 要 5 列，两处硬编码会分叉 | 静态 `COLUMNS`/`COL_*` 变运行时数据；列宽 / 导出 / 标记按 `id` 而非位置；首版固定 logcat 7 列 |
| A5 | **取消超大文件确认**（删 1 GB 阈值）：经代理一律整体暂存 | 确认块只在 `frmMain.cs:1100`–`:1109`；spec 15 条 Requirement 无此条款（§2.1 注） | 删常量/判定/确认块/文案/断言/变异项（§3.10）；`SourcePlan.needsConsent` 删除 |
| S1 | **plan → commit 两段式** | 判定先于关窗；UI 需在「路由已知、装载未启」时重置界面；DLT 判定环 | plan 副作用止于「读头部 / 拉起代理 / open 拿 size」；两阶段各有取消口径 |
| S3 | **探测参数留 Rust 常量 + 测试注入点** | 8 KB 窗口、10% 阈值现为写死常量（`TextProbe.cs:18`、`:19`）；D14 规定 spool 与探测均不加设置字段 | 注入点只服务窗口与阈值两项，不暴露到设置界面 |

## 2. 现状事实（HEAD 核对）

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

对应的既有条款（「装载入口分流」「代理使用判定」「分流时序」「失败分类与错误消息」「渠道状态区」「乱码配置回退关闭」「重载」）都在 `openspec/specs/fileproxy/spec.md`：**15 条 Requirement / 27 个场景**。

> 更正：初稿把「超大文件确认」列为 spec 条款，实际 15 条 Requirement 标题中无此项——它只是实现细节，A5 删除它不需要改 spec。

另两份 spec 同样约束获取链，本轮未逐条核对：`chunked-file-load`（8 条）、`sidecar-index-cache`（7 条）。

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
| DLT 判定必须先于代理分派，且 DLT 恒本地解码 | `openspec/notes/DLT-INTEGRATION.md` §3.4 |
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
| 现有测试基线 **852 条**全过，行覆盖 94.26%、分支 88.64%、方法 92.77% | `docs/TESTING.md` §0 |

## 3. 契约形状（当前草案）

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
| `device` | `{serial}`（设备枚举、连接、选择留 C#） | `SelectedSerial()` |
| `mode` | `off` / `on` / `auto` | `FileProxyModes.Parse` |
| `proxy` | `pipeName`、`launchFile`、`launchArgs`、`launchTimeoutMs` | 设置 5 键中的 4 键 |
| `options` | 嗅探窗口 / 阈值；`tempDir`（S17） | 现为写死常量（S3） |
| `cancel` | 原子取消标志 | `CancellationTokenSource` |

### 3.3 `SourcePlan`（Rust → UI，无装载副作用）

| 字段 | 内容 |
| --- | --- |
| `displayPath` | 标题/MRU 用路径（DLT 时为源 `.dlt` 路径） |
| `route` | `LocalDirect` / `ProxySpool` / `LocalDecode` / `DeviceStream` |
| `estimatedSize` | 代理路径来自 `open.size`；本地来自文件信息；采集为**未知**（不参与任何大小判定） |
| `probeEvidence` | 探测字节数、非文本占比、命中分支（排障用，UI 不展示） |

无 `needsConsent`：A5 后 plan 不返回宿主裁决项。将来若出现新裁决需求，以独立字段加入，本阶段不预留。

### 3.4 `SourceDescriptor`（Rust → UI，commit 成功后）

| 轴/字段 | 取值 | 消费方 |
| --- | --- | --- |
| `channel` | `Local` / `FileProxy` / `AdbCapture` / `DltDecoded`（互斥） | 状态栏渠道区 |
| `format` | `Logcat` / `Dlt` / … | 列声明与时间语义 |
| `encoding` | `Utf8` / `Gbk` / … | 字节→文本（首版仅 Utf8） |
| `columns[]` | `{id, 语义键, 建议宽度}` | 表头/导出/标记全部按 `id` 索引（A4） |
| `identity` | `size` + `mtime` + 首尾指纹（可空） | sidecar 命中与缓存失效 |
| `sidecarHit` | bool | 状态文案、跳过重复落盘 |
| `appendMode` | `Appendable` / `ReloadOnly` / `None` | 自动重载与增量追加 |
| `leaseKind` | `Ephemeral`（关闭即删）/ `Retained`（保留供保存） | 生命周期口径 |
| `timeSemantics` | `Original` / `Relative` | 时间列口径 |
| `leaseId` | 暂存/解码目录标识：`Ephemeral` 关闭即删，`Retained` 只解除句柄 | 生命周期与启动清理 |
| `warnings[]` | `{category, count}` | 跳过 N 条的提示（现状无此通道） |

### 3.5 顺序不变量

1. 判定顺序在 Rust 内固定：存在性/权限 → 格式（魔数/首行，含回退再判）→ 文本嗅探（仅 auto）→ 代理决策 → spool/解码 → 索引。
2. `format` 与 `encoding` **只在 open 时确定，运行期不变**。
3. 租约随句柄走，关闭顺序固定「停扫描 → 释放句柄 → 释放租约/删目录」。

### 3.6 采集在契约里的形状

```
AcquireRequest{kind: Device, device:{serial}}
   --plan---->  SourcePlan{route: DeviceStream, estimatedSize: 未知}
   --commit-->  开流写临时文件 -> 首次整份索引
                -> SourceDescriptor{channel: AdbCapture, appendMode: Appendable,
                                    leaseKind: Retained, loadMode: OneShot}
   --事件---->  appended(totalRows, delta)   <- 替代现状「1 秒 UI 定时器拉 Reload」
   --stop---->  关流、保留句柄与临时文件（文档仍可过滤 / 导出 / 保存日志）
   --close--->  释放句柄；临时文件删除策略见 S16
```

不变量：

1. **同一时刻最多一个活动源**：采集与文件双向互斥；Rust 对第二个活动句柄返回 `BUSY`。
2. **`stop` 不关句柄**：只关流与增长事件，不释放 `leaseId`、不删文件。
3. **采集临时文件是保留物，不是租约**：`leaseKind = Retained`，与 spool 的 `Ephemeral` 区分。

口径：

- `displayPath` 只给路径，`[实时采集]` 这层文案留 C#（B1 同源）。
- 取流实现归属不影响契约：C2 两方案在契约上同形（`channel = AdbCapture` + `appendMode = Appendable` + `appended` 事件）。

### 3.7 事件通道（轮询，不用回调）

| 事件 | 内容 |
| --- | --- |
| `stage` + `progress` | 阶段枚举 + 原子计数（spool 0–50%，索引 50–100%） |
| `channelChanged` | spool 在途升为 `FileProxy`，替代 UI 侧 `_proxySpooling` |
| `warning` | 跳过/容错计数增量 |
| `done` / `error(code, params)` | 错误码表统一，两宿主共用；保留「直接打开失败」附注位 |

硬约束：**不用回调**（进度用原子计数、取消用原子标志），Node 侧 koffi 不需要 threadsafe function。

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
| B2 | 描述符字段布局与两宿主 | Node（MCP）若读同一份描述符，需与 `RUST-CORE-REWRITE-SLICING.md` §4.4 的布局口径对齐 |
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
| `openspec/specs/fileproxy/spec.md` | **不改**：15 条 Requirement 无此条款 |

行为差异仅一处：经代理打开 >1 GB 的文件不再询问，直接 spool；磁盘不足仍按既有口径透出 I/O 错误原文。若断言所在用例整条删除，基线 852 → 851，`docs/TESTING.md` 与两份 README 数字需同批更新。

### 3.11 验收判据

```
契约等价 = 852 条用例不降
         + fileproxy 27 个场景逐条过
         + adb-capture 4 个场景逐条过（A3 后纳入）
         + ProxyChannelStates 16 格真值表不变（FileProxyChannelStateTests.cs）
```

下限性质：`chunked-file-load`（8 条）与 `sidecar-index-cache`（7 条）能否由描述符完整表达，本轮未逐条核对（§7）。

## 4. 待澄清（阻塞 propose）

### S4 首版是否包含 `warnings[]` 通道

- **事实**：DLT 需要「中间损坏跳过 + 计数继续」（`DLT-INTEGRATION.md` §3.4）；现状没有任何「装载告警」通道，只有致命错误。
- **可选**：甲 首版留空数组；乙 DLT change 时再加字段（需改描述符结构，C# 与 Node 两侧同步）。
- **阻塞点**：描述符字段集在 propose 定稿，事后加字段是契约版本变更。

### S9 与 DLT change 的先后与冲突面

- **事实**：`DLT-INTEGRATION.md` K1 要求 DLT 也在 `frmMain.StartIndex` 加分派；本契约的判定入口同处一处，并行实施会在同一函数产生合并冲突；K4 的「读魔数失败回退代理再判」已被本契约吸收。
- **可选**：甲 本契约先走，DLT 基于契约实现 `format = Dlt`；乙 DLT 先走，本契约吸收其判定链；丙 合并成一个 change。
- **阻塞点**：决定 change 的边界与顺序。

### S10 参照实现的落地形态

- **事实**：S8 乙 的「C# 参照实现」是照契约重写 `StartIndex` 分支；落在现有代码旁会出现两套判定链并存。
- **可选**：甲 纯契约层（`Services/SourceAcquire/*` 纯函数 + 等价测试），不接线 UI；乙 接线 UI 的真替换（等于提前实施）；丙 契约层 + 影子模式开关。
- **阻塞点**：决定本阶段交付物形态与验收方式。

### S11 契约落哪份 spec

- **事实**：`openspec/specs/` 现 13 个 capability；`fileproxy`（15 条）覆盖代理协议与分流，**获取链本身**（判定 → 装载 → 描述符）当前没有对应 capability。
- **可选**：甲 新开 `source-acquire`；乙 并入 `fileproxy`；丙 新开并把 fileproxy 中与获取相关的条款迁过去。
- **阻塞点**：决定 specs delta 写在哪个 capability。

### 建议一并拍（采集侧）

| 编号 | 事实 | 可选 |
| --- | --- | --- |
| S15 增量驱动 | UI 1 秒定时器拉 `Reload`（§2.4） | 甲 UI 轮询 `poll_append`；乙 Rust 在采集线程内增量索引，UI 读 `appended` 事件（页查询走 `arc-swap` 快照，`RUST-CORE-REWRITE-SLICING.md` §4.5） |
| S17 临时目录归属 | `<exe 目录>/Temp`，UI 侧决定（§2.4） | 甲 UI 传 `options.tempDir`；乙 Rust 取系统临时目录；丙 Rust 按 exe 目录算 |

## 5. design 阶段定（不阻塞 propose）

| 编号 | 事实 | 可选 |
| --- | --- | --- |
| S6 编码判定位置 | 未实现，现状硬编码 UTF-8 | 沿用 C7：独立 change 立 spec，用 `utf8_cn_hits` / `gbk_cn_hits`；本契约只留 `encoding` 字段 |
| S7 UI 是否允许 Rust 读 `settings.json` | 设置存储与 SchemaVersion 迁移在 UI 侧 | 甲 UI 传快照；乙 Rust 读文件 |
| S8 本阶段落地形态 | — | 甲 只出 design + spec delta；乙 design + C# 参照实现 + 等价测试（形态受 S10 约束） |
| S12 旧判定链去留 | `frmMain.StartIndex` 是唯一实现（§2.1） | 甲 切流后立即删；乙 并存一个版本；丙 永久保留作对照 |
| S13 `startup.log` 归属 | 获取链每次分流/探测/拉起都写 `StartupLog`，是排障主依据 | 甲 Rust 出诊断事件、C# 写日志；乙 Rust 直接写文件；丙 分级（结构化事件 + C# 转写） |
| S14 sidecar 归属 | 读写含 LRU / 容量上限 / 原子替换 / 静默降级，格式钉死 logcat 列宽 | 甲 sidecar 留 C#，契约只给 `identity` 与可否走缓存；乙 随获取链入 Rust |
| S16 采集临时文件生命周期 | 停止后保留，供「保存日志」复制；无清理逻辑 | 甲 `Retained`，关文档也不删；乙 关文档即删；丙 保留 + 启动老化清理 |
| S18 采集首次索引是否分块 | 现为整份 `BuildAsync`（§2.4） | 甲 维持；乙 改分块渐进（需处理边建索引边增长的竞态） |

## 6. 倾向汇总（判断，非事实）

| 编号 | 倾向 | 理由要点 |
| --- | --- | --- |
| S4 | 甲（首版留空数组） | 加字段成本远低于改描述符 |
| S9 | 甲（本契约先走） | DLT 的判定链依赖本契约；反序要白写一次 |
| S10 | 甲（纯契约层） | 三条验收判据（§3.11）都在契约层可验；乙丙会把阶段拖成实现项目 |
| S11 | 甲（新开 `source-acquire`） | 代理协议与源获取是两件事，混在一起后续 DLT/编码都要改代理 spec |
| S15 | 乙（Rust 推） | 增量判定本在文档层；留 UI 定时器等于把「何时建索引」留在 UI |
| S17 | 甲（UI 传 `tempDir`） | 与 S7 同源：Rust 不该知道 exe 布局与设置 |
| S6 | 沿用 C7 | 启发式已定，本契约只留字段位 |
| S7 | 甲（UI 传快照） | Rust 读会复制一套解析与默认值规则 |
| S8 | 乙（含参照实现） | 契约不经消费者验证必错 |
| S12 | 甲（切流即删旧链） | 并存期的偏差只被等价测试发现，切流点必须显式 |
| S13 | 甲（C# 写日志） | 与 B1 同源；Rust 不应知道 `%LOCALAPPDATA%` 与日志格式 |
| S14 | 甲（sidecar 留 C#） | 缓存的是 logcat 8 段列宽的索引，与 DLT 5 列格式不兼容 |
| S16 | 甲（Retained） | 零行为变化，清理另立 backlog |
| S18 | 甲（维持整份） | 本阶段目标是契约等价，不是体验改进 |

## 7. 显式猜测（非事实）

| 内容 | 标注 |
| --- | --- |
| 两段式能表达现状全部分支 | **推断**，需在参照实现里逐条核对（尤其「Auto 探测失败但拉起代理也失败」的二级失败） |
| `FakeProxyServer` / `FakeAdbSocket` 足够承载契约测试 | **推断**，未逐条核对 27 个场景的可测性 |
| 列声明下发不会显著增加 `LogListView` 改造量 | **未实测**，取决于列 id 耦合程度（B3） |
| 判定搬 Rust 后「探测 + 拉起代理」耗时与现状相当 | **未实测** |
| 描述符字段集对 MCP 侧够用 | **未验证**，MCP 工具面两套口径未拍板 |
| 契约层能覆盖 `chunked-file-load` 与 `sidecar-index-cache` 的行为 | **推断**，未逐条核对 |
| 27 个 fileproxy 场景全部可在纯契约层验证 | **推断**，其中「渠道状态区」「拉取期间渠道状态」含 UI 行为，可能需要额外用例 |
| `columns[]` 下发后 sidecar 与导出仍按 id 稳定索引 | **推断**，现状列宽是位置而非 id，列集可切后会错位（B3） |
| 参照实现放新目录不影响覆盖率口径 | **推断**：现口径只统计 `logcat-viewer` 的 `Services` + `Models`，新增目录会进分母（现 94.26%） |
| 采集纳入后 C2 两方案可共用同一契约 | **推断**，差异被 `channel` 与 `appended` 事件吸收；Rust 直连 adb 时的断连/重连语义未验证 |
| 采集期改事件驱动后跟随尾部与状态栏口径不变 | **未实测**；`_adbReloadTimer` 与 `_adbStatTimer` 职责不同，不可合并 |

## 8. 与既有结论的关系

| 既有结论 | 出处 | 本笔记的处理 |
| --- | --- | --- |
| 刀5「获取链入 Rust」 | `RUST-CORE-REWRITE-SLICING.md` §5 | 前置一步「契约先行」：先定契约并让现有 C# 实现充当参照，再换语言 |
| 七刀顺序（刀0 / 刀1 谁先） | 同上 §8.1 C5 | 不受影响，仍待拍 |
| D1「文件操作完全归 Rust」 | 同上 §1 | 成立；本契约是其对外可见面 |
| 「ADB 采集走 Reload 增长分支，故不进获取链」 | 同上 §3.3 | **被 A3 改写**：采集进入契约；取流实现归属仍不在本阶段 |
| C3「Windows 外壳边界粒度」 | 同上 §8.1 | 本契约不暴露这些细节给 UI，只给 `channel` 与 `leaseId`；C3 选择仍待拍 |
| DLT 解码器落 `Services/Dlt*/` | `DLT-INTEGRATION.md` K2 | **将被改写**：解码归 Rust core，C# 不再有 DLT 解码器 |
| DLT「读魔数失败回退代理再判」 | 同上 K4 | 由本契约吸收（判定环在 Rust 内） |
| DLT「静态 COLUMNS 改为按格式重建」 | 同上 §3.5 | **升级为 A4**：按 `descriptor.columns[]` 重建，C# 不持有格式→列集映射 |
| 状态栏渠道三态在 UI 侧判定 | `ProxyChannel.cs` | 入参从「UI 自算」改为「描述符 + 事件」；文案与互斥规则不变 |
| 编码判定口径待定 | `RUST-CORE-REWRITE-SLICING.md` §8.2 C7 | 不变，独立 change 立 spec |
| DLT 与本契约同改 `frmMain.StartIndex` | `DLT-INTEGRATION.md` K1 | 冲突面显式化：按 S9 定先后 |

## 9. 下一步

1. 拍板 §4：S4 / S9 / S10 / S11，以及采集侧 S15 / S17。
2. 走 `/opsx:propose` 建 change `source-acquire-contract`：范围 = §3 契约 + S8 乙（契约层参照实现 + 等价测试）；非目标 = 任何 Rust 代码、DLT 解码实现、ADB 取流实现归属（C2）、UI 交互改动、sidecar 存储治理。
3. 本笔记不实现任何代码；实现从该 change 的 tasks 走。
4. change 归档时，本笔记按 AGENTS.md §3.2 同批移入 `openspec/notes/archive/`。

## 10. 关联

- `RUST-CORE-REWRITE-SLICING.md`：七刀与 FFI 契约面（§4.3 分页原语、§4.5 panic 与并发硬约束、§7 分发实账）。本契约是刀5 的前置。
- `RUST-CORE-EVALUATION.md`：语言决策与性能基线。
- `DLT-INTEGRATION.md`：格式侧设计与 K1–K8；本契约改写其 K2 与 §3.5。
- `openspec/specs/fileproxy/spec.md`（15 条 / 27 场景）、`adb-capture/spec.md`、`chunked-file-load/spec.md`、`sidecar-index-cache/spec.md`：验收语料。
- `BACKLOG-MCP-AND-FORMATS.md`：MCP 侧对源描述的需求口径（两套未拍板）。
