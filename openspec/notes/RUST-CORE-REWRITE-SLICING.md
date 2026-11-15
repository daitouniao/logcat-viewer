# Rust Core 重写：定稿（结论与事实）

- 状态：**探索笔记 · 定稿版**（2026-11-16 重构：只保留结论与事实，过程叙述、已关闭的审查记录与被取代的方案讨论已移除，全文见 git 历史，末次完整版提交 `9c73dae`）
- 输入链 part 1（本地直读 + 8 KB 探针 + 描述符）已实施并归档：`openspec/changes/archive/2026-11-15-source-acquire-contract/`
- **当前唯一待人决策项：C3**（§3）；后续排队 C4 / C8 / C12（§4），design 阶段项见 §5
- 本笔记不实现任何代码；进入实施的入口是 `/opsx:propose`

## 1. 目标态

**Rust 拥有全部 log 文件操作与结果集，编译成一个 C ABI cdylib，由 C#（P/Invoke）与 Node.js MCP（koffi）共用同一个 dll；C# 只做配置与分页渲染，不持有日志数据、不映射文件。**

```
+---------------- C# : 配置 + 显示 ----------------+
| 设置 / MRU / Loc / 对话框 / DPI / 字号            |
| 虚拟列表渲染：颜色、标记、行号列、截断文案         |
| 设备枚举与选择 / 工具箱 UI / 截图(GDI+)           |
| 分页页缓存 + 查询世代                            |
+------------------------+-------------------------+
                         |  C ABI (P/Invoke)
                         v
+---------------- Rust : 全部 log 文件操作 --------+
| probe(文本/编码) -> acquire(本地|代理|设备)        |
| -> spool -> 索引(9列 + intern + 崩溃打标)         |
| -> 结果集持有 -> 过滤(全量/增量) -> 分页供数      |
| -> sidecar 落盘与淘汰 -> 导出 -> 增长/重载策略     |
| -> 会话生命周期与租约                             |
+------------------------+-------------------------+
                         ^
                         |  同一个 dll，koffi (async)
+---------------- Node.js MCP ---------------------+
| JSON 薄壳：probe/count/window/trace_field/...     |
+--------------------------------------------------+
```

归属三例外（写进非目标，边界才自洽）：

| 例外 | 内容 | 理由 |
| --- | --- | --- |
| 设备侧 | 设备枚举/连接/monitor、adb server 自愈、截图、工具箱文件面板留 C#；**采集字节由 C# 读原始流后 push 给 Rust（C2 已定）** | `AdbManager` 在 UiCore 且被两个 exe 共享；截图是 GDI+ |
| Windows 外壳 | `SaveFileDialog` 等模态框、`explorer.exe` 集成留 C#；**管道客户端 / 活性锁 / 启动清理的归属 = C3（§3，唯一待拍项）** | 模态框必须由 UI 进程呈现；大文件确认走一次 consent 往返（Rust 发「需确认(size)」，C# 弹框回答） |
| 配置与文案 | 过滤参数解析（`SplitTerms` / `ParseMinutes` / `ParseInts`）、`Loc`、进度呈现、设置存储 | Rust 只回错误码/事件码与结构化参数 |

## 2. 已定决策（全部为维护者拍板结论）

### 2.1 基础决策（2026-11-08）

| 编号 | 决策 | 影响 |
| --- | --- | --- |
| D1 | log 文件操作完全归 Rust，C# 仅配置 + 显示 | C2 定案后文件操作侧**无例外**：spool、sidecar、临时文件全在 Rust |
| D2 | 显示走 SQL 查询窗口式分页：传分页参数或锚定行号，返回固定条数 | C# 不映射文件、不持有结果集；不返回长期裸指针 |
| D3 | Rust 编译一个 dll，供 C# 与 Node.js MCP 调用 | CLI 保留作对拍与退路 |
| D4 | Node 侧 koffi 调同一个 C ABI dll | 一个 native 产物，不引 napi-rs 构建矩阵 |
| D5 | panic 防护取 `catch_unwind` 逐导出函数包装 | `panic = "abort"` 不是等价替代（§6.4） |

### 2.2 顺序与形态（2026-11-10 拍板）

| 编号 | 决策 |
| --- | --- |
| C19 | GUI 走 `crates/cabi` 窄 FFI（只长 acquire 面），MCP 第一版走 `crates/cli` 的 stdio；koffi 后移到 cabi 成熟之后（D3/D4 目标态不变） |
| C20 | **GUI 主航道 + 三轨并行**（见 §3），原七刀顺序保留作对照，实际按三轨推进 |
| R1 | 并行只发生在 `crates/core` 层，契约层不并行 |
| R2 | 轨C（MCP）不阻塞轨A |
| R3 | 输入先行：log 输入两半（打开文件含代理 / 采集）前移 |
| R4 | **验收不设数字锚**，排序按可感知度（多久能上手摸一次）；对拍仍作技术判据 |
| R5 | 保留免费副产品：CLI dump 与 C# 结果的 diff；启动日志标记「本次打开由 Rust / C# 提供」 |

