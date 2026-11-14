# 契约最终状态：source-acquire-contract

- 状态：规划产物已收口，无待决项
- 口径：只记录最终结论，不保留决策日期、过程叙事与被否决方案；行为契约见 `specs/logcore/source-acquire/spec.md`，实现决策见 `design.md`。
- 原始探索：`openspec/notes/2026-11-08-SOURCE-ACQUIRE-CONTRACT.md`

## 一、流程与句柄

- 两段式：plan 做存在性、格式、文本嗅探、代理路由、首尾样本与元数据读取、缓存命中判定，只读不写（不建索引、不建目录、不写条目）；plan 成功返回计划与句柄，commit 复用该句柄与判定结果。
- 判定环与「直接打开失败转代理」只在 plan 内完成；plan 返回的路由即最终渠道，commit MUST NOT 改渠道；受理后直接打开失败按运行期失败处理（码 12 + 错误事件 + `Failed`），不再尝试代理。
- commit 受理即返，返回值只表达受理；暂存、解码、索引与缓存复核在 core 内部线程继续。
- 受理前校验失败同步返回：状态 / 参数不合法按对应码；计划结果不可用（如计划连接已断）返回 11，句柄保持 `PlanReady`、释放该代理连接（计数 -1）、作废计划结果、不产出描述骨架；此后再次 commit 返回 `INVALID_STATE`，宿主先 `close` 再重新 `plan`。
- 受理后运行期失败只经错误事件上报，句柄转 `Failed`，不回填已返回的调用；失败只是状态，句柄保留至宿主显式释放。
- 完成信号：文件源以 `done` 为唯一完成信号；采集源受理即 `Running`，终态是 `stopped` 或 `error`；commit 受理本身不发布事件。
- 取消：plan 与 commit 受理后、装载完成前有效（句柄转 `Closed` 并释放计划阶段代理连接与产物）；装载完成后返回 `INVALID_STATE`。
- 八状态：`PlanReady` / `Committing` / `Loaded` / `Running` / `Stopping` / `Stopped` / `Failed` / `Closed`。状态 × 操作按 design D23 矩阵，表外组合一律 `INVALID_STATE`。
- `close` 在 `PlanReady` / `Committing` 上等价 `cancel`；在 `Stopping` 上中止排空、此后不发 `stopped`，被阻塞的 `stop` 返回 `CLOSED`。
- `close` 只释放源资源，不释放句柄；`free_handle` 仅 `Closed` 合法，其余状态返回 `INVALID_STATE` 且不释放资源；`close` 后各读取入口返回 `CLOSED`。
- plan 阶段：`poll_events` 返回空数组、`get_progress` 返回 `Opening` 初始快照、`get_descriptor` 返回 `INVALID_STATE`。
- 多活动源并存：core 不设数量上限、不回收、不因已有源拒绝；宿主负责关闭闲置源并保留最近使用的活动源；`BUSY` 保留不再触发。

## 二、文件源、身份与缓存

