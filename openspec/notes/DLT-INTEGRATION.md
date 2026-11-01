# DLT 格式接入：路线对比与设计约束

- 状态：**待评审**（2026-10-30 / 10-31 七轮探索均已记录，见 §10；§11 待决项已全部收口，Q7 按倾向默认、Q8–Q10 按默认，不同意再改口；未编码，待 `/opsx:propose` 指令）
- 日期：2026-10-30（2026-10-31 增补 §12 / §13 / §14 / §15，含 dlt-viewer 官方设置页对照）
- 关联：积压卡 `openspec/notes/BACKLOG-MCP-AND-FORMATS.md`（DLT 是其第三切片；其 D2/D7 的排序描述已按 P5 同批修订）；在途 change `chunked-progressive-load`（0/13）
- 输入通道（用户口径）：① `.dlt` 二进制文件；② **外部 DLT Viewer 导出的文本**；③ 维护者自研的 `dltviewer` 试验工程（格式文档 + 参考实现 + 测试数据，见 §2.1 / §15）
- 验收结论：不适用（未实施）

## 1 目标与非目标

**目标**：在当前日志查看器里打开 DLT 来源的日志，并复用现有的视图列、过滤、时间过滤、导出能力。

| 做 | 不做 |
| --- | --- |
| 打开 `.dlt` 二进制并解码为可读记录 | 原样展示二进制块（hex dump 视图） |
| 打开外部 DLT Viewer 2.31.0 导出的 ASCII 文本，识别其列结构 | 适配任意第三方导出变体（CSV / 其他版本） |
| verbose 参数全解析；控制报文服务名；non-verbose `id + hex` | FIBEX 驱动的 non-verbose 全解析 |
| 字段映射到按格式区分的列集（DLT 固定 5 列 `# / Time / Lv / Tag / Message`，F2） | 为 DLT 提供 ECU/Session/mcnt 专属列与列配置（留给切片 3） |
| 大文件流式解码 | 亿级消息的内存模型 |
| 时间按文件原值显示（不做时区换算，N8） | 时区归一 / 跨源时间对齐 |

## 2 事实核对（2026-10-30）

### 2.1 外部 dltviewer 工程（仓库外，未纳入本仓库 git）

代码位置（相对该工程根）：`DltParser/`、`docs/DLT解析格式.md`、`testdata/`。**该工程是维护者自研的 DLT 解析试验工程**（非本仓库、不在本仓库 git 历史内）；根目录无 LICENSE、无 README；`docs/DLT解析格式.md` 的位定义与端序结论参考了 COVESA/dlt-daemon、esrlabs/dlt-core、mbehr1/dlt-logs 三个上游仓库（见 §15）。

| 文件 | 事实 |
| --- | --- |
| `DltParser/Dlt.cs` | v1 协议常量：HTYP/MSIN/MSTP/MTIN、TypeInfo 位域、SCOD、TYLE、服务名表 |
| `DltParser/DltFileParser.cs` | `File.ReadAllBytes` 整份读入后按 `len`(BE) 逐条切块；含魔数 resync；产出 `DltMessage` 列表 |
| `DltParser/DltMessage.cs` | 消息模型：ECU/APID/CTID/mcnt/TMSP/MSTP/MTIN/Verbose/Noar/payload |
| `DltParser/DltPayloadDecoder.cs` | verbose TypeInfo 解码；控制报文 `svc id`；non-verbose 输出 `id + hex` |
| `DltParser/Program.cs` | CLI：`parse` / `gen` / `selftest`；输出的 `[ECU] APID/CTID …` 形态 **不是**外部导出格式 |
| `docs/DLT解析格式.md` | 自包含格式规范：`len` 不含 Storage Header、头部 BE / 载荷 LE（MSBF=0）、TMSP 0.1 ms、字符串长度含 `\0`、7 条已知坑位；文末附 dlt-daemon / dlt-core / Python 对照实现 |
| `testdata/01_cpu_mon.dlt` | 真实文件（36 KB，247 条，来自 mbehr1/dlt-logs），verbose 字符串报文 |
| `testdata/sample.dlt` | `SampleDltWriter` 合成的 4 条（覆盖 STRG/SINT/UINT/FLOA/BOOL/RAWD、控制报文、non-verbose） |
| `testdata/1.txt` | `01_cpu_mon.dlt` 经 DLT Viewer 导出的 ASCII 文本（247 行，一行一消息，行尾带空格） |
| `testdata/index/*.dix` | DLT Viewer 的索引缓存，与本方案无关，不在引用范围 |

结构性特点：整份读入（无流式）、解码与索引分离（正文不落盘）、无自动化测试。

### 2.2 当前 viewer 的装载与索引面

| 环节 | 事实（代码位置） |
| --- | --- |
| 入口分流 | `frmMain.StartIndex`：`TextProbe.LooksLikeText` 只用于决定是否转「文件代理」，与格式无关 |
| 文本嗅探 | `Services/TextProbe.cs`：ASCII 控制字符（含 NUL）占比 >10% 判非文本 |
| 构建 | `LogDocument.Build` → `DoBuild` → `ScanRange`：按 `'\n'` 切行，mmap 原文件，建 9 列索引 |
| 列语义 | `offs`/`lens`/`moff`/`minute`/`pid`/`tid`/`lvl`/`tagId`/`flags` |
| 行解析 | `Services/LogParser.ParseLine`：快速路径只认 `MM-dd` 与 `yyyy-MM-dd`（`-` 分隔） |
| 新记录判定 | `LogParser.LooksLikeNewRecord`：只认 `-` 分隔的两种日期形态、`[` 开头、`L/Tag` 形态、`---------` |
| 时间列 | `LogDocument.HeaderBytes` 截 `[off, off+moff)` 内的时间戳原始字节，同样只认 `-` 分隔 |
| 消息列 | `LogListView.MakeFields` 用 `MessageBytes(row)` = 原文件 `[off+moff, off+len)` |
| 续行合并 | `ScanRange`：仅当上一条 `prevHead`（`WithinMs>=0 || Level>0`）且本行不像新记录才并进上一条 |
| 过滤 | `FilterEngine` 块扫描直接扫 mmap 裸指针；时间过滤吃 `minute` 列（0–59，无时间=255） |
| 崩溃 | `CrashExtractor` 快筛关键字 `FATAL EXCEPTION` / `SIG` / `ANR in ` / `*** ***` |
| 不变式 | 行号、列、正文、导出全部以「原文件字节 + 偏移」为唯一数据源；没有格式抽象层 |

### 2.3 外部 DLT Viewer 的导出行为（实测）

本机 DLT Viewer 2.31.0 的命令行转换：

```
dlt-viewer.exe -t -s -u -c <out.txt> <in.dlt>                         ASCII 文本
dlt-viewer.exe -t -s -u --csv --signature TSEACP -c <out.csv> <in.dlt> CSV
```

