# DLT 格式接入与 Rust 实现基础

- 状态：**待评审**（合并后的唯一 DLT 主文档；未 propose、未编码）
- 日期：2026-11-12 合并成文；2026-11-13 复核在途 change 定稿与仓库现状
- 关联提交：无（未编码）
- 验收结论：不适用（未实施）
- **取代关系**：本文取代原 `openspec/notes/DLT-INTEGRATION.md`（按维护者 2026-11-12 指令删除，原文要点已并入本文）

## 0 一句话方案

DLT 以**格式**身份进入 Rust core 的源获取契约：core 在 plan 阶段判定 `format = Dlt`、路由 `route = LocalDecode`，commit 阶段把 `.dlt` 二进制流式解码为规范文本，与 logcat 文本汇入同一条索引路径；列集由 core 随源描述下发（DLT 固定 5 列），宿主只按声明渲染。

```
 .dlt 二进制  --[core: 流式解码 + 魔数 resync]--> 规范文本（临时） --+
                                                                    +--> core: 建索引 --> columns[] 下发 --> 宿主渲染
 外部导出文本  --[core: 直读 + DLT 文本分支]------------------------+
```

## 1 合并说明（来源与取舍）

| 来源 | 采纳内容 | 处理 |
| --- | --- | --- |
| 原 `DLT-INTEGRATION.md`（2026-11-06 重构 / 2026-11-12 复核） | 格式事实、字段映射、已关闭决策、风险许可 | 主体并入本文 |
| 原 `DLT-INTEGRATION.md` §3.3 / §3.5（C# 期落地改动：`ParseLine` 分支、`HeaderBytes` 接受 `/`、`LogListView` 静态列集改运行时） | — | **整条删除**（维护者 2026-11-12 口径）：判定与索引均归 Rust，列集由 `columns[]` 下发，C# 侧不再有 DLT 分支 |
| `RUST-CORE-EVALUATION.md` C3（解码归属三选项） | 选项框架 | 改写为 §5.1（旧倾向「丙」被 D1 取代） |
| `RUST-CORE-REWRITE-SLICING.md` 的「DLT 解码不进内核」 | — | **作废**：与 D1「log 文件操作完全归 Rust」冲突；其活性锁雷保留并转入 §5.6（Rust 侧已用 `share_mode(0)` 收口，见该笔记 §6.3） |
| `2026-11-15-SOURCE-ACQUIRE-CONTRACT.md`（已归档）§2.5 / A2 / A4 / A8 / S9 | 契约面、判定顺序、列集下发 | 并入 §4，且以已归档 change `source-acquire-contract` 的 design 定稿与主规范 `logcore/source-acquire` 为准 |
| `BACKLOG-MCP-AND-FORMATS.md` B3 / D2 / D7 | MCP 侧切片位置 | 压到 §9 |

## 2 范围

| 做 | 不做 |
| --- | --- |
| `.dlt` 二进制流式解码为规范文本（verbose 全解析） | 二进制 hex dump 视图 |
| 外部 DLT Viewer 2.31.0 的 ASCII 导出原样直读 | 适配第三方 CSV / 其他版本变体 |
| non-verbose 输出 `[十进制 id] hex`；control 输出 `[服务名] hex` | FIBEX 驱动的 non-verbose 全解析 |
| 列集按格式下发（DLT 固定 5 列） | 每格式列配置、ECU/Session/mcnt 专属列 |
| 时间按文件原值显示（不换算时区） | 时区归一 / 跨源时间对齐 |

## 3 DLT 格式事实

> 继承自原笔记，本轮未重新取样复核。协议字段与位定义属公开规范事实，不受版权保护。

### 3.1 一条 DLT 记录的结构

`.dlt` 无文件头，记录顺序拼接；每条 = Storage Header（仅落盘文件）+ Standard Header + 可选 Extra + 可选 Extended Header + Payload。

