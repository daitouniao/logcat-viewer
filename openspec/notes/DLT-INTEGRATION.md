# DLT 格式接入：方案与待决项

- 状态：**待评审** —— 设计已定稿，剩 §2 的 K1–K8 待维护者拍板；未编码，等 `/opsx:propose`
- 日期：2026-11-06 重构（原 2026-10-30 / 10-31 七轮探索结论全量保留，事实按 2026-11-06 代码复核）
- 关联：`openspec/notes/BACKLOG-MCP-AND-FORMATS.md`（B3；DLT 是其第三切片）；在途 change `sidecar-cache-index`
- 验收结论：不适用（未实施）

## 1 一句话方案

打开 DLT 日志分两条通道，**解码后都归一到「一行一条消息」的文本，复用现有引擎**（列式索引 / 过滤 / 时间过滤 / 导出），DLT 文档只换表头。

```
 .dlt 二进制   --[自研流式解码]--> 临时规范文本 --+
                                                 +--> BuildChunkedAsync --> 视图 / 过滤 / 导出
 外部导出文本  --[原样直读]--------------------+
```

| 做 | 不做 |
| --- | --- |
| `.dlt` 二进制流式解码为可读记录 | 二进制 hex dump 视图 |
| 外部 DLT Viewer 2.31.0 的 ASCII 导出原样直读 | 适配第三方 CSV / 其他版本变体 |
| verbose 全解析；control 服务名；non-verbose `id + hex` | FIBEX 驱动的 non-verbose 全解析 |
| 按格式切换列集（DLT 固定 5 列） | 每格式列配置、ECU/Session/mcnt 专属列 |
| 时间按文件原值显示（不做时区换算） | 时区归一 / 跨源时间对齐 |

## 2 需要你决策（K1–K8）

### 阻塞 propose

| 编号 | 议题 | 选项 | 倾向 | 卡住什么 |
| --- | --- | --- | --- | --- |
| **K1** | 什么时候做 | 甲 现在 propose，独立 worktree `wt/dlt-log-format` 并行<br>乙 等 `sidecar-cache-index` 归档后再做<br>丙 现在 propose、暂不 apply | **甲** | 两者都要在 `frmMain.StartIndex` 加分派判定（见 §4.4），并行只在这一处产生一次合并冲突 |
| **K2** | 解码器落哪个工程 | 甲 主 exe `Services/Dlt*/`（零 UI 依赖，将来随 `core-extraction` 一次 `git mv`）<br>乙 等 `logcat.Core` 建库后再做（= 推迟 DLT）<br>丙 放 `logcat.UiCore` | **甲** | tasks 的目录结构与后续搬迁成本 |
| **K3** | 解码产物要不要持久缓存 | 甲 不缓存，每次打开重新解码到临时文本<br>乙 按 `.dlt` 源路径 + size/mtime 缓存「解码文本 + 索引」，二次打开近零成本<br>丙 只缓存索引 | **甲**（乙记为后续优化） | 临时文件生命周期、磁盘口径、与 sidecar `cache/` 的配额关系 |
| **K4** | 读魔数失败怎么走（原 Q7 补分支） | 甲 回退代理 spool 取副本再判；`FileProxyMode=Off` 时直接报错<br>乙 一律先 spool<br>丙 回退扩展名（`.dlt` 就当 DLT） | **甲** | `StartIndex` 判定链的代码形状 |

### 默认项（不反对即按此写进 spec）

| 编号 | 内容 | 默认 |
| --- | --- | --- |
| **K5** | 时间列两种形态（自研 `-` / 外部导出 `/`）本期不统一 | 保持不统一，不额外加规范化遍 |
| **K6** | 转换前按源大小 ×1.5 预检磁盘，不足即报错 | 保持，不做自动清理 |
| **K7** | `app_trace` / `nw_trace` 与含换行 payload 仍无样本 | 不补样本，spec 标「待样本验证」直接 propose |
| **K8** | 原「`LooksLikeNewRecord` 补白名单」 | **取消**：join 删除后生产侧零调用点（见 §4.3） |

## 3 已定设计（不再讨论）

### 3.1 规范文本形态

自研解码输出（**无索引号 + `-` 日期 + 方括号字段段**）：

```
2026-10-13 08:00:00.000000 [TEST] [TCTX] [log] [info] [verbose] [12] Hello DLT 200 65500
```

