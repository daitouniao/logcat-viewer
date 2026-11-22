# Design

## Context

刀2 收口后，分页读取的唯一读面是整份 `Payload`：`crates/core/src/page.rs` 的 `page_rows(payload, start, count)` 直接把 `start` 当文档行号；源句柄 `crates/core/src/source.rs` 的 `read_page` 再用 `PageRow.ord` 回查 `offs/lens` 取原文。LPAG v1 行记录里只有一个 4 字节行号字段（由 `encode_page` 写入），当前语义是结果集序号。

下一刀的过滤需要“结果集序”这一层；`open(filter)` 的契约已定为「结果 buffer 存 `u32` 文档行号、升序、只追加；无过滤用恒等视图、不复制」。本 change 只把这个终态结构在 core 内部先立起来，不引入查询句柄与扫描。

约束：

- 只动 `crates/core`；cabi 导出面、C# 绑定、LPAG 字节布局不变。
- 外部行为必须逐字节不变；现有页面测试应原样通过。
- 不提前定查询生命周期、异步扫描、`set_result` 等后续切片才需要的形状。

## 数据流举例

以过滤结果 `[0,2,5]` 为例（本刀仅测试构造）：

```text
对外请求 page(start=2, count=2)        // start 是真实文档行号
  定位：第一条 docRow ≥ 2 → ord 1（docRow 2）
  内核取 ord [1, 2] ──doc_row()──► docRow 2,5
      ──按 docRow 取数──► 文档第 2、5 行的级别/pid/tid/tag/moff/崩溃/原文
  encode: 行记录首字段 = docRow（2、5）
          页头第 4 字段 = 首行 ord（1）
```

- 恒等视图（`Identity { rows: 10 }`）时定位恒等、`doc_row(ord) == ord`，整页字节与改前一致。
- `read_page` 读原文的偏移仍是 `payload.offs[docRow]` / `payload.lens[docRow]`；改变的是“用哪个行号去索引”，不是索引方式。
- 顺序翻页传上一页末行的 `docRow + 1`；跳到真实行先 `resolve` 取最近结果行，再以其 `doc_row` 调 `page`。

## Goals / Non-Goals

**Goals:**

- 在 core 内提供永久形状的结果集模型：恒等视图与显式文档行号表，二者对页装配同形。
- 页装配改成「`ord` → `docRow` → 取数」，并让文件源句柄走恒等视图，验证抽象与现网路径等价。
- 提供 `resolve(doc_row) -> Option<u32>`：恒等视图算术钳位、显式结果集二分取最近，供后续 UI 锚点/MCP 直接复用。

**Non-Goals:**

- 不实现过滤、扫描、查询句柄、异步/取消/进度、`set_result`、`next_match`/`prev_match`/`context`。
- 不改变 `page`/`row_count` 的对外状态门禁与错误码，不改变 descriptor 的 `row_count`（仍是文档总行数）。
- 不为采集源建统一快照；`Running`/`Stopped` 仍不可分页。

## Decisions

### D1：结果集用 `Identity | Rows(Arc<[u32]>)` 两态枚举

```text
ResultSet
  Identity { rows: u32 }      // 无过滤：doc_row(ord) = ord，不分配
  Rows(Arc<[u32]>)            // 过滤结果：升序 docRow 表，Arc 便于快照克隆
```

- **为什么**：恒等视图必须零分配、零复制；显式结果集要能随查询快照便宜地克隆（`Arc`），页读取只借不拥有。两态枚举的匹配点只在 `len` / `doc_row`，后续若为采集追加加第三态（分块追加）也只扩这两个方法。
- **例**：`Rows([0,2,5])` 的 `len() = 3`，`doc_row(0) = 0`、`doc_row(1) = 2`、`doc_row(2) = 5`。
- **不变量**：`Rows` 严格升序、元素小于文档行数。构造只由 core 内部产生（本刀仅测试构造），用 `debug_assert` 守不变量；不引入运行期 O(n) 校验与错误码——本刀没有外部 `set_result` 入口，校验没有真实的系统边界。若将来出现外部推入，再在边界处加校验。
- **宽度口径**：数据面统一 `u32`——`Identity.rows`、`Rows` 元素、`PageRow.doc_row`、`resolve` 的入参与返回值、LPAG 字段；位置面（切片、循环、`len()` 边界比较）用 `usize`。转换只在两处：构造 `Identity` 时把 `payload.row_count()` 收窄（`debug_assert` 守 ≤ `u32::MAX`，与 `page.rs` 现有 `as u32` 惯例一致），内核用 `doc_row as usize` 取数组。`len()` 必须返回 `usize`：对外 `start` 可达 `i64` 范围，用 `u32` 比较会截断超大起点、错误翻出首页，破坏“起点越界返回空页”。
- **备选**：
  - 继续用隐式 `ord == docRow`：会让下一刀同时改分页语义、LPAG 字段与全部测试，否决。
  - 现在就做分块可追加结果集：采集追加没有生产者，属于假想需求；第三态是加法，不必现在付复杂度。
  - 用 trait 抽象行号访问：单实现、无动态分发需求，过度设计。

### D2：行记录写 `docRow`，页头第 4 字段保持“首行 `ord`”