- `-t` 转换完退出，`-s` 无 GUI，`-u` 输出 UTF-8；进度写 stderr，退出码 0。
- **必须在安装目录作为工作目录执行**，否则进程静默挂起（已复现 2 次，10–15s 无输出即挂）。
- 用 `testdata/01_cpu_mon.dlt`（真实 247 条）与 `testdata/sample.dlt`（合成 4 条）均可成功导出。

ASCII 文本实测形态（**一条消息一行**，行尾有一个空格）：

```
0 2026/10/13 08:00:00.000000 1.0000 0 TEST TAPP TCTX 1 log info verbose 12 Hello DLT 200 65500 424242 ... ???? ?
1 2026/10/13 08:00:01.000111 2.0000 1 TEST TAPP TCTX 1 log error verbose 1 error test
2 2026/10/13 08:00:02.000222 3.0000 2 TEST TAPP TCTX 1 control request non-verbose 0 [set_log_level] 54 41 50 50 54 43 54 58 04
3 2026/10/13 08:00:03.000333 4.0000 3 TEST TAPP TCTX 1 log info non-verbose 0 [4660] -----|e8 03 00 00 07
```

字段序（`--signature` 默认 `ITSOEACNYUMRP`）：

```
索引 I | yyyy/MM/dd HH:mm:ss.ffffff | 相对秒 | mcnt | ECU | APID | CTID | sessionId | type | subtype | mode | nargs | payload
```

- `type`：实测有 `log`、`control`；`app_trace` / `nw_trace` **未取样**（合成样例里没有）。
- `subtype`：log 的级别来源，实测 `info` / `error`；`fatal` / `warn` / `debug` / `verbose` 未取样但按规范存在。
- `mode`：`verbose` / `non-verbose`。
- non-verbose 与 control 的 payload 形态：`[svc名]` 或 `[十进制 id]` 后跟十六进制字节，non-verbose 还带 `-----|` 前缀。
- CSV：首行是表头 `"Time","Timestamp","Ecuid","Apid","Ctid","Payload"`（`TSEACP`），每列带引号；`--signature` 选列、`--delimiter` 改分隔符。**CSV 丢掉了 type/subtype/mode/nargs**，级别与 Tag 都拿不到。

以上是**外部导出形态**。自研解码器（§10 N1 丙）产出的规范文本不同：无索引号、日期用 `-`、字段段按 P1 定稿。两种形态都由同一个 `ParseLine` 分支识别（N2 甲）。

### 2.4 形态冲突（更新）

```
              viewer 需要                DLT 导出文本
记录边界        '\n'                        '\n'（一行一消息，前提成立）
时间            行首 yyyy-MM-dd 形态        yyyy/MM/dd（斜杠），且前面可能有索引号
PID / TID      有                          没有
级别            V/D/I/W/E/F                type/subtype 文本（log info / log error …）
Tag             字符串                     APID + CTID 两列
正文            ':' 之后                    nargs 之后的 payload
```

结论：**外部导出文本与现有引擎只差「行首时间戳识别」这一层**——记录边界、行内正文都已经是文本；真正的重活只在 `.dlt` → 文本的解码。这与首轮的猜测方向相反，是本轮最有价值的修正。

## 3 字段映射（更新）

| viewer 列 | 来源 | 说明 |
| --- | --- | --- |
| Time | `yyyy/MM/dd HH:mm:ss.ffffff` | 原文显示（斜杠形态）；需让 `HeaderBytes` 认 `/` |
| PID / TID | 无 | 恒 `-1`（`LogListView` 对 `-1` 显示空） |
| Lv | log 的 subtype：fatal→F、error→E、warn→W、info→I、debug→D、verbose→V | `control` / `app_trace` / `nw_trace` 无级别 → 未知 |
| Tag | `APID/CTID` | 决定 Tag 过滤项粒度（§8 C5） |
| Message | payload（nargs 之后到行尾） | control 含 `[svc]`（未知服务 `[十进制 id]`）、non-verbose 含 `[id] hex`；不复刻 dlt-viewer 的 `-----` 装饰（I4） |
| Minute | 由行首时分派生（0–59） | 时间过滤的唯一依据 |

丢弃字段（本期不进 UI）：索引、相对秒、mcnt、ECU、sessionId。若用户需要，可后续加列或拼进 Message。

## 4 路线

```
A 文本物化    .dlt --[流式解码]--> dlt-viewer 同形文本 --[现有 BuildAsync]--> 视图/过滤/导出
B 原生块索引  .dlt --[块扫描]--> 扩展 9 列 + 「已解码文本」旁路表 --[新过滤路径]
D 外部转换    .dlt --[调用 dlt-viewer.exe -c]--> 文本 --[现有 BuildAsync]--> 同上（零解码代码）
C 先 A 后 B   A 立即可用；B 留给 ILogFormat 抽象（积压卡第三切片）
```

| 维度 | A 文本物化 | B 原生块索引 | D 外部转换 |
| --- | --- | --- | --- |
| 改动面 | 解码模块 + 文本行分支 + 入口分流 + 表头差异化（F2/H1） | `LogDocument`/`HeaderBytes`/`FilterEngine`/`LogListView` 全动 | 仅入口分流（进程调用） |
| 与 `chunked-progressive-load` | 冲突小（入口层） | 强冲突（同文件、同编排段） | 冲突最小 |
| 过滤性能 | 复用现有 SIMD 块扫描 | 需为解码后字符串另建匹配路径 | 同 A |
| 依赖 | 无 | 无 | **依赖外部安装**（路径/版本/工作目录） |
| 磁盘 | 多一份解码文本（临时目录） | 无 | 多一份，且转换慢（Qt 进程启动） |
| 交付形态 | 自包含单 exe | 自包含 | 不是自包含 |
| 风险 | 中（解码器要自己写） | 高（破坏「原文即真相」） | 低，但产品化不可接受 |

**已定 A（§10 C2 甲 / N1 丙 / N2 甲）**：自研流式解码产出「无索引 + `yyyy-MM-dd` + 专用字段段」的规范文本，外部 dlt-viewer 文本原样直读，两条通道共用同一个 `ParseLine` 分支；`LogDocument` 已支持 `displayPath`（显示原 `.dlt`、实际读临时文件），临时文件复用代理模式的 spool 目录与生命周期口径。

D 仅保留为验证手段：先用 `dlt-viewer.exe -c` 对照结果校准自研解码器，不进产品路径；路线 B 仍归积压卡的 `ILogFormat` 切片。

## 5 文本行解析口径（基于实测）

### 5.1 现有引擎在该文本上的实际行为（已推演）

| 判据 | 结果 |
| --- | --- |
| `LooksLikeNewRecord` | **false**（索引号开头或 `/` 日期都不在识别列表）→ 不会开启续行合并 |
| `ParseLine` 快速路径 | 不命中（只认 `line[2]=='-'` / `line[4]=='-'`）→ `(WithinMs=-1, Level=0, Tag="", MsgOffset=0)` |
| `prevHead` | false → payload 内换行会变成独立垃圾记录，而不是续行 |
| `HeaderBytes` | 斜杠日期不匹配 → 返回空 → **Time 列空白** |
| 列结果 | 每行成为「无时间、无级别、无 Tag、整行即 Message」的记录 |

