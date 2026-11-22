# Proposal

## Why

刀1（索引内核）与刀2（分页读取）已归档。今天 core 只有一个“隐含”的结果集：结果集就是文档的全部行，结果集里的第 n 条永远对应文档第 n 行。分页接口 `page(start, count)` 里的 `start` 同时承担了两种含义——“取结果集第几条”和“取文档第几行”，两者被同一个数字掩盖。

下一刀的过滤会打破这种重合：假设过滤命中文档的第 0、2、5 行，那么“结果集第 1 条”（序号 1）对应“文档第 2 行”。如果等过滤落地时才引入这层区分，分页语义、LPAG 行号字段与全部既有测试会被同一批改动裹挟，出问题时分不清是过滤算错了还是分页取错了。

现在先把“结果集序号（`ord`）”与“文档行号（`docRow`）”拆成两层，用独立、可测试的内核固定下来。本刀没有过滤，结果集恒等于全量文档，`ord` 与 `docRow` 处处相等，因此**对外行为与 LPAG 字节输出都不变**，只是内部计算路径换了。

## 两个行号概念

| 概念 | 含义 | 全量 10 行文档 | 过滤后只剩第 0/2/5 行 |
| --- | --- | --- | --- |
| `docRow` 文档行号 | 行在**文档**里的位置，与 sidecar 索引、`offs/lens`、崩溃表直接对应 | 0,1,2,…,9 | 0,2,5 |
| `ord` 结果集序号 | 行在**当前结果集**里的排位，从 0 起连续递增 | 0,1,2,…,9 | 0,1,2 |

- **恒等视图**：结果集 = 文档全量，`doc_row(ord) = ord`，不复制任何行号数组。本刀唯一真实使用的形态。
- **显式结果集**：升序 `u32` 文档行号表，下一刀过滤的产物；本刀只由测试构造（如 `[0,2,5]`），用来验证抽象成立。
- **对外契约**：`page` 只接受真实文档行号；`ord` 是 core 内部的页坐标，不对外暴露。

## 一次取页：改前 / 改后

**坐标约定**：对外 `page(start, count)` 的 `start` 永远是**真实文档行号**，本刀与将来过滤后都不变；`ord` 只活在 core 内部。

**改前**：`page_rows(payload, start, count)` 把 `start` 直接当文档行号，按 `start..start+count` 逐行取列、字典、崩溃与原文。

**改后**：分三步——

1. 入口定位：在结果集里找第一条 `docRow ≥ start`（恒等视图下就是 `start` 本身）；
2. 内核取页：从定位到的 `ord` 起取连续一段，映射回 `docRow`（恒等视图下就是 `ord` 本身）；
3. 内核按 `docRow` 取列、字典、崩溃与原文；布局编码把 `docRow` 写进行记录首字段，页头第 4 字段写本页首行 `ord`。

以过滤结果 `[0,2,5]`、请求 `page(2, 2)` 为例：定位到 `docRow` 2（`ord` 1）→ 取 `ord` 1、2 → 映射为 `docRow` 2、5 → 返回文档第 2、5 行；行记录首字段是 2、5，页头第 4 字段是 1。恒等视图下定位与映射都是恒等，整条链路与今天逐字节相同。

顺序翻页传上一页末行的 `docRow + 1`；跳到某个真实行时先 `resolve(doc_row)` 取最近结果行，再以它的 `doc_row` 调 `page`。

## What Changes

1. **结果集模型**：core 内新增 `ResultSet` 两态枚举——
   - `Identity { rows }`：无过滤，`doc_row(ord) = ord`，零分配、零复制；
   - `Rows(Arc<[u32]>)`：显式文档行号表，严格升序、元素小于文档行数；`Arc` 便于后续查询快照便宜克隆。
   只提供 `len()` 与 `doc_row(ord)`；本刀不提供追加或写入接口。
2. **页装配分层**：新增“按 `docRow` 取数”的内核与低层包装 `page_rows_for(&ResultSet, &Payload, ord_start, count)`（`ord_start` 是内核坐标，不是对外契约）；`PageRow.ord` 改名 `doc_row`。越界截短、起点越界返回空页。
3. **LPAG 字段语义**：行记录首字段由“结果集序号”改为 `docRow`；页头第 4 个 `u32` 保持“首行序号”语义，写本页首行 `ord`。宿主由 `ord = 页头值 + i` 推出每行排位、由行记录拿真实行。LPAG v1 的 magic、version、长度与字段偏移都不变；恒等视图下两者数值相同，字节输出不变。
4. **源句柄接线**：`read_page` 内构造 `ResultSet::Identity`，对外 `start` 原样透传（仍是文档行号），文本偏移按 `doc_row` 回查 `offs/lens`；`row_count()`、状态矩阵、descriptor 行数都不动。
5. **`resolve(doc_row) -> Option<u32>`**：两态实现——恒等视图直接对应并把越界钳到末行（O(1)、不分配）；显式结果集二分取最近，平局取前一条，越界取首/末；空结果集返回 `None`。显式结果集规则与现有 `NearestModelRow` 一致，供后续 UI 锚点与 MCP 直接复用。
6. **测试**：Rust UT/IT 覆盖非恒等结果集的取页（正文、时间区间、崩溃字段均按 `docRow` 取数）、`resolve` 边界、恒等路径逐字节不变。

## 为什么这算“纯内部重构”

- 对外接口（cabi 导出面、C# 绑定、MCP）无新增、无改名、无错误码变化；
- LPAG 字节布局不变，且本刀结果集恒为全量，`doc_row` 与旧序号数值相同，宿主读到的每个字节与今天一致；
- 现有 C# 侧页 IT 与 Rust 页 IT 原样通过即为证明。

## 非目标（out of scope）

- 过滤条件、扫描、大小写折叠、正则、`FilterExpr`、`open(filter)`；
- 查询句柄 / queryId / 注册表 / 查询状态机 / 异步扫描 / 进度 / 取消；
- 过渡接口 `set_result(ids)`（刀3 删除，本 change 不做）；
- `next_match` / `prev_match` / `context`；
- 采集读取与统一快照、可追加结果集；
- cabi 导出面、C# 绑定、UI、MCP；
- 任何对外行为变更与 spec 行为条款变更。

## Capabilities

### New Capabilities

无。本 change 为纯内部重构与地基，外部行为不变；已在 `.openspec.yaml` 设置 `skip_specs: true`。

### Modified Capabilities

无。

## Impact

- 代码：`crates/core/src/page.rs`、`crates/core/src/source.rs`、`crates/core/src/lib.rs`，新增结果集模块 `crates/core/src/result.rs`；仅 Rust core 与其 UT/IT。
- 接口：cdylib 导出面、cabi、C# 绑定、MCP 均不变；LPAG v1 字节不变。
- 数据：不新增依赖；不改变 sidecar 字节格式与缓存行为；采集源仍不可分页读取。
- 文档：`docs/TESTING.md` 随 Rust 用例条数变化同批更新。