固定 6 位小数秒；`-` 前缀让现有 `ParseLine` / `HeaderBytes` 的 `yyyy-MM-dd` 分支天然命中。

### 3.2 字段映射（视图 5 列）

| 列 | 来源 | 可空场景 |
| --- | --- | --- |
| Time | 存储头 `seconds` + `microseconds`（原文显示，不换算时区） | 无 |
| Lv | MSTP=LOG 时由 MTIN 映射 `F/E/W/I/D/V` | APP_TRACE / NW_TRACE / CONTROL 无级别 → 留空 |
| Tag | `APID/CTID` | 无 UEH 时为空 |
| Message | 解码后的参数文本 / control 的 `[服务名] hex` / non-verbose 的 `[十进制 id] hex` | 恒有 |
| # | 行号（现有实现，不是 DLT 索引） | — |
| （不进 UI） | ECU / SEID / mcnt / TMSP / 相对秒 / 原始 hex | 留给积压卡切片 3 |

PID/TID 恒空。Message 不复刻 dlt-viewer 的 `-----` 装饰符；control 未知服务输出 `[十进制 id]`；`[control]` / `[non-verbose]` 不做前缀。

### 3.3 落地改动（三处 → 现在只剩两处）

1. `Services/LogParser.cs` `ParseLine`：新增 DLT 文本分支，接受「可选数字索引 + `/` 或 `-` 日期」；产出 `WithinMs`（年内毫秒）/ `Minute` / `Level`（由 subtype 映射）/ `Tag = APID/CTID` / `MsgOffset = payload 起点`。
2. `Services/LogDocument.cs` `HeaderBytes`：日期分隔符同时接受 `/`，并跳过可选的「数字 + 空格」索引前缀（≤10 位索引仍远小于 40 字节截断余量）。属 `time-column` 覆盖行为，按 AGENTS §2.1 补 delta。
3. ~~`LooksLikeNewRecord` 白名单~~ **取消**：续行合并机制已随 `remove-join-merge` 整体删除，`LogParser.LooksLikeNewRecord`（`Services/LogParser.cs:384`）生产侧零调用点。

### 3.4 入口判定与装载

- **先于代理分派**：二进制判魔数 `DLT\x01`（`.dlt` 扩展名作快路径）；文本解析首个非空行命中 DLT 形态即按 DLT 装载。不保证每台机器都配代理，配了也不保证能处理 DLT，故 DLT 恒走本地解码。
- 命中后：`.dlt` 本地流式解码到临时文本，`displayPath` = 源路径（标题栏/MRU 仍显示 `.dlt`），转换进度复用现有进度条并按字节计。
- 临时文件：沿用代理 spool 目录与活性锁，关闭即删，取消/失败同口径清理；>1 GB 沿用现有确认框。
- 失败口径：**首条解析失败 → 弹提示、不打开**；中间个别消息损坏 → 跳过 + 计数继续；普通 logcat 文本维持现状宽容行为。
- Reload：DLT 文档禁用自动重载，`Reload` 只重读临时文本（源 `.dlt` 变更不反映）。
- 转换期把真实换行写成字面 `\r\n` 两个字符，保证一行一消息；`\t`、引号、反斜杠原样。

### 3.5 列集与表头

`Controls/LogListView.cs` 的静态 `COLUMNS`/`COL_*` 改为按文档格式重建列集：DLT 固定 `# / Time / Lv / Tag / Message`（5 列），logcat 维持 7 列（`# / Time / PID / TID / Lv / Tag / Message`）。只碰 `LogListView`，不动 `LogDocument` / `FilterEngine`；本期不做列配置（对照 dlt-viewer Project Table 的取舍见旧结论：Count / Timestamp / ECU / Session / Message Id / Mode 均不进列）。

### 3.6 spec 覆盖

新 capability `dlt-log-format`（6 条 requirement：入口判定 / 解码 / 字段映射 / 临时文件 / 未取证容错 / 文本分支；含「DLT 文档使用 DLT 列头」）+ `time-column` 1 条 delta。

「无级别记录只在无级别过滤时可见」**不需要新增条款**：`openspec/specs/old/message-filter/spec.md` 的「元数据类过滤下的记录可见性」已覆盖。

## 4 事实基线

### 4.1 一条 DLT 记录的结构

`.dlt` 无文件头，记录顺序拼接；每条 = Storage Header（仅落盘文件）+ Standard Header + 可选 Extra + 可选 Extended Header + Payload。