### 2.3 语言与边界（2026-11-16 拍板）

| 编号 | 决策 | 关键约束 |
| --- | --- | --- |
| C1 | **跳过 C# 并行探针，Rust 正式定案**。口径：**Rust 不必完整复刻 C# 的能力，按最优方案做** | 退出路径放弃；切流 + C8 执行后不可逆；对拍是验证手段，不是目标 |
| C2 | **C# 采集、push 给 Rust，文件操作全归 Rust**。C# 只保留设备 I/O：跑 adb、读输出流（直读原始字节、不解码），把字节推给 Rust；spool 临时文件的写、读、生命周期全在 Rust | D1 无例外成立；推送接口形态（cabi acquire 面字节推送导出、分块与回压）与「行切分在推前还是收后」留 part 2 design；Rust 直连协议 / 拉子进程两方案出局 |
| C5 | **刀0 取消**。UI 是新做不是改造：以现有界面功能为功能基准，C# 侧直接接入 Rust 数据层 | UI 接入直接对着 Rust 分页契约做，落在 cabi 分页面就绪之后 |
| C21 | **采集字节保真取乙：直传字节**。C# 不再把字节经 UTF-8 解码成 `string` 再编码，非 UTF-8 字节原样保留 | 必须绕开 `ExecuteRemoteEnumerableAsync(..., Encoding.UTF8)` 的解码层（`LogcatStream.cs:89`）；行级过滤（空行、横幅）在字节层做；行尾 LF/CRLF 差异属 part 2 design；验收以设备原始流为正确性基准，与旧 C# 采集文件的非 UTF-8 语料差异是**预期差异**，单列不混入「等价」 |

### 2.4 契约补丁（2026-11-15 拍板）

| 编号 | 决策 | 关键约束 |
| --- | --- | --- |
| C16 | 刀2 加临时 `set_result(ids)`：C# 把过滤结果行号推给 dll，dll 只负责分页；刀3 删除该接口 | 「刀0 接口即最终契约」作废；删除列为刀3 必验项；千万命中 = 40 MB `int[]` 一次性跨界成本由刀2 承担 |
| C17 | markset 归 core：暴露 `mark` / `unmark` / `clear_marks` / `list_marks`；持久化走独立 `.lcm`（与 `.lcs` 同目录同键、不参与缓存回收），存活规则 = 忽略 mtime、头哈希 + 逐条 `(offs, len)` 复验 | 标记变更**不**使查询失效；只有 `markedOnly` 选项变化时重建结果集 |
| C18 | 刀1 完成定义含三层判据，缺一不得进刀2（详见 §4.5） | 字节级 spec + 黄金向量；命中硬断言；旧条目清理向量 |

### 2.5 缓存切流（2026-11-15 拍板）

| 项 | 决策 |
| --- | --- |
| 遗留 v1 缓存处置（§16 相关探索的结论） | **乙：一次性失效**。升级后清掉历史 v1 条目，首开全部重解析一次；此后 Rust 是唯一写者与读者，不再背路径归一化与大小写映射的历史包袱 |
| 切流前提 | 「切生产 `cache/`」与「解析补齐到与 C# 等价」**必须同一批交付**；解析未补齐前不得清理遗留条目（旧条目反而比 Rust 重新解析更完整） |
| 一次性清理实现 | 清理只执行一次（如 `cache/` 内哨兵文件 + 目录扫描），不得误删清理后 Rust 新写的条目；具体形态在刀1 design 定 |

## 3. 待人决策：C3（当前唯一阻塞项）

#### C3 外壳三杂务（活性锁 / 命名管道客户端 / 启动清理）的归属

- **为什么需要决策**：C2 已定「文件操作全归 Rust」，这三样本质上都是文件/系统操作，归属直接决定输入链剩余部分（代理 spool、租约）与刀5 的 change 边界；且三样各有已知的 Rust 侧技术坑，不定无法写 tasks。
- **有哪些方案**：
  - **甲** 全入 Rust（windows-rs 调原生 Win32），活性锁语义重验；
  - **乙** 管道客户端入 Rust、活性锁与启动清理留 C#；
  - **丙** 全留 C#，Rust 只吃已 spool 好的本地路径（与 C2 定案矛盾）。
- **当前偏向**：**甲**。理由：① 与 C2「文件操作全归 Rust」口径一致；② 启动清理的对象（崩溃残留的 spool / 缓存）正是 Rust 写的，留在 C# 需要跨语言协调「哪些文件能删、按什么命名约定」，全放 Rust 则天然内聚。
- **必须配套的技术处理（已知坑，非阻塞但需写进 tasks）**：Rust `std::fs` 在 Windows 上恒以全共享模式打开文件，**无法表达独占**——用 std 做判活会把「有主」误判成「无主」，进而删掉活实例的 spool 目录（静默错误）。现状 C# 判活：持有方 `FileAccess.ReadWrite + FileShare.Delete`（`FileProxyService.cs:421`），清理方以 `FileShare.None` 试开（`:445`）。Rust 侧收法二选一（design 定）：`CreateFileW(share_mode = 0)`；或改用不依赖共享语义的机制（命名互斥 / Job Object）。无论哪种，**判活语义必须单列验证任务**。

