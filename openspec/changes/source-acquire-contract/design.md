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

- **决策**：导出面 = `plan` / `commit` / `feed` / `stop` / `close` / `poll_events` / `get_progress` / `cancel` / `sidecar_maintain` / `free_*`（内存释放配对；`sidecar_maintain` 不持句柄、不依赖活动源，见 D19）。请求与源描述（plan / descriptor）用 JSON 字符串（UTF-8）传入传出；错误码与返回状态用数值枚举表，两宿主共用一张表；句柄为不透明指针。
- **理由**：JSON 封送让 C#（P/Invoke）与 Node（koffi）共用同一布局，避免二结构体布局两处维护（B2 的跨宿主面）；错误码走数值表是因为它是唯一需要高频率、零分配传递的面。§3.7「不用回调」硬约束由此自然满足：事件靠 `poll_events` 拉取，进度靠原子计数 `get_progress`。
- **替代**：struct 直接封送——字段演进时两侧 ABI 同步成本高；protobuf——为 4 个消息引依赖不划算。
- **字段定死（B2 收口）**：`feed` 行数组 = `{const uint8_t* data, uintptr_t len}` 数组 + 总数，单次调用行数上限 **65536**（防误用；宿主实际批次 500 行远低于此），行内为 UTF-8 字节、不含行尾符（宿主侧已逐行产出）；空批合法。
- **句柄流（Q3 甲）**：`plan` 返回「计划 JSON + 句柄」，`commit(handle)` 复用同一句柄与 plan 阶段的判定结果及已拉起的代理连接；`cancel(handle)` 在 plan 与 commit 两阶段均有效，取消后句柄占用资源释放。不采用「plan 返回纯 JSON、commit 重新提交」，因为 plan 阶段可能已拉起代理，没有句柄就无处释放该连接。

### D3. 采集队列模型（A14 落地）

- **决策**：core 内每活动源一个消化线程 + 无上限 MPSC 队列。状态机 `Running → Stopping → Stopped` / `Running → Failed` / `Stopped | Failed → Closed`（close 排空完成后）；`close` 之后 `feed` 返回 `CLOSED`、不入队；`feed` 入队即返；消化线程循环「解析 → 索引追加 → append 落盘 → 事件入队」；`stop` 置状态后消化线程排空队列再 flush + 发 `stopped`；`close` 同样排空后只释放源资源（文件、消化线程、租约），句柄保留至 `free_handle`（D13），排空进度 = 已消化行数原子计数，经 `get_progress` / `poll_events` 可查。落盘失败 → 清队列、状态转 `Failed`、发 `error`，已落盘行保留。`stop` 对文件源返回 `INVALID_STATE`；`stop` / `close` 幂等——重复调用返回与首次相同的结果，不重复发事件；`Failed` 上 `stop` 返回 `FAILED`。每句柄一份独立状态机，core 不设全局活动源槽位，多个源可并存（D17）。
- **理由**：A14 已定丙-1（无上限队列、不丢行）；单消化线程使 `feed`/`stop`/`close` 竞争自然串行化，不需要额外的锁协议；队列无上限换来「永不丢行」，积压风险由 IT 压测覆盖（见 Risks）。幂等与非法状态返回是绑定层与 IT 的判据，不留给实现随手决定。
- **替代**：有界队列 + 背压——宿主侧被阻塞违反 A14 定案；多消化线程——解析可并行但落盘顺序与事件单调性要求串行，并行无收益。

### D4. 事件通道实现：core 内事件队列 + 轮询

- **决策**：core 侧每句柄一个事件队列（`stage`/`channelChanged`/`appended`/`warning`/`stopped`/`done`/`error`），`poll_events` 一次取走全部待处理事件；进度与排空计数用原子量另存，`get_progress` 无锁读取。终态按源类型分派：文件源为 `done` / `error`，采集源为 `stopped` / `error`；`close` 同步返回、不发终态事件，`stopped` 只在 `stop` 排空完成后发布。
- **理由**：与 §3.7 轮询制一致；Node 侧 koffi 不需要 threadsafe function，C# 侧 P/Invoke 轮询同样成立；事件量级（每批一条）下轮询成本可忽略。`stopped` 与 `done` 分列是因为采集停止是宿主主动收尾，与文件源「装载完成」语义不同，共用一个事件会让宿主无法区分主动停止与装载结束。
- **消费义务（Q4 甲）**：事件队列不设上限、不丢弃、不合并（增量与批次一一对应是既有契约）；宿主 MUST 按固定周期轮询以控制内存，迟轮询只影响内存、不丢事件。