- 请求 `path` MUST 为绝对路径（盘符绝对或 UNC），相对路径一律 `INVALID_ARG`（`field = "path"`）；`displayPath` 原样保留，不规范化、不解析符号链接、不改分隔符与大小写；`sourcePath` 为实际打开路径（超长补 `\\?\` 或 `\\?\UNC\`），失败时 `params.path` 回显原路径。
- 身份四要素：`size`、`mtimeMs`、`headHash`、`tailHash`（首尾各 64 KB FNV-1a，16 位小写十六进制；不超过 64 KB 的文件复用头哈希），空身份整体 `null`。
- 身份只属于本地直读源：plan 读首尾样本算全身份并判定缓存命中（只读），描述骨架在受理时即给出身份与装载模式，commit 开始复核，文件已变则两者一并更新一次、转 `Progressive` 完整解析；代理暂存源与采集源整体为 `null`（不适用），不补算暂存文件身份。
- 缓存归 core：键派生沿用既有实现，字节格式冻结 v1（旧缓存直接复用），缓存不含文本，只服务本地直读源。
- 命中（且复核一致）为 `OneShot`、跳过解析；未命中、身份不一致、条目损坏或版本不符为 `Progressive`；低版本条目原地重写，高版本不覆盖；任何缓存异常静默降级，不发布错误、不使获取失败。
- 写入走临时文件 + 原子替换；容量按条目与目录两级上限（可由快照覆盖，缺省 1 GB / 4 GB）、LRU 回收，先淘汰到装得下，新写必留、在用必留，单条超上限不缓存。
- 在用 = 本进程内未关闭的本地直读源所对应的缓存键：命中或写回时登记（引用计数），关闭 / 取消 / 失败收尾时注销；容量回收跳过在用条目；`startup_maintain` 以空在用集合运行，可回收其他进程的条目，后果仅为该文件下次获取重新解析；`kept` 为回收后保留的有效条目数。
- 按 route 取值：`LocalDirect` → `ReloadOnly` / `None` / `null` / `Original`；`ProxySpool` → `ReloadOnly` / `Ephemeral` / 暂存目录名 / `Original`；`DeviceStream` → `Appendable` / `Ephemeral` / 采集文件名主干 / `Original`。每个枚举值必须有生产者，无生产者删除或标预留（`Retained` 已删，`Relative` 预留）。
- 启动维护：无句柄、幂等；缓存侧校验条目、清理损坏与写入残留、容量回收；临时目录侧以锁标记判定存活（可独占视为无主，删产物与标记；被占用则跳过；产物无标记视为无主；孤立标记删除；不碰不匹配命名）；目录不存在视为空且不创建；任一目录失败不抛错、不阻断后续获取。

## 三、采集

- `feed` 异步入队即返；行数据在返回前深拷贝为 core 自有内存，宿主可立即释放或复用缓冲；队列无上限、不丢行。
- 单批超过 65536 行整批拒绝，返回 `INVALID_ARG`（`field = "lines"`），不部分入队、不发事件；上限随绑定层编译期常量发布，core 不提供查询入口。
- 单消化线程按入队顺序解析、追加索引、落盘、发事件；每批完成发增量事件，仅在行数增长时发布，空批次不发。
- 采集文件固定 UTF-8 无 BOM、行尾 CRLF，由 core 创建并追加；路径经描述 `sourcePath` 交还宿主。
- `stop` 前置：宿主先停止取流并推完残余；进入排空后 `feed` 返回 `STOPPED`，`stop` 幂等；排空完成发 `stopped`（终态）并保留采集文件供保存。
- 排空进度只描述本次排空：进入归零，总量为发起停止时的队列行数快照，结束时两者相等；累计行数取自增量事件的累计值或描述行数。
- `close`：停消化 → 作废未消化队列 → 尽力删除采集文件 → 释放其余源资源 → 同步返回；不进入排空、不发终态事件、未取走的事件随关闭作废。
- 产物删除尽力而为：外部占用导致删除失败时不改 `close` 返回、不发事件、不改状态，锁标记照常解除，残留由下次启动维护回收；无外部占用时关闭后不留任何采集产物；宿主不得以产物是否存在判定关闭结果。
- 排空期间落盘失败 → `Failed`：清队列、发 `error`（终态）、不发 `stopped`，被阻塞的 `stop` 返回 `FAILED`，已落盘行与采集文件保留，租约由 `close` 按 `Ephemeral` 处理。
- 采集源无用户路径：`displayPath` 为 `null`，标题与最近使用由宿主按渠道决定；`identity` 不适用。

## 四、描述、事件、进度与错误

- 描述骨架在 commit 受理时产出：渠道等静态字段为终值；身份与装载模式取 plan 阶段确定值（复核可更新一次）；`rowCount` 从 0 起；`Failed` 返回最后快照（可能只有骨架）；描述可读不表示产物存在或装载成功。
- `poll_events` 一次返回全部待处理事件，JSON 数组，空数组合法，不合并、不丢弃、不设上限；关闭后返回 `CLOSED`。
- 事件类型只有 `stage` / `appended` / `stopped` / `done` / `error`；未知类型与未知字段由宿主忽略；无 `warning`，无渠道变化事件。`appended` 仅采集源产生；文件源装载不发增量事件，行数经描述 `rowCount` 读取。
- 终态按源类型分派：文件源 `done` / `error`，采集源 `stopped` / `error`；同源至多一个终态，终态后不再发该源装载事件；plan 失败只经同步返回，不建句柄、不占事件通道。
- 进度为 `{ stage, value, total | null }` 原子快照；五阶段 `Opening` / `Spooling` / `Indexing` / `Capturing` / `Draining`，按所属路径固定顺序推进、不倒退；阶段内 `value` 单调不减，切换时归零；无总量为 `null`；终态后返回最后快照；core 不算百分比。
- 错误码：`0 OK`、`1 BUSY`（保留）、`2 STOPPED`、`3 FAILED`、`4 CLOSED`、`5 INVALID_STATE`、`6 INVALID_ARG`、`7 INTERNAL`、`10 PROXY_LAUNCH_TIMEOUT`、`11 PROXY_DISCONNECT`、`12 DIRECT_OPEN_FAILED`、`13 NOT_FOUND`、`14 UNSUPPORTED_FORMAT`、`15 IO_ERROR`、`16 PROXY_LAUNCH_FAILED`；`params` 只含数据键，不含文案；宿主忽略未知码与未知键；代理失败且直开也曾失败时附 `directOpenError`，直开失败后经代理成功不发事件。
- 代理实例：进程内按实际管道名登记、拉起单飞、多源共享；取得连接计数 +1（plan 阶段），释放计数 -1（关闭 / 取消 / 失败收尾，含受理前发现连接已断）；归零且由 core 拉起时结束进程。外部代理不登记、不结束。core 自拉起使用派生名 `<基础名>-<pid>`（超 256 判 `INVALID_ARG`，`field = "proxy.pipeName"`）。`shutdown` 只结束本进程拉起的实例并清空登记，中断的连接按 11 处理，幂等。

## 五、测试与边界

- 本阶段不提供行 / 列读取接口；IT 以描述行数、文档大小与落盘文件逐行内容为准，列断言留下一阶段。
- 迁移期生产 `cache/` 仍由 C# 独占；Rust 缓存只在测试独立目录验证，禁止两侧同写。
