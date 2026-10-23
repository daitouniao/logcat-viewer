# UTF-8 中文关键词过滤：字节级 SIMD 预筛探索

- 状态：探索中（未建 change）
- 日期：2026-10-23
- 关联提交：无（纯探索，未改动代码）
- 验收结论：不适用

## 1. 结论先行

方向成立：在 mmap 原始字节上做关键词预筛、只对命中行解码，比现状「逐行 ReadArray + 每行分配」更有性能上限。

但给出的草案有 4 处与 .NET 实际 API / 本仓库过滤语义不符，直接照抄会得到错误结果（不是慢，是**误报/漏报**）。落地前必须按 §4 校正。

## 2. 现状（代码事实）

- 文档模型：`MemoryMappedFile` + `CreateViewAccessor(0, 0)`，文本本体留在磁盘，内存只有定长列（`offs` / `lens` / `moff` / `lvl` / ...）。
- Message 过滤（`FilterEngine.MsgFilter`）逐候选行执行：
  1. `doc.MessageBytes(row)` → **每行 new byte[]** + 一次 `_view.ReadArray`（小随机读）；
  2. 区分大小写时直接匹配；忽略大小写时先复制到 scratch 就地折叠 ASCII；
  3. 含非 ASCII 时回退 `GetString → ToLowerInvariant → GetBytes`（每行 3 次分配）；
  4. `MatchAny` 对每个关键词跑 `hay.IndexOf(needle)`（`Span<byte>.IndexOf` 在 .NET 8+ 已是向量化实现）。
- 即：**现状不是全量解码**，只有含非 ASCII 的行才走编码往返。瓶颈候选是「逐行小随机读 + 每行数组分配」，不是字符级匹配本身。

```text
现状（每候选行）
  offs/lens/moff --+
                   v
        [view.ReadArray 小读] --> new byte[] --> 折叠/回退编码 --> IndexOf(needle)

目标（块扫描）
  mmap 指针 --+
              v
   [Parallel 块 + 字节 IndexOf] --命中--> 行归属/复核 --> 仅对命中行 Decode
```

## 3. 草案中成立、可直接采纳的部分

1. UTF-8 自同步：ASCII 字节不会出现在多字节序列的续字节（续字节恒为 0x80–0xBF），所以英文关键词（ERROR/WARN）绝不会在汉字内部误匹配。
2. 中文关键词转 UTF-8 字节后直接搜索，在**合法 UTF-8** 中不会误报也不会漏报（首字节决定长度 + 续字节区间 + 禁止超长编码）。
3. `0x0A` 不可能出现在多字节序列内部 → 按字节找换行零解码；CRLF 亦然（`0x0D` 同理）。
4. 分块并行需要重叠 `最长关键词字节数 - 1`，否则跨块关键词漏匹配。
5. 只对命中行调用一次 `Encoding.UTF8.GetString` 用于展示。
6. `ptr + accessor.PointerOffset` 才是数据起点、`Capacity` 才是有效长度 —— 与仓库既有 `LineIndexProbe` 的 `AcquirePointer` 用法一致。
7. 全文件块扫描顺带修复逐行小随机读，与待办 C1（网络路径过滤慢、嫌疑跨页率）方向一致。

## 4. 必须校正的 4 点

### 4.1 多模式字节 `SearchValues` 重载不存在

草案里 `SearchValues.Create(patterns.Span)`（`ReadOnlyMemory<byte>[]`）没有对应重载；`SearchValues.Create(ReadOnlySpan<byte>)` 的语义是「**任意一个字节值**」，把多个关键词的字节拼平成数组会退化成单字节 any-of → 大面积误报。多子串 SIMD 匹配只有 .NET 9+ 的 `SearchValues<string>`（`char` 域）提供，**不适用于字节域**。

置信度：高（依据 .NET 8/9/10 API 面），但按探索规范仍需探针确认，见 §7。