| 层 | 大小 | 字段 | 端序 | 说明 |
| --- | --- | --- | --- | --- |
| Storage Header | 16 | `magic` char[4] / `seconds` u32 / `microseconds` i32 / `ecu` char[4] | LE | `44 4C 54 01`；损坏时按魔数 resync |
| Standard Header | 4 | `htyp` u8 / `mcnt` u8 / `len` u16 | `len` 为 BE | `len` 不含 Storage Header；htyp 位：UEH 0x01 / MSBF 0x02 / WEID 0x04 / WSID 0x08 / WTMS 0x10，版本 v1 在高 3 位 |
| Extra | 0/4/8/12 | `ECID` / `SEID` u32 / `TMSP` u32 | BE | 按 htyp 顺序出现；TMSP 单位 0.1 ms |
| Extended Header | 10（UEH=1） | `msin` u8 / `noar` u8 / `apid` / `ctid` char[4] | — | msin = VERB 0x01 + MSTP + MTIN |
| Payload | 可变 | verbose：`noar` × (TypeInfo u32 + 数据)<br>non-verbose：u32 id + 原始字节<br>control：u32 服务 id + [状态码] + 参数 | LE（MSBF=0） | non-verbose 无 TypeInfo，全解析需 FIBEX（不做） |

MSTP/MTIN：LOG（0）→ OFF/FATAL/ERROR/WARN/INFO/DEBUG/VERBOSE；APP_TRACE（1）→ VARIABLE/FUNCTION_IN/FUNCTION_OUT/STATE/VFB；NW_TRACE（2）；CONTROL（3）→ REQUEST/RESPONSE/TIME。

### 4.2 外部 dlt-viewer 导出形态（实测 2.31.0）

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
- 结论：外部文本与现有引擎**只差「行首时间戳识别」一层**；重活只在 `.dlt` 解码。

### 4.3 现有引擎现状（2026-11-06 复核）

| 环节 | 事实 |
| --- | --- |
| 工程结构 | 3 个 csproj：`logcat`（主 exe）+ `logcat.UiCore`（共享 adb/本地化/设备 UI）+ `logcat.AndTools`（工具箱 exe）；版本号在根 `Directory.Build.props` |
| 入口分派 | `frmMain.StartIndex`：先按 `FileProxyMode` 决定是否代理（`TextProbe.LooksLikeText` 仅用于 Auto 探测），本地文件走 `RunChunkedLoadAsync`（`frmMain.cs:1029`） |
| 加载 | `LogDocument.BuildChunkedAsync`（首块 4 MB 出首屏、稳态 32 MB、发布 100 ms 节流）；`Build` / `BuildAsync` 仍存于 `LogDocument.cs:367/374` 供小文件与代理路径 |
| 行口径 | **自然行**：除空行与横幅行外，每个物理行各成一条记录；续行合并机制（join）已删除 |
| 索引 | 8 段 SoA：`offs` / `lens` / `moff` / `min` / `pid` / `tid` / `lvl` / `tagId`（`flags` 列随 join 删除） |
| 行解析 | `LogParser.ParseLine`：快速路径只认 `MM-dd` 与 `yyyy-MM-dd`（`-` 分隔）+ 方括号 long 回退 |
| 时间列 | `LogDocument.HeaderBytes`：40 字节内匹配 `MM-dd` / `yyyy-MM-dd`（允许 `[ ` 前缀），否则返回空 → Time 列空白；分钟列 `Minute`（0–59，无时间 = `MINUTE_NONE` 255）由行首 `mm` 直接取 |
| 过滤 | `FilterEngine` 块扫描直接在 mmap 裸指针上跑，吃 pid/tid/lvl/tag/min 列 |
| 不变式 | 行号、列、正文、导出全部以「原文件字节 + 偏移」为唯一数据源 |

据此推演，外部导出文本**不改也能打开，但 6 列里 4 列失效**（无时间、无级别、无 Tag、整行即 Message）——这正是 §3.3 两处改动要补的。

### 4.4 与在途 change 的关系

- `chunked-progressive-load`、`remove-join-merge`、`split-android-tools-exe` 均已归档。
- `sidecar-cache-index` 在途（0/12）：首次解析后把索引落盘复用，接入点同为 `StartIndex` 本地文件分支；它明确排除代理 spool 等非静态文件。
- 因此 DLT 的 `StartIndex` 判定必须排在代理分派之前、也早于 sidecar 的命中判定之前——两者可叠加成顺序判断，冲突面一次合并即可（K1）。