### D5. 文件链判定实现：嗅探参数注入点（S3）

- **决策**：嗅探窗口（8 KB）与阈值（10%）作为 core 内默认常量，同时可被请求快照覆盖（仅这两项）；代理决策九格、判定顺序（存在性 → 格式 → 嗅探（仅 auto）→ 代理决策）在 core 内固定实现；identity 四要素（size、mtime、首尾各 64 KB FNV-1a）计算归 core；sidecar 缓存的键派生、命中判定与读写同样归 core（维护者 2026-11-12 定，反转 S14 甲，见 D18）。`sidecarHit` 仍不入 core 产物：命中结果以 `loadMode` 表达（D14），不新增布尔字段。文件源的编码判定同样归 core：本轮仅支持 UTF-8，检测启发式（笔记 S6 的 `utf8_cn_hits` / `gbk_cn_hits`）随独立 change 落地，不阻塞切片。
- **理由**：与探索笔记 S3/S14 定案一致；注入点只服务测试，不进设置界面（D14 规则）。

### D6. 代理 spool：core 实现代理管道客户端协议

- **决策**：core 按既有代理协议实现客户端侧（连接、拉起超时、spool 接收、三态出口 + 失败分类）；失败分类至少区分拉起超时、直接打开失败（附注保留）、运行中断线，码值与 `params` 键集见 D15；C# 侧既有 `FakeProxyServer`（协议无关、走命名管道）作为 IT 的对端复用。
- **理由**：判定与 spool 必须同在 core（判定环「读魔数失败回退代理再判」在宿主侧无法表达，A1）；FakeProxyServer 走命名管道、不绑宿主语言，笔记 §7 已标记可复用（推断，apply 首个 IT 用例即验证）。

### D7. 采集落盘格式

- **决策**：core 写采集文件，UTF-8 无 BOM、行尾 CRLF，落在请求快照的 `tempDir` 下；路径经描述符 `sourcePath` 交还；「保存日志」仍由宿主复制该文件，不新增导出接口。命名与唯一性：采集文件 `logcat_live_<yyyyMMdd_HHmmss>_<seq>.log`，暂存目录 `logcat_spool_<seq>`，`seq` 为 core 进程内全局原子计数；两者一律独占创建（文件 create_new / 目录 create_dir），已存在则递增 `seq` 重试。时间戳只负责可读性、不承担唯一性——多源并存（D17）下同一秒可启动多个采集；独占创建同时挡住进程重启后索引归零与两个进程共用 `tempDir` 两种覆盖。
- **理由**：字节口径复刻现状，将来切流后保存产物与历史行为一致；A11 已定落盘归 core。独占创建让唯一性不依赖毫秒或 GUID，崩溃残留只会让下次跳号，不影响正确性。采集路径不做编码判定：宿主已按 UTF-8 解码后推入完整行，非 UTF-8 字节在宿主侧已成为替换字符；字节保真属 C21 非目标。

### D8. panic 与内存边界

- **决策**：所有导出函数体包 `catch_unwind`，panic 转错误码；跨 FFI 的内存一律「谁分配谁释放」——core 返回的 JSON 字符串与行数组由 `free_*` 配对释放，宿主传入的内存在调用期间有效；crate 设 `panic = "abort"` 仅用于非 FFI 的 bin 目标，cdylib 保持 unwind 以便拦截。
- **理由**：FFI 边界不允许 unwind 外泄（既有重写探索 §4.5 的硬约束）；分配配对是最不易错的跨语言约定。

### D9. 测试分层落位（A6）