## 4. 后续待拍（一览，每次过一项）

| 编号 | 问题 | 方案 | 偏向 |
| --- | --- | --- | --- |
| C4 | panic 捕获后句柄是否可继续用（阻塞刀2 FFI design）。`AssertUnwindSafe` 是断言不是证明；panic 可能发生在持锁/半更新状态，继续查询可能读到损坏索引（sidecar 无 payload 校验、读侧不 bounds-check 字典 id） | 甲 句柄标记 poisoned，返回致命错误码，宿主关闭重开；乙 原地恢复继续服务；丙 abort 宿主（放弃 D5） | 甲 |
| C8 | C# 引擎实现最终去留：决定 `LogDocumentTests` / `LogParserTests` / `FilterEngineTests` / `CrashTests` 等引擎用例迁 Rust 还是长期保留 | 甲 保留一个发布周期作 native 加载失败的降级，之后删；乙 刀2 验收即删；丙 长期双实现 | 甲（该偏向写在 C1 定案前；C1 已定「不复刻、无退路」口径，fallback 的意义下降，拍板时重估） |
| C12 | 平台矩阵：决定 CI 构建矩阵与 npm 包 os/cpu 声明。GUI 是 WinForms（Windows-only），MCP 理论可跨平台 | 甲 v1 只 win-x64；乙 加 win-arm64；丙 加 linux/darwin（仅 MCP 侧可用） | 甲（本机无 aarch64 target，取乙需先 `rustup target add`） |

## 5. design 阶段项（不阻塞 propose，本表给偏向）

| 编号 | 问题 | 方案 | 偏向 |
| --- | --- | --- | --- |
| C6 | sidecar 是否借机补校验（现状无 payload 校验和、读侧不 bounds-check） | 甲 v1 字节不变、校验另立 change；乙 升 v2 加校验和（旧缓存全废） | 甲 |
| C7 | 编码判定口径（新功能非迁移项；`MCP-LOG-ANALYSIS-API.md:15` 已有启发式） | 甲 采用该启发式立 spec；乙 另定；丙 先 UTF-8 后 GBK | 甲，独立 change 立 spec |
| C9 | dll 分发方式（`.gitignore` 全局忽略 `*.dll`，与「构建产物不入 git」红线冲突） | 甲 不入库，CI/本地构建后拷进 publish；乙 入库开例外；丙 独立包管理 | 甲 |
| C10 | 版本号第 6 处（Cargo.toml 与 `Directory.Build.props` 对齐） | 甲 build 脚本注入；乙 发版清单手工同步；丙 dll 不标版本 | 乙 |
| C11 | 覆盖率口径（Rust 不进 coverlet，数字会虚高） | 甲 口径改「C# 侧」并声明不含 Rust；乙 引 Rust 工具合并；丙 暂停门禁 | 甲，刀2 落地时同批改 `docs/TESTING.md` |
| C13 | 页字段布局 | 甲 定长数值列 + 变长字符串区（复用 sidecar 列式）；乙 DTO 序列化；丙 JSON | 甲；丙仅 MCP 侧 |
| C14 | 查询世代与缓存失效 | 甲 单调世代号随页响应下发，不匹配即丢；乙 Rust 主动通知 | 甲（无回调） |
| C15 | 句柄线程安全与并发 | 甲 句柄内 arc-swap 快照 + 导出函数线程安全；乙 全局串行锁；丙 每线程独立句柄 | 甲；乙违反「页查询不被扫描堵住」硬约束 |

## 6. 推进顺序（结论）

### 6.1 三轨

```
轨A 可见收益   刀1 索引内核 --> cabi 分页 --> UI 接入(新做) --> 切流
轨B 入口归位   输入链 part1(已归档) --> 剩余: 代理 spool + 采集 --> cabi acquire 面 --> GUI 接线
轨C MCP        等轨A的 core --> MCP 壳(stdio) --> search / count / context --> 可选切 koffi
                                  ^
                          汇合点: cabi = acquire 面 + index / paging 面
```

输入链按块推进、一块一个 change：part 1（直读 + 探针 + 描述符）已归档；剩余**代理 spool** 与**采集**两块。采集形状已由 C2 / C21 定案；代理 spool 归 Rust（文件操作），依赖 C3 定外壳归属。

MCP 相关性不对称（`MCP-FUNCTIONALITY.md` §5.5：MCP 只读、只接受显式绝对路径、不暴露 adb）：part 1 直读是 `list(path)` 入口必需；代理 spool 大概率不需要；采集不暴露给 MCP。

### 6.2 各刀结论

**刀0（C# 分页接缝）——已取消**（C5）：UI 新做，直接对 Rust 分页契约接入。