| 层 | 大小 | 字段 | 端序 | 说明 |
| --- | --- | --- | --- | --- |
| Storage Header | 16 | `magic` char[4] / `seconds` u32 / `microseconds` i32 / `ecu` char[4] | LE | `44 4C 54 01`；损坏时按魔数 resync |
| Standard Header | 4 | `htyp` u8 / `mcnt` u8 / `len` u16 | `len` 为 BE | `len` 不含 Storage Header；htyp 位：UEH 0x01 / MSBF 0x02 / WEID 0x04 / WSID 0x08 / WTMS 0x10，版本 v1 在高 3 位 |
| Extra | 0/4/8/12 | `ECID` / `SEID` u32 / `TMSP` u32 | BE | 按 htyp 顺序出现；TMSP 单位 0.1 ms |
| Extended Header | 10（UEH=1） | `msin` u8 / `noar` u8 / `apid` / `ctid` char[4] | — | msin = VERB 0x01 + MSTP + MTIN |
| Payload | 可变 | verbose：`noar` × (TypeInfo u32 + 数据)<br>non-verbose：u32 id + 原始字节<br>control：u32 服务 id + [状态码] + 参数 | LE（MSBF=0） | non-verbose 无 TypeInfo，全解析需 FIBEX（不做） |

MSTP/MTIN：LOG（0）→ OFF/FATAL/ERROR/WARN/INFO/DEBUG/VERBOSE；APP_TRACE（1）→ VARIABLE/FUNCTION_IN/FUNCTION_OUT/STATE/VFB；NW_TRACE（2）；CONTROL（3）→ REQUEST/RESPONSE/TIME。

### 3.2 外部 dlt-viewer 导出形态（实测 2.31.0）

```
dlt-viewer.exe -t -s -u -c <out.txt> <in.dlt>    # 必须在安装目录作工作目录，否则静默挂起
```

一行一消息，行尾带一个空格，字段序 `索引 yyyy/MM/dd HH:mm:ss.ffffff 相对秒 mcnt ECU APID CTID session type subtype mode nargs payload`：

```
2 2026/10/13 08:00:02.000222 3.0000 2 TEST TAPP TCTX 1 control request non-verbose 0 [set_log_level] 54 41 ...
3 2026/10/13 08:00:03.000333 4.0000 3 TEST TAPP TCTX 1 log info non-verbose 0 [4660] e8 03 00 00 07
```

- `subtype` 是级别来源（`info` / `error` 已实测，`fatal/warn/debug/verbose` 未取样）。
- CSV 导出（`--csv --signature TSEACP`）丢掉 type/subtype/mode/nargs，级别与 Tag 都没有 → 不采用。
- 结论：外部文本与 core 索引路径**只差「行首时间戳识别」一层**；重活只在 `.dlt` 解码。

### 3.3 样本核对

`01_cpu_mon.dlt` 首帧 vs dlt-viewer 导出首行：存储头 `seconds`=1585240949 → 2020-03-26 16:42:29 UTC，导出显示 2020/03/27 00:42:29.483000（+8 本地时区）；htyp=0x31（UEH+WTMS+v1）/ mcnt=77 / len=128；TMSP=835993（83.5993 s）而导出「相对秒」列 83.5225（口径未证实，自研输出不用该列）。

**dlt-viewer 会换算本地时区**，与本文「不换算」的口径不同 → 两通道视为不同的 Log，不追求一致。

## 4 与源获取契约的对接面（core 宿主）

> 依据：已归档 change `source-acquire-contract`（`openspec/changes/archive/2026-11-15-source-acquire-contract/`）的 design D5 / D14；spec delta 已并入 `openspec/specs/logcore/source-acquire/`。本节只写 DLT 需要补的那一面。

### 4.1 三轴取值与判定位置

| 轴 | DLT 取值 | 说明 |
| --- | --- | --- |
| `route` | `LocalDecode` | design D14 已为 DLT 预留此值 |
| `format` | `Dlt` | design D14：首版仅 `Logcat`，`Dlt` 随 DLT change 加入 |
| `channel` | `Local`（直读）/ `FileProxy`（经代理暂存后判出） | design D14 已删除原 `DltDecoded` 取值——DLT 不新增渠道，只动 format |
| `encoding` | `Utf8` | DLT 是二进制格式，解码产物为 UTF-8 文本；DLT 不触发编码判定 |

其余描述符字段的 DLT 取值（依据 design D14）：

| 字段 | DLT 取值 | 说明 |
| --- | --- | --- |
| `appendMode` | `ReloadOnly` | DLT 禁用自动重载；`Reload` 只重读解码产物，源 `.dlt` 变更不反映 |
| `leaseKind` | `Ephemeral`（关闭即删） | 与采集的 `Retained` 相对；DLT 产物不供「保存日志」复用 |
| `timeSemantics` | `Original` | 按文件原值显示，不换算时区；`Relative`（相对秒）不采用 |
| `loadMode` | `Progressive` | 首版不入 sidecar 缓存（§7 K3） |
| `identity` | 源 `.dlt` 的四要素 | 见 §4.4 |
| `displayPath` | 源 `.dlt` 路径 | 标题 / 最近使用 / 导出命名均用它，不外泄临时产物路径 |
| `sourcePath` | 解码产物路径（内部） | 仅内部使用，不进用户可见位置 |