- **决策**：UT 主体 = Rust 侧 `cargo test`（模式判定九格、嗅探边界、路由、描述符组装、sidecar 键派生与命中/降级、缓存维护、事件不变量、推入/收尾语义、取消）；C# 侧新增绑定层 UT（封送、错误码映射、句柄生命周期、维护入口封送）与 IT（xUnit 经 FFI 打真实 core：本地直读、sidecar 二次打开与启动维护、FakeProxyServer spool、推入批次读回、失败路径、多源并存与关闭隔离），采集 IT 用预推批次模拟取流，不依赖真机 adb。Rust 覆盖率口径独立（cargo llvm-cov），不并入 C# 94.26% 口径。
- **理由**：§3.11 已定两层分工与判据（读回数据与预置期望逐项相等，不与旧实现比对）。

### D10. 构建链

- **决策**：`dotnet build` 不感知 Rust；`docs/BUILDING.md` 增补独立小节：cargo build 产出 dll → 绑定层按约定目录探测加载；缺失 dll 时绑定层抛明确错误（不影响既有功能，A13）。
- **理由**：切片未接线，C# 主构建链保持零依赖；文档承载新动作（§3.5 口径）。

### D11. C# 绑定层落位：`logcat-viewer/` 下新增文件

- **决策**：FFI 绑定层为 `logcat-viewer/` 下的新增源文件，不新建类库，不改 `logcat.slnx` 与 csproj（SDK 默认编译包含）；绑定层类型保持 public，测试工程已引用 `logcat-viewer`，UT 无需 `InternalsVisibleTo`。
- **理由**：A13 要求既有链路零改动；新增文件不触发构建配置变更，8.1 可按「既有工程文件未改」判定。
- **替代**：独立 `logcat-ffi` 类库——隔离更干净，但需改 slnx、csproj 与测试引用，构建面多一个单元，切片期无收益。

### D12. 宿主收尾时序与退出等待（退出策略 c）

- **决策**：`stop` / `close` 只由宿主发起，且必须在宿主停止取流并推完残余批次之后；core 不触碰宿主取流。`close` 为同步阻塞调用，返回即源资源已释放、已入队行全部落盘（句柄对象保留至 `free_handle`，见 D13）；排空期间宿主在另一线程轮询进度。宿主不得在 `close` 返回前退出进程；等待上限默认值由 6.3 压测得出的消化吞吐推导，不硬编码固定秒数；超时后由用户显式选择「继续等待 / 放弃并退出」，放弃须记录为数据丢失，不得静默。
- **理由**：core 无法停止宿主侧 adb 取流（A13 取流留 C#），残余批次只有宿主知道何时推完，因此收尾的「停止取流」半步必须归宿主；`close` 同步返回让「返回即安全退出」成为可断言的事实，比事件通知更不易误用。用户已选退出策略 c（超时可显式放弃），契约只约束「不得静默」，UI 呈现属切流期实现。
- **替代**：`close` 异步 + 终态事件——宿主需额外状态机区分「源资源释放」与「排空完成」，且退出路径要等事件，易写成竞态；固定 2 秒等待——与现状 `OnFormClosing` 的超时同形，但无吞吐依据，压测后按数据取值更稳。

### D13. 句柄生命周期与关闭期并发（Q1 甲）

- **决策**：`close` 只释放源资源（文件句柄、扫描线程、租约或临时目录），**不释放句柄对象**；句柄对象由宿主显式 `free_handle` 释放，与 D8「谁分配谁释放」同口径。`close` 返回后，对句柄的 `feed` / `poll_events` / `get_progress` 一律安全返回 `CLOSED`，不解引用已释放内存。宿主可在关闭期间从另一线程轮询进度，core 内部以关闭态标志与源资源独占保证安全。多句柄互相独立：关闭或释放一个句柄不影响其他句柄的状态与事件流；不存在「已有源」导致的拒绝（D17）。
- **理由**：若 `close` 直接释放句柄，关闭线程返回与另一线程 in-flight 的轮询构成跨 FFI 的 use-after-free，`catch_unwind` 无法拦截；把「源资源释放」与「句柄对象释放」拆开，既保住 `CLOSED` 返回值语义，也让并发轮询天然安全。代价是宿主在退出路径上多一次 `free_handle`，属既有 `free_*` 配对约定的延伸。
- **替代**：句柄引用计数——同样安全，但要引入原子计数与 in-flight 追踪，切片期复杂度高于收益；「宿主保证 close 返回前停止轮询」——靠纪律而非契约，漏一处即崩溃。