即：**不做任何改动也能打开，但六列里四列失效**。

### 5.2 需要新增/修改的位置（按 §10 定稿：N1 丙 + N2 甲）

自研输出的规范行（N1 丙）形如：

```
yyyy-MM-dd HH:mm:ss.ffffff <APID> <CTID> log <subtype> <mode> <nargs> <payload>
```

该形态的时间前缀是 `-` 分隔，现有 `LooksLikeNewRecord`（`yyyy-MM-dd` 分支）与 `HeaderBytes`（`yyyy-MM-dd` 分支）**天然命中，不需要改**。

需要改的仍是三处，但原因与首轮不同——全部来自「外部文本原样直读」（N2 甲）：

1. `LogParser.ParseLine`：新增 DLT 文本分支，同时接受「可选数字索引 + `/` 或 `-` 日期」；产出 `WithinMs`（年内毫秒，`Year` 透出）、`Minute`、`Level`（由 subtype 映射）、`Tag = APID/CTID`、`MsgOffset = payload 起点`。
2. `LogParser.LooksLikeNewRecord`：为「索引号 + `/` 日期」形态补白名单。**必须与 1 同批**：DLT 行一旦时间生效，`WithinMs>=0` 就会置 `prevHead`，若本判据不认 DLT 行，`control` 等 `Level=0` 的行会被并进上一条记录。自研输出的 `-` 形态已命中现有规则，不受影响。
3. `LogDocument.HeaderBytes`：日期分隔符同时接受 `/`，并在有索引前缀时跳过「数字 + 空格」（仅外部文本需要；自研输出无索引）。40 字节截断余量足够（≤10 位索引仍 < 40）。


### 5.3 未取证项与已定口径

- 仍缺样本（不阻塞）：`app_trace` / `nw_trace` 的 type/subtype/mode 文本形态；payload 含换行/制表符时外部导出的**实际**行为（我方策略已定，见 N7 与 P6，仅影响断言写法）。
- 已定（N8）：时间不做时区换算，按 DLT 存储头时间原样显示；外部文本以文本里写的时间为准，viewer 不重新解释。
- 现有真实样本里 `subtype` 只出现 `info`，`fatal/warn/debug` 拼写未证实——按 DLT 规范实现并标注待验证（P10）。

## 6 影响面（propose 依据）

- `frmMain.StartIndex`：**在代理分流之前**判 DLT（N4/F1）：二进制 = 魔数 `DLT\x01`（`.dlt` 扩展名作快路径），文本 = 解析首行（跳过空行）命中 DLT 文本形态；命中则跳过代理、走 DLT 装载（二进制本地解码为临时文本，`displayPath` = 源路径，C6）；未命中维持现有 logcat 路径不变，不做逐行试探。
- **解析失败口径（F3 + Q2/I3）**：判定命中 DLT（魔数或首行）后——**首条解析失败 → 弹提示、不打开**；**中间个别消息解不开 / 损坏 → 跳过 + 计数，继续其余**（P7 同一机制）。普通 logcat 文本维持现状宽容行为。
- 新增 DLT 解码/转换模块（流式、零 UI 依赖、独立目录，C4）：`.dlt` → §10 N1 规范文本（临时文件）。
- `Services/LogParser.cs`：新增 DLT 文本分支 + `LooksLikeNewRecord` 白名单（同批，N2）。
- `Services/LogDocument.cs`：`HeaderBytes` 支持 `yyyy/MM/dd` 与可选索引前缀（仅外部文本路径，N2）。
- `Controls/LogListView.cs`：按文档格式重建列集（F2/R1 = H1）：DLT 固定 5 列 `# / Time / Lv / Tag / Message`（E4），logcat 维持 7 列。
- 不改：`FilterEngine`、`CrashAggregator` 核心。
- 测试：合成 `.dlt` 生成器（自带 4 条已知答案，C3）；字段级等价比对（消息数 + 五列，N3）；外部样本仅验解析器（P4 倾向甲）。

## 7 风险

1. **许可（已处置，C3 甲）**：dltviewer 工程无 LICENSE、非 git 仓库；`testdata/01_cpu_mon.dlt` 来自第三方 → 按 `docs/DLT解析格式.md` 重写，测试样本自造，不入第三方样例。
2. **与在途 change 的先后（已处置，C4 乙）**：`chunked-progressive-load` 正改加载编排；本方案独立 worktree 并行，已核实其 design 不改 `BuildAsync` 签名，冲突面限于 `frmMain.StartIndex` 与进度回调。
3. **大文件**：解码必须流式（不能照抄 `ReadAllBytes`）；转换与索引是两遍 I/O。
4. **临时文件生命周期（已处置，N5）**：沿用代理 spool 目录与活性锁，关闭即删，取消/失败同口径清理。
5. **体积膨胀**：ASCII 文本一般大于二进制源，磁盘占用需有口径（P7 未覆盖，实施期给估算）。
6. **时区（已处置，N8）**：不换算，以文件时间为准；外部文本与自研输出可能因导出机时区不同而不同，比对口径见 P4。
7. **崩溃误报开销**：快筛关键字 `SIG` 会命中 DLT 里的 `SIGNAL` 等词，只增加解码次数，不产生误报（`ExtractCrashAt` 仍需完整判据）。
8. **换行（已处置，N7）**：转换阶段把真实换行转成字面 `\r\n`，一行一消息前提成立；其余控制字符见 P6。

## 8 需要澄清的问题

### C1 外部导出文本的真实格式（**已解决**）

实测见 §2.3：ASCII 文本、`yyyy/MM/dd`、一行一消息、type/subtype/mode 齐全。仍缺 `app_trace` / `nw_trace` 与含换行 payload 的样本（§5.3），不阻塞主线。

### C2 二进制通道是否同批做

- **为什么需要澄清**：只支持外部导出文本的话，`.dlt` 仍不能直接打开。
- **方案**：甲 两条通道同批（自研流式解码 + 文本分支）；乙 先做外部文本，`.dlt` 下一期；丙 先用路线 D 调外部转换顶住。
- **已拍板（2026-10-30）**：**甲**。两条通道同批，change 同时覆盖解码器与文本分支。

### C3 代码来源与许可

- **为什么需要澄清**：无 LICENSE 的工程代码不能直接搬进本仓库。
- **方案**：甲 按 `docs/DLT解析格式.md` 重写（行为对齐、代码独立）；乙 取得授权后引入；丙 只引入 `testdata` 样例。
- **已拍板（2026-10-30）**：**甲**，且测试样本自造（写合成 `.dlt` 生成器），不引入第三方 `testdata`。

### C4 与在途 change / 积压卡的先后