- 行记录第一个 `u32` 从“结果集序号”改为文档行号 `docRow`；宿主从行记录直接拿真实行。
- 页头第 4 个 `u32` 维持今天的“首行序号”语义，写本页首行的 `ord`（不是首行 `docRow`）。宿主由 `ord = 页头值 + i` 推出每行在结果集中的排位，两种坐标一次下发。
- 恒等视图下两者数值相等，**字节输出与今天完全一致**。
- **为什么现在定**：字段只有一个槽位；若这次把它写成“首行 `docRow`”，将来查询页按真实行起页时宿主就推不出 `ord`，只能升版或加字段。当前 C# 绑定把行记录首字段读成 `Ord`，产品代码零引用；页头第 4 字段宿主从未读取。现在定语义是唯一零成本窗口。
- **代价**：C# 绑定在 cabi 那一刀把行记录字段改名为 `DocRow`，页头值仍叫 `Ord`，`ord = 首行Ord + i` 本地推导；这是既有计划中的绑定变更，不是 core 返工。

### D3：页装配拆成「按 docRow 取数」的内核 + 结果集包装

- 新增内核：给定一组 `docRow`（迭代器）与 `&Payload`，产出 `PageRow`（`doc_row`、级别、pid/tid、tag、moff、崩溃摘要）。
- 低层包装：`page_rows_for(result, payload, ord_start, count)` 取 `[ord_start, min(ord_start+count, len))` 的 `ord`，映射成 `docRow` 后交给内核；越界截短，起点越界返回空页。
- **坐标边界**：`page_rows_for` 的 `ord_start` 是内核坐标，不是对外契约。对外 `page(start, count)` 的 `start` 永远是真实文档行号；本刀源句柄是恒等视图，`start` 原样透传。将来查询句柄按真实行起页：入口定位“第一条 `docRow ≥ start` 的结果行”，没有则空页；拿到起始 `ord` 再调原语。
- `PageRow.ord` 改名为 `doc_row`；`read_page` 读取 `offs/lens` 改用 `doc_row`，文本读取与布局编码不变。
- **为什么**：把“结果集身份”“行内容装配”“对外入口定位”三层分开；下一刀的查询句柄只需做入口定位，不必再碰分页与文本路径。

### D4：`resolve` 两态实现——恒等钳位、显式二分

- 输入结果集与 `doc_row`，返回 `Option<u32>`（空结果集返回 `None`）。
- `Identity { rows }`：无表可二分，算术钳位——`rows == 0` 返回 `None`，否则 `Some(doc_row.min(rows - 1))`。恒等视图每行都是成员，不存在最近/平局；越界落末条。O(1)、零分配。
- `Rows(table)`：`partition_point` 二分——命中返回该 `ord`；不命中取左右相邻两项中距离更近者；距离相等取**前一条**；`doc_row` 小于首项取首、大于末项取末；空表返回 `None`。O(log n)。
- 与 `page` 的分工：`resolve` 面向“跳到真实行”（取最近，允许向前回退）；`page` 的入口定位只向后取（第一条 `docRow ≥ start`），保证顺序翻页不重复。UI 跳转先 `resolve`，再以结果行的 `doc_row` 调 `page`。
- **为什么分两支**：恒等视图按 D1 必须零分配，不能为复用二分而物化 `0..rows`；两态各走 O(1) / O(log n)，契约在两条分支上分别锁死。`Rows` 规则与现有 `NearestModelRow` 的「二分找最近存活行、平局取前」一致，UI 锚点不需要另写规则；空结果集用 `Option` 把解释权留给调用方，不发明错误码。

### D5：源句柄只接恒等视图，不引入查询句柄

- `read_page` 内构造 `ResultSet::Identity { rows: payload.row_count() }`，走新装配路径。
- 对外 `page(start)` 的 `start` 原样传入恒等结果集，仍是文档行号，语义与今天相同。
- `row_count()`、状态矩阵、cabi 导出均不动。
- **为什么**：这是让新抽象经过真实路径、同时把外部行为锁死的验收手段；查询句柄等到 `open(filter)` 有真实调用方时再定生命周期与状态。

## Risks / Trade-offs

- **`Rows` 分支在下一刀前没有生产消费者** → 已定甲：保持独立 change，用 UT/IT 直接构造 `[0,2,5]` 之类结果集覆盖取页与 `resolve`；过滤落地后该分支才有真实生产者。
- **LPAG 行记录字段语义变了、页头字段语义不变** → 行记录首字段由序号改为 `docRow`，页头仍是“首行 `ord`”；恒等视图下两者数值相同、C# 产品零引用，现有 IT 全绿。C# 绑定在 cabi change 中把行记录字段改名 `DocRow`，并按页头值本地推导 `ord = 首行Ord + i`。
- **`debug_assert` 不守运行期不变量** → 本刀没有外部入口；下一刀扫描是唯一生产者，构造点集中，测试黄金集覆盖升序与范围。若将来引入外部推入，必须在边界加校验与错误码。
- **页头第 4 字段从“信息位”变成宿主推导 `ord` 的依据** → 本刀宿主不读；下一刀的 C# 绑定读它推 `ord`。字段偏移与数值口径（首行 `ord`）不变，不构成兼容风险。

## Migration Plan

无持久化格式变化、无对外行为变化：回滚即回退本次提交；sidecar 缓存与 LPAG 字节无需迁移。C# 绑定在 cabi change 中随 `DocRow` 改名一起更新。