### D14. plan / 描述符的 JSON schema 与取值枚举（Q5 甲）

- **决策**：字段名沿用笔记 §3.4 的 camelCase，取值沿用其 PascalCase 枚举；按 S5 删除 `sidecarHit`（命中由 core 自行判定，结果以 `loadMode` 表达，见 D18）；补 `loadMode`、`identity` 结构、`warnings` 结构。两宿主（C# / Node）共用这一份 schema。

plan 返回：

| 字段 | 类型 | 取值 |
| --- | --- | --- |
| `route` | string | `LocalDirect` / `ProxySpool` / `DeviceStream`；`LocalDecode` 随 DLT change 加入 |
| `estimatedSize` | number \| null | 字节数；采集源为 `null`，不参与大小判定 |

描述符（commit 成功后返回）：

| 字段 | 类型 | 取值 / 说明 |
| --- | --- | --- |
| `channel` | string | `Local` / `FileProxy` / `AdbCapture`；删除 `DltDecoded` |
| `format` | string | 首版仅 `Logcat`；`Dlt` 随 DLT change 加入 |
| `encoding` | string | 首版仅 `Utf8`；`Gbk` 随编码判定 change 加入 |
| `columns` | array | `[{ "id": string, "labelKey": string, "width": number }]`，`labelKey` 为语义键（文案归宿主，B1） |
| `identity` | object \| null | `{ "size": number, "mtimeMs": number, "headHash": string, "tailHash": string }`；采集源为 `null` |
| `loadMode` | string | `OneShot` / `Progressive` |
| `rowCount` | number | 已装载行数 |
| `docSize` | number | 文档字节数 |
| `appendMode` | string | `Appendable` / `ReloadOnly` / `None` |
| `leaseKind` | string | `Ephemeral`（关闭即删）/ `Retained`（保留供保存） |
| `leaseId` | string \| null | 暂存 / 解码目录标识，无租约时为 `null`；有租约时等于唯一目录名 `logcat_spool_<seq>`（见 D7） |
| `timeSemantics` | string | `Original` / `Relative` |
| `displayPath` | string | 用户可见路径（标题 / 最近使用 / 导出命名） |
| `sourcePath` | string | 实际读取路径（代理暂存路径 / 采集文件路径），仅供内部使用 |
| `warnings` | array | `[{ "category": string, "count": number }]`，首版为空数组 |

