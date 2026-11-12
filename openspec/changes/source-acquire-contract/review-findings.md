# 审查记录：source-acquire-contract

- 状态：S1–S5、P2.*、P3.* 与采集终态 / 收尾时序已全部收口，无待定项
- 日期：2026-11-12
- 来源：proposal / specs delta / design / tasks 四件产物的一致性审查，与 `openspec/notes/2026-11-08-SOURCE-ACQUIRE-CONTRACT.md` 交叉核对

## 已应用（2026-11-12）

- S1 编码口径：采集文件固定 UTF-8 无 BOM + CRLF（宿主已按 UTF-8 解码后推入）；`feed` 超上限推入被拒且不入队；文件源编码判定归 core、本轮仅 UTF-8、检测随独立 change。
- S2 失败分类：spec 新增 Requirement「失败分类」+ 3 场景；design D6 补失败种类；proposal 事件条目补注。
- S3 关闭后保留：spec「采集收尾语义」补场景「关闭后采集文件保留」。
- S4 绑定层落位：design D11（`logcat-viewer/` 新增文件、不改 csproj / slnx、类型 public）；tasks 6.1 / 8.1 同步。
- S5 `sidecarHit`：移出 core 产物，由宿主按 identity 查自己的 sidecar 存储（2026-11-12 方向调整后改由 core 自查缓存，命中以 `loadMode` 表达，见文末）。
- P2.6 / P2.8 / P2.9 / P2.10：采集跳过探测场景、格式编码不变 / 配置快照 / plan 取消用例。
- P2.12 / P3.13 / P3.15：A10 引用、采集文件名、`config.yaml` 分类句。

## 已收口：采集终态事件语义与退出等待（2026-11-12）

- 终态按源类型分派（方案甲）：文件源 `done` / `error`，采集源 `stopped` / `error`；同源至多一个终态事件，终态后不再发该源装载事件；`close` 同步排空、不发终态事件。
- 落点：spec「采集收尾语义」「事件通道为轮询制」补需求与 4 个场景（先停取流、只消化已入队行、超时放弃须显式记录、关闭不发终态）；design D4 事件清单补 `stopped` 并写分派理由；proposal 事件条目同步。
- 退出策略 c：`stop` / `close` 由宿主在停止取流并推完残余后发起，core 不停止宿主取流；`close` 同步阻塞、返回即源资源释放且已入队行全落盘；宿主不得在 `close` 返回前退出；等待上限由 6.3 压测吞吐推导，超时后显式选择「继续等待 / 放弃并退出」，放弃记录为数据丢失。落点为 design D12 与 tasks 4.4 / 6.2 / 6.3。

## 已收口：句柄生命周期、stop 合法性、plan 句柄、事件轮询义务（2026-11-12）

- 句柄（Q1 甲）：`close` 只释放源资源，句柄保留至 `free_handle`；关闭后 `feed` / `poll_events` / `get_progress` 安全返回 `CLOSED`；关闭期间允许另一线程并发轮询。落点 spec「取消与不变量」、design D13、tasks 1.3 / 6.1 / 6.2。
- stop 合法性与幂等（Q2 偏向默认）：`stop` 对文件源返回 `INVALID_STATE`；`Failed` 上返回 `FAILED`；`stop` / `close` 幂等且不重复发事件；错误码表增 `INVALID_STATE` / `INTERNAL`。落点 spec「取消与不变量」、design D3、tasks 1.2 / 4.4。
- plan 句柄（Q3 甲）：`plan` 返回句柄，`commit` 复用判定与已拉起代理，`cancel` 两阶段有效。落点 spec「两段式获取流程」、design D2、tasks 1.3。
- 事件轮询义务（Q4 甲）：事件不设上限、不丢不合并；宿主 MUST 周期轮询以控内存。落点 spec「事件通道为轮询制」、design D4、tasks 5.1。

## 已收口：描述符 JSON schema（2026-11-12）

- 方案甲：收编笔记 §3.4 值表进 design D14，删 `sidecarHit`、删 `channel.DltDecoded`（DLT 产物仍是文件源）、`format` 首版仅 `Logcat`；补 `loadMode`、`identity` 结构与 `warnings` 结构；`identity` 空值为整体 `null`；哈希用 16 位小写十六进制字符串、`mtimeMs` 用整数毫秒（Node JSON 精度）。落点 design D14、tasks 1.3 / 2.5 / 6.1。

## 已收口：错误码表与 error.params（2026-11-12）