### 4.5 真实样本逐字段核对

`01_cpu_mon.dlt` 首帧 vs dlt-viewer 导出首行：存储头 `seconds`=1585240949 → **2020-03-26 16:42:29 UTC**，导出显示 **2020/03/27 00:42:29.483000**（+8 本地时区）；htyp=0x31（UEH+WTMS+v1）/ mcnt=77 / len=128；TMSP=835993（83.5993 s）而导出「相对秒」列 83.5225（口径未证实，自研输出不用该列）。结论：dlt-viewer 会换算本地时区，与「不换算」的既定口径不同 → 两通道视为不同的 Log，不追求一致。

## 5 风险与许可

| 风险 | 处置 |
| --- | --- |
| 许可 | 仓库外试验工程的代码**不整体搬运**，按协议规范独立重写；确需引用片段时在 THIRD-PARTY/README 注明来源与「该工程无 LICENSE」的事实。协议字段与位定义是事实，不受版权保护 |
| 测试样本 | 自造合成 `.dlt` 生成器（已知答案的 4 条覆盖 STRG/SINT/UINT/FLOA/BOOL/RAWD、控制报文、non-verbose）；第三方真实样本（`01_cpu_mon.dlt`、`1.txt`）不入库，需要时从外部路径读取 |
| 大文件 | 解码必须流式（不照抄整份 `ReadAllBytes`），转换与索引是两遍 I/O |
| 体积 | ASCII 文本一般大于二进制源，磁盘占用需在实施期给估算 |
| 崩溃误报 | 快筛关键字 `SIG` 会命中 `SIGNAL`，只多解码几次，不产生误报 |
| 并行冲突 | 见 §4.4；解码模块保持零 UI 依赖、独立目录，后续 `core-extraction` 只需 `git mv` |

## 6 已关闭决策（压缩）

| 主题 | 结论 |
| --- | --- |
| 通道范围 | 两条通道同批：`.dlt` 解码 + 外部文本直读 |
| 路线 | A 文本物化（自研解码 → 规范文本 → 现有引擎）；D 仅作校准手段（`dlt-viewer -c` 对照，不进产品）；B 原生块索引归积压卡 `ILogFormat` 切片 |
| 规范行形态 | 无索引 + `-` 日期 + 方括号字段段；固定 6 位小数秒 |
| 外部文本 | 原样直读，`ParseLine` 分支同时接受「可选索引 + `/` 或 `-`」，去 `-----` 装饰符 |
| 字段口径 | Tag = `APID/CTID`；PID/TID 恒空；ECU/mcnt/session/索引本期不进 UI |
| 呈现 | DLT 固定 5 列、不做列配置；无级别行 Lv 留空；导出仅 `.txt` UTF-8；不做原始 `.dlt` 另存 |
| 时间 | 不换算时区，按文件原值显示；两通道时间差可接受 |
| 生命周期 | 打开 `.dlt` 即静默转临时文件（`displayPath` = 源路径）；关闭即删、不复用缓存；取消 → 清空文档并删临时文件；DLT 禁用自动重载 |
| 失败口径 | 首条失败不打开；中间损坏跳过 + 计数；空文件当空文档；保留魔数 resync，单条超长跳过 |
| 不拆 exe | 维持单 exe；若将来要隐藏设备功能，用启动模式（`--dlt` / 扩展名关联）而非拆包 |
| 性能 | 不设性能指标；转换进度按字节（位置/总长） |
| 验收 | 字段级等价：消息数 + Time/Minute/Lv/Tag/Message 五列逐条比对，不要求与 dlt-viewer 逐字节一致；双通道口径比对只对自研输出，外部样本仅验解析器 |
| 测试落点 | 并入既有 `tests/logcat.Tests`（不新增测试工程） |

**代码变化导致的自动关闭**：原 Q9「续行合并开关」——join 机制已整体删除，决策作废；原 Q3/I1「未知级别可见性需新增条款」——`message-filter` 主规范已覆盖。

## 7 下一步

1. 拍板 §2 的 K1–K8（未表态项按「倾向」写进 spec 并标「待确认」）。
2. 走 `/opsx:propose`，change 名 `dlt-log-format`，范围 = §3（自研流式解码 + 文本行分支 + 入口预判 + 表头差异化）。
3. 实施在独立 worktree `wt/dlt-log-format`；发版归下一 minor `0.2.0`，需另轮明确指令。