判定位置（plan 阶段，commit 之前）——沿用 design D5 的固定判定顺序「存在性 → 格式 → 嗅探（仅 auto）→ 代理决策」：

```
plan:    存在性 → 魔数判定(DLT\x01) → 文本嗅探 → 代理决策 → 返回 route / format
commit:  暂存 → 解码（route=LocalDecode）→ 建索引 → 返回源描述
```

- **魔数先于嗅探**：`.dlt` 头部含大量非文本字节，嗅探必然判「非文本」；若先嗅探，DLT 文件会被误送代理。故格式判定必须排在嗅探之前（继承原笔记「先于代理分派」的口径，在 Rust 契约里落为判定顺序）。
- **判定环**：直读能开但魔数不可读 → 继续走嗅探与代理决策；经代理暂存后再判出 DLT → 仍按 `route = LocalDecode` 解码（解码对象是暂存副本）→ route 与 channel 的组合语义见 §7 K2。

### 4.2 列声明（columns[]）

| 列 | 来源 | 可空场景 |
| --- | --- | --- |
| Time | 存储头 `seconds` + `microseconds`（原文显示，不换算时区） | 无 |
| Lv | MSTP=LOG 时由 MTIN 映射 `F/E/W/I/D/V` | APP_TRACE / NW_TRACE / CONTROL 无级别 → 留空 |
| Tag | `APID/CTID` | 无 UEH 时为空 |
| Message | 解码后的参数文本 / control 的 `[服务名] hex` / non-verbose 的 `[十进制 id] hex` | 恒有 |
| # | 行号（core 侧行序，不是 DLT 索引） | — |
| （不进 UI） | ECU / SEID / mcnt / TMSP / 相对秒 / 原始 hex | 留给积压卡切片 |

- 列 id 必须跨版本稳定（契约 B3）。**建议 id 集**（待与 logcat 首版 id 集同批定死，本文只给建议，不算定稿）：
  - logcat 7 列：`row` / `time` / `pid` / `tid` / `level` / `tag` / `message`
  - DLT 5 列：`row` / `time` / `level` / `tag` / `message`
- 语义键 `labelKey` 归宿主文案（契约 B1），core 只出键。
- PID/TID 恒空 → DLT 文档不提供 pid/tid 列，宿主按声明渲染即不会出现这两列。
- 不复刻 dlt-viewer 的 `-----` 装饰符；`[control]` / `[non-verbose]` 不做前缀。

### 4.3 警告计数（warnings[]）

- 契约首版 `warnings[]` 留空数组（探索笔记 S4 甲：留空成本远低于将来改描述符结构）。**DLT 是第一个真实使用方** → 本 change 必须定死元素形状，否则跨语言结构改动落在 DLT 落地时。
- 建议形状 `{ code, count }`，DLT 至少两码：
  - `DLT_RECORD_SKIPPED`：中间损坏记录跳过数（含 resync 后仍不可解析者）；
  - `DLT_TRUNCATED`：单条超长（超出单条长度上限）跳过数。
- 「首条解析失败 → 不打开」不进 `warnings[]`，走终态错误事件（`error` code + params），错误码表见 §7 K8。

### 4.4 身份四要素（identity）

- 契约：四要素 = size / mtime / 首尾各 64 KB 的 FNV-1a 指纹（现状实现：头尾各 64 KB，小文件首尾同段；时间戳取 `LastWriteTimeUtc.Ticks`）。
- **DLT 的身份必须取「用户所选的源 `.dlt` 文件」**，不是解码产物：否则源文件改一个字节而解码文本恰好未变时，缓存与失效判定会给出错误结论。
- 解码产物（规范文本临时文件）是**派生物**，不参与身份计算。

### 4.5 装载模式与 sidecar 缓存

- 契约：`loadMode` = `OneShot`（命中缓存，一次到位）/ `Progressive`（未命中，分块渐进）；缓存只服务本地直读源，暂存与采集产物恒 `Progressive`。
- DLT 是否入缓存见 §7 K3（**倾向：首版不入**）。

