# Design：source-acquire-contract

## Context

探索定案见 `openspec/notes/2026-11-08-SOURCE-ACQUIRE-CONTRACT.md`（A1–A9、A11–A14（A10 已由 A11 作废）、S/B 系列编号下同）。现状：获取链判定/探测/代理决策在 C# 窗体层，采集是独立入口；本轮要求契约与 Rust core 可运行切片同轮交付（A7），C# 既有链路不动（A13）。规格面（行为契约）见本 change 的 specs delta，本文件只写工程实现决策。

## Goals / Non-Goals

Goals：Rust crate 骨架 + C ABI + 封送 + 构建链打通到「IT 可跑」；UT 主体落 Rust 侧 cargo test；C# 侧交付绑定层与 IT。

Non-Goals：UI 接线、旧链切流、DLT 解码、MCP 侧实现（见 proposal 非目标）。

## Decisions

### D1. Rust 工程落位：仓库根新增独立 crate `logcore/`

- **决策**：新增顶层目录 `logcore/`（package 名 `logcore`），不建 Cargo workspace、不与 C# 工程混目录；产出 `cdylib`（供 FFI）与 `rlib`（供自身 cargo test）。
- **理由**：A7 要求切片与契约同轮交付；独立目录与 `logcat-viewer`/`logcat-uicore` 解耦，切流前两者互不依赖。不建 workspace 是因为当前只有这一个 crate，等第二个 crate（如 dlt 解码器）出现再收编。
- **替代**：放 `rust/` 或 `src-rust/` 子目录——无信息增益；并入现有 csproj 管辖目录——违反 A13 解耦意图。

### D2. FFI 形式：C ABI 导出 + JSON 字符串封送 + 错误码数值表

- **决策**：导出面 = `plan` / `commit` / `feed` / `stop` / `close` / `poll_events` / `get_progress` / `get_descriptor` / `cancel` / `startup_maintain` / `shutdown` / `free_*`（内存释放配对；`startup_maintain` 与 `shutdown` 不持句柄、不依赖活动源，见 D19 / D6；`get_descriptor` 读取源描述，见 D24）。行/列读取不在导出面内：本阶段不提供读接口（D20）。请求与源描述（plan / descriptor）用 JSON 字符串（UTF-8）传入传出；错误码与返回状态用数值枚举表，两宿主共用一张表；句柄为不透明指针。
- **理由**：JSON 封送让 C#（P/Invoke）与 Node（koffi）共用同一布局，避免二结构体布局两处维护（B2 的跨宿主面）；错误码走数值表是因为它是唯一需要高频率、零分配传递的面。§3.7「不用回调」硬约束由此自然满足：事件靠 `poll_events` 拉取，进度靠原子计数 `get_progress`。
- **替代**：struct 直接封送——字段演进时两侧 ABI 同步成本高；protobuf——为 4 个消息引依赖不划算。
- **字段定死（B2 收口）**：`feed` 行数组 = `{const uint8_t* data, uintptr_t len}` 数组 + 总数，单次调用行数上限 **65536**（防误用；宿主实际批次 500 行远低于此），上限随绑定层以编译期常量与接口一同发布、core 不提供查询导出，行内为 UTF-8 字节、不含行尾符（宿主侧已逐行产出）；空批合法。入队前 core 在调用内深拷贝每行到自有缓冲，宿主内存在返回后即可释放（D8）。超过 65536 行的批次整批拒绝、返回 `INVALID_ARG` 且 `params.field = "lines"`（D15），不截断、不自动分批、不入队、不发事件。
- **句柄流**：`plan` 返回「计划 JSON + 句柄」，`commit(handle)` 复用同一句柄与 plan 阶段的判定结果及已拉起的代理连接；`commit` 受理即返，不携带装载结果（D24）。`cancel(handle)` 在 plan 与 commit 受理后、装载完成前均有效，取消后句柄占用资源释放。不采用「plan 返回纯 JSON、commit 重新提交」，因为 plan 阶段可能已拉起代理，没有句柄就无处释放该连接。

### D3. 采集队列模型（A14 落地）

- **决策**：core 内每活动源一个消化线程 + 无上限 MPSC 队列（元素为 core 自有内存的完整行，D8）。状态机 `Running → Stopping → Stopped` / `Running → Failed` / `Running | Stopping | Stopped | Failed → Closed`（close 停止消化并作废队列后）；排空期间 `feed` 返回 `STOPPED` 不入队、`stop` 幂等等待首次结果、`close` 中止排空且此后不再发 `stopped`（被阻塞的 `stop` 返回 `CLOSED`）；`close` 之后 `feed` 返回 `CLOSED`、不入队；`feed` 入队即返；消化线程循环「解析 → 索引追加 → append 落盘 → 事件入队」；`stop` 置状态后消化线程排空队列再 flush + 发 `stopped`；`close` 停消化线程、作废未消化队列、尽力删除采集文件并释放其余源资源（删除失败不改返回，D12），句柄保留至 `free_handle`（D13）。排空只属于 `stop`，排空进度 = 本次排空已消化行数原子计数（D16），经 `get_progress` 可查。落盘失败 → 清队列、状态转 `Failed`、发 `error`，已落盘行保留。`stop` 对文件源返回 `INVALID_STATE`；`stop` / `close` 幂等——重复调用返回与首次相同的结果，不重复发事件；`Failed` 上 `stop` 返回 `FAILED`。每句柄一份独立状态机，core 不设全局活动源槽位，多个源可并存（D17）。
- **理由**：沿用 A14 的无上限队列口径（不丢行）；单消化线程使 `feed`/`stop`/`close` 竞争自然串行化，不需要额外的锁协议；队列无上限换来「永不丢行」，积压风险由 IT 压测覆盖（见 Risks）。幂等与非法状态返回是绑定层与 IT 的判据，不留给实现随手决定。
- **替代**：有界队列 + 背压——宿主侧被阻塞违反 A14 定案；多消化线程——解析可并行但落盘顺序与事件单调性要求串行，并行无收益。

### D4. 事件通道实现：core 内事件队列 + 轮询

- **决策**：core 侧每句柄一个事件队列（`stage`/`appended`/`stopped`/`done`/`error`），`poll_events` 一次取走全部待处理事件；进度与排空计数用原子量另存，`get_progress` 无锁读取。终态按源类型分派：文件源为 `done` / `error`，采集源为 `stopped` / `error`；`close` 同步返回、不发终态事件，`stopped` 只在 `stop` 排空完成后发布（排空被关闭中止时同样不发）。`appended` 只由采集源产生；文件源装载不发增量事件，装载中的行数经描述 `rowCount` 读取。
- **理由**：与 §3.7 轮询制一致；Node 侧 koffi 不需要 threadsafe function，C# 侧 P/Invoke 轮询同样成立；事件量级（每批一条）下轮询成本可忽略。`stopped` 与 `done` 分列是因为采集停止是宿主主动收尾，与文件源「装载完成」语义不同，共用一个事件会让宿主无法区分主动停止与装载结束。
- **消费义务**：事件队列不设上限、不丢弃、不合并（增量与批次一一对应是既有契约）；宿主 MUST 按固定周期轮询以控制内存，迟轮询只影响内存、不丢事件。关闭即作废尚未取走的事件——关闭语义是丢弃当前文档，不提供关闭后的残余读取（D13）。