- **为什么需要澄清**：会碰同一批文件（入口、`LogDocument`），顺序不定则 tasks 与测试基线写不准。
- **方案**：甲 等 `chunked-progressive-load` 归档后再做；乙 独立 worktree 并行；丙 并入积压卡第三切片统一排期。
- **已拍板（2026-10-30）**：**乙**，独立 worktree（分支 `wt/dlt-log-format`）并行。前提：解码模块零 UI 依赖、独立目录，后续 Core 抽取只需 `git mv`；已核实 chunked 的 design 不改 `BuildAsync` 签名，冲突面只有 `frmMain.StartIndex` 与进度回调。

### C5 字段口径

- **为什么需要澄清**：影响 Tag 过滤项可用性，以及 ECU/session/mcnt 会否丢失。
- **方案**：Tag 甲 `APID/CTID`；乙 仅 `APID`（CTID 进 Message）；丙 拆两个 Tag。PID/TID 留空；control/app_trace/nw_trace 归未知级别；丢弃字段默认不进 UI。
- **已拍板（2026-10-30）**：**甲 + PID/TID 留空 + 丢弃字段不进 UI**。

### C6 转换时机

- **为什么需要澄清**：决定是「打开时自动转」还是「显式菜单转换另存」。
- **方案**：甲 打开 `.dlt` 时静默转临时文件并展示（源路径作 `displayPath`）；乙 提供「转换为文本」命令，产出独立文件后由用户打开；丙 两者都要。
- **已拍板（2026-10-30）**：**甲**（体验一致），转换进度复用现有加载进度条。

## 9 建议的下一步

1. 拍板项已全部收口（§10 第一至七轮）；默认项 Q7 / Q8–Q10 不同意再改口。
2. 走 `/opsx:propose`，change 名 `dlt-log-format`，范围 = 路线 A（自研流式解码 + 文本行分支 + 入口预先判定）。
3. 路线 D 仅作校准手段（`dlt-viewer.exe -c` 对照），不进产品路径；路线 B 并入积压卡的 `ILogFormat` 切片。
4. 补两份取证样本（`app_trace`/`nw_trace`、payload 含换行）：用 DLT Viewer 对本机任意真实 `.dlt` 导出；未取证前按 §10 P10 口径处理。

## 10 决策记录（2026-10-30 / 2026-10-31）

### 第一轮

| 编号 | 议题 | 结论 | 落地含义 |
| --- | --- | --- | --- |
| C2 | 通道范围 | 甲 两条通道同批 | change 同时覆盖 `.dlt` 解码与外部文本直读 |
| C3 | 代码与样本许可 | 甲 按规范重写；允许参考引用并附来源说明 | 不整体搬运仓库外代码；引用其片段/样本时按 §15 加来源声明；测试样本以自造为主 |
| C4 | 与其他工作先后 | 乙 独立 worktree 并行 | 分支 `wt/dlt-log-format`；解码模块零 UI 依赖、独立目录，后续 Core 抽取只需 `git mv` |
| N1 | 规范文本形态 | 丙 无索引 + `-` 日期 + 专用字段段 | `yyyy-MM-dd HH:mm:ss.ffffff APID CTID type subtype mode nargs payload`；`-` 前缀让现有 `LooksLikeNewRecord` / `HeaderBytes` 无需改动 |
| N2 | 外部文本接入 | 甲 原样直读 | `ParseLine` 分支同时接受「可选索引 + `/` 或 `-`」；`HeaderBytes` 补 `/` 与索引前缀；`LooksLikeNewRecord` 补 DLT 形态（与 `ParseLine` 同批） |
| N3 | 验收口径 | 字段级等价 | 消息数 + Time/Minute/Lv/Tag/Message 五列逐条比对；不要求与 dlt-viewer 逐字节一致 |
| C5 | 字段口径 | 甲 `APID/CTID`；PID/TID 恒空；ECU/mcnt/session/索引不进 UI | Tag 过滤项为 `APID/CTID`；control 行归未知级别 |
| C6 | 转换时机 | 甲 打开 `.dlt` 即静默转临时文件 | 源路径作 `displayPath`，转换进度复用现有进度条 |
| N4 | 入口判定 | 预先判定 DLT，先于代理分流 | 判据 = 魔数 `DLT\x01`（扩展名 `.dlt` 作快路径）。理由：不保证每台机器配了代理，配了代理也不保证能处理 DLT；DLT 恒走本地解码，文本通道维持原分流不变 |
| N5 | 临时文件生命周期 | 沿用 spool 目录与活性锁；关闭即删；不复用缓存；>1GB 沿用确认框 | 与代理 spool 同一套「何时删、失败/取消怎么清」口径 |
| N6 | Reload 语义 | DLT 文档禁用自动重载；`Reload` 只重读临时文本 | 源 `.dlt` 变更不反映，避免静默过期 |
| N7 | payload 换行 | 转义为字面 `\r\n` 序列，保证一行一消息 | 转换阶段把真实换行写成 `\r` `\n` 两个字符；其余控制字符见 §11 P6 |
| N8 | 时区 | 不换算，按文件时间原样显示 | 时间列 = DLT 存储头时间；不做 UTC→本地换算。两通道时间差 8 小时可接受（视为不同的 Log，G1/Q11 已关闭） |

### 第二轮（§11 原待决项全部按倾向关闭）

| 编号 | 议题 | 结论（= 原倾向） | 落地含义 |
| --- | --- | --- | --- |
| P1 | 字段段语法 | 方括号分段 | `yyyy-MM-dd HH:mm:ss.ffffff [APID] [CTID] [log] [info] [verbose] [12] payload`；与普通 logcat 行区分度最高 |
| P2 | 小数秒位数 | 固定 6 位 | 与 dlt-viewer 同形，保留 DLT 0.1 ms 粒度 |
| P3 | 不可读 `.dlt` | 允许代理只取副本（spool）再本地解码 | 代理的价值是「读得到」，不是「转格式」；DLT 恒本地解码 |
| P4 | 双通道时间口径 | 比对只对自研输出，外部样本仅验解析器 | 避免测试随导出机时区漂移 |
| P5 | 规格载体 | 新 capability `dlt-log-format` + `time-column` delta；同批更新积压卡 B3 | `HeaderBytes` 认 `/` 属 `time-column` 覆盖行为，按 AGENTS §2.1 补 delta；B3 的 D2/D7 已同步修订 |
| P6 | 其余控制字符 | 只转 `\r`/`\n`；`\t`、引号、反斜杠原样 | — |
| P7 | 损坏/超大 `.dlt` | 保留魔数 resync；单条超长跳过并计数；空文件当空文档 | — |
| P8 | 无级别行呈现 | Lv 留空；Message 不加 `[control]` / `[non-verbose]` 前缀 | — |
| P9 | 取消转换 | 清空文档并删除临时文件 | — |
| P10 | 未取证样本 | 按 DLT 规范猜测实现 + spec 标「待样本验证」 | 更新：`testdata/1.txt` 提供 247 行真实导出可作解析基准；仍缺 `app_trace`/`nw_trace` 与含换行 payload 的样本 |
| P11 | 发版 | 归下一 minor `0.2.0`（另需当轮指令） | — |

