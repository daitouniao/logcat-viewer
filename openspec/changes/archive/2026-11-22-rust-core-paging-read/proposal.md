# Proposal

## Why

刀1 索引内核已归档，Rust core 能建索引、发布段、读写 sidecar，但没有任何行/列读取导出：C# 仍靠 mmap 自读文本，未来的 MCP 也无法复用同一个 dll。刀2 的完整范围较大（`set_result`、采集读取、线程级 poisoning、渐进读），本期只做最小可交付闭环——文件源装载完成后按结果集序号分页读取，先把打包二进制页的布局与 P/Invoke 路径用测试钉死，再逐块扩展。

## What Changes

- 新增 `logcore/paging` 行为契约：按结果集序号 `ord` 寻址的只读分页，限已装载文件源；单页上限、越界空页、页内快照一致、读取失败返错。
- core 新增页组装：以整份快照（文件源即已装载 `Payload` 的 8 列 + 字典/崩溃表）为唯一读面产出页，不依赖段列表；每行携带原文、消息起始偏移、时间戳区间、列值，以及 tag / 异常类型 / 包名的页内字符串表（崩溃行附摘要所需引用）。缓存命中与首次解析走同一读面；页文本经装载时保留的只读句柄读取，源路径轮转/替换后不混入新文件内容。
- cabi 新增导出：`rowCount`、`page`、释放页缓冲；页缓冲由 core 分配、宿主释放，沿用现有"谁分配谁释放"约定。
- C# 侧只扩 `Logcore` 绑定层（P/Invoke + 托管封装）与绑定层 IT，不接桌面 UI。
- 内部地基修正（不新增对外面）：`LiveIndex` 改为先发布段、再发布行数，消除可见窗口；采集消化线程每批冻结并发布段（本 change 仍不开放采集源读取）。
- 非目标（out of scope）：`set_result(ids)`、过滤与正则、标记与 `.lcm`、线程级 panic poisoning、渐进装载中的页读取、采集源分页读取、桌面 UI 接入、切流与旧引擎删除、sidecar 两个既有缺口（Reload 不重落盘、读侧不校验）、DLT 解码、MCP。

## Capabilities

### New Capabilities

- `logcore/paging`: 已装载文件源的只读分页读取契约——寻址、页内容、状态限制、边界与错误语义。

### Modified Capabilities

无。

## Impact

- 代码：`crates/core`（页组装与读取）、`crates/cabi`（新导出）、`logcat-viewer/Services/Logcore`（绑定层）。
- API：新增 C ABI 导出与 C# 绑定面；既有获取面、错误码既有值、sidecar 字节格式均不变。
- 测试：Rust UT + IT（页布局固定字面量、跨块、缓存命中、边界、字符串表、读取失败、轮转替换、空页与状态矩阵）、C# 绑定 IT、P/Invoke 页耗时探针。
- 文档：`docs/TESTING.md` 更新 Rust 侧条数与覆盖率口径；关联探索笔记随归档同批处理。
- 不受影响：过滤/标记/MCP/UI/切流；生产 `cache/` 与采集文件生命周期。