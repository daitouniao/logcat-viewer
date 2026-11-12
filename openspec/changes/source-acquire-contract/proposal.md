# Proposal：source-acquire-contract（源获取契约 + Rust core 切片）

## Why

源获取（文件读入 + ADB 采集）目前是窗体里的两条异源入口：判定/探测/代理决策散在 `frmMain` 与 `Services/FileProxy*`，采集另走一条不经过装载入口的独立链路。要把获取链迁入 Rust core（Rust 重写方向刀5），必须先把这条链收成一份显式契约，且按 A7 本轮即产出 Rust core 可运行切片——契约与实现同轮交付，不再走「先定契约、C# 当参照、后换语言」的三步走。

探索定案全部来自 `openspec/notes/2026-11-08-SOURCE-ACQUIRE-CONTRACT.md`（A1–A9、A11–A14（A10 已由 A11 作废）、S 系列默认解、§3 契约草案、§3.11 验证规格），本 change 是其 propose 落地，不重新讨论已定案项。

## What Changes

- 新增两段式源获取契约（plan → commit）：UI 传「目标 + 配置快照」，Rust 判定文件源的路由/格式/编码（采集源按请求声明的设备流处理），返回三轴拆开（channel / format / encoding 正交）的源描述。
- 新增 Rust core 最小切片（A7）：文件读入全链（存在性/格式判定/嗅探 → 代理决策 → spool → 直读 + identity 四要素 + sidecar 缓存命中与落盘）+ 采集输入（A8），本轮即承载 UT 主体（cargo test），IT 经 FFI 直打 Rust 实现。
- 新增采集推入通道（A11 / A14）：宿主（仅 GUI，A9）取流后经 `feed(handle, lines[])` 异步入队完整行，core 消化线程解析 → 索引追加 → 落盘 → 发事件；无上限队列不丢行；宿主先停取流并推完残余再 stop / close，stop 排空后发停止事件，close 同步排空返回后释放源资源（句柄由宿主显式释放），排空进度可轮询。
- 新增事件通道（§3.7 轮询制，不用回调）：stage/progress、channelChanged、appended、warning、stopped/done/error(code, params)（含失败分类与直接打开失败附注；终态按源类型分派——文件源 done/error、采集源 stopped/error，close 不发终态事件）。
- 配置边界（A12）：Rust 不读设置文件，配置一律由宿主传快照（mode / proxy / options / 暂存目录 / 缓存目录与容量上限）。
- 新增 sidecar 缓存与启动维护：core 接手缓存键派生、冻结既有字节格式、命中判定与原子落盘；命中跳过解析（装载模式为一次到位），未命中完整解析后写回；宿主在进程启动时调用一次维护入口，校验条目、清理损坏与写入残留、按容量回收。
- C# 侧仅新增 FFI 绑定层与封送（A13）：不改既有获取链任何行为，旧链不切流、不清理。
- 取消超大文件确认（A5）与旧链清理（§3.10 清单）**不在本轮执行**，随切流 change 落地。

## Capabilities

### New Capabilities

- `logcore/source-acquire`：源获取契约的行为面——plan/commit 两段式、三轴源描述、代理 spool、采集推入（feed/stop/close 语义）、事件通道、sidecar 缓存复用与启动维护、取消口径、多活动源并存与生命周期外部管理。分类 `logcore` 承接 Rust core 宿主（分类登记见 config.yaml 规范分层段；本轮 Rust 切片未接入 UI，故 `old/` 既有 capability 的契约暂不动，切流 change 再按「动谁迁谁」迁移）。

### Modified Capabilities

（无——A13 本轮不改 C# 现有代码，`old/` 各 capability 行为不变。）

## Impact

- 新增 Rust crate（目录与命名见 design.md），cargo 产出 cdylib + cargo test；`logcore/testdata/` 新增由现 C# 实现生成的小样本键向量与 sidecar 字节（golden，只加不改）；`docs/BUILDING.md` 增补构建步骤、`docs/TESTING.md` 新增 Rust 段（随 apply 落地）。
- 新增 C# FFI 绑定层与封送代码（新增文件，不改既有链路）；测试工程新增绑定层 UT 与 FFI IT 用例。
- `openspec/config.yaml`：规范分层段登记新分类 `logcore`（本 change 落 delta 前置）。
- 不影响：既有 UI 行为、`old/` specs、MCP 侧（Node 不接采集，A9）、DLT 解码（随 DLT change）。

## Non-Goals（非目标）

- DLT 解码实现（S9 维持，随 DLT change）。
- 采集取流实现（留 C#：adb、设备枚举、断连语义均不动）。
- 采集字节保真改造（C21）；将来设备侧 GBK 走字节推入，本轮不做。
- UI 交互改动（stop/close 进度条属切流期接线，不在本轮）。
- sidecar 字节格式升级与校验和（C6，另立 change）；切流前生产 `cache/` 仍由 C# 独占，Rust 侧缓存只在测试独立目录验证，所有权移交与 C# 存储退役随切流 change（S12）。
- 旧链切流与清理（S12、§3.10 清单顺延至切流 change）。
- 多源回收策略（超时阈值、保留哪个源）由宿主实现，core 不内建超时回收（Q8）。
- MCP 侧采集能力（A9：采集只属 UI）。