### 4.6 呈现与时间

- 转换期把真实换行写成字面 `\r\n` 两个字符，保证一行一消息；`\t`、引号、反斜杠原样。
- 两通道时间形态不同（自研 `-` / 外部导出 `/`）本期不统一，不额外加规范化遍。

### 4.7 事件与进度

- 转换进度按**字节**（已处理字节 / 总字节），复用契约既有进度原语（阶段 + 已处理量 + 总量）；总量取源文件大小，客观不存在时如实为空。
- 阶段序列固定且单调不回退：解码 → 索引 → 完成（契约硬约束）。
- 中间损坏走 `warning` 计数累加，不打断流程；首条失败走终态 `error`。

## 5 Rust 侧落点与骨架

### 5.1 解码器归属（待拍板）

| 选项 | 内容 | 代价 |
| --- | --- | --- |
| **甲（倾向）** | 解码器进 `crates/core` 内部模块（如 `crates/core/src/format/dlt/`）：core 内部把 `.dlt` 解成规范文本，接同一条索引路径 | 不扩 FFI 面（cabi 导出面不变）；解码与索引同侧，不经跨界 |
| 乙 | 独立 crate `crates/dlt`，`crates/core` 依赖它 | 协议规范与许可隔离更清晰、可单独 cargo test；多一个 crate 与版本协同，与甲差别不大 |
| 丙（旧口径） | 解码留 C#，core 只判 format | **与 D1 冲突**：宿主不持有日志数据、不映射文件，core 无法消费宿主写的临时文本；除非宿主先解码再把文本路径交给 core，但「首条失败不打开」「损坏计数」等口径会分裂在两侧，且 core 的 identity 语义要重定义 |

倾向甲：先用模块边界（不新增 crate），乙留作 design 阶段的等价备选；丙建议作废并在 §6.2 记明。

### 5.2 crate 布局

```
crates/core   纯算法 + IO，无 FFI 面
  └─ format/dlt/   DLT 解码（魔数判定 / 记录解析 / resync / 规范文本产出）
crates/cabi   cdylib：extern "C" + catch_unwind 薄包装  ->  logcat_core.dll
crates/cli    同一 core 的可执行壳  ->  对拍 oracle / 调试 / MCP 退路
```

- DLT 解码只在 `crates/core` 内，**不新增 cabi 导出函数**——宿主侧感知 DLT 的唯一途径是源描述里的 `format` 与 `columns[]`。
- `crates/cli` 可提供 `dump --format dlt <file>` 形态的入口，用于 §10 的对拍（文本层 diff，不做 dll diff）。

### 5.3 解码流水线

1. **魔数扫描**：读 16 字节存储头，验 `44 4C 54 01`；不匹配 → 不是 DLT（调用方回退嗅探 / 代理路径）。
2. **流式记录解析**：按 §3.1 的层序推进，`len` 为 BE 且不含 Storage Header；记录边界 = 标准头 `len` + 实际头长。
3. **resync**：记录长度不可信或校验失败时，回到「下一个 `44 4C 54 01` 出现处」重新对齐；命中即计一次 `DLT_RECORD_SKIPPED`。
4. **payload 分派**：verbose（按 `noar` 个 TypeInfo + 数据解参数文本）/ non-verbose（`u32 id` + 原始字节 → `[十进制 id] hex`）/ control（`u32 服务 id` + 状态码 + 参数 → `[服务名] hex`，未知服务输出 `[十进制 id]`）。
5. **级别映射**：MSTP=LOG 时按 MTIN 映射 `F/E/W/I/D/V`；其它 MSTP 留空。
6. **规范文本产出**：见 §5.4。
7. **结束**：源读尽即完成；中途 I/O 错误 → 终态错误。

约束：**必须流式**（不整体读入内存）；解码与建索引是两遍 I/O（解码产出文本 → 索引读文本）。

### 5.4 规范文本形态

自研解码输出（**无索引号 + `-` 日期 + 方括号字段段**）：

```
2026-10-13 08:00:00.000000 [TEST] [TCTX] [log] [info] [verbose] [12] Hello DLT 200 65500
```

- 固定 6 位小数秒；`-` 日期前缀让行首时间戳识别天然命中 `yyyy-MM-dd`。
- 字段段顺序：ECU / CTID / type / subtype / mode / apid / payload。
- 上表只描述**文本形态**；其解析在 Rust 侧（§5.7），C# 侧无对应改动。