### 第三轮（2026-10-31，新增探索结论）

| 编号 | 议题 | 结论 | 落地含义 |
| --- | --- | --- | --- |
| E1 | logcat 与 DLT 是否拆成两个 exe | **不拆**，维持单 exe | 依据见 §12；若将来需要「DLT 入口看不到设备功能」，用启动模式（`--dlt` / `.dlt` 关联）而非拆包 |
| E2 | 素材来源与许可 | 该 dltviewer 工程是维护者自研试验工程；代码可引入并附来源说明；测试样本标注来源 | 见 §15：以独立重写为主；引用代码或再分发真实样本时加 THIRD-PARTY / NOTICE 声明 |
| E3 | 一条 DLT 记录的字段口径 | 以协议为准（§14 三层头 + 载荷），视图仍按 C5 映射 | ECU/SEID/mcnt/TMSP/原始 hex 本期不入 UI |

### 第四轮（2026-10-31，对照 dlt-viewer 官方设置页）

| 编号 | 议题 | 结论 | 落地含义 |
| --- | --- | --- | --- |
| E4 | 列集与可配置性 | **本期不做列配置**，DLT 文档固定列集 `# / Time / Lv / Tag / Message`（5 列） | 取官方默认勾选里的高频项（Time、Type/Subtype、Application/Context、Payload）；ECU、Count、Timestamp、Mode、Session、Message Id、Columns 一律不做列。列集固定于代码，不提供设置项；将来要配置走 §13 H3 / 积压卡切片 3 |

### 第五轮（2026-10-31，入口判定 / 表头 / 失败口径）

| 编号 | 议题 | 结论 | 落地含义 |
| --- | --- | --- | --- |
| F1 | 打开文件时如何判定 DLT / logcat | 二进制靠解析（魔数）确认；文本解析一行确认 | 二进制：魔数 `DLT\x01`（先于代理分流，N4）；文本：首行（跳过空行）命中 DLT 文本形态即按 DLT 装载，否则维持现有 logcat 路径，不做逐行试探 |
| F2 | 表头按 DLT / logcat 区分 | R1 拍板 = **H1**（仅 `LogListView` 按格式重建列集） | DLT 固定 5 列（E4），logcat 维持 7 列；不碰 `LogDocument` / `FilterEngine`，与 C4 乙并行前提兼容；H3 记积压卡切片 3 |
| F3 | 解析失败口径 | **提示失败，不硬解析** | 判定命中 DLT 但解析不出有效记录 / 解码异常 → 弹提示、不打开，不产出垃圾记录；个别损坏记录仍按 P7（跳过 + 计数）；普通 logcat 文本维持现状宽容行为 |

### 第六轮（2026-10-31，时间口径 / 导出 / 性能）

| 编号 | 议题 | 结论 | 落地含义 |
| --- | --- | --- | --- |
| G1 | 时间口径（Q11 关闭） | **维持 N8 不换算**；两通道时间不同就当作不同的 Log 看 | 自研输出 = 存储头 UTC 原值；外部文本 = 文本所写时间；不追求一致，不做时区归一 |
| G2 | 导出语义（Q5 关闭） | **仅支持 .txt UTF-8 导出** | DLT 文档导出走现有文本导出能力，不提供原始 `.dlt` 另存、不新增其他格式 |
| G3 | 性能指标（Q6 关闭） | **不设性能指标** | 转换进度仍按字节（位置/总长）；不做「247 条 / 1 GB」性能回归 |

### 第七轮（2026-10-31，Q3/Q4 拍板；Q1/R3 重述待拍）

| 编号 | 议题 | 结论 | 落地含义 |
| --- | --- | --- | --- |
| I1 | 未知级别与级别过滤（Q3 关闭） | **甲**：本期不加「未知」筛选项 | control / app_trace / nw_trace 行 Lv 留空；spec 明写「只在无级别过滤时可见」 |
| I2 | spec 条款粒度（Q4 关闭） | **甲**：6 条 requirement + `time-column` 1 条 delta | 入口判定 / 解码 / 字段映射 / 临时文件 / 未取证容错 / 文本分支，另加 DLT 列头（R1/F2）；`HeaderBytes` 认 `/` 归 `time-column` delta |
| I3 | 解不开数据的处理（Q2 关闭） | **首条解析失败 → 提示不打开；中间消息解不开 → 跳过 + 计数，继续** | 首条失败说明文件大概率不是 DLT 或严重损坏，拒绝打开（F3）；中间个别失败跳过该条（与 P7 同一机制），不因少见类型废掉整份真实文件 |
| I4 | non-verbose 正文装饰符（Q1 关闭） | **乙：去掉 `-----` 装饰分隔符** | non-verbose 显示 `[十进制 id] hex`；control 显示 `[服务名] hex`，未知服务输出 `[十进制 id]`；不复刻 dlt-viewer 的装饰符 |
| I5 | ECU / mcnt / session 是否显示（R3 关闭） | **甲：维持 C5，本期不显示** | Time / Lv / Tag / Message 够用；三字段留给积压卡切片 3 的每格式列改造 |

## 11 待决项（已全部收口，2026-10-31）

> 已核实、无需决策：仓库已有入库测试工程 `tests/logcat.Tests`，合成 `.dlt` 生成器与解码/解析测试放该工程即可（不涉及新增工程结构）。

### 阻塞 propose

| 编号 | 议题 | 为什么必须定 | 选项与倾向 |
| --- | --- | --- | --- |
| Q1 | control / non-verbose 报文的 Message 怎么写 | **已关闭（第七轮 I4）**：去掉 dlt-viewer 的 `-----` 装饰分隔符；non-verbose = `[十进制 id] hex`，control = `[服务名] hex`（未知服务输出 `[十进制 id]`） | — |
| Q2 | 解不开的数据怎么办 | **已关闭（第七轮 I3）**：首条解析失败 → 提示、不打开；中间消息解不开 → 跳过 + 计数、继续解析其余 | — |
| Q3 | 未知级别与级别过滤的交互 | **已关闭（第七轮 I1）**：本期不加「未知」筛选项，control 行 Lv 留空，spec 明写「只在无级别过滤时可见」 | — |
| Q4 | spec 条款粒度 | **已关闭（第七轮 I2）**：6 条 requirement（入口判定 / 解码 / 字段映射 / 临时文件 / 未取证容错 / 文本分支）+ `time-column` 1 条 delta | — |
| **Q11** | 时间口径重审（与 N8 冲突） | **已关闭（第六轮 G1）**：维持 N8 不换算；两通道时间差 8 小时可接受，视为不同的 Log（自研输出 = 存储头 UTC 原值，外部文本 = 文本所写时间） | — |

### 半阻塞（propose 前定，影响设计段）