### D5. 文件链判定实现：嗅探参数注入点（S3）

- **决策**：嗅探窗口（8 KB）与阈值（10%）作为 core 内默认常量，同时可被请求快照覆盖（仅这两项）；代理决策九格、判定顺序（存在性 → 格式 → 嗅探（仅 auto）→ 代理决策）在 core 内固定实现；identity 四要素（size、mtime、首尾各 64 KB FNV-1a）计算归 core；sidecar 缓存的键派生、命中判定与读写同样归 core（见 D18）。`sidecarHit` 仍不入 core 产物：命中结果以 `loadMode` 表达（D14），不新增布尔字段。文件源的编码判定同样归 core：本轮仅支持 UTF-8，检测启发式（笔记 S6 的 `utf8_cn_hits` / `gbk_cn_hits`）随独立 change 落地，不阻塞切片。
- **理由**：与探索笔记 S3/S14 定案一致；注入点只服务测试，不进设置界面（D14 规则）。

### D6. 代理 spool：core 实现代理管道客户端协议

- **决策**：core 按既有代理协议实现客户端侧（连接、拉起超时、spool 接收、三态出口 + 失败分类）；失败分类至少区分代理无法启动、拉起超时、直接打开失败（附注保留）、运行中断线，码值与 `params` 键集见 D15；C# 侧既有 `FakeProxyServer`（协议无关、走命名管道）作为 IT 的对端复用。
- **实例复用与结束**：连接口径沿用现实现——先短超时试连（200 ms），连上即视为外部代理（不登记、不结束）；连不上且配置了启动命令才拉起。core 内维护「管道名 → 实例」登记表，每项记录进程句柄、使用者计数与是否由 core 拉起。拉起走单飞（同一管道名的并发需要只产生一次 `spawn`，其余等待结果），因此多个源可并发暂存于同一实例，不需要串行化；每个源在取得代理连接时计数 +1（plan 阶段确定代理路由并拉起时）、释放该连接时 -1（关闭、取消、失败收尾，含 commit 受理前发现计划连接已断），计数归零且该项为 core 拉起时结束进程。新增无句柄导出 `shutdown`：结束全部 core 拉起的代理并清空登记（不关闭源、不释放句柄，幂等），供宿主退出前调用；被其中断的连接按运行中断线（11）处理、宿主可重试，之后的新获取重新拉起（core 无进程级终态）；外部代理一律不结束。
- **跨进程隔离**：多宿主进程（UI 与 MCP）各有独立 core，进程内计数无法反映对方连接，同名共享会让一方关源或退出掐断另一方。口径：先按配置的基础管道名短超时试连，成功即外部代理（跨进程可共用、不登记、不结束，存活由用户负责）；连不上且配置了启动命令时，core 用派生名 `<基础名>-<pid>` 拉起并连该名。派生名无需用户改配置——现 C# 的 `BuildLaunchArgs` 保证管道名总经启动参数下发（模板含 `{pipe}` 则替换，否则末尾追加 `--pipe`），core 沿用该口径。登记表按实际连接名记录，进程内「使用者归零结束」与 `shutdown` 由此只影响本进程；派生名总长按管道名上限（256）在请求校验时按基础名加后缀判定，超限 `INVALID_ARG`（`field = proxy.pipeName`）。进程崩溃留下的派生名实例不在本进程复用与回收范围内（与现状「代理可能活过宿主」同级），如需收口再由启动维护扩展。
- **理由**：判定与 spool 必须同在 core（判定环「读魔数失败回退代理再判」在宿主侧无法表达，A1）；FakeProxyServer 走命名管道、不绑宿主语言，笔记 §7 已标记可复用（推断，apply 首个 IT 用例即验证）。实例归属方面：`proxy.pipeName` 是设置里的单值（D21），单进程内多源并存下必然共用；协议消息带 `id`、样例代理为 `PIPE_UNLIMITED_INSTANCES` + 每连接一线程，多连接并发在协议层成立，复用单实例只是去掉「并发需要 → 各自 spawn」的竞态。结束规则若留白，core 拉起的进程会活过宿主；若改为「连接释放即杀」，则第一个源关闭会打断第二个源的暂存。跨进程场景无法用进程内计数表达对方的连接，同名共享必然出现「一方归零结束、另一方被断线」，因此把共享限定在用户手动启动的外部代理，core 自己拉起的实例一律派生名隔离——与现 C# 每进程应用级单例的行为一致。

### D7. 采集落盘格式

- **决策**：core 写采集文件，UTF-8 无 BOM、行尾 CRLF，落在请求快照的 `tempDir` 下；路径经描述符 `sourcePath` 交还；「保存日志」仍由宿主复制该文件，不新增导出接口。命名与唯一性：采集文件 `logcat_live_<yyyyMMdd_HHmmss>_<seq>.log`，暂存目录 `logcat_spool_<seq>`，`seq` 为 core 进程内全局原子计数；两者一律独占创建（文件 create_new / 目录 create_dir），已存在则递增 `seq` 重试。时间戳只负责可读性、不承担唯一性——多源并存（D17）下同一秒可启动多个采集；独占创建同时挡住进程重启后索引归零与两个进程共用 `tempDir` 两种覆盖。
- **理由**：字节口径复刻现状，将来切流后保存产物与历史行为一致；A11 已定落盘归 core。独占创建让唯一性不依赖毫秒或 GUID，崩溃残留只会让下次跳号，不影响正确性。采集路径不做编码判定：宿主已按 UTF-8 解码后推入完整行，非 UTF-8 字节在宿主侧已成为替换字符；字节保真属 C21 非目标。采集文件在关闭时随源资源删除（租约性质为关闭即删）；停止不删除，便于停止后保存。
- **锁标记**：每个临时产物在临时目录内有一个同名旁置标记——暂存目录 `logcat_spool_<seq>` 对应 `logcat_spool_<seq>.lock`，采集文件 `logcat_live_<yyyyMMdd_HHmmss>_<seq>.log` 对应 `logcat_live_<yyyyMMdd_HHmmss>_<seq>.log.lock`。顺序固定为「先以独占方式打开标记 → 再创建产物」，产物存续期间标记句柄一直被持有；产物随源资源释放时一并删除标记——暂存目录与采集文件都在 `close` 时释放。`stop` 只排空、不释放产物，标记继续被占用，因此停止后仍可保存的文件不会被并发的维护误删；宿主若未 `close` 就释放句柄，标记随进程退出失效，产物留给下一次启动维护回收（不额外要求 `free_handle` 承担删文件职责）。标记内容为空，不写入 PID——存活判定只靠「能否独占打开」，崩溃时由操作系统释放句柄，无需清理僵尸标记内容。不引入年龄阈值：年龄在长时采集下会误删，而锁判定在「先标记后产物」的顺序下没有误删窗口。

### D8. panic 与内存边界

- **决策**：所有导出函数体包 `catch_unwind`，panic 转错误码；跨 FFI 的内存一律「谁分配谁释放」——core 返回的 JSON 字符串与行数组由 `free_*` 配对释放，宿主传入的内存在调用期间有效；`feed` 的行数据在返回前深拷贝为 core 自有内存，队列不持有宿主指针，宿主内存在返回后即可释放或复用，无需保活；crate 设 `panic = "abort"` 仅用于非 FFI 的 bin 目标，cdylib 保持 unwind 以便拦截。
- **理由**：FFI 边界不允许 unwind 外泄（既有重写探索 §4.5 的硬约束）；分配配对是最不易错的跨语言约定。