替代方案（按推荐排序）：
- 多关键词：并发/循环跑 N 次 `span.IndexOf(needle)`，每次本身即 SIMD；关键词通常个位数，代价可控。
- 单关键词：`IndexOf` 就是最优路径，不需要 `SearchValues`。
- 关键词多到几十个时：先 `IndexOfAny(首字节集合)` 定位候选，再逐点校验完整模式（候选少时更快）。

### 4.2 忽略大小写与原始字节扫描冲突

现状 `MsgCase=false` 是**折叠 haystack 后用小写 needle**。扫描原始文件时无法折叠 haystack，两条路：
- 展开 needle 的 ASCII 大小写变体（k 个字母 → 2^k 个模式），模式数爆炸；
- **推荐**：预筛只用于「排除」，命中行再按现有折叠逻辑做一次确认匹配 —— 语义零变化，预筛漏不掉（预筛用 needle 小写形式，任何大小写命中都必然经此路径）。

### 4.3 Message 语义与整行扫描不一致

现过滤只匹配 message 段（`moff` 之后），整文件字节扫描会把 tag / pid / 时间戳也算进去 → 行为变化（false positive）。
处理：命中 offset 后按行核验是否落在 `[offs[row] + moff[row], offs[row] + lens[row])`；`offset → row` 用 `offs` 二分。命中点通常远少于总行数，代价可忽略。

### 4.4 重叠区重复命中与行归属

朴素「每块覆盖 `[offset, offset+len+overlap)` 并各自报命中」会让落在重叠区的命中被相邻两块各报一次；行归属也必须唯一。
采用仓库已有范式（`LineIndexProbe.ScanChunk`）：**行首落在本块 `[rawStart, rawEnd)` 的行才归本块**，重叠只用于跨块把整行读完；结果按行去重。

## 5. 其他风险

- mmap 冷缓存下顺序扫描受磁盘带宽限制；200MB 的「几十~几百毫秒」是热缓存量级，冷缓存不可比。需要大小文件阈值（小文件走现状，避免固定开销）。
- 非法 UTF-8 字节可能造出假命中；作为显示型工具可接受，但复核路径会自然消解（复核基于 `Decode` 后的文本或现有字节匹配）。
- 中文关键词的大小写折叠是 no-op，可跳过折叠分支（纯优化）。

## 6. 待澄清

### Q1 预筛范围

- 为什么需要澄清：决定是否依赖解析结果，直接影响改动面和正确性。
- 方案：A 整文件字节扫描 + 命中后按行复核 message 区间；B 只扫 message 段（需先有行/列索引，等价于现状数据）。
- 偏向：**A**。可复用现成列索引做复核，且不依赖新的解析产物。

### Q2 忽略大小写策略

- 为什么需要澄清：决定预筛能否直接用于「判定」还是仅「排除」。
- 方案：A 展开 2^k 变体（可单次判定，模式数爆炸）；B 命中后复核（预筛仅排除，语义不变）。
- 偏向：**B**。

### Q3 改动边界

- 为什么需要澄清：避免夹带 Tag 过滤等任务外改动。
- 方案：A 仅重写 `MsgFilter` 内部；B 抽独立「原始扫描器」服务。
- 偏向：**A**，`ApplyFilter` 签名与进度语义不动；若后续要复用到 Tag/导出再抽。

### Q4 非目标

- 正则、跨行上下文匹配、按 Level 的字节级预筛（Level 已是列，不需要）。

## 7. 下一步

1. 探针（约 5 分钟）：在 `.tmp-test` 临时工程里验证 `SearchValues.Create` 的字节域重载面，确认 §4.1（不入库）。
2. 基准：造 200MB 含中文日志，A/B 对比「现状 MsgFilter」vs「块扫描预筛 + 命中复核」；同时记录冷/热缓存与网络路径（联动 C1）。
3. 结论稳定后转 `/opsx-propose`，范围限定 Message 过滤，specs delta 只写行为契约（结果集不变）。

## 8. 判定标准（验收前提）

- 对同一输入与同一 FilterSpec，新旧路径输出**行号集合完全一致**（含空 needle、and/or、忽略大小写、中文、混合中英）。
- 200MB 热缓存纯 Message 关键词过滤较现状有可测量的下降；冷缓存不劣化。