### 5.5 临时文本生命周期（core 拥有）

- DLT 解码产物是 core 内部的**派生产物**：落在宿主传入的 `tempDir` 下（契约 A12：配置只经快照传入）。
- 命名：与采集/暂存同族但需可区分，建议 `logcat_dlt_<seq>`；`seq` 为 core 进程内全局原子计数，独占创建（已存在则递增重试），与已归档 change `source-acquire-contract` design D7 的采集文件口径同形。
- 租约：DLT 产物属「关闭即删」类（`leaseKind = Ephemeral`），与采集的「保留供保存」相对。
- 生命周期 = 源句柄生命周期：关闭 → 删产物；取消 → 清空 + 删产物；失败 → 删产物不残留。

### 5.6 活性锁：Rust 侧已识别的雷

- 现状（C#）：暂存目录内 `.alive.lock` 以 `FileAccess.ReadWrite + FileShare.Delete` 持有；清理侧以 `FileShare.None` 试开，**打得开 = 无主**（`FileProxyService.cs:421`、`:445`）。
- Rust `std::fs::OpenOptions` 在 Windows 上恒以全共享模式打开，无法表达独占 → **Rust 持锁 + Rust 探测 = 判无主 → 删掉活着的暂存目录**（静默错误）。
- 收法（design 阶段定）：`windows-sys` 直接 `CreateFileW(share_mode = 0)`；或改用不依赖共享语义的机制（Job Object / 命名互斥）。
- **DLT 产物若落在同一暂存根下并沿用同一锁机制，则同样踩这条雷** → DLT 落地前必须先定这一项（§7 K5）。

### 5.7 外部导出文本通道

- 外部 dlt-viewer ASCII 导出文本**原样直读**，但行首形态与 logcat 不同：`可选数字索引 + yyyy/MM/dd`（斜杠分隔），logcat 是 `MM-dd` / `yyyy-MM-dd`（连字符）。
- Rust 侧行首识别须同时接受两种分隔符 + 可选索引前缀；否则**外部文本能打开但 5 列里 4 列失效**（无时间、无级别、无 Tag、整行即 Message）。
- 该分支属 core 的行解析器，不属 DLT 解码器（解码器只吃 `.dlt` 二进制）。

### 5.8 与索引路径的对接

```
plan(format=Dlt) ──► commit: .dlt ─►[decode]─► 规范文本(临时) ─►[index 同一条路径]─► 列式索引
                              └─►[直读]───────────────────────────────┘
```

- 索引层对 DLT 不感知格式，只吃文本 → 索引、过滤、导出、分页全部复用，无 DLT 专属分支。
- 崩溃 / ANR 打标绑在索引期（标在父行），DLT 文本同样会命中关键词 → 行为与 logcat 一致。
- 列投影：索引产出既有 8 段列式索引（`offs` / `lens` / `moff` / `min` / `pid` / `tid` / `lvl` / `tagId`）；DLT 的 pid/tid 恒空、tag 取 APID/CTID，由**解析层**决定，不在索引层特判。
- 侧记：「9 列」是 `remove-join-merge` 删掉 `flags` 列之前的口径，当前为 8 段；Rust 笔记已同步改为「8 列」。

### 5.9 FFI 与 panic 边界

- DLT 不新增 cabi 导出函数 → panic 防护口径不变：逐导出函数 `catch_unwind` 薄包装，panic 转错误码，绝不让 unwind 逃出边界（Rust 笔记 §9.2 已本机实测：裸 `extern "C"` 内 panic = 宿主进程直接死）。
- DLT 属**长耗时输入处理**：与页查询并发时不得互相阻塞——解码在独立线程，页查询走已发布索引快照（Rust 笔记 §3.4 并发硬约束）。
- 取消：解码支持原子取消标志，粒度不小于一条记录；取消后删除产物。

## 6 已定决策与已关闭项

### 6.1 沿用（原笔记已关闭，本轮不重开）