**刀1 `crates/core` + `crates/cli`：索引内核**——轨A 第一步，可进 propose。
- 内容：`LogParser` + `LogDocument` 扫描/装配路径（`ScanRange`、`BuildChunkedCore`、`MergePart`）+ `CrashExtractor`；产出 9 列、tag intern、崩溃打标、整份与分块两种装载；sidecar v1 字节格式写出。
- 对拍：CLI dump 与 C# 逐位 diff（golden dump：9 列 + tag 字典顺序 + 崩溃行号 + sidecar 字节，小样本入 `testdata/`，只加不改；**必须在动 `LogDocument` 之前产出**）。
- 本刀不动 C# 产品代码；Rust 写 sidecar 到测试目录，C# 复用 `LoadIndexed` 通路。
- 崩溃打标绑在索引期父行，不可后置；分块 ≡ 整份本身是验收项。
- sidecar 隐藏契约须写成**字节级 spec**并作为交付物（C18 第 1 层）：magic / 版本 / 8 段宽度 `{8,4,4,1,4,4,1,4}` / 段长（`SidecarCache.cs:155`、`:163`、`:264-314`）；条目名派生 = `GetFullPath` + 文件名主干 `Sanitize` + `FNV-1a(UTF8(path.ToUpperInvariant()))` + `x16`（`:124-129`）；身份摘要 = 64 KB 头尾采样 FNV-1a + `LastWriteTimeUtc.Ticks`（.NET ticks，非 FILETIME，需换算）（`:42`、`:58-77`）。
- 验收（C18 三层）：① 字节级 spec + 黄金向量（含键派生路径向量）；② 命中硬断言——注入临时缓存目录，Rust 先写、C# `LoadLocalFileAsync` 打开，断言 `ChunkedLoadInvocations == 0 && LastSidecarLoadMs > 0`（`frmMain.Sidecar.cs:16`、`:19`），并设无缓存对照组防空断言恒真；③ 预置 C# 历史 v1 条目，断言一次性迁移后清掉、Rust 新条目不误删。
- 刀1 期间格式保持 v1、键派生与身份摘要与 C# 逐位一致；cabi 接管装载后第 ② 层退役。

**刀2 `crates/cabi`：dll 接管分页**。
- 内容：C ABI 导出面 + `catch_unwind` 包装 + 句柄/缓冲区/错误码契约；临时 `set_result(ids)`（C16）；panic 注入测试（导出函数内故意 panic，宿主存活并收到错误码）；页查询与在途扫描并发不阻塞。
- 过滤仍留 C#（刀3 才迁）。

**刀3 过滤入 Rust**。
- 内容：`ApplyFilter` / 块扫描 / 排除 / 大小写折叠 / `FilterTail`，实现 `open(filter)` 最终契约；`FilterExpr` 含 `tagMatch`（substring/exact）与 `markedOnly`；markset 归本刀（C17）；正则随本刀实现，方言 = Rust `regex`（线性时间、无反向引用/环视），多模式单遍（`aho-corasick` / `regex-automata` 预筛）为内部候选，`BLOCK_SCAN_MAX_KEYS = 8` 随本刀重估。
- 必验：**删除 `set_result`**（C16）；`to_lowercase` ≡ `ToLowerInvariant`（`ß`、土耳其 i）；SIMD ≡ 标量；增量 ≡ 全量；正则黄金集逐条对拍 + `REGEX_INVALID`；子串与正则两引擎互不实现。
- 定位：性能收益 ≈ 0（过滤已贴带宽地板），驱动是 MCP 语义统一与结果集归属。参数解析留 C#。

**刀4 Node koffi + MCP 薄壳**：npm 包 + 预编译 dll + koffi async 绑定；长驻会话；工具面口径（`MCP-FUNCTIONALITY.md` vs `MCP-LOG-ANALYSIS-API.md`）未拍，属该 change 待决项。前置刀3。

**刀5 获取链入 Rust**：`TextProbe` / 编码判定（C7，独立 change）/ FileProxy 客户端 / spool / 租约与启动清理 / sidecar 存储治理。范围受 C3 约束。输入链剩余两块与刀5 合流。

**刀6 设备取流**：已由 C2 定形（C# 采集 push 给 Rust），不再是独立待拍刀。

**不单独切片的两项**：并行建索引（语言无关，属刀1 内部实现选择，不在首版上）；sidecar 格式保持 v1 字节不变（C6）。

### 6.3 分发与 CI 实账（进 tasks 的逐项）

| 项 | 要做的活 |
| --- | --- |
| CI 工具链 | 加 rustup + cargo build + 产物拷贝；GitCode / GitHub 双站都要过；构建缓存与时长 |
| 发布步骤 | publish 后补 cargo 产物拷贝；sc 与 fdd 两包各补一条 `Test-Path` 断言 |
| fdd 体积 | 现价 2.0 MB（含 1.2 MB 演示 gif）；加 2–5 MB dll 后翻倍到 3 倍，需认账 |
| `.gitignore` | 只剩 C9（预编译 dll 分发不被 `*.dll` 误伤） |
| 版本号 | `Directory.Build.props` 唯一来源，Cargo.toml 对齐 → 第 6 处（C10） |
| 属性传染 | `SelfContained=true` 绝不能进 csproj；dll 拷贝同理走 publish 后独立步骤 |
| 覆盖率 | Rust 不进 coverlet，口径必须改（C11） |

