# 探索结论：消除 YearTables 与「时间列转 byte[]」评估

- 状态：已转化为 OpenSpec change `minute-byte-column`（proposal / specs / design / tasks 四件套已定稿；2026-10-24 用户授权 apply，worktree 分支 `wt/minute-byte-column`）
- 日期：2026-10-21
- 关联提交：无（探索阶段，未改动代码）
- 验收结论：待定

## 结论摘要（2026-10-21 修订）

用户提出关键修正：过滤只有分钟过滤，**用一列 byte 把分钟存上即可**，过滤直接比较、去掉除法；未来加小时过滤就再加一列 byte；年份猜测计算全家可清。评估后：

1. **分钟 byte 列：推荐，列存储里性价比最高的一刀**。语义与现状 `(ts/60000)%60` 严格等价（证明见下），内存 8 B/row → 1 B/row（10M 行省约 70 MB），解析零新增成本（`mi` 字段解析器本来就算出了）。有一个语义例外：`s=60` 闰秒行会从分钟 0 桶移到分钟 59 桶——方向是修掉「看得见、滤不出」割裂，但属行为变化，须写进 design。
2. **整列 ts 转 byte（定宽编码 / 原始文本 / 字节序比较）：不做**。三种形状均不划算且都消不掉年份机制（详见「被否决的原始方案」）。
3. **“年份猜测计算全清”推荐变体 B**：分钟列替过滤 + 崩溃也放弃绝对 ts（改行序 + 原文显示时间）→ FinalizeTs/GuessYear/YearTables/TsToText/TsFromParts/NearestPos/BaseYear/HasYear 全删，约 250 行净删除。变体 A（绝对 ts 只服务崩溃行）保留全部机制，仅输出瘦身，不推荐。

## 现状：时间数据流与消费方地图

```
                     扫描期（ScanRange，每行）
  LogParser.ParseLine ──► within（年内毫秒，闰年日序编码）+ year（行内年份，常缺）+ mi（分钟，已算出未透传）
                              │
                     Assemble / Append：FinalizeTs（跨年检测 + yAbs + YearTables 查表）
                              ▼
                    _ts 列（long[]，8 B/row，占列存储 38 B/row 的 21%）
                              │
   ┌──────────────────────────┼──────────────────────────┬──────────────────────┐
   ▼                          ▼                          ▼                      ▼
FilterEngine 分钟过滤      frmMain 右键菜单         LogListView 显示兜底    CrashEvent.Ts（稀疏）
(ts/60000)%60，两处，      按此行分钟过滤           仅 -v long 方括号行    聚合 First/Last + 排序
唯一每行热路径消费                                          （HeaderBytes 空时）  + 两处 UI 显示
   NearestPos / TsFromParts / HasYear：无生产调用方（仅测试）
```

## 分钟 byte 列方案

**语义等价证明**：`ts = within + offs - adj`，`offs` 为 Jan1→Jan1 毫秒差、`adj` 为 0 或一个 `DAY_MS`，均为 60000 整数倍 ⇒ `(ts/60000)%60 ≡ mi`。日界不移相位，跨年 / 闰年 / adj 补偿均不影响。无时间行现 `ts<0` 被 continue；分钟后用哨兵 255（分钟集只含 0-59）同样排除。

**变更点**：
- `LogParser.ParseResult` 加 Minute 字段（`TsMmdd`/`TsYyyy`/`MsOf` 已算出 `mi`，透传即可；无时间行 -1）
- `LogDocument`：`_ts`（long[]，8 B）→ `_min`（byte[]，1 B）；`SetColumns`/`EnsureCap`/`ScanPart`/`Append` 同步
- `FilterEngine` 两处 + frmMain 右键菜单：byte 查表，去除法与 `ts<0` 分支
- 扩展性：小时过滤 = 再加一列 byte