| 主题 | 结论 |
| --- | --- |
| 通道范围 | 两条通道同批：`.dlt` 解码 + 外部文本直读 |
| 路线 | 文本物化（解码 → 规范文本 → 既有索引路径） |
| 规范行形态 | 无索引 + `-` 日期 + 方括号字段段；固定 6 位小数秒 |
| 外部文本 | 原样直读；行首识别同时接受「可选索引 + `/` 或 `-`」 |
| 字段口径 | Tag = `APID/CTID`；PID/TID 恒空；ECU/mcnt/session/索引不进 UI |
| 呈现 | DLT 固定 5 列、不做列配置；无级别行 Lv 留空；导出仅 `.txt` UTF-8 |
| 时间 | 不换算时区，按文件原值显示；两通道时间差可接受 |
| 生命周期 | 打开 `.dlt` 即静默转临时产物；关闭即删、不复用缓存；DLT 禁用自动重载 |
| 失败口径 | 首条失败不打开（终态错误）；中间损坏跳过 + 计数；空文件当空文档；保留魔数 resync，单条超长跳过 |
| 不拆 exe | 维持单 exe；若将来要隐藏设备功能，用启动模式而非拆包 |
| 验收 | 字段级等价：消息数 + Time/Lv/Tag/Message 逐条比对，不要求与 dlt-viewer 逐字节一致 |
| 测试落点 | Rust 侧 cargo test 为主体（UT）；IT 经 FFI 打 Rust 实现 |

### 6.2 被 Rust 方向改写或作废的旧决策

| 旧决策 | 现状 | 依据 |
| --- | --- | --- |
| 「DLT 解码不进内核」（Rust 笔记已删除的旧结论） | **作废**：与 D1「log 文件操作完全归 Rust」冲突；解码归属见 §5.1（待拍板） | Rust 笔记 §2.1 D1；DLT 归属见其 §12 |
| 「解码器落主 exe `Services/Dlt*/`」（原 K2 甲） | **作废**：C# 侧不再有 DLT 解码器 | 获取链笔记 A8 / 本文 §5.1 |
| C# 期三处改动（`ParseLine` DLT 分支、`HeaderBytes` 接受 `/`、`LogListView` 静态列集改运行时） | **整条删除**：判定与索引归 Rust，列集由 `columns[]` 下发 | 维护者 2026-11-12 口径 / 契约 A4 |
| 「原 `LooksLikeNewRecord` 补白名单」（原 K8） | **取消**：续行合并机制已随 `remove-join-merge` 整体删除，生产侧零调用点 | 归档 change `remove-join-merge` |
| 「等 `sidecar-cache-index` 归档后再做」（原 K1 乙） | **已满足**（2026-11-06 归档） | 归档 change `sidecar-cache-index` |
| 路由 / 格式判定的归属（原 K9「留 C# 前置还是 Rust plan」） | **已定**：判定归 core（契约 A1），DLT 魔数判定是 core 判定链的一环 | 已归档 change `source-acquire-contract` |

## 7 待决项

> 编号延续原笔记 K 系列（K1–K9 取活口重排），新增项从 K10 起。

| 编号 | 议题 | 选项 | 倾向 | 卡住什么 |
| --- | --- | --- | --- | --- |
| **K1** | DLT 解码器落点 | 甲 `crates/core` 内模块<br>乙 独立 crate `crates/dlt`<br>丙 留 C#（旧口径） | **甲**（乙为等价备选，丙建议作废） | crate 布局、FFI 面是否扩张 |
| **K2** | `route = LocalDecode` 与 `channel` 的组合语义 | 甲 route 只表「本地解码」，channel 表来源，解码对象由 core 内部记录<br>乙 增 route 值区分「直读解码 / 暂存解码」 | **甲** | 描述符字段集（C# / Node 两宿主共用） |
| **K3** | DLT 是否入 sidecar 缓存 | 甲 不入（每次解码 + 建索引）<br>乙 入，缓存条目额外记源身份 | **甲** | 入缓存需给字节格式加字段 → 触及 C6「sidecar 格式冻结」禁区，须另立 change；且命中仍需解码，收益只覆盖索引一段 |
| **K4** | DLT 产物的租约语义 | 甲 复用现取值集，DLT = `Ephemeral`<br>乙 增第三种取值 | **甲**（design D14 现取值集为 `Ephemeral` / `Retained`） | 描述符字段集 |
| **K5** | Rust 侧暂存 / 产物目录的活性锁机制 | 甲 `windows-sys` `CreateFileW(share_mode = 0)` 复刻独占语义<br>乙 改用 Job Object / 命名互斥，不依赖共享语义 | **甲**（与 C# 现状同形，对拍口径最小） | DLT 产物与代理暂存同根时的误删风险（§5.6） |
| **K6** | DLT 列 id 集与 logcat 首版 id 集同批定死 | 甲 随在途切片一并定死（契约 B3 已要求）<br>乙 DLT change 内再定 | **甲（2026-11-16 已定）**：随**下一个触及列契约的 change**（在途切片已归档）把 DLT 5 列 id（`row` / `time` / `level` / `tag` / `message`，§4.2 建议集）与 logcat id 集一并写死——纯命名，成本接近零；其余 K 项仍待 DLT change design 时拍 | 宿主「列集切换」IT 用例的前置 |
| **K7** | `warnings[]` 元素形状 | 甲 `{ code, count }` 对象<br>乙 纯计数数组 | **甲** | 跨语言结构 + DLT 损坏计数的落点 |
| **K8** | DLT 专属错误码 | 甲 新增 `DLT_DECODE_FAILED` + params<br>乙 复用 `UNSUPPORTED_FORMAT` | **甲** | 错误码表（design D15）扩展 |
| **K9** | 时间语义字段的表达 | 甲 复用现 `timeSemantics` 取值，DLT = `Original`<br>乙 显式加「不换算时区」标志位 | **甲**（现取值集为 `Original` / `Relative`，DLT 用 `Original`） | 描述符字段集；DLT 与 logcat 的时间语义需可区分（logcat 亦为原值，需确认是否要另立取值） |