## 7. 契约与硬约束

### 7.1 分页契约原语（最终态）

| SQL 窗口 | 原语 |
| --- | --- |
| `SELECT ... WHERE <filter>` | `open(source, filter) -> handle, total_count` |
| `LIMIT n OFFSET m` | `page(handle, offset, limit, buf, cap)` |
| keyset anchor | `locate(handle, doc_row) -> ordinal | nearest_ordinal` |
| 取完整大字段 | `full_text(doc_row, buf, cap)` |
| `COUNT(*)` 增量 | `appended(new_count)` 事件（轮询原子计数） |
| `SELECT ... INTO OUTFILE` | `export(handle | ids, path)` |
| 语句取消 | `cancel(handle)` + 原子标志 |

- `open(filter)` 由 Rust 执行过滤、结果集常驻 Rust——刀3 起成立；刀2 以 `set_result(ids)` 过渡（C16）。
- **导出一次都不跨界**：Rust 直接写盘，C# 只传查询句柄或标记行号集合。

### 7.2 缓冲区与字段布局（C13 偏向甲）

- 调用方给缓冲区，Rust 填：`page(..., buf, cap) -> written / needed`；一页 200 行 × ~200 B ≈ 40 KB。**不返回长期裸指针**。
- 页内两段：定长数值列（doc_row / pid / tid / lvl / tag_id / msg_len / 崩溃字段）+ 变长字符串区（offset/len 索引）；tag 字典按查询下发一次，行内只带 tagId。
- 字符串一律 UTF-8；Rust 不保证缓冲区内容跨调用有效。
- 长消息：页内带截断 message + 原始长度；`MSG_LIMIT = 4000` 截断文案留 C#，全文走 `full_text`。
- 崩溃摘要：Rust 回结构化 `(exc, pkg, kind, time)`，兜底提取归 Rust，`Loc.F("☠ …")` 拼字归 C#。

### 7.3 FFI 契约四条

1. **句柄生命周期**：不透明句柄，`open` 创建、`close` 释放；关闭顺序「停查询 → 释放句柄 → 释放内部映射」；页缓存不得跨句柄关闭存活。
2. **缓冲区约定**：谁分配、容量语义、`written` / `needed`、截断标记。
3. **错误码**：负值/枚举统一，两个宿主共用一张表；panic 有独立码。
4. **panic 边界**（D5，本机已实测坐实，§8.3）：每个导出函数薄包装 + `catch_unwind`，捕获转错误码；`panic = "abort"` 与 D3 冲突不取；现代 Rust 中 unwind 逃出 `extern "C"` 触发 abort（宿主直接死），必须防护；koffi 无法替宿主捕获 Rust panic。

### 7.4 并发与 Node 硬约束

- **页查询不得被在途全量扫描/过滤堵住**：读页走已发布索引快照（arc-swap / RCU 形状），扫描在另一份上跑——对应现状分块加载的「前缀发布」语义。
- **Node**：长扫描必须走 koffi async 或 worker_threads；MCP 会话必须长驻（索引生命周期跟进程走）。

### 7.5 Cargo 布局

```
crates/core   纯算法 + IO，无 FFI 面
crates/cabi   cdylib：extern "C" + catch_unwind 薄包装  ->  logcat_core.dll
                                                          ^          ^
                                                    C# P/Invoke   Node koffi(async)
crates/cli    同一 core 的可执行壳  ->  对拍 oracle / 调试 / MCP 退路
```

- CLI 不是可选：对拍靠它；koffi 不可用时的退路（Node 改拉 stdio，`core` 一行不改）。
- 导出面只在 `cabi`；`core` 内不出现 `unsafe` FFI 类型。
- 契约与 DTO 放独立 interop 工程；**进程内适配不能进 interop**（会反向引用 viewer 引擎形成循环依赖）；interop 先只有接口 + record + 错误码表，刀2 才长出 P/Invoke。

### 7.6 输入链剩余两块的已知坑（HEAD 坐实）

| 坑 | 内容 | 收法 |
| --- | --- | --- |
| 活性锁 | Rust std 恒全共享打开，判活误判（§3 详述） | `CreateFileW(share_mode=0)` 或命名互斥 / Job Object；单列验证 |
| 进程树回收 | Rust `Child::kill` 只杀直接子进程 | Job Object（`KILL_ON_JOB_CLOSE`）或 `taskkill /T` |
| 命令行组装 | `BuildLaunchArgs` / `BuildCommandLine`（`FileProxyService.cs:49-63`）三条规则（`{pipe}` 替换含空格加引号 / 缺省追加 `--pipe <名>` / 旧配置剥尾部模板）是隐藏契约 | 搬 Rust 必须逐字节复刻，否则旧 `settings.json` 行为静默改变 |

### 7.7 不要做的事