### D9. 测试分层落位（A6）

- **决策**：UT 主体 = Rust 侧 `cargo test`（模式判定九格、嗅探边界、路由、描述符组装、sidecar 键派生与命中/降级、缓存维护、事件不变量、推入/收尾语义、取消）；C# 侧新增绑定层 UT（封送、错误码映射、句柄生命周期、维护入口封送）与 IT（xUnit 经 FFI 打真实 core：本地直读、sidecar 二次打开与启动维护、FakeProxyServer spool、推入批次落盘校验、失败路径、多源并存与关闭隔离），采集 IT 用预推批次模拟取流，不依赖真机 adb。Rust 覆盖率口径独立（cargo llvm-cov），不并入 C# 94.26% 口径。
- **理由**：§3.11 已定两层分工与判据（落盘内容与预置期望逐项相等，不与旧实现比对）。行/列读取本阶段不导出（D20），IT 的数据正确性以「采集文件逐行内容」与「描述符行数 / 文档大小」为判据；列装配与顺序属包内校验，留在 cargo test 的内部 API。

### D10. 构建链

- **决策**：`dotnet build` 不感知 Rust；`docs/BUILDING.md` 增补独立小节：cargo build 产出 dll → 绑定层按约定目录探测加载；缺失 dll 时绑定层抛明确错误（不影响既有功能，A13）。
- **理由**：切片未接线，C# 主构建链保持零依赖；文档承载新动作（§3.5 口径）。

### D11. C# 绑定层落位：`logcat-viewer/` 下新增文件

- **决策**：FFI 绑定层为 `logcat-viewer/` 下的新增源文件，不新建类库，不改 `logcat.slnx` 与 csproj（SDK 默认编译包含）；绑定层类型保持 public，测试工程已引用 `logcat-viewer`，UT 无需 `InternalsVisibleTo`。
- **理由**：A13 要求既有链路零改动；新增文件不触发构建配置变更，8.1 可按「既有工程文件未改」判定。
- **替代**：独立 `logcat-ffi` 类库——隔离更干净，但需改 slnx、csproj 与测试引用，构建面多一个单元，切片期无收益。

### D12. 宿主收尾时序（停止排空 / 关闭即丢弃）

- **决策**：`stop` / `close` 只由宿主发起。`stop` 必须在宿主停止取流并推完残余批次之后发起——core 不触碰宿主取流；`stop` 同步排空队列、刷盘、发 `stopped`，之后采集文件保留供保存。排空期间另一线程可调用 `close`：关闭生效、排空中止、此后不发 `stopped`，被阻塞的 `stop` 返回 `CLOSED`；排空期间重复 `stop` 幂等等待同一结果。`close` 不要求该前置顺序：core 停止消化线程、作废未消化队列、尽力删除采集文件、释放源资源后同步返回，不等积压消化、不进入排空、不发终态事件，排空只属于 `stop`。产物删除为尽力而为——被外部进程占用删不掉时（Windows 共享冲突）不改 `close` 返回、不发事件、不放宽句柄状态转换，残留产物与已解除的锁标记由下次启动维护按无主回收（D19）；因此宿主不得以关闭后产物是否存在判定关闭结果。两种收尾都是同步阻塞调用，返回即资源处置完成（句柄对象保留至 `free_handle`，见 D13）；宿主不得在 `close` 返回前退出进程。关闭即丢弃当前文档，因此不设「排空等待上限」，也不存在「超时放弃排空」——被丢弃的未消化行属于关闭语义，不计为数据丢失。
- **理由**：core 无法停止宿主侧 adb 取流（A13 取流留 C#），残余批次只有宿主知道何时推完，因此「停止取流」半步必须归宿主；但关闭的语义是丢弃当前文档（用户已定：关闭即不保存、下次不召回），排空后再删文件是把即将删除的数据先写一遍的纯浪费，且会把退出路径拖到与积压量成正比。把排空收敛到 `stop`，关闭变成常数级、语义单一，退出路径也不再需要按吞吐推导上限。
- **替代**：`close` 排空后再删文件——写盘全是无用功，退路径依赖积压量，已否决；`close` 异步 + 终态事件——宿主需额外状态机区分「源资源释放」与「排空完成」，退出路径要等事件，易写成竞态；固定等待上限——上限本身没有意义，因为排空已被移出 `close`。

### D13. 句柄生命周期与关闭期并发
- **决策**：`close` 只释放源资源（文件句柄、扫描线程、租约或临时目录及其锁标记；采集源含尽力删除采集文件，失败不改 `close` 返回，D12；计划阶段已拉起的代理连接按 D23 等价取消释放），**不释放句柄对象**；句柄对象由宿主显式 `free_handle` 释放，与 D8「谁分配谁释放」同口径（`free_handle` 以已关闭为前提，未关闭调用返回 `INVALID_STATE`，见 D23）。`close` 返回后，对句柄的 `feed` / `poll_events` / `get_progress` / `get_descriptor` 一律安全返回 `CLOSED`，不解引用已释放内存。宿主可在关闭期间从另一线程轮询进度，core 内部以关闭态标志与源资源独占保证安全。多句柄互相独立：关闭或释放一个句柄不影响其他句柄的状态与事件流；不存在「已有源」导致的拒绝（D17）。关闭即作废尚未取走的事件与进度快照，关闭后的轮询与进度读取只返回 `CLOSED`。
- **理由**：若 `close` 直接释放句柄，关闭线程返回与另一线程 in-flight 的轮询构成跨 FFI 的 use-after-free，`catch_unwind` 无法拦截；把「源资源释放」与「句柄对象释放」拆开，既保住 `CLOSED` 返回值语义，也让并发轮询天然安全。代价是宿主在退出路径上多一次 `free_handle`，属既有 `free_*` 配对约定的延伸。
- **替代**：句柄引用计数——同样安全，但要引入原子计数与 in-flight 追踪，切片期复杂度高于收益；「宿主保证 close 返回前停止轮询」——靠纪律而非契约，漏一处即崩溃。

### D14. plan / 描述符的 JSON schema 与取值枚举
- **决策**：字段名沿用笔记 §3.4 的 camelCase，取值沿用其 PascalCase 枚举；按 S5 删除 `sidecarHit`（命中由 core 自行判定，结果以 `loadMode` 表达，见 D18）；补 `loadMode`、`identity` 结构。两宿主（C# / Node）共用这一份 schema。

plan 返回：

| 字段 | 类型 | 取值 |
| --- | --- | --- |
| `route` | string | `LocalDirect` / `ProxySpool` / `DeviceStream`；`LocalDecode` 随 DLT change 加入 |
| `estimatedSize` | number \| null | 字节数；采集源为 `null`，不参与大小判定 |

描述符（commit 受理后经 `get_descriptor` 读取；受理时即产出骨架，静态字段终值（渠道在 plan 阶段确定）、身份与装载模式取 plan 阶段确定值（装载开始复核发现文件已变时一并更新一次，D18）、动态字段取当前值，失败态返回最后快照；装载未完成时行数为当前值）：

