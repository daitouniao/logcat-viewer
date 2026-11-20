# Proposal

## Why

刀1（索引内核）是 Rust core 重写「轨 A」的第一步：现有解析雏形在单箱 `logcore/`，缺方括号 long 格式、时间戳越界校验、崩溃打标与分块渐进，且缓存命中路径在 plan 阶段整份读取并解码索引（`sidecar.rs` 的 `probe`），大条目会拖慢描述返回，峰值内存约为索引本身的两倍。前置决策已定：命中判定保留在 plan，但只比对定长头部，解码移到装载开始（乙）。

## What Changes

- **目录重组**：把单箱 `logcore/` 拆成三箱 workspace（`crates/core`、`crates/cabi`、`crates/cli`），先于内核改造执行，后续刀次按箱落位。
- **解析内核**：补齐与 C# 基准的语义差额——方括号 long 格式、时间戳字段越界按未知元数据收、崩溃打标（Java / Native / ANR / Tombstone，绑定索引期父行）、8 列索引与 tag 去重表。
- **分块渐进**：未命中时按首块 4 MiB、稳态 32 MiB 逐块 Scan→Merge→原子发布（段化列存），块边界对齐换行、块尾 3 行前瞻，保证"分块结果 ≡ 整份结果"。
- **sidecar 写侧**：v1 字节格式不变；逐列写出、装载完成后交后台线程落盘（`done` 先发、退出不等待）、临时名补进程内唯一后缀。
- **sidecar 读侧（乙）**：plan 阶段只读定长头部（含身份四要素）做命中判定，不解码列；装载开始复核身份后解码，解码失败降级为完整解析。

**非目标（out of scope）**：

- 不动 C# 产品代码；UI 仍走 C# 旧线，Rust 侧只把索引写到测试目录并读回验证。
- 不做 C ABI 导出与分页（刀2）、不过滤入 Rust（刀3）、不做旧缓存一次性清理与生产 `cache/` 切流（刀5）。
- 不改 sidecar 字节格式；不改缓存键派生算法（切流后 Rust 自洽即可）；不新增缓存目录清理入口。
- 不上并行建索引；不做 DLT 解码；不做正则；不改 `docs/TESTING.md`。

## Capabilities

### New Capabilities

无。刀1 的解析 / 缓存语义以既有 C# 基线规范为权威来源（差额按基线对齐），Rust 内核尚未接入宿主；把相关 capability 迁入 `logcore/` 分类随切流刀次执行，本刀不制造双份契约。

### Modified Capabilities

- `logcore/source-acquire`：「sidecar 缓存复用」的命中判据限定为条目定长头部（magic / 版本 / 行数 / 段长形状 / 总长 / 身份四要素），字典与列数据不在 plan 阶段读取，命中条目在装载开始时解码、失败按条目损坏静默降级；「三轴源描述」的装载模式单次更新触发条件由"装载开始复核发现文件已变"扩展为"文件已变**或**命中条目解码失败"。

## Impact

- 代码：`logcore/` 目录结构重组为 `crates/core`、`crates/cabi`、`crates/cli`；解析、索引装配、sidecar 编解码、装载编排模块按箱迁移并补齐上述行为。
- 测试：新增 Rust UT / IT（`testdata/` 小样本 + 运行时生成的大样本），覆盖解析差额、分块 ≡ 整份、崩溃打标落点、头部探测命中、损坏条目降级。
- 依赖：新增 `arc-swap`（分块索引快照的无锁发布）；workspace 与 crate 边界变化。
- 不受影响：C# 绑定、C ABI 面、宿主调用契约、sidecar 字节格式。