1. 不先做 `core-extraction` 1a/1b；目录重组宜早于刀1（`logcat-core/` 要装 Cargo workspace）。
2. 刀1 首版不上并行建索引。
3. 不顺手迁 `Loc` / DLT / 工具箱文件面板（DLT 解码归属见 `DLT-FORMAT-AND-RUST.md` K1，待拍）。
4. 不返回长期裸指针；不用回调做进度/取消（进度=原子计数轮询，取消=原子标志）。
5. 不用 `panic = "abort"` 替代 `catch_unwind`。
6. 迁移期不改 sidecar 字节格式（C6）。
7. 不把过滤外迁当性能项立项（收益 ≈ 0）。

## 8. 现状事实（HEAD 核对）

### 8.1 引擎与性能

| 事实 | 依据 |
| --- | --- |
| 引擎代码量集中在四个类 | `LogParser.cs` 441 行、`LogDocument.cs` 1100 行、`FilterEngine.cs` 1133 行、`CrashExtractor.cs` 225 行 |
| **过滤已并行 + SIMD** | `FilterEngine.cs:493`/`:521` `Parallel.For`；`:678` `Vector256.Create`；`:479` 硬件加速分派 |
| 块扫描三道分派闸 | `FilterEngine.cs:23` `BLOCK_SCAN_MIN_ROWS = 2_000_000`、`:27` `BLOCK_SCAN_MAX_KEYS = 8`、`:370` 候选比阈值 |
| **建索引才是单线程** | `LogDocument.cs:609` `ScanRange` 顺序扫描；`:440`/`:487` `Task.Run` 只是单工作线程 offload |
| 主界面走分块装载 | `frmMain.Load.cs:57` `BuildChunkedAsync`；整份入口 `frmMain.cs:2262`（ADB 自动重载） |
| 崩溃打标绑在索引扫描期、标在父行 | `CrashExtractor.cs:48`/`:59` |
| 增量追加走 `Reload` | `LogDocument.cs:942`：长度比较 + 64KB 头部比较 → unchanged/appended/rebuilt |
| 编码探测未实现（硬编码 UTF-8） | `TextProbe.cs` 只做二进制/文本嗅探 |
| 目录时间线 / 多文件合并未实现 | `BACKLOG-DIR-TIMELINE.md`；`MergePart` 是文档内分块合并 |

### 8.2 显示侧（D2 的现状依据）

| 事实 | 依据 |
| --- | --- |
| 锚点语义已存在 | `LogListView.cs:273` `CaptureAnchor()`；`:291` `PinAnchor`、`:309` `NearestModelRow`（二分找最近存活行） |
| 控件持有全部命中行号（数组所有权，不复制） | `:107` `SetRows`、`:121` `AppendRows`；分段结构 + 段前缀和 |
| 行号换算三件套依赖本地数组 | `:344` `DocRow`、`:357` `ModelRowOf`、`:309` `NearestModelRow`、`:330` `SegLastGe` |
| `RetrieveVirtualItem` 同步取数 | `:402` 缓存未命中即在 UI 线程读 mmap 解码 |
| `CacheVirtualItems` 钩子已挂、处理为空 | `:413`——分页预取的天然落点 |
| 行缓存键 = 模型行号，CACHE_SIZE 20000 超限删半 | `:61`、`:62` |
| 消息截断 `MSG_LIMIT = 4000` 是显示策略 | `:14` |
| 标记既是 UI 状态也是过滤输入 | `:32` `HashSet<int> _marked`；`FilterSpec.MarkedOnly` 参与过滤（`FilterEngine.cs:871`、`:1057`） |
| 无排序 | 列固定，分页契约 v1 不需要排序 |
| 导出读行文本 | `FilterEngine.cs:1100` `ExportRows` 读 `doc.LineBytes(row)` 写原始字节 |

### 8.3 文件操作侧（D1 搬迁面）

| 事实 | 依据 |
| --- | --- |
| 管线编排塞在窗体里 | `frmMain.cs:1087` `LoadViaProxyAsync`（建租约→拉代理→1GB 确认→spool→建索引→挂 doc→失败兜删） |
| ADB 取流已落本地临时文件 | `frmMain.cs:2103` 写 `<exe目录>\Temp\logcat_live_<时间戳>.log`，走 `Reload` 增长分支 |
| 文件获取已跨进程且语言无关 | `FileProxyClient.cs:39` `NamedPipeClientStream` + JSON 行/原始块帧；示例代理 `sample/fileproxy/` |
| spool 与活性锁依赖 Windows FileShare 语义 | `FileProxyService.cs:224` `SpoolCoreAsync`（8MB 块）、`:419-451` `.alive.lock` |
| 设备侧是 .NET 依赖 + Windows-only | `LogcatStream.cs:2` `AdvancedSharpAdbClient`、`:65` `"logcat -v threadtime"`；`AdbManager.cs:353-396` 截图 GDI+，被两个 exe 共享 |
| sidecar 治理是纯 IO | `SidecarCacheStore.cs`：`cache/`、temp + `File.Move(overwrite)`、LRU、1GB/条目 4GB/目录上限 |
| sidecar 格式钉死、无 payload 校验、不可追加 | `SidecarCache.cs:155` `FormatVersion = 1`；变长字典在数据区前，增长即整份重写 |
| sidecar 命中是一等公民且**命中失败静默降级** | `frmMain.Sidecar.cs:33` 无条件先读；`:9-10` 失败只写启动日志、UI 无感——这就是 C18 硬断言存在的原因 |
| 命中后 C# 自己映射源文件（索引不含文本） | `LogDocument.cs:200` `LoadIndexed` → `:216` `OpenMap` |
| 输入链代码体量 | 管道客户端+帧协议 15.2 KB；拉起/轮询/杀进程树约 200 行；spool 约 200 行；租约+锁+清理约 100 行；`LogcatStream.cs` 140 行；`AdbManager` 留 C# |