| 字段 | 类型 | 取值 / 说明 |
| --- | --- | --- |
| `channel` | string | `Local` / `FileProxy` / `AdbCapture`；删除 `DltDecoded`；在 plan 阶段确定，受理后不变 |
| `format` | string | 首版仅 `Logcat`；`Dlt` 随 DLT change 加入 |
| `encoding` | string | 首版仅 `Utf8`；`Gbk` 随编码判定 change 加入 |
| `columns` | array | `[{ "id": string, "labelKey": string, "width": number }]`，`labelKey` 为语义键（文案归宿主，B1） |
| `identity` | object \| null | `{ "size": number, "mtimeMs": number, "headHash": string, "tailHash": string }`；本地直读源在 plan 阶段读首尾各 64 KB 样本算全并于受理骨架给出，commit 复核发现文件已变时更新为复核值；代理暂存源与采集源为 `null` |
| `loadMode` | string | `OneShot` / `Progressive`；plan 阶段命中缓存为 `OneShot`，装载开始复核身份不一致时转 `Progressive` |
| `rowCount` | number | 已装载行数；受理后至装载完成前为当前值（初始 0），完成后为终值 |
| `docSize` | number | 文档字节数 |
| `appendMode` | string | `Appendable`（文档会继续增长）/ `ReloadOnly`（不再增长，重开须重载）；按 route 取值见下表 |
| `leaseKind` | string | `None`（无临时产物）/ `Ephemeral`（关闭即删）；按 route 取值见下表 |
| `leaseId` | string \| null | 临时产物标识：暂存目录名 `logcat_spool_<seq>`、采集文件名主干 `logcat_live_<yyyyMMdd_HHmmss>_<seq>`；`leaseKind = None` 时为 `null` |
| `timeSemantics` | string | `Original`（日志自带绝对时间）/ `Relative`（相对时间，预留：随 DLT change 引入）；本阶段全部 `Original` |
| `displayPath` | string \| null | 用户可见路径（标题 / 最近使用 / 导出命名）；采集源为 `null`——无用户路径，标题与最近使用由宿主按渠道自行决定 |
| `sourcePath` | string | 实际读取路径（代理暂存路径 / 采集文件路径），仅供内部使用 |