- **数值与哈希编码**：`size` / `docSize` / `rowCount` 为整数；`mtimeMs` 为 Unix epoch 毫秒整数（不带时区字符串，避免两宿主解析口径分叉）；`headHash` / `tailHash` 为 64 位 FNV-1a 的 16 位小写十六进制字符串——不用 JSON 数字，因为 Node 的 `JSON.parse` 在 2^53 以上丢精度。
- **空身份表达**：整个 `identity` 为 `null`，不允许四字段各自 null。
- **路径口径（Q9 甲）**：`displayPath` 恒等于请求中的原路径——不规范化、不解析符号链接、不改分隔符与大小写；`sourcePath` 为实际打开路径，仅当原路径为绝对路径且长度超出常规上限时补 `\\?\`（UNC 补 `\\?\UNC\`）。打开失败返回 `DIRECT_OPEN_FAILED`，`params.path` 回显不经前缀改写的原路径，避免错误提示与用户所见不一致。
- **装载模式取值（2026-11-12 定）**：`OneShot` = plan 阶段命中 sidecar 缓存，commit 复用索引、不重新解析；`Progressive` = 未命中，本次解析建立索引。缓存只服务本地直读源（D18），代理暂存与采集源恒为 `Progressive`。
- **理由**：唯一有值表的是探索笔记，而笔记归档后冻结且已被 S5 改动；不收编会导致 Rust 结构体、C# 反序列化与 IT 断言各自发明一套。沿用原命名而非重起 camelCase 值集合，是为了让笔记里的判定规则、事件表与本 schema 保持同名，减少跨文档漂移。
- **替代**：重定义一套小写下划线值——命名更统一，但与笔记全面脱钩，评审时需逐值对照；让 serde 结构体当事实源——C# 与 Node 只能靠试错反推，且结构体私有字段会泄漏进线格式。

### D15. 错误码数值表与 error.params（Q6 甲）

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

- **附注**：代理失败且直开也曾失败时，错误的 `params` 追加 `directOpenError` 子对象（形如码 12 的 `params`），保留「直接打开失败」证据。
- **前向兼容**：宿主 MUST 忽略未知 `params` 键与未知码值，未知码按通用失败处理并记录原始数值；core 不得因宿主版本旧而改变码值。
- **理由**：1.2 的「数值不漂移」测试需要一份可锁定的表；失败分类用码而非字符串，跨语言拼写错误在编译期就能发现。码值按状态类（0–9）与失败类（10–15）分段，段内连续、段间留空便于追加。`params` 只放数据，宿主按码查文案，正好落实 B1。
- **替代**：分段编码（1xx/2xx/3xx）——扩展空间更大，但测试常量与文档表都更长，当前 14 个码不值得；只留 `FAILED`/`INTERNAL` + `params.reason` 字符串——失败分类退化为字符串比较，Rust 与 C# 拼写不一致只能靠 IT 发现。

### D16. stage 枚举与 get_progress 结构（Q7 甲）

- **决策**：`stage` 五值，按下表定义单位与总量；`get_progress` 返回 `{ "stage": string, "value": number, "total": number | null }` 的原子快照。core 不计算百分比，也不按阶段加权——权重与百分比归宿主（本轮不接 UI，切流期实现）。

| `stage` | 出现路径 | `value` 单位 | `total` |
| --- | --- | --- | --- |
| `Opening` | 全部 | 无（恒 0） | `null` |
| `Spooling` | 代理暂存 | 已暂存字节 | 暂存总字节 |
| `Indexing` | 文件源（直读未命中缓存 / 暂存后） | 已解析字节 | 文件字节数 |
| `Capturing` | 采集运行中 | 已解析行数 | `null`（无终点） |
| `Draining` | 采集 stop / close | 已消化行数 | 发起 stop / close 时队列行数快照 |

- **阶段顺序**：本地直读命中缓存 `Opening →（完成，无 Indexing）`；本地直读未命中 `Opening → Indexing`；代理暂存 `Opening → Spooling → Indexing`；采集 `Opening → Capturing → Draining`。阶段按所属路径推进、不倒退，路径内不跳阶段。
- **计数规则**：同一阶段内 `value` 单调不减；阶段切换时 `value` 归零重新计（宿主按阶段各自算比例）；终态后 `get_progress` 返回最后阶段快照，不再变化。
- **事件口径**：`stage` 事件只在阶段切换时发布，携带新阶段的 `{stage, value: 0, total}`；持续进度不逐次发事件，由宿主轮询 `get_progress`。这样事件量仍是「每批一条 `appended`」量级。
- **理由**：采集运行期客观上没有总量，只有「已解析行数」这一事实；排空的总量要等 stop 冻结队列才存在。绝对量 + `total=null` 能同时表达三种口径，IT 也能直接断言「排空结束时 `value` 等于提交行数」。百分比权重（笔记 `:259` 的 0–50 / 50–100）是状态栏呈现取舍，写进 core 会让换 UI 就要改 core。
- **替代**：core 直接出 0–1000 千分比——宿主零换算，但权重固化、采集中无总量、IT 只能验单调与封顶；只报 `stage` 不报数值——需改 spec 的「进度用原子计数」，且装载期与排空都失去量化信息。

### D17. 多活动源与生命周期外部管理（Q8 定案）

- **决策**：取消「同一时刻至多一个活动源」。同一 core 实例内可并存多个文件源与采集源，每句柄独立状态机、独立事件队列、独立进度；core 不设数量上限、不自行超时回收、不因已有源拒绝新句柄。回收策略归宿主：宿主负责关闭闲置源，且 MUST 保留最近使用的活动源；`BUSY` 码保留但不因第二个源触发。
- **理由**：MCP 宿主需要同时打开多个文件源，core 强加单源会把「谁该关」的决策塞进底层；GUI 的「打开文件先关旧文档」是宿主策略，与契约能力不是一回事。宿主在两次调用之间可能长时间思考，core 无法判断源是否仍在使用，内建超时回收会错误销毁文档。外部管理让两宿主各用各的策略，core 只保证句柄独立与资源释放正确。
- **替代**：保留单源 + BUSY——MCP 无法多文件；core 内建 LRU 超时回收——需要知悉宿主意图，误回收会丢文档，且回收阈值又要变成契约字段。
- **MCP 侧**：多源并存与外部回收都在宿主管辖内；采集仍只属 GUI（A9），MCP 只开文件源。GUI 切换源时沿用现行「先停采集、再关旧文档」，属宿主策略，不进 core。

### D18. sidecar 缓存：归属、格式与复用（维护者 2026-11-12 定）

- **决策**：sidecar 缓存的键派生、命中判定与读写全部归 core（反转 S14 甲的「存储留 C#」）。缓存目录、条目上限与目录上限由请求快照传入（缺省取当前生产值：条目 1 GB、目录 4 GB），core 不发明默认目录、不读设置文件。键派生沿用既有实现（全路径 + 文件名主干 + 路径大写不变式的 FNV-1a + `x16`），字节格式冻结在当前版本（`FormatVersion = 1`、8 段宽度），缓存不含文本——命中仍须打开源文件取文本。命中判定在 plan 阶段完成且只读（不建目录、不写条目），commit 开始时以四要素复核身份：一致则复用索引、跳过解析（`loadMode = OneShot`）；不一致、条目损坏或版本不符则完整解析（`Progressive`），低版本条目在原位置重写，高版本条目 MUST NOT 覆盖。写入走「临时文件 + 原子替换」；容量先淘汰到装得下、新写必留，单条目超上限则不缓存。只服务本地直读源，暂存与采集产物不入缓存。任何缓存读写异常静默降级，不发错误事件、不使获取失败。
- **理由**：sidecar 的输入（身份）已在 core，命中判定与装载路径同一条执行链；留在宿主就得把索引经 FFI 搬回再交给 core 装载，命中反而多一次跨语言拷贝。`loadMode` 只有同时握有身份与缓存的 core 才能给出，这是本轮最早暴露的缺口。格式冻结在同一版本，是因为迁移期旧缓存必须直接可用、且 C# 侧仍是运行时所有者；改格式会同时废掉两份。原子替换、淘汰优先与新写必留沿用既有容量口径，避免出现两套策略。
- **替代**：宿主查缓存（S14 甲原案）——`loadMode` 无判定依据；缓存格式升级并加校验和（C6）——另立 change，本轮不动；Rust 另起一套键与格式——旧缓存全废，切流时两侧互不命中。
- **迁移期单一所有者**：本轮 UI 未接 Rust（A13），C# 的 sidecar 存储仍是运行时唯一所有者；Rust 侧缓存只在测试的独立目录中验证，不指向生产 `cache/`。所有权移交与 C# 存储退役随切流 change（S12 清单）。迁移期内不得让两方同时写同一目录——原子替换只保证条目不半成品，不保证两侧的在用集合与淘汰视图一致。

### D19. 启动维护流程（维护者 2026-11-12 定）

- **决策**：新增无句柄导出 `sidecar_maintain(requestJson)`，由宿主在进程启动时调用一次；输入为缓存目录与两级容量上限，输出 `{ "status": string, "removedCorrupt": number, "removedTemp": number, "evicted": number, "kept": number, "freedBytes": number }`，`status` 取 `Ok` / `Partial`。动作：条目结构校验（魔数、版本、段宽、段长）、删除损坏条目与写入残留的临时文件、按 mtime 从旧到新淘汰至容量上限内。幂等：重复调用不产生额外删除。目录不存在视为空目录（返回零统计，不创建目录）；目录不可写等失败以 `Partial` 返回，不抛错、不阻断后续获取，也不删除结构可读的有效条目。
- **理由**：缓存改由 core 拥有后，容量与完整性的兜底需要一个与任何源都无关的入口；放在启动而非内建后台定时，是为了不引入定时器与跨源并发，也让 IT 用一次调用就能断言全部清理结果。本轮无 C# 改动（A13），该入口只由 IT 调用；切流期在宿主启动顺序中接线。
- **替代**：只在写入时淘汰（现状 C# 口径）——崩溃残留与外部破坏无人处理，目录只增不减；core 内建定时维护——多线程与生命周期复杂度上升，本轮无收益。

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