**语义例外（须写 design）**：`s=60` 闰秒容忍行（如 `23:59:60.500`）。现状 `(ts/60000)%60` 将其算进分钟 0（within 溢出到下一分钟），即备忘 LEAP-SECOND 条目所述「HeaderBytes 显示 :60、分钟 59 滤不出」；分钟列存原始 `mi=59`，过滤与显示一致。属行为变化，方向为修复割裂。

## 变体对比（“年份猜测计算全清”）

| | 变体 A（保守） | 变体 B（推荐） |
|---|---|---|
| 绝对 ts | 保留，只服务崩溃行（~0.01%） | 删除；崩溃改行序 + 原文字节显示时间 |
| FinalizeTs/GuessYear/YearTables | 保留（输出瘦身） | 全删（约 250 行净删除，含 ScanRange 的 within/years 临时收集、`_yearLast`/`_withinLast`、`HasYear`/`BaseYear`、`TsToText`/`TsFromParts`/`NearestPos`） |
| 崩溃语义 | 零变化（乱序 `LastTs=Math.Max` 保持） | 两处变化（见下） |
| 静态缓存竞态 | 原样保留 | 顺带消除 |

**变体 B 的两处语义代价**：
1. 乱序文件（时钟回跳）中崩溃组末次从「组内最大时间」变「行位最后」；`-1`（时间未知）排最后语义消失。`CrashTests` 乱序用例（100→300→200→-1 期望 300）须改期望或删除。
2. `-v long` 方括号行时间列：现靠 ts 兜底，B 后须给 `HeaderBytes` 加 `"[ "` 前缀感知（约 5 行），否则这类行时间列空白。

**测试 / 工具面**：FilterEngineTests 分钟过滤用例不动（等价性安全网）；LogDocumentTests 跨年套件（~6 条）、TsToText（4 条）、TsFromParts（~10 条含 Theory）、NearestPos（2 条）、增量对比 2 处 `Ts[i]` 断言重写或删；`UT-AUDIT` N3 条目与 A/B/C/D 跨年场景分析段、`mutation-test.py` 4 条锚点（NearestPos/GuessYear/TsFromParts-adj/YearTables-adj）同步清理。附带收益：GuessYear 相关“看日历吃饭”的测试消失。

## 被否决的原始方案（整列 ts 转 byte）

| 形状 | 内存/row | 随机访问 | 过滤热路径 | 年份机制 |
|---|---|---|---|---|
| 现状 `long[]` | 8 B | 1 次 load | 1 次除法 | 需要 |
| 定宽 byte[]（5-6 B） | 5-6 B，仅省 2-3 B | 字节拼装慢 2-4×，`ReadOnlySpan<long> Ts` API 变形 | 更慢 | 仍在 |
| 原始时间文本 byte[] | 18-28 B，涨 2-3.5× | 变长需二级索引 | 每行解析 ~15-30 ns | 仍在 |
| 原文按字节序比较 | 同上 | — | 比 18-28 B vs 8 B | MM-DD 无年份，跨年检测照样要 |

## 流程判定与落地顺序

B 触发 AGENTS.md §一.3 清单（公开 API 变动 + 数据模型变更 + ≥3 文件 ≥100 行）→ 需 OpenSpec change（proposal + design + tasks；specs 未覆盖时间戳模型，无 spec delta）。design 须写明：分钟列等价性证明、s=60 行为变化、变体 B 两处崩溃语义代价。

建议落地序（每步可独立验证）：
1. `ParseResult` 加 Minute（解析侧零成本）
2. `_min` 列替 `_ts`（列存储五处同步）
3. `FilterEngine` 两处 + frmMain 菜单改字节判定
4. `HeaderBytes` 加 `"[ "` 前缀感知（保 `-v long` 显示）
5. 崩溃改 Row 序 + 原文显示时间（CrashAggregator/CrashSummaryForm/LogListView）
6. 删 FinalizeTs/GuessYear/YearTables 及连带字段
7. 测试 / mutation-test.py / UT-AUDIT 清理