- **数值与哈希编码**：`size` / `docSize` / `rowCount` 为整数；`mtimeMs` 为 Unix epoch 毫秒整数（不带时区字符串，避免两宿主解析口径分叉）；`headHash` / `tailHash` 为 64 位 FNV-1a 的 16 位小写十六进制字符串——不用 JSON 数字，因为 Node 的 `JSON.parse` 在 2^53 以上丢精度。
- **空身份表达**：整个 `identity` 为 `null`，不允许四字段各自 null。
- **路径口径**：请求 `path` 必须是绝对路径，相对路径在请求校验阶段即被拒（D21），不会进入判定；`displayPath` 恒等于请求中的原路径——不规范化、不解析符号链接、不改分隔符与大小写；采集源没有用户路径，取 `null`（与 `identity` / `leaseId` 的「不适用」同构），宿主不得把 `null` 当路径用。`sourcePath` 为实际打开路径，仅当原路径为绝对路径且长度超出常规上限时补 `\\?\`（UNC 补 `\\?\UNC\`）；采集源的 `sourcePath` 为 core 创建的采集文件。打开失败返回 `DIRECT_OPEN_FAILED`，`params.path` 回显不经前缀改写的原路径，避免错误提示与用户所见不一致。
- **装载模式取值**：`OneShot` = plan 阶段命中 sidecar 缓存，commit 复用索引、不重新解析；`Progressive` = 未命中，本次解析建立索引。缓存只服务本地直读源（D18），代理暂存与采集源恒为 `Progressive`。
- **按 route 取值**：

  | route | `appendMode` | `leaseKind` | `leaseId` | `timeSemantics` |
  | --- | --- | --- | --- | --- |
  | `LocalDirect` | `ReloadOnly` | `None` | `null` | `Original` |
  | `ProxySpool` | `ReloadOnly` | `Ephemeral` | `logcat_spool_<seq>` | `Original` |
  | `DeviceStream` | `Appendable` | `Ephemeral` | 采集文件名主干 `logcat_live_<yyyyMMdd_HHmmss>_<seq>` | `Original` |
  | `LocalDecode`（DLT，后续） | `ReloadOnly` | 随 DLT change 定 | 解码目录名 | `Relative` |

  本地直读无临时产物，`leaseKind = None` 且 `leaseId = null`；sidecar 缓存条目不属于租约，不出现在这两个字段里。采集源 `stop` 后文件仍保留，但那是句柄存活期的状态，不改变描述符取值。
- **身份按 route**：`identity` 仅 `LocalDirect` 有值；`ProxySpool` 与 `DeviceStream` 整体为 `null`（不适用）——代理暂存源在 plan 阶段读不到原文件，暂存文件是临时副本，不补算身份。
- **取值规则**：每个枚举值 MUST 有明确生产者；无生产者的值一律删除或显式标为预留。据此删除 `leaseKind` 的 `Retained`（无生产者）与 `appendMode` 的 `None`（无使用者、语义空洞）；`timeSemantics` 的 `Relative` 保留并标注预留（DLT 时间戳相对设备启动）。
- **理由**：唯一有值表的是探索笔记，而笔记归档后冻结且已被 S5 改动；不收编会导致 Rust 结构体、C# 反序列化与 IT 断言各自发明一套。沿用原命名而非重起 camelCase 值集合，是为了让笔记里的判定规则、事件表与本 schema 保持同名，减少跨文档漂移。
- **替代**：重定义一套小写下划线值——命名更统一，但与笔记全面脱钩，评审时需逐值对照；让 serde 结构体当事实源——C# 与 Node 只能靠试错反推，且结构体私有字段会泄漏进线格式。

### D15. 错误码数值表与 error.params
- **决策**：一张扁平小表，码值一经发布不改、新增只追加；`params` 为 JSON 对象，值只含字符串与数值，不含文案与本地化（文案归宿主，B1）。

| 码 | 名称 | 含义 | `params` 键 |
| --- | --- | --- | --- |
| 0 | `OK` | 成功，不构成错误事件 | — |
| 1 | `BUSY` | 保留码：多源模型下不再由获取触发，码值不复用 | `{}` |
| 2 | `STOPPED` | 采集已停止，推入被拒 | `{}` |
| 3 | `FAILED` | 采集处于失败态 | `{}` |
| 4 | `CLOSED` | 句柄已关闭 | `{}` |
| 5 | `INVALID_STATE` | 该状态不允许此操作（如文件源 stop） | `{}` |
| 6 | `INVALID_ARG` | 请求字段非法 | `{ "field": string }` |
| 7 | `INTERNAL` | panic 或内部错误 | `{}` |
| 10 | `PROXY_LAUNCH_TIMEOUT` | 代理拉起超时 | `{ "proxyPath": string, "timeoutMs": number }` |
| 11 | `PROXY_DISCONNECT` | spool 中途断线 | `{ "reason": string }` |
| 12 | `DIRECT_OPEN_FAILED` | 直接打开失败 | `{ "path": string, "osError": number }` |
| 13 | `NOT_FOUND` | 目标不存在 | `{ "path": string }` |
| 14 | `UNSUPPORTED_FORMAT` | 格式不支持 | `{ "format": string }` |
| 15 | `IO_ERROR` | 采集文件读写失败 | `{ "path": string, "osError": number }` |
| 16 | `PROXY_LAUNCH_FAILED` | 代理可执行文件无法启动（不存在、不可执行、启动被拒） | `{ "proxyPath": string, "osError": number }` |

- **附注**：代理失败且直开也曾失败时，错误的 `params` 追加 `directOpenError` 子对象（形如码 12 的 `params`），保留「直接打开失败」证据。`directOpenError` 只出现在真实失败上——直开失败后经代理成功时不发任何事件。
- **`INVALID_ARG` 的适用范围**：同一码既用于获取请求字段（`field` 为 D21 的点分路径），也用于 `feed` 的行数组参数——单批超过 65536 行时 `field = "lines"`，整批不入队、不发事件（D2）。两者都是入参形状判定，与句柄状态无关（状态不符另判 `INVALID_STATE` / `STOPPED` / `CLOSED`）。
- **plan 失败的交付口径**：plan 阶段失败不建立句柄，错误只经同步返回（码 + `params`）交付，不发布事件；事件通道自 plan 成功建立句柄后生效，同一失败不在两路重复上报（D24 的「受理前同步、受理后事件」在 plan 段同理）。
- **前向兼容**：宿主 MUST 忽略未知 `params` 键与未知码值，未知码按通用失败处理并记录原始数值；core 不得因宿主版本旧而改变码值。
- **理由**：1.2 的「数值不漂移」测试需要一份可锁定的表；失败分类用码而非字符串，跨语言拼写错误在编译期就能发现。码值按状态类（0–9）与失败类（10–16）分段，段内连续、段间留空便于追加。`params` 只放数据，宿主按码查文案，正好落实 B1。
- **替代**：分段编码（1xx/2xx/3xx）——扩展空间更大，但测试常量与文档表都更长，当前 14 个码不值得；只留 `FAILED`/`INTERNAL` + `params.reason` 字符串——失败分类退化为字符串比较，Rust 与 C# 拼写不一致只能靠 IT 发现。

### D16. stage 枚举与 get_progress 结构
- **决策**：`stage` 五值，按下表定义单位与总量；`get_progress` 返回 `{ "stage": string, "value": number, "total": number | null }` 的原子快照。core 不计算百分比，也不按阶段加权——权重与百分比归宿主（本轮不接 UI，切流期实现）。

| `stage` | 出现路径 | `value` 单位 | `total` |
| --- | --- | --- | --- |
| `Opening` | 全部 | 无（恒 0） | `null` |
| `Spooling` | 代理暂存 | 已暂存字节 | 暂存总字节 |
| `Indexing` | 文件源（直读未命中缓存 / 暂存后） | 已解析字节 | 文件字节数 |
| `Capturing` | 采集运行中 | 已解析行数 | `null`（无终点） |
| `Draining` | 采集 stop | 本次排空已消化行数（进入排空时归零） | 发起 stop 时队列行数快照 |

- **阶段顺序**：本地直读命中缓存 `Opening →（完成，无 Indexing）`；本地直读未命中 `Opening → Indexing`；代理暂存 `Opening → Spooling → Indexing`；采集停止 `Opening → Capturing → Draining`。阶段按所属路径推进、不倒退，路径内不跳阶段。`close` 不进入 `Draining`：关闭直接作废队列并结束。
- **计数规则**：同一阶段内 `value` 单调不减；阶段切换时 `value` 归零重新计（宿主按阶段各自算比例）；`value` 与 `total` 只描述当前阶段内的进度，不含此前阶段的累计；终态后 `get_progress` 返回最后阶段快照，不再变化。排空进度因此从 0 起算到队列快照，完成时 `value` 等于 `total`；全程累计行数：采集源取 `appended.totalRows`（D22），文件源取描述符 `rowCount`（D14）——文件源装载不发增量事件。
- **事件口径**：`stage` 事件只在阶段切换时发布，携带新阶段的 `{stage, value: 0, total}`；持续进度不逐次发事件，由宿主轮询 `get_progress`。这样采集的事件量仍是「每批一条 `appended`」量级。
- **理由**：采集运行期客观上没有总量，只有「已解析行数」这一事实；排空的总量要等 `stop` 冻结队列才存在，`close` 不进入排空、不需要该口径。绝对量 + `total=null` 能同时表达三种口径，IT 也能直接断言「排空结束时 `value` 等于 `total`」。排空进度只计本次排空：若沿用全程累计，`value` 会以「已消化行数」起步、与「队列快照」的 `total` 不同源，进度条一进排空就越过 100%；阶段内进度与累计行数分离后，宿主只需一条规律——任何阶段都按本阶段 `value`/`total` 算比例，累计量另取 `appended.totalRows` / `rowCount`。百分比权重（笔记 `:259` 的 0–50 / 50–100）是状态栏呈现取舍，写进 core 会让换 UI 就要改 core。
- **替代**：core 直接出 0–1000 千分比——宿主零换算，但权重固化、采集中无总量、IT 只能验单调与封顶；只报 `stage` 不报数值——需改 spec 的「进度用原子计数」，且装载期与排空都失去量化信息。

### D17. 多活动源与生命周期外部管理
- **决策**：取消「同一时刻至多一个活动源」。同一 core 实例内可并存多个文件源与采集源，每句柄独立状态机、独立事件队列、独立进度；core 不设数量上限、不自行超时回收、不因已有源拒绝新句柄。回收策略归宿主：宿主负责关闭闲置源，且 MUST 保留最近使用的活动源；`BUSY` 码保留但不因第二个源触发。
- **理由**：MCP 宿主需要同时打开多个文件源，core 强加单源会把「谁该关」的决策塞进底层；GUI 的「打开文件先关旧文档」是宿主策略，与契约能力不是一回事。宿主在两次调用之间可能长时间思考，core 无法判断源是否仍在使用，内建超时回收会错误销毁文档。外部管理让两宿主各用各的策略，core 只保证句柄独立与资源释放正确。
- **替代**：保留单源 + BUSY——MCP 无法多文件；core 内建 LRU 超时回收——需要知悉宿主意图，误回收会丢文档，且回收阈值又要变成契约字段。
- **MCP 侧**：多源并存与外部回收都在宿主管辖内；采集仍只属 GUI（A9），MCP 只开文件源。GUI 切换源时沿用现行「先停采集、再关旧文档」，属宿主策略，不进 core。

### D18. sidecar 缓存：归属、格式与复用
- **决策**：sidecar 缓存的键派生、命中判定与读写全部归 core（反转 S14 甲的「存储留 C#」）。缓存目录、条目上限与目录上限由请求快照传入（缺省取当前生产值：条目 1 GB、目录 4 GB），core 不发明默认目录、不读设置文件。键派生沿用既有实现（全路径 + 文件名主干 + 路径大写不变式的 FNV-1a + `x16`），字节格式冻结在当前版本（`FormatVersion = 1`、8 段宽度），缓存不含文本——命中仍须打开源文件取文本。命中判定在 plan 阶段完成且只读（读取首尾各 64 KB 样本算全四要素身份，不建目录、不写条目），commit 开始时以四要素复核身份：一致则复用索引、跳过解析（`loadMode = OneShot`）；不一致、条目损坏或版本不符则完整解析（`Progressive`），描述身份更新为复核值（D14），低版本条目在原位置重写，高版本条目 MUST NOT 覆盖。写入走「临时文件 + 原子替换」；容量先淘汰到装得下、新写必留、在用必留，单条目超上限则不缓存。只服务本地直读源，暂存与采集产物不入缓存。任何缓存读写异常静默降级，不发错误事件、不使获取失败。
- **理由**：sidecar 的输入（身份）已在 core，命中判定与装载路径同一条执行链；留在宿主就得把索引经 FFI 搬回再交给 core 装载，命中反而多一次跨语言拷贝。`loadMode` 只有同时握有身份与缓存的 core 才能给出，这是本轮最早暴露的缺口。格式冻结在同一版本，是因为迁移期旧缓存必须直接可用、且 C# 侧仍是运行时所有者；改格式会同时废掉两份。原子替换、淘汰优先与新写必留沿用既有容量口径，避免出现两套策略。
- **替代**：宿主查缓存（S14 甲原案）——`loadMode` 无判定依据；缓存格式升级并加校验和（C6）——另立 change，本轮不动；Rust 另起一套键与格式——旧缓存全废，切流时两侧互不命中。
- **在用条目**：在用 = 本进程内未关闭的本地直读源所对应的缓存键——命中或写回时登记（按键引用计数），源关闭、取消或失败收尾时注销；容量回收跳过这些键，计入 D19 的 `kept`。跨进程不共享在用视图：`startup_maintain` 以空在用集合运行，删除其他进程的在用条目是允许的，后果仅为该文件下次获取重新解析。缓存是纯性能产物，跨进程租约要额外承担崩溃残留与过期判定，收益只是省一次重新解析，与 D6 不引入跨进程锁的口径一致。
- **plan 读范围**：本地直读源的只读边界是「首尾各 64 KB 样本 + 大小 + mtime」而非「仅头部」——命中判定以四要素为键，缺尾指纹无法判命中，既有 `SidecarIdentity.Capture`（超过 64 KB 的文件 seek 到末尾再取 64 KB，小文件复用头哈希）与冻结的缓存格式也按首尾采样。plan 仍不写盘、不建目录、不写条目；描述骨架的身份即 plan 算全的值，commit 复核只在文件确实变化时更新一次、并使该次装载转 `Progressive`。代理暂存源在 plan 阶段读不到原文件（正因直开失败才改走代理），其身份取 `null`，不补算暂存文件身份。替代是「plan 只读头、命中复核推迟到 commit」——计划返回的 `loadMode` 就不可信，且描述身份在受理骨架里只能留空，与「受理即可读」冲突。
- **迁移期单一所有者**：本轮 UI 未接 Rust（A13），C# 的 sidecar 存储仍是运行时唯一所有者；Rust 侧缓存只在测试的独立目录中验证，不指向生产 `cache/`。所有权移交与 C# 存储退役随切流 change（S12 清单）。迁移期内不得让两方同时写同一目录——原子替换只保证条目不半成品，不保证两侧的在用集合与淘汰视图一致。

### D19. 启动维护流程：缓存 + 临时产物
- **决策**：新增无句柄导出 `startup_maintain(requestJson)`，由宿主在进程启动时调用一次。输入为 `{ "cacheDir": string, "tempDir": string, "entryLimitBytes"?: number, "dirLimitBytes"?: number }`（后两项缺省取 D18 的 1 GiB / 4 GiB）；输出 `{ "status": string, "removedCorrupt": number, "removedTempWrites": number, "removedOrphans": number, "skippedInUse": number, "evicted": number, "kept": number, "freedBytes": number }`，`status` 取 `Ok` / `Partial`；`kept` 为回收结束后目录内保留的有效缓存条目数，缓存侧以空在用集合运行（D18）。
- **缓存侧动作**：条目结构校验（魔数、版本、段宽、段长）、删除损坏条目与写入残留的临时文件、按 mtime 从旧到新淘汰至容量上限内。
- **临时目录侧动作**（命名与锁标记见 D7）：对匹配命名约定的 `logcat_spool_<seq>` 与 `logcat_live_<yyyyMMdd_HHmmss>_<seq>.log` 逐个尝试独占打开同名 `.lock` 标记——成功即无主，删除产物与标记并计入 `removedOrphans`；被占用则跳过并计入 `skippedInUse`；产物存在而标记缺失视为无主；只有标记而无对应产物的孤立标记也删除，计入 `removedTempWrites`。不匹配命名约定的条目一律不碰——同一临时目录里可能还有宿主或其他程序的文件。
- **可靠性口径**：幂等（重复调用不产生额外删除）；目录不存在视为空目录（返回零统计，不创建目录）；任一侧失败（目录不可写、条目被占用无法删除等）以 `Partial` 返回，不抛错、不阻断后续获取，且不删除结构可读的有效条目或仍被占用的产物。
- **理由**：缓存改由 core 拥有后，容量与完整性的兜底需要一个与任何源都无关的入口；放在启动而非内建后台定时，是为了不引入定时器与跨源并发，也让 IT 用一次调用就能断言全部清理结果。临时目录并入同一入口，是因为「`stop` 保留采集文件」让临时目录成为持久累积点，而崩溃残留又不可能由单个进程按名字或年龄判定存活——锁标记是唯一能区分「活动源」与「遗留物」的依据，且与已有的独占创建口径同源。本轮无 C# 改动（A13），该入口只由 IT 调用；切流期在宿主启动顺序中接线。
- **替代**：不清理临时产物——停止保留的采集文件与崩溃残留会无限累积；按年龄阈值清理——长时采集会被误删，阈值本身没有可判定的正确取值；由宿主清理——core 是产物创建者，命名约定与锁语义只有它完整掌握；core 内建定时维护——多线程与生命周期复杂度上升，本轮无收益。

### D20. 行/列读取接口推迟到下一阶段
- **决策**：本阶段导出面不提供行/列读取（分页读行、按列取值、字典下发），读取接口随切流 change 实现。本阶段的数据正确性判据改为：描述符的 `rowCount` / `docSize` 与已知内容一致；采集源以落盘文件逐行内容与顺序为准；命中/降级以 `loadMode`、阶段序列与缓存条目为准；列装配与顺序用 cargo test 的内部 API 校验。
- **理由**：切片目标是「获取链 + 事件 + 缓存」的形状与判据，不是渲染面；先把读接口做出来会连带定义分页、字符串归属、字典生命周期与并发语义，超出本阶段所需。读取是纯增量导出（句柄已在、索引已在），下一阶段补上不影响本阶段任何契约。
- **替代**：本轮出最小分页读——IT 能直接断言每列值，但引入一整套封送与生命周期规则；本轮出完整查询面——过滤/排序一并定义，范围失控。
- **代价与对冲**：C# IT 在下一阶段前无法按列断言直读结果，只能验行数、文档大小与落盘事实；这一空白在 coverage-map 中显式标注为「下一阶段覆盖」，不得记为已验证。

### D21. 请求（plan 入参）schema 与字段校验
- **决策**：请求为单个 JSON 对象，字段名 camelCase、枚举取值 PascalCase（与 D14 同风格；笔记 §3.2 的小写 `off/on/auto` 不再沿用）。未列出的字段一律忽略（前向兼容）；缺必填项或取值非法返回 `INVALID_ARG`，`params.field` 用点分路径指到具体字段。`cancel` 不是请求字段——取消走 `cancel(handle)` 导出（D2）。

| 字段 | 类型 | 必填 | 取值 / 默认 |
| --- | --- | --- | --- |
| `kind` | string | 是 | `File` / `Device` |
| `path` | string | `kind=File` 时必填 | 用户原路径（D14 路径口径）；`Device` 时忽略 |
| `device` | object | 否 | `{ "serial"?: string }`，仅来源标注与排障；`File` 时忽略 |
| `mode` | string | 否 | `Off` / `On` / `Auto`，缺省 `Auto`；非法值回退 `Off`（spec） |
| `proxy` | object | `kind=File` 时必填 | `Device` 时忽略 |
| `proxy.pipeName` | string | 是 | 非空 |
| `proxy.launchFile` | string | 是 | 代理可执行文件路径 |
| `proxy.launchArgs` | array\<string\> | 否 | 缺省空数组 |
| `proxy.launchTimeoutMs` | number | 是 | 正整数 |
| `options` | object | 是 | — |
| `options.probeWindowBytes` | number | 否 | 正整数，缺省 8192（D5） |
| `options.probeThreshold` | number | 否 | 0–1 比值，缺省 0.10（D5） |
| `options.tempDir` | string | 是 | 暂存 / 采集目录（S17），`Device` 同样必填 |
| `options.sidecar.dir` | string | `kind=File` 时必填 | 缓存目录（D18）；`Device` 时忽略 |
| `options.sidecar.entryLimitBytes` | number | 否 | 正整数，缺省 1 GiB（D18） |
| `options.sidecar.dirLimitBytes` | number | 否 | 正整数，缺省 4 GiB（D18） |

- **校验口径**：数值不做字符串兼容（`"8192"` 判非法）；空字符串路径判非法；`path` 必须为绝对路径（盘符绝对 `C:\...` 或 UNC `\\server\share\...`），盘符相对 `C:foo`、根相对 `\foo`、普通相对 `foo\bar` 一律判非法（`field = "path"`），core 不按进程当前目录解析——core 跑在宿主进程内，cwd 随启动方式变化，相对路径会让显示路径与实际打开文件不一致。`INVALID_ARG` 的 `field` 取值形如 `kind`、`path`、`proxy.pipeName`、`proxy.launchTimeoutMs`、`options.tempDir`、`options.sidecar.dir`。`proxy.pipeName` 按派生后的实际名（基础名 + 进程标识后缀）判长度，超出管道名上限同样判 `INVALID_ARG`（D6）。
- **理由**：输出侧已 schema 化（D14），入参不锁会让 Rust serde、C# DTO 与 Node 各用一套键名，`INVALID_ARG` 的 `field` 也没有可断言的取值。沿用笔记 §3.2 的分组（kind / path / device / mode / proxy / options）让既有设置键直接映射；相对笔记新增的只有缓存目录与容量（D18）和嗅探注入项（D5）。
- **替代**：只锁最小集——`INVALID_ARG` 无字段可报，IT 无法断言是哪一项出错；沿用笔记的小写枚举——同一份 wire 出现两种枚举风格，绑定层要多写一套转换。

### D22. 事件线格式与 poll_events 返回
- **决策**：`poll_events(handle)` 一次返回此刻队列中的全部事件，载荷为 JSON 数组字符串（UTF-8），空数组合法；事件按产生顺序排列，不合并、不丢弃、不设上限。每个事件对象以 `type` 判别（取值与 spec 同名小写），字段逐类如下；未知 `type`、未知字段由宿主忽略（前向兼容）。关闭后调用返回 `CLOSED`、不返回数组（未取走的事件随关闭作废，生命周期见 D13）。渠道在 plan 阶段确定、受理后不变，「退回代理」同样发生在 plan 内，因此不设渠道变化事件。每个 `type` 必须有生产者，无生产者的事件类型不得留在清单里（`warning`、`channelChanged` 因此删除，见 D14 取值规则）。

| `type` | 字段 |
| --- | --- |
| `stage` | `stage`(string，D16 五值)、`value`(number)、`total`(number \| null) |
| `appended` | `rows`(number，本批增量)、`totalRows`(number，累计已装载行数)；仅采集源产生 |
| `stopped` | 无 |
| `done` | 无 |
| `error` | `code`(number，D15)、`params`(object，D15 键集) |

- **理由**：事件是宿主唯一的状态来源；D16 的阶段数值与 D15 的错误参数只有线格式固定后才能被绑定层与 IT 断言。走 JSON 与 D2「低频封送」一致（采集量级是每批一条 `appended`），`appended` 只带两个数值以压低高频路径成本，且只由采集源产生。`commit` 受理本身不发事件（D24）：文件源的 `done` 是装载完成的唯一信号，`commit` 返回码只说明受理；采集源没有 `done`，受理即进入 `Running`。
- **替代**：二进制事件数组——省解析，但与 D2 的封送口径分叉，两宿主各写一套布局；只发种类不带数值——宿主得再轮询进度对账，事件与批次无法一一对应。

### D23. 句柄状态与「状态 × 操作」矩阵

- **决策**：每句柄任一时刻处于下列之一：`PlanReady`（plan 成功、commit 未发起）、`Committing`（commit 进行中）、`Loaded`（文件源 commit 成功）、`Running`（采集 commit 成功）、`Stopping`（采集 stop 已发起、排空进行中）、`Stopped`（采集 stop 完成）、`Failed`（受理后的装载失败或采集落盘失败）、`Closed`（关闭、取消或关闭式中止之后）。操作合法性如下，表外组合一律 `INVALID_STATE`：

| 操作 | `PlanReady` | `Committing` | `Loaded` | `Running` | `Stopping` | `Stopped` | `Failed` | `Closed` |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `commit` | OK | `INVALID_STATE` | `INVALID_STATE` | `INVALID_STATE` | `INVALID_STATE` | `INVALID_STATE` | `INVALID_STATE` | `CLOSED` |
| `feed` | `INVALID_STATE` | `INVALID_STATE` | `INVALID_STATE` | OK | `STOPPED` | `STOPPED` | `FAILED` | `CLOSED` |
| `stop` | `INVALID_STATE` | `INVALID_STATE` | `INVALID_STATE` | OK | OK（幂等） | OK（幂等） | `FAILED` | `CLOSED` |
| `close` | OK | OK（中止该次 commit 并释放） | OK | OK | OK（中止排空，不发 `stopped`） | OK（幂等） | OK | OK（幂等） |
| `poll_events` | 空数组 | OK | OK | OK | OK | OK | OK | `CLOSED` |
| `get_progress` | `Opening` 初始快照 | OK | 最后阶段快照 | OK | OK（排空进度） | 最后阶段快照 | 失败时最后快照 | `CLOSED` |
| `get_descriptor` | `INVALID_STATE` | OK（当前值） | OK | OK | OK | OK | OK（最后快照） | `CLOSED` |
| `cancel` | OK | OK | `INVALID_STATE` | `INVALID_STATE` | `INVALID_STATE` | `INVALID_STATE` | `INVALID_STATE` | `CLOSED` |
| `free_handle` | `INVALID_STATE` | `INVALID_STATE` | `INVALID_STATE` | `INVALID_STATE` | `INVALID_STATE` | `INVALID_STATE` | `INVALID_STATE` | OK |

- **迁移**：`plan` 成功 → `PlanReady`；`commit` 受理后立即返回、状态仍为 `Committing`（D24），文件源装载完成 → `Loaded`，采集受理 → `Running`，装载失败 → `Failed`；`stop` 从 `Running` 出发 → `Stopping`（开始排空）→ `Stopped`；`Stopping` 上重复 `stop` 幂等等待首次结果，`close` → `Closed` 且不再发 `stopped`（被阻塞的 `stop` 返回 `CLOSED`）；`cancel` 从 `PlanReady` / `Committing` 出发 → `Closed`（等价中止并释放资源）；`close` 在这两个状态上等价 `cancel`——释放计划阶段已拉起的代理连接（计数 -1）与该阶段产物、不发终态事件；`close` 从任意非 `Closed` 状态 → `Closed`。排空期间落盘失败 → `Failed`：清队列、发 `error`（终态）、不发 `stopped`，被阻塞的 `stop` 返回 `FAILED`，已落盘行与采集文件保留、租约按 `Ephemeral` 由 `close` 处理。commit 受理前校验失败（含计划结果不可用）不改状态：句柄保持 `PlanReady`，计划阶段的代理连接按释放计数 -1、计划结果作废、不产出描述骨架；此后再次 commit 返回 `INVALID_STATE`，宿主先 `close` 再重新 `plan`。
- **`free_handle` 的前置**：只允许在 `Closed` 上调用，其余状态返回 `INVALID_STATE` 且不释放——把「先 close 再 free」从纪律变成可断言的前置，防止句柄在仍有资源或仍有 in-flight 操作时被释放（D13 的跨 FFI 安全由此闭合）。
- **理由**：留白让每个导出自行决定返回码，绑定层的错误映射与 IT 无法逐格断言；矩阵一页纸，两边都能直接照着写。plan 阶段选择「轮询返回空数组、进度返回 `Opening` 快照」而不是判非法，是因为宿主有统一的轮询循环，不需要为「还没 commit」多写一条分支。补 `Stopping` 列，是因为排空耗时且能被另一线程观察；没有这一列，排空期间的 `close` / `feed` / `free_handle` 只能由实现各自拍，正好落在「关闭与停止竞争」这个最容易写错的窗口。
- **替代**：只写「未定义操作返回 `INVALID_STATE`」不列表——把「随手定」换成「随手判非法」，IT 仍不可断言；`free_handle` 允许在任意状态隐式关闭——省一次调用，但会让「close 的返回语义」和「free 的清理语义」重新混在一起。

### D24. commit 执行模型：受理即返 + 事件完成

- **决策**：`commit(handle)` 只做受理前检查（状态、请求与 plan 结果仍可用），通过后进入 `Committing` 并立即返回 `OK`；暂存、解码、索引与缓存复核在 core 内部线程继续。文件源装载完成发 `done`、失败发 `error`，采集源受理即 `Running`。受理前失败（`INVALID_STATE` / `INVALID_ARG` / `CLOSED`，以及计划结果不可用的断线错误）同步返回，句柄停留原状态、不产出描述骨架；受理后的运行期失败只经 `error` 事件上报，句柄转 `Failed`，不回填已返回的调用。源描述经 `get_descriptor(handle)` 读取：`Committing` 返回受理时产出的骨架（静态字段终值（渠道在 plan 阶段确定）、文件身份与装载模式取 plan 阶段确定值（代理暂存与采集源身份为空；装载开始复核发现文件已变时一并更新一次，D18）、`rowCount` 从 0 起），`Loaded` / `Running` / `Stopped` 返回终值或最后值，`Failed` 返回最后快照（可能只有骨架）；描述可读不表示源路径所指产物存在或装载成功。`PlanReady` 判 `INVALID_STATE`、`Closed` 返 `CLOSED`（D23）。取消在 `Committing` 仍有效（D23），宿主以 `done` / `error` 判定完成。
- **理由**：同步阻塞要求绑定层强制「不得在 UI 线程调用 commit」；而 176 MB / 272 万行的装载要十几秒，宿主漏一处就把窗口连同取消按钮一起冻住，取消也只能从另一个线程改标志——等于把线程纪律写进契约却无法断言。受理即返让 `commit` 在任意线程安全返回，等待显式化为事件轮询，IT 可直接断言「返回时装载未完成」「运行期失败经事件而非返回」。描述符新增读取入口，是因为 `rowCount` 在受理后仍在变化：随 `commit` 返回只能给出零值且没有更新入口；塞进 `done` 又会分叉——采集源没有 `done`（终态是 `stopped`）。
- **替代**：同步阻塞——UI 易冻结、取消依赖额外线程约定；描述符随 `done` 下发——采集源无 `done`，线格式按源类型分叉；`commit` 返回描述快照 + 变更事件——新增事件类型，宿主需自行合并快照。

## Risks / Trade-offs

- [消化线程吞吐跟不上 adb 输出速率，无上限队列积压撑大内存] → 首个 IT 切片附压测用例（固定速率推入 10 万行量级，观测积压收敛）；超预期则回到 A14 重新评估背压，不改契约面。
- [FakeProxyServer 跨语言复用不成立（协议实现耦合了 C# 细节）] → apply 首个 IT 用例先行验证；不成立则 Rust 侧补最小 fake 对端，成本隔离在测试工程内。
- [Rust 切片一轮内达不到 IT 可跑（封送/构建链卡壳）] → tasks 按「先打通用例」排序，骨干用例先行；未完成部分如实以未验证口径交付，不声称通过。
- [JSON 封送在 append 高频路径的分配开销] → `feed` 路径不走 JSON（行数组直接封送），JSON 只用于低频的 plan/descriptor；`appended` 事件字段为数值。
- [mtime 等平台差异导致时间戳语义不稳] → identity 仅用于缓存命中比较，core 与宿主对同一文件的读数一致即可，IT 断言两次打开 identity 相等而非绝对值。
- [Rust 的键派生与 C# `ToUpperInvariant` / 全路径口径不一致，导致两侧互不命中] → 以现 C# 实现生成小样本键向量与 sidecar 字节（含非 ASCII 路径）入库 `logcore/testdata/` 作为 golden，UT 直接对拍，不一致即在本轮修。
- [迁移期两侧同时写同一缓存目录，淘汰与在用视图分叉] → 单一所有者约束：Rust 侧只用测试独立目录，生产 `cache/` 仍归 C#（D18）；原子替换不作为双写安全的依据。

## Migration Plan

无切流：本轮产物全部为新增（crate、绑定层、测试），C# 既有行为零改动（A13）。sidecar 缓存本轮只在测试的独立目录读写，生产 `cache/` 仍由 C# 独占（D18）；所有权移交与 C# 存储退役随切流 change。回滚 = 删除新增目录与绑定层引用，主链不受影响。切流与旧链清理（S12、§3.10）由后续切流 change 承接。

## Open Questions

- cargo llvm-cov 在 CI 的接入成本（笔记 §7 未评估项）——不阻塞 apply，随 `docs/TESTING.md` Rust 段落地时定。
- `feed` 单批 65536 上限是否需要在压测后调整——纯数值，可在切片内改，不动契约结构。