| 编号 | 议题 | 选项与倾向 |
| --- | --- | --- |
| Q5 | 导出语义 | **已关闭（第六轮 G2）**：仅支持 .txt UTF-8 导出，不提供原始 `.dlt` 另存、不新增其他格式 |
| Q6 | 转换性能与进度口径 | **已关闭（第六轮 G3）**：不设性能指标；进度按字节（`位置/总长`） |
| Q7 | 入口判定失败路径 | 读魔数前 4 字节失败/超时（网络路径）时：甲 回退扩展名并直接报错；乙 回退到代理 spool 取副本再判定（与 P3 乙 一致）；丙 一律先 spool。倾向 **乙**（按默认执行） |

### 实施期可默认（不同意再改口）

| 编号 | 议题 | 默认 |
| --- | --- | --- |
| Q8 | Time 列形态不统一 | 自研输出 `-`、外部导出 `/`，同一列两种显示形态；本期不统一（统一需再改 `HeaderBytes` 或加规范化遍） |
| Q9 | 续行合并开关（`_actJoin`） | DLT 文档不强制关闭：N7 已转义换行，理论上无续行；外部文本若未转义，续行合并只是把断行并回上一条，不误并 |
| Q10 | 磁盘空间预检 | 转换前按源大小 ×1.5 估算，不足即报错；不做自动清理 |

### 表头 / 索引格式（2026-10-31 新增，见 §13）

| 编号 | 议题 | 为什么需要定 | 选项与倾向 |
| --- | --- | --- | --- |
| **R1** | 表头差异化落点 | 决定 `dlt-log-format` 是否多一条 requirement，以及是否碰 `LogDocument` | **已关闭（第五轮 F2）**：H1，仅 `LogListView` 按格式重建列集，并入本 change；H3 只记为积压卡切片 3 的需求输入，不提前 |
| **R2** | H1 下 DLT 列集形态 | 决定列头、列宽与空列取舍 | **已关闭（第四轮 E4）**：固定 `# / Time / Lv / Tag / Message` 5 列，不做列配置 |
| **R3** | ECU / mcnt / session 要不要趁机显示 | **已关闭（第七轮 I5）**：维持 C5，本期不显示，留给积压卡切片 3 | — |

## 12 附：logcat 与 DLT 拆成两个 exe 的可行性（2026-10-31 探索）

**结论：技术上没有硬障碍，但成本不在「多编一个 exe」，而在拆共享 UI 层与发布/测试配套；当前不拆（E1）。**

### 12.1 现状事实（决定可行性的三条硬约束）

| 事实 | 位置 | 对拆分的影响 |
| --- | --- | --- |
| 全仓库只有 1 个 exe 工程（根 `logcat.csproj`，无 `.sln`）；测试工程直接 `ProjectReference` 该 exe 并用 `InternalsVisibleTo` 访问 internal | `logcat.csproj`（测试用 `InternalsVisibleTo`）、`tests/logcat.Tests/logcat.Tests.csproj` | 拆分必须新增库工程；测试引用与 internal 可见性按程序集重配 |
| 「核心」不纯：`LogDocument` 直接调 `FileProxyService.DeleteDirBestEffort` 管 spool 租约；`Loc` 返回 `System.Drawing.Font`；`FilterEngine`/`LogDocument`/`LogParser` 共 11 处引用 `Loc` | `Services/LogDocument.cs`、`Services/Loc.cs` | 若要求「Core 库无 UI」，先解 `Loc`；否则共享库只能跟 WinForms 绑在 `net10.0-windows` |
| 进程身份写死：互斥量固定 `Local\logcat-viewer-single-instance`；设置存 exe 同目录 `settings.json`；发布两形态（自包含约 120 MB / 框架依赖约 2 MB），单文件 exe 实测不可启动 | `Services/SingleInstance.cs`、`Services/AppSettings.cs`、`logcat.csproj` | 两个 exe 放同目录会共用 `settings.json` 并互相顶掉单实例；需分目录或参数化名字 |

规模参考：`frmMain.cs` 超过 2260 行；`Services/` 约 32 个文件、`Forms/` 约 18 个、`Controls/` 4 个。设备侧（`AdbManager`/`LogcatStream`/`DeviceFilePane`/`FileBrowserDialog`/`Apk*`/`Command*`/`Screen*`/`DeviceOpsDialog`）约一半；日志视图侧（`LogDocument`/`LogParser`/`FilterEngine`/`LogListView`/`FilterDialog`/`Crash*`/`TextProbe`/导出）是可共享的另一半。

### 12.2 两种真实形态

```
形态 A：共享库 + 两个薄壳（真正省维护）
  logcat.exe（设备页签/代理/adb 流/APK…）  dlt.exe（本地文件 + DLT 转换）
                    \                        /
                     -> 共享 Core + Log UI 库（文档/解析/过滤/列表/崩溃/导出）

形态 B：两个壳各自带一套 UI（复制）
  logcat.exe [设备 + 日志 UI]    dlt.exe [简版日志 UI，复制粘贴]
  -> 两个 exe 有了，但列表/过滤/导出逻辑从此双份维护
```

成本的主要项是「把 `frmMain` 的日志视图与设备视图切开」，与「是不是两个 exe」几乎无关——即使不拆 exe，要让 DLT 模式隐藏设备功能也得做这层切割。

### 12.3 选项与代价

| 选项 | 做法 | 增量成本 | 收益 | 风险 |
| --- | --- | --- | --- | --- |
| O1 完整拆分 | 3 个库工程（Core / LogUI / DLT 解码）+ 2 个壳 + 测试改引用 | 大：切 `frmMain`、拆 `Loc` 表、参数化单实例与设置、4 种发布产物、README/许可/关联双份、测试矩阵翻倍 | 边界清晰；DLT 包不带 adb；可独立发版 | 中高：动到所有 UI 入口，回归面大 |
| O2 共享库 + 单窗体注入特性 | 一个 `ViewerForm`，按启动配置注入设备或 DLT 特性 | 中：加特性开关与菜单裁剪，不物理拆 UI | 2 个 exe、1 份 UI 代码 | 中：大窗体长满条件分支，易腐化 |
| O3 单 exe + DLT 模式 | 同一 exe，`--dlt` 或 `.dlt` 关联启动时隐藏设备页签 | 最小（几十行） | 体验一致、零拆分 | 仍是一个包，仍带 Android 依赖 |
| O4 同源双构建 | 同一源码树，MSBuild 开关裁掉设备相关 `Compile` 项，产出两个 exe | 中：需精确文件级裁剪清单；IDE/测试看到两套编译面 | 2 个 exe、源码一份 | 中高：被排除文件在另一产物无测试覆盖 |

### 12.4 体积/分发账

- 自包含包约 120 MB 主要是 .NET 运行时，去掉 `AdvancedSharpAdbClient` 省不了多少；框架依赖包约 2 MB 才有可见差别。
- 两个自包含目录 = 约 240 MB 总下载；共享同一自包含目录 = 约 120 MB + 数 MB，但要处理同名依赖与设置文件。
- 拆分**不减少 DLT 功能本身的实现量**：解码器、临时文件、入口判定照旧；先拆包再写功能只会拖慢。