## 9. 解析与索引缓存：切流前的差额与结构（刀1 工作地图）

### 9.1 Rust 现状与 C# 基准的差额（`logcore/src` HEAD 核对）

| 面 | Rust 现状 | C# 基准 | 后果 |
| --- | --- | --- | --- |
| 方括号 long 格式 | `parse_line` 无 `[` 快速路径、无回退正则（`parse.rs:94`） | 方括号行走 `ReLong` 正则兜底（`LogParser.cs:83`、`:216`） | long 行 pid/tid/level/tag 全空 |
| 时间戳校验 | 只夹分钟 0–59；月/日/时/分/秒不校验（`parse.rs:112`、`:128`） | 字段越界抛 `ParseException`，整行按未知元数据收（`LogParser.cs:269`、`LogDocument.cs:748`） | 非法时间戳两侧列值不同 |
| 崩溃打标 | **未实现**，`Payload.crashes` 恒空（`parse.rs:5`、`:280`） | 字节快筛 + 3 行前瞻 + Java/Native/ANR/Tombstone + 两字典（`LogDocument.cs:778`、`:829`） | Rust 缓存崩溃表为 0，崩溃面板空 |
| 分块渐进 | 未命中时整份 `fs::read`，解析完才 `store_result`（`source.rs:872`、`:894`） | 首块 4 MiB、稳态 32 MiB 逐块 Scan→Merge→发布（`LogDocument.cs:465-491`） | 1 GB 文件峰值 = 整份字节 + 整份列 |
| 块边界 | 无分块故无对齐与前瞻 | 块末对齐换行，块尾多看 3 行只索引到 `indexEnd`（`LogDocument.cs:531`、`:562`） | 补分块必须一并补，否则跨块崩溃丢失/重复 |
| 缓存写时机 | loader 线程内、`Done` 之前同步写（`source.rs:881`、`:808`） | 装载完成后后台线程写，退出不等（`frmMain.Sidecar.cs:98`） | `done` 被落盘拖后 |
| 缓存编解码内存 | `encode` 建整份 `Vec<u8>`，`probe`/`decode` 整份 `fs::read`（`sidecar.rs:92`、`:370`、`:175`） | 写侧逐列 Stream、读侧直读列数组（`SidecarCache.cs:242`） | 命中路径峰值 ≈ 索引两倍 |
| 缓存键派生 | 只换 `/`→`\` 再 `to_uppercase` 做 FNV（`sidecar.rs:303`、`:313`） | `Path.GetFullPath` + `ToUpperInvariant`（`SidecarCache.cs:126`） | 少数路径算出不同条目名 → 旧条目未命中（只多一次重解析） |
| 缓存临时文件 | 条目名 + `.tmp-<pid>`（`sidecar.rs:432`） | `.tmp-<GUID>`（`SidecarCacheStore.cs:155`） | 同进程并发写同条目撞名 |
| 临时条目回收 | 4 GiB LRU 按目录全部 `.lcs` 统计 | 同 | 遗留条目按 §2.5 乙一次性清理，LRU 只管新条目 |
| 缓存所有权 | 仅测试目录 | 生产 `cache/` | 切流后 Rust 唯一写者（§2.5） |

### 9.2 切流三条硬约束

1. 解析语义先于（或同批于）缓存写者切换——残缺语义会固化进缓存且身份校验挡不住。
2. 同一 `FORMAT_VERSION` 内不得静默改义——失效闸只有身份摘要；先切后补语义 = 接受一次整目录失效。
3. 遗留条目只有「读」一个方向——键派生不一致只造成未命中，不会张冠李戴；**解析未补齐前不得清理遗留条目**。

### 9.3 结构决定：渐进发布用「段化列存」（design 阶段建议，倾向已明确）

| 方案 | 问题 |
| --- | --- |
| 整份 Arc 换手 | 每块拷全量 payload，O(n²)，否决 |
| 单条 Vec + 读写锁 | 解析期间持写锁，页查询被堵，违反硬约束 |
| **段化列存 + 原子发布** | 每块成不可变 Segment，`ArcSwap<Vec<Arc<Segment>>>` 追加；发布 O(1)，读页按行号定位段 |

选段化：同时解决渐进发布、崩溃 3 行前瞻跨段、异步落盘（后台线程共享段）。代价：写侧按列跨段遍历 + 内存表示与落盘连续列之间一次适配。**必须在缓存切片定，否则同一块地翻两遍。**

### 9.4 缓存路径四个实现坑（HEAD 坐实）

1. `done` 被落盘拖后：改装载完成后交后台，`done` 先发，退出不等待。
2. 整份缓冲：写侧改 `BufWriter` 逐列写，读侧逐列读进目标数组（命中是高频路径）。
3. 临时名用 pid 会撞：补进程内唯一后缀（原子计数或 GUID）。
4. plan 阶段整份读缓存（`sidecar.rs:368`）：大条目拖慢 plan；不可接受则把命中判定后移到 commit 的 loader 线程（描述符先按无缓存给，命中后修正 `loadMode`）。

## 10. 本机环境实测（2026-11-08，全实测，探针工程在 `.tmp-test/`）

### 10.1 工具链

| 项 | 值 |
| --- | --- |
| rustc / cargo | 1.99.1（stable-x86_64-pc-windows-msvc，单工具链） |
| MSVC 链接器 | VS 18 Community，rustc 经 vswhere 自动定位；**坑**：PATH 上的 `link.exe` 命中 Git 自带版本，不可手工改 PATH，写 CI 别把 Git bin 提前 |
| 已装 target | x86_64-pc-windows-msvc、x86_64-unknown-linux-gnu（无 aarch64） |
| .NET / Node | SDK 10.0.401；Node v26.8.1 + npm 11.19.0（koffi 宿主就绪） |
| 版本固定 | 无 `rust-toolchain.toml`；引入 workspace 时建议补一个 |

### 10.2 端到端与 panic（结论）

- cdylib 编译、`extern "C"` 导出、C# P/Invoke、Node koffi 同步/异步（API 形态：`fn.async(args, cb)`，签名 `(err, result)`，无 `lib.asyncFunc`）全通。
- 依赖 `memmap2` 0.9 / `rayon` 1.10 / `regex` 1.11 编译通过；`Mmap::map` 可用。
- edition 2024 可用，但**每个导出函数须写 `#[unsafe(no_mangle)]`**。
- **panic 实测**：裸 `extern "C"` 内 panic → 宿主进程直接死（abort，退出码 0xC0000409），不是静默损坏；`catch_unwind` 薄包装 → 宿主存活、其余导出正常。D5 确认必需且有效。C4（捕获后句柄是否损坏）未验证，独立 design。