- 方案甲：design D15 扁平表（0–7 状态类、10–15 失败类），`params` 只含语义键与数值、无文案；代理失败且直开也失败时追加 `directOpenError`；宿主忽略未知键与未知码（前向兼容）。落点 design D6 / D15、tasks 1.2 / 5.1 / 6.2。

## 已收口：stage 枚举与进度口径（2026-11-12）

- 方案甲：design D16 定五阶段（`Opening` / `Spooling` / `Indexing` / `Capturing` / `Draining`）、各路径固定顺序、`get_progress` 返回 `{stage, value, total|null}`；装载 bytes、采集中与排空 rows，无总量为 `null`；core 不算百分比，宿主自换算；`stage` 事件仅在切换时发布。落点 spec「事件通道为轮询制」、design D16、tasks 5.1 / 6.2。

## 方向调整：多活动源（2026-11-12，维护者定）

- 决策：取消「同一时刻至多一个活动源 / 第二个返回 BUSY」。MCP 需要同时打开多个文件源，采集与文件也可多实例并存；core 不设数量上限、不自行回收，生命周期归宿主外部管理；宿主超时回收时 MUST 保留最近使用的活动源。
- 覆盖关系：本条取代笔记 A3 的「采集与文件双向互斥」与笔记 §3.5 第 4 条的单活动源约束；GUI「切换源先停采集再关旧文档」降级为宿主策略，不进 core。
- 落点：spec「取消与不变量」改为多源并存 + 4 个场景（并存 / 关闭隔离 / core 不回收 / 宿主保留最后源）；design D3 / D13 / D15（`BUSY` 转保留码）/ 新增 D17；tasks 1.2 / 3.3 / 6.2；proposal 能力描述与非目标。

## 已收口：临时产物命名与唯一性（2026-11-12）

- 方案甲：采集文件 `logcat_live_<yyyyMMdd_HHmmss>_<seq>.log`、暂存目录 `logcat_spool_<seq>`，`seq` 为进程内全局原子计数；一律独占创建、撞名递增重试。时间戳只承担可读性，唯一性由 `seq` + 独占创建保证，不引入毫秒或 GUID；崩溃残留只导致跳号。落点 design D7 / D14（`leaseId`）、tasks 3.1 / 4.1。

## 已收口：UNC / 长路径口径（2026-11-12）

- 方案甲：`displayPath` 恒为请求原路径（不规范化、不解析符号链接、不改分隔符/大小写）；`sourcePath` 为实际打开路径，仅超长绝对路径补 `\\?\` / `\\?\UNC\`；失败时 `params.path` 回显原路径。落点 spec「入口判定归 core」、design D14、tasks 2.3 / 3.2 / 6.2。

## 方向调整：sidecar 入 core 与启动维护（2026-11-12，维护者定）

- 决策：sidecar 缓存（键派生、字节格式、命中判定、读写、容量）并入本轮 Rust 实现，覆盖 S14 甲的「存储留 C#」；装载起点新增命中判定流程（命中 → `OneShot` 且不进入 `Indexing`；未命中 → `Progressive`）；进程启动新增一次维护流程（结构校验、清理损坏与写入残留、容量回收）。
- 覆盖关系：本条取代 S14 甲；S5 的「`sidecarHit` 不入 core 产物」仍成立——命中改以 `loadMode` 表达，不新增布尔字段。C6（格式升级/校验和）与缓存所有权移交仍不在本轮。
- 默认口径（未另行拍板即按此）：① 字节格式冻结在现有版本（8 段宽度），旧缓存直接可用；② 键派生沿用既有 C# 规则，以 golden 键向量与字节对拍把关；③ 迁移期单一所有者——生产 `cache/` 仍归 C#，Rust 缓存只在测试独立目录验证；④ 容量沿用现有口径（条目 1 GB、目录 4 GB、LRU、新写必留），缓存目录与两级上限经快照传入；⑤ 只服务本地直读源，命中仍须打开源文件取文本；⑥ 维护入口无句柄、幂等、失败不阻断获取。
- 落点：spec 新增「sidecar 缓存复用」「启动维护流程」两 Requirement（共 11 个场景），并同步「三轴源描述」「配置只经快照传入」；design D2（导出面加 `sidecar_maintain`）、D5（归属反转）、D9（测试分层）、D14（`loadMode` 取值规则）、D16（命中路径不进入 `Indexing`）、新增 D18 / D19、Migration 与 Risks；tasks 3.4 / 3.5 / 5.1 / 6.1 / 6.2；proposal 能力与范围。