### 12.5 若将来重估拆分，前置顺序

```
dlt-log-format（功能） -> core-extraction（纯搬迁） -> ui-extraction（切日志视图/设备视图） -> 2 exe（O1）
```

保留重估触发条件：① 合规/杀软对 adb 敏感；② 两个产品要独立发版节奏；③ 存在「DLT 用户不应持有 Android 工具能力」的分发对象。仅「模块边界整洁」不构成拆分理由。

## 13 附：DLT 与 logcat 使用不同表头 / 索引格式的可行性（2026-10-31 探索）

**结论：表头差异化便宜且自包含（已拍板 H1，第五轮 F2）；索引格式只能改「元数据列」层，核心 5 列必须通用。**

### 13.1 索引与消费方的事实

```
9 列索引                                         消费方                    列无关?
offs/lens/moff/min/flags   <- 记录边界、时间过滤、续行合并、零拷贝正文   不可动
pid/tid/lvl/tagId          <- FilterEngine 匹配、LogListView 渲染、Tag 过滤   可按格式变化

导出   = 原文整行；复制行 = 遍历 SubItems；崩溃系 = 扫正文文本   -> 三者均列无关，零改动
```

- 列定义是静态的：`Controls/LogListView.cs` 的 `COLUMNS` 与 `COL_*` 常量，构造时一次性建列；`MakeItem` 要求 SubItems 数等于列数；`MakeFields` 硬编码「time/pid/tid/lvl/tag/msg」6 字段。
- 过滤热路径直接读列 span：`Services/FilterEngine.cs` 两条扫描路径同构引用 pid/tid/lvl/tag。
- 解析输出为固定 8 字段 struct（`ParseResult`，含 Pid/Tid）：`Services/LogParser.cs`。
- 日志视图列宽当前不持久化（只有文件面板持久化）→ 改列集无迁移负担。
- 内存账：核心 5 列约 22 B/行；logcat 元数据 13 B/行；DLT 若加 ECU/mcnt/session/type 约 14–18 B/行，省掉无用的 pid/tid（−8 B）后基本对消 → **「不同索引格式」省不了内存，动机只能是展示与过滤能力**。

### 13.2 四个落点

| 方案 | 做法 | 改动面 | 得到什么 | 代价/风险 |
| --- | --- | --- | --- | --- |
| H1 表头差异化 | `SetDocument` 时按文档格式重建列集；`MakeFields` 按格式渲染（DLT 去掉 PID/TID） | 仅 `LogListView`；过滤器/导出零动 | DLT 显示自己的表头；不碰 `LogDocument`，与 C4 乙的并行前提兼容 | 信息量不变：仍受 C5 约束，ECU/mcnt/session 依旧没有 |
| H2 语义重载 | pid←mcnt、tid←session，表头改名 | `LogListView` + `FilterDialog` 标签 | 白得 mcnt/session 过滤（现有 PID/TID 过滤框直接可用） | 语义污染：导出文本、复制、用户认知都要重解释；ECU 仍无家（塞 Tag 前缀会破坏 C5 甲的 `APID/CTID` 粒度） |
| H3 每格式索引 schema | 核心 5 列 + 每格式元数据列 + schema 描述符；过滤对缺席列短路 | `LogDocument`（存储+构建）、`ParseResult`、`FilterEngine`、`LogListView`、`FilterDialog` | ECU/mcnt/session/type 真正进列、可过滤 | 中大型；与在途 `chunked-progressive-load` 同文件冲突（它正改 `LogDocument` 构建编排），破坏 C4 乙 并行前提；实质是把积压卡切片 3 提前 |
| H4 全量 `ILogFormat` | 积压卡原计划抽象层 | 同 H3 + API 泛化 | 终态 | 最大，维持原排期 |

### 13.3 判断

- H1 **已拍板（第五轮 F2）**，并入 `dlt-log-format`：一个格式描述符（列名数组 + 默认列宽 + 字段渲染分支），改动集中在 `LogListView` 一个文件，spec 加一条「DLT 文档使用 DLT 列头」的 requirement。
- H2 不建议：便宜但把列语义变成约定俗成，等 H3 / `ILogFormat` 落地时全是返工点。
- H3 若确为真需求，不夹带：与 chunked 同批文件冲突，正确顺序是等 chunked 归档或明确把积压卡切片 3 提前并重排 worktree 计划；本轮只把「DLT 想要 ECU/mcnt/session 列」记为 H3 的需求输入。

### 13.4 对照 dlt-viewer 官方设置页（Project Table）

官方设置页（Settings → Project Table）可选字段与实测默认值：

| 官方项 | 默认 | 我们本期取舍 |
| --- | --- | --- |
| Show Index | 勾选 | 视图 `#` 列是**行号**（现行实现），不是 DLT 索引 |
| Show Time | 勾选 | Time 列 |
| Show Count（mcnt） | 未勾 | 不做列（留给切片 3） |
| Show Timestamp（TMSP 相对） | 未勾 | 不做列 |
| Show Ecu Id | 勾选 | 不做列（C5 已定） |
| Show Type（log/control/…） | 勾选 | 不单列；作为 Lv 的判定来源与 Message 语义 |
| Show Subtype（info/error/…） | 未勾 | Lv 列的实际取值来源 |
| Show Mode（verbose/non-verbose） | 未勾 | 不单列（体现在 Message 形态） |
| Show Payload | 勾选 | Message 列 |
| Show Columns / Show #Arguments | 未勾 | 不做列（参数直接拼进 Message） |
| Show Application（App Id / Description） | 勾选（App Id） | Tag 前半（APID） |
| Show Context（Con Id / Description） | 勾选（Con Id） | Tag 后半（CTID） |
| Show Session/Process Id | 未勾 | 不做列 |
| Show Message Id（Non-Verbose） | 未勾 | 不单列（按 Q1 进 Message） |
| Set time column automatic to local recording time | 勾选（UTC+08:00，自动取 DLT） | **印证 Q11**：官方默认就把存储头时间转本地时区 |

结论（已定，第四轮 E4）：**本期不做列配置**，固定 `# / Time / Lv / Tag / Message` 五列；官方那些未勾选的字段（Count、Timestamp、Ecu、Session、Message Id、Columns）既不进列也不在本期补设置项。将来开放配置时按 H3 的 schema 描述符做，而不是加开关。

## 14 附：一条 DLT 记录包含的内容（2026-10-31 核对）

`.dlt` 文件没有文件头，是「记录顺序拼接」；每条记录 = Storage Header（仅文件）+ Standard Header + 可选 Extra + 可选 Extended Header + Payload。

### 14.1 字节层