**默认项**（不反对即按此写进 spec）：

| 编号 | 内容 | 默认 |
| --- | --- | --- |
| **K10** | `app_trace` / `nw_trace` 与含换行 payload 仍无样本 | 不补样本，spec 标「待样本验证」 |
| **K11** | 转换前按源大小 ×1.5 预检磁盘，不足即报错 | 保持，不做自动清理 |
| **K12** | 时间列两种形态（自研 `-` / 外部导出 `/`）本期不统一 | 保持不统一，不额外加规范化遍 |

## 8 风险与许可

| 风险 | 处置 |
| --- | --- |
| 许可 | 仓库外试验工程的代码**不整体搬运**，按协议规范独立重写；确需引用片段时在 THIRD-PARTY/README 注明来源与「该工程无 LICENSE」的事实。协议字段与位定义是事实，不受版权保护 |
| 测试样本 | 自造合成 `.dlt` 生成器（已知答案的 4 条覆盖 STRG/SINT/UINT/FLOA/BOOL/RAWD、控制报文、non-verbose）；第三方真实样本（`01_cpu_mon.dlt`、`1.txt`）不入库，需要时从外部路径读取 |
| 大文件 | 解码必须流式；转换与索引是两遍 I/O |
| 体积 | 规范文本一般大于二进制源，磁盘占用需在实施期给估算 |
| 崩溃误报 | 快筛关键字 `SIG` 会命中 `SIGNAL`，只多解码几次，不产生误报 |
| 静默错误 | §5.6 活性锁在 Rust 侧的独占语义失效 → 误删活目录；属**静默**错误，必须单列验证任务 |
| 进度失真 | 解码进度按字节，总量取源文件大小；源不可 stat 时总量如实为空（契约硬约束） |

## 9 MCP 侧位置

- MCP 卡（B3）原本把 DLT 排在第三切片；该卡路线已被 `source-acquire-contract` 接管，**DLT 不占 MCP 切片序位**——它由 core 的 format 面承载。
- MCP 工具面两套口径（`MCP-FUNCTIONALITY.md` 与 `MCP-LOG-ANALYSIS-API.md`）尚未拍板；DLT 落地后，MCP 侧只需能对 `format = Dlt` 的源读回同样 5 列，不需理解 DLT 二进制。
- 契约「采集只属图形界面」不影响 DLT：DLT 是文件源，第二宿主（Node）可用。

## 10 测试与验收切面

> 口径对齐已归档的获取契约 change：UT 验基本逻辑、尽可能多覆盖（Rust 侧 cargo test）；IT 验「按契约调一次，拿得回正确数据」。

### 10.1 UT（cargo test，core 内）