### 10.3 仓库就绪度与实测缺口

- 缺口：CI 无 Rust 工具链；`logcat-core/` 目录未建；C9/C10/C11 未落。
- **实测未覆盖**：`to_lowercase` ≡ `ToLowerInvariant` 口径（刀3 必验）；P/Invoke marshalling 开销与页大小匹配（刀2 量）；dll 真实体积（推算 2–5 MB，fdd 翻倍到 3 倍待实测）。

## 11. 未解的显式猜测

| 内容 | 标注 |
| --- | --- |
| 一页 200 行 ≈ 40 KB、按页拷出微秒级，同步 `RetrieveVirtualItem` 未命中取一页可接受 | 推断；进程内适配先量，刀2 再量 native |
| 快速拖动滚动条无可感知停顿 | 未实测；需维护者实机（2K 缩放屏）验证 |
| koffi async 足以承载 MCP 粗粒度长任务 | API 形态已实测；承载能力未实测，退路 CLI + stdio |
| Rust std `OpenOptions` 恒全共享打开、无法表达独占 | 推断（据 std Windows 实现），C3 取甲时需本机探针坐实 |
| `GetFullPathNameW` 能覆盖 `Path.GetFullPath` 全部归一化 | 推断，需向量对拍（长路径前缀/设备路径行为可能不同） |
| `to_uppercase` 与 `ToUpperInvariant` 仅在 `ß` 等极少数发散 | 推断，需路径向量坐实（§2.5 乙后影响面 = 旧条目是否命中） |
| 段化列存后读一页定位成本可忽略 | 推断，cabi 分页刀实测 |
| 整份 `fs::read` 在 1 GB 文件上的峰值 ≈ 文件字节 + 列 + 编码缓冲 | 估算，未实测；与 C# mmap + 32 MiB 块用同一文件对照 |
| 输入链 part 1 约 1200–1800 行、part 2 约 200–400 行 | 估算，按 C# 行数折算 |

## 12. 关联

- `RUST-CORE-EVALUATION.md`：语言决策来源；其 §4 范围、§5 接缝推荐、§6 内存峰值口径已被本笔记修订。
- `2026-11-08-SOURCE-ACQUIRE-CONTRACT.md`：已归档；输入链契约出处（裸路径状态说明）。
- `BACKLOG-MCP-AND-FORMATS.md`、`MCP-FUNCTIONALITY.md`、`MCP-LOG-ANALYSIS-API.md`：MCP 工具面两套口径未拍板，属刀4 待决项。
- `DLT-FORMAT-AND-RUST.md`：DLT 解码归属待拍（其 K1）。
- `docs/TESTING.md`、`docs/BUILDING.md`：测试基线（不内联数字）与发布步骤。
- `FILTER-PREFILTER-AND-RUST.md` §10：正则随刀3 交付的出处。