| 层 | 大小 | 字段 | 端序 | 说明 |
| --- | --- | --- | --- | --- |
| Storage Header（仅落盘文件） | 16 | `magic` char[4] | — | 固定 `44 4C 54 01`（`"DLT"` + `0x01`），损坏时按此魔数 resync |
| | | `seconds` uint32 | **LE** | Unix 秒（UTC） |
| | | `microseconds` int32 | **LE** | 微秒 |
| | | `ecu` char[4] | — | ECU ID，ASCII，`\0` 填充 |
| Standard Header | 4 | `htyp` uint8 | — | UEH(0x01) / MSBF(0x02) / WEID(0x04) / WSID(0x08) / WTMS(0x10) / 版本高 3 位（v1=0x20） |
| | | `mcnt` uint8 | — | 消息计数器，逐条 +1，255 回绕；跳变=丢包 |
| | | `len` uint16 | **BE** | 完整消息长度，**不含 Storage Header**（= 4 + extra + ext + payload） |
| Extra（顺序固定，按 HTYP） | 0/4/8/12 | `ECID` char[4] | — | WEID 时存在 |
| | | `SEID` uint32 | **BE** | WSID 时存在，Session ID |
| | | `TMSP` uint32 | **BE** | WTMS 时存在，相对时间，**单位 0.1 ms**（秒 = 值/10000） |
| Extended Header（UEH=1） | 10 | `msin` uint8 | — | VERB(0x01) + MSTP(0x0E>>1) + MTIN(0xF0>>4) |
| | | `noar` uint8 | — | verbose=参数个数；non-verbose=0 |
| | | `apid` char[4]、`ctid` char[4] | — | 应用 ID / 上下文 ID |
| Payload | 可变 | verbose | **LE**（MSBF=0） | `noar` × (TypeInfo uint32 + 数据) |
| | | non-verbose | LE | `uint32 message id` + 原始参数字节（无 TypeInfo，需 FIBEX 才能解出数值） |
| | | control（MSTP=3） | LE | `uint32 service id` + [uint8 状态码，仅 RESPONSE] + 参数 |

### 14.2 msin 派生的语义

| MSTP | 类型 | MTIN 含义 |
| --- | --- | --- |
| 0 | LOG | 0 OFF / 1 FATAL / 2 ERROR / 3 WARN / 4 INFO / 5 DEBUG / 6 VERBOSE |
| 1 | APP_TRACE | 1 VARIABLE / 2 FUNCTION_IN / 3 FUNCTION_OUT / 4 STATE / 5 VFB |
| 2 | NW_TRACE | 按规范定义（本次无样本） |
| 3 | CONTROL | 1 REQUEST / 2 RESPONSE / 3 TIME |

### 14.3 一条记录实际「有」的语义字段（13 项）

绝对时间（`seconds`+`microseconds`）、相对时间（`TMSP`）、ECU（Storage 的 `ecu` 或 Extra 的 `ECID`，优先 ECID）、Session ID、消息计数 `mcnt`、APID、CTID、消息类型（LOG/APP_TRACE/NW_TRACE/CONTROL）、子类型（级别或 control/trace 子类）、verbose 标志、参数个数 `noar`、各参数的 TypeInfo+值（或 non-verbose 的 id+原始字节）、原始 payload 字节长度与 hex。

映射到当前视图（受 C5 约束）：

| 视图列 | 取自 | 可空场景 |
| --- | --- | --- |
| Time | Storage Header `seconds`+`microseconds`（时区口径见 Q11） | 无（每条文件记录都有） |
| Minute | 由 Time 派生 0–59 | 同左 |
| Lv | MSTP=LOG 时由 MTIN 映射 F/E/W/I/D/V | APP_TRACE / NW_TRACE / CONTROL 无级别 |
| Tag | `APID/CTID` | 无 UEH 时为空 |
| Message | 解码后的参数文本 / control 的 svc / non-verbose 的 id+hex | 恒有 |
| PID / TID | — | 恒空（C5） |
| （未进 UI） | ECU / SEID / mcnt / TMSP / 原始 hex | 留给 §13 H3 与积压卡切片 3 |

### 14.4 真实样本逐字段核对（`testdata/01_cpu_mon.dlt` 首帧 vs `testdata/1.txt` 第 1 行）

| 字段 | 二进制解出 | dlt-viewer 导出 |
| --- | --- | --- |
| Storage 时间 | `seconds`=0x5E7CDB75=1585240949 → **2020-03-26 16:42:29 UTC**；`usec`=483000 | **2020/03/27 00:42:29.483000** |
| htyp / mcnt / len | 0x31（UEH+WTMS+v1）/ 77 / 128 | 未显示 |
| Extra | TMSP=835993（83.5993 s） | 相对秒列 = 83.5225 |
| msin | 0x41 = VERB + LOG + INFO(4) | `log info verbose 1` |
| ECU / APID / CTID | MGUL / MON / CPUS | `MGUL MON CPUS` |
| 参数 | 1 个 STRG："CPU usage in interval : 1.79257% …" | 同文本 |

两个结论：

1. **dlt-viewer 会把存储头时间换算成本地时区**（UTC 16:42:29 → +8 后 00:42:29），与 N8「不换算」存在差异——已拍板（G1）：不统一，两通道视为不同的 Log，自研输出按存储头 UTC 原值显示。
2. 导出的「相对秒」列与 TMSP **不相等**（83.5225 vs 83.5993），口径未证实；自研输出不使用该列，不构成阻塞。

### 14.5 参考实现的行为（不构成契约）

`DltPayloadDecoder` 的输出形态仅供对照：control 打 `svc=0x…(名称) status=… data=hex`；non-verbose 打 `nonverbose id=0x… payload=hex`；不支持类型打 `(unsupported typeinfo 0x…)`；尾部多余字节打 `(unparsed tail) …`。这些是试验工程的选择，与 §11 Q1/Q2 的最终口径无关。

## 15 附：素材来源与许可（2026-10-31）

| 素材 | 来源 | 再分发要求 |
| --- | --- | --- |
| `DltParser/*.cs` | 维护者自研试验工程（根目录无 LICENSE） | **不整体搬运**；按规范独立重写。确需引用片段时，在 THIRD-PARTY/README 注明来源与该工程无 LICENSE 的事实（E2） |
| `docs/DLT解析格式.md` | 维护者自研，位定义/端序结论参考 COVESA/dlt-daemon、esrlabs/dlt-core、mbehr1/dlt-logs | 作为事实参考使用；不复制其成段文字 |
| `testdata/sample.dlt` | 该工程合成的 4 条 | 可由本仓库自造生成器重新产出，不入库原文件 |
| `testdata/01_cpu_mon.dlt` | 第三方（mbehr1/dlt-logs） | 若要入库作真实样本，须在 NOTICE 标注来源与上游许可；否则测试时从外部路径读取、不入库 |
| `testdata/1.txt` | 由上述样本经 DLT Viewer 导出（派生数据） | 同上；优先在测试中现场生成 |
| `testdata/index/*.dix` | DLT Viewer 索引缓存 | 不使用 |

原则：**协议字段与位定义是事实，不受版权保护**；可安全地按规范重写。风险只在「直接复制代码/样本文件」两处，按上表处理即可。