| 面 | 覆盖点 |
| --- | --- |
| 魔数判定 | 命中 `DLT\x01` / 不命中 / 文件短于 4 字节 / 扩展名不是 `.dlt` 但魔数命中 |
| 记录解析 | 各层组合（有无 Extra / UEH / MSBF / WTMS）；`len` 为 BE 的边界；端序 |
| resync | 损坏记录后重新对齐到下一个魔数；跳过计数正确 |
| payload 分派 | verbose 参数文本；non-verbose `[id] hex`；control `[服务名] hex` 与未知服务 `[十进制 id]` |
| 级别映射 | MTIN → `F/E/W/I/D/V`；APP_TRACE / NW_TRACE / CONTROL 无级别 → 留空 |
| 规范文本 | 固定 6 位小数秒；真实换行转字面 `\r\n`；字段段顺序 |
| 行首识别 | logcat 文本（`MM-dd` / `yyyy-MM-dd`）与外部导出文本（`可选索引 + yyyy/MM/dd`）两条分支 |
| 取消 | 单条记录粒度取消；取消后产物删除 |
| 描述符组装 | `route=LocalDecode` / `format=Dlt` / `columns[]` 5 列 / `identity` 取源 `.dlt` 四要素 / `warnings[]` 计数 |

### 10.2 IT（经 FFI 打 Rust 实现）

| 用例 | 预置数据源 | 断言 |
| --- | --- | --- |
| DLT 直读 | 含首条 / 中间损坏记录的合成 `.dlt` | 记录数与预置一致；`columns[]` 5 列；`warnings[]` 跳过计数与预置一致；首条损坏则终态错误、不建立源 |
| 外部导出文本直读 | dlt-viewer 导出的 ASCII 文本 | 5 列逐列读回值与预置相等；行数一致 |
| 列集切换 | 同一次获取中换格式 | 宿主按 `columns[]` 重建表头后按 id 取列不错位 |
| 身份与失效 | 同一 `.dlt` 二次打开；再改一个字节 | 身份一致 / 变化两态各自的表现符合描述符声明 |
| 进度与终态 | 解码中途轮询事件 | 进度单调不回退；终态唯一；取消后无残留产物 |

### 10.3 验收判据

字段级等价：消息数 + Time/Lv/Tag/Message 逐条比对；不要求与 dlt-viewer 逐字节一致（时间口径本就不同，§3.3）。对拍在文本层做（CLI dump diff），不在 dll 层。

## 11 下一步

0. **排期已定（2026-11-16）**：Rust 重写本期（刀1–刀5）**零 DLT 工作**；唯一例外是 K6（列 id 随下个列契约 change 同批定死）。接缝现状（2026-11-18 核对）：`format` 轴、`columns[]` 机制已在 code 与 spec；`route = LocalDecode` 与 `warnings[]` 仍不存在（现有渠道只有 `LocalDirect` / `ProxySpool` / `DeviceStream`）；「魔数先于嗅探」与 14 的生产者随前置 change `reject-unsupported-format` 落地，只用于错误标签、不解码。DLT 整体作为独立 change 插队与否由维护者届时定。
1. 拍板 §7 的 K1–K5、K7–K9（未表态项按「倾向」写进 spec 并标「待确认」；K6 已于 2026-11-16 定甲）；K10–K12 按默认项。
2. 走 `/opsx:propose` 建 change（名建议 `dlt-log-format`），范围 = §4 契约面 + §5 Rust 侧实现；获取契约 `source-acquire-contract` 已归档、接缝已落进主规范，本文不改其行为。
3. apply 在该 change 专属 worktree `wt/dlt-log-format` 实施；发版归下一 minor（需另轮明确指令）。

## 12 关联

- `openspec/notes/RUST-CORE-REWRITE-SLICING.md`：Rust 七刀 / 三轨、crate 布局、FFI 契约、panic 边界、活性锁雷（本文 §5.6 的来源，见其 §6.3）。
- `openspec/notes/RUST-CORE-EVALUATION.md`：语言决策与对拍契约（其 C3 被本文 §5.1 改写）。
- `openspec/notes/archive/2026-11-15-SOURCE-ACQUIRE-CONTRACT.md`：获取契约的完整定案（本文 §4 的上位来源，已归档）。
- `openspec/notes/BACKLOG-MCP-AND-FORMATS.md`：MCP 卡（B3），DLT 的切片序位（§9）。
- `openspec/notes/MCP-FUNCTIONALITY.md`、`openspec/notes/MCP-LOG-ANALYSIS-API.md`：MCP 侧两套未拍板口径。
- 已归档 change `source-acquire-contract`：design 定稿；spec delta 已并入 `openspec/specs/logcore/source-acquire/`（本文 §4 / §5.1 以其为准）。
- `docs/TESTING.md`（测试口径唯一权威源）、`docs/BUILDING.md`（Rust 构建步骤待随 Rust 切片补）。
