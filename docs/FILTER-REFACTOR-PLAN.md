# 工具栏过滤 与 过滤面板 解耦改造计划

> 状态：**✅ 已执行完毕（2026-10-04）** —— 三件事全部落地并验证
>日期：2026-10-04
> 关联需求：检查工具栏过滤选项与面板过滤选项是否一致；面板更复杂，工具栏是简化版；能否「各自处理自己控件」，底层复用同一过滤函数。
> 本次修订结论：
> 1. **保留双向镜像**（撤销原 v1 的「删除镜像」方案）；
> 2. **新增输入框颜色提示**：用背景色表示 `or/and`、用文字色表示大小写敏感；
> 3. **移除正则（已确认）**（面板 `正则` 复选框及其整条引擎路径、测试、文档一并删除）；
> 4. **op 归属收敛**：工具栏**继承**面板 op、自身**不具备改写能力**；Message 面板 op 默认由 `and` 改为 `or`；与 `TOOLBAR-FAVORITES-DESIGN.md` §2.4 的 op 处理统一为「删除副作用」。
>
> **v3 修订说明**：v2 评审通过后逐条对源码核对，发现 v2 有 5 处写得过宽、直接照做会掉坑，
> 已在 §0.1 列出并把修正写进对应小节。改动本身（三件事）不变。

### 执行结果

| 项                                     | 结果                                                                                     |
| -------------------------------------- | ---------------------------------------------------------------------------------------- |
| 编译                                  | 0 警告 0 错误                                                                            |
| 单元测试                              | **351 通过 / 0 失败**（354 → 整删 5 → 349 → 补 2 → 351）                                   |
| 变异测试（`FilterEngine`）              | **KILLED 16 / SURVIVED 3**，3 个存活项均为本次改动前既存的等价变异（UT-AUDIT §4.1已甄别） |
| UI 着色验证（临时 harness）              | 17 项全通过（背景色/文字色/空框不着色/双向镜像/工具栏不改写 op/正则控件已移除）              |
| 提交| `d1a4ffd` 实现三件事 → `6dc2e79` 行号修正 → `dc4d908` 补增量路径用例 |

> **执行中发现的新问题**（已修）：变异测试修正行号后，`FilterTail` 的分钟守卫变异从
> SKIP 变成 SURVIVED，暴露出「`ApplyFilter` 与 `FilterTail` 是两份重复实现、
> `ts<0` 守卫只被测了一边」的测试空洞。已补2 个用例，现两侧都 KILLED。

---

## 0. 本次修订要点（相对 v1）

| 项             | v1 方案                                   | v2 修订后                                        |
| -------------- | ----------------------------------------- | ------------------------------------------------ |
| 双向镜像       | 删除，两侧各自收集 `FilterSpec`           | **保留**：工具栏文本框与面板文本框继续互相镜像   |
| 快速组字段     | 新增 `QuickTags/QuickMsg/QuickMinutes`    | **取消**：镜像保留后无需第二套字段               |
| 合并语义       | 两组条件 AND 合并                          | **取消**：只有一份条件，无合并问题               |
| op（or/and）   | 各控件独立                                 | 以面板 op 为准，工具栏框用**背景色**提示         |
| 大小写         | 各控件独立                                 | 以面板开关为准，工具栏框用**文字色**提示         |
| 正则           | 保留                                       | **整条移除**                                     |
| 静默改写 op    | 随镜像删除一并根除| 仅删除副作用那一行（镜像保留）                   |

> 现在的方案明显更小：核心只有「1 行副作用删除 + 颜色提示 + 正则移除」三件事。

---

## 0.1 v3 评审补充：5 处必须遵守的执行约束

v2 的方案本身站得住（已逐条对源码核对，见 §2），但v2 有 5 处**写得过宽**，照做会掉坑。
本节是**执行时的硬约束**，与后文有冲突时以本节为准。

### 坑 1：测试不能整删——3 个是混合用例，只删正则那几行

v2 §3.4 写「所有含 `Regex = true` 的用例 → 删除」。实际 `Regex = true` 共 **27 处**，
分布在 **8 个**用例里，其中 **3 个是混合用例**（同一用例内既有正则断言也有子串断言）：

| 用例名                                | 位置                | 正确处理                                                         |
| ------------------------------------- | ------------------- | ---------------------------------------------------------------- |
| `message忽略大小写_德语eszett大小写等价` | :894-895         | **只删这 2 行**，主体（891-893、896-906）全部保留                   |
| `tag忽略大小写与message行为一致`        | :952-953         | **只删这 2 行**，主体保留                                         |
| `增量路径的忽略大小写结论与全量一致`    | :1005            | `foreach (var msgRegex in new[] { false, true })` → **改为单值**   |
| 其余 5 个（`tag开正则模式` 等）        | 224/ 246 / 305 / 673 / 962 | 可整删                                                |

> **为什么必须区分**：整删会丢掉 ß/ẞ、土耳其无点 i、中文、增量路径这些**非 ASCII 折叠回归保护**，
> 而这正是 `MsgFilter` 最脆的部分（`FoldLowerAsciiInPlace` 的非 ASCII 回退路径）。

### 坑 2：README 不能按「正则相关说明」整条删

`README.md:24` 与 `README.zh-CN.md:51` 里的 "regex fallback" 指的是
**`LogParser` 的日志格式回退正则**（`ReLong`），与过滤正则**完全无关**，删了会写出错误文档。

| 文件                            | 该删                                             | **必须保留**                                                     |
| ------------------------------- | ------------------------------------------------ | ---------------------------------------------------------------- |
| `README.md`                     | 34、152、164                                     | **24**（六种格式自动识别 / regex fallback）                      |
| `README.zh-CN.md`               | 59、205、217                                     | **51**（同上）                                                   |

### 坑 3：`using System.Text.RegularExpressions;` 不能删

`ParseMinutes`（[FilterEngine.cs:67](file:///d:/01.0.Code/C%23/logcat/Services/FilterEngine.cs#L67)）
与 `ParseInts`（[:85](file:///d:/01.0.Code/C%23/logcat/Services/FilterEngine.cs#L85)）
仍在用 `Regex.Split` 做词法切分。**只删** `_reCache` / `CompileRegex` / `pats` 分支，using 保留。

### 坑 4：Message 默认 op 改 `or` 有 4 处连带

v2 §3.2 只提了 frmMain.cs:358，实际还要一起改，否则代码里留两套默认值：

| 位置                          | 现状     | 改为             |
| ----------------------------- | -------- | ---------------- |
| frmMain.cs:358（实参）         | `"and"`  | `"or"`           |
| frmMain.cs:734（`CollectSpec` Tag侧兜底）| — | **不动**（Tag 默认本就是 or）|
| frmMain.cs:735（`CollectSpec` Msg侧兜底）| `?? "and"` | `?? "or"`  |
| frmMain.cs:989/ 992（`FindStep`）          | `?? "and"` | `?? "or"`  |
| `README.zh-CN.md:216`         | 「Message 默认 `and`」 | 「Message 默认 `or`」 |

### 坑 5：删正则会让两处外部引用失效

| 位置                        | 失效形式                                                | 处理                   |
| --------------------------- | ------------------------------------------------------- | ---------------------- |
| `docs/UT-AUDIT.md:185`      | 引用的 FilterEngine 行号 301/308/315、372/377/382 全部漂移 | 删正则后重跑一次变异测试，更新行号 |
| `scripts/mutation-test.py:208-211` | `CompileRegex：正则不再 IgnoreCase` 变异目标不存在     | **删掉这条变异**，否则输出 `SKIP-匹配数=0` 噪音干扰判读 |

---

## 1. 需求回应（逐条）

| 需求                                                   | 现状 / 修订后判断                                   |
| ------------------------------------------------------ | --------------------------------------------------- |
| 工具栏与面板的过滤选项是否一致                         | 部分不一致，见 §2.2（镜像导致语义实际以面板为准）   |
| 能否各自处理自己控件                                   | **不再追求**：保留镜像后，两侧共享同一份文本与开关  |
| 底层过滤是否复用同一函数                               | **是**，已满足，见 §2.1                             |
| 面板更复杂 / 工具栏是简化版                            | 只成立一半，见 §2.2                                 |
| （新）op/大小写是否直观可见                            | **当前不可见**，v2 用颜色解决，见 §3.3              |
| （新）是否需要正则                                     | **不需要**，v2 移除，见 §3.4                        |

---

## 2. 现状检查结论（已逐条核对源码）

### 2.1 底层引擎：已统一 ✅

两侧输入最终都汇成同一份 `FilterSpec`，再交给同一个过滤引擎，不存在第二套过滤逻辑：

| 环节             | 位置                                                                                          | 说明                                                     |
| ---------------- | --------------------------------------------------------------------------------------------- | -------------------------------------------------------- |
| 汇总模型         | [FilterSpec.cs](file:///d:/01.0.Code/C%23/logcat/Models/FilterSpec.cs)                         | 所有条件 AND，单条件内多值 OR                            |
| 解析输入         | `CollectSpec()`，[frmMain.cs:725-746](file:///d:/01.0.Code/C%23/logcat/frmMain.cs#L725-L746)   | 唯一入口，读面板控件（含被镜像写入的工具栏文本）         |
| 全量过滤         | `FilterEngine.ApplyFilter`，[FilterEngine.cs:271-348](file:///d:/01.0.Code/C%23/logcat/Services/FilterEngine.cs#L271-L348) | —                                       |
| 增量过滤（采集） | `FilterEngine.FilterTail`，[FilterEngine.cs:351-399](file:///d:/01.0.Code/C%23/logcat/Services/FilterEngine.cs#L351-L399)  | 复用同一套判定路径                                       |
| 词法解析         | `SplitTerms` / `ParseInts` / `ParseMinutes`，[FilterEngine.cs:31-94](file:///d:/01.0.Code/C%23/logcat/Services/FilterEngine.cs#L31-L94) | 工具栏与面板共用                                         |

### 2.2 选项能力对照（不一致清单）

| 能力           | 工具栏                                            | 面板                                        |
| -------------- | ------------------------------------------------- | ------------------------------------------- |
| 级别           | ✘                                                 | ✔ 8 个复选框 + 全选/清空                    |
| Tag            | ✔ 文本框（文本被镜像进面板，实际语义=面板设置）    | ✔ 文本框 + and·or + 大小写 + 排除 + 收藏（正则 v2 移除） |
| Message        | ✔ 文本框（同上）                                   | ✔ 同上（默认 or，v2 修订）                          |
| PID / TID      | ✘                                                 | ✔ 多值 + 排除                               |
| 分钟           | ✔ 文本框（与面板**同一语义**，双向镜像）           | ✔ 文本框                                    |
| 仅标记行       | ✔ 复选框                                          | ✘                                           |
| 大小写（正则已移除） | ✘                                            | ✔ 仅保留大小写（正则 v2 整条移除，见 §3.4）  |
| 排除           | ✘                                                 | ✔                                           |
| 收藏           | ✘                                                 | ✔ ▾ / ★                                     |

> 保留镜像后，**工具栏文本框 = 面板文本框的快捷入口**：文本双向同步，op/大小写/排除等语义一律以面板控件为准。工具栏框不再有自己的独立语义，因此「不一致」在语义层消失，只需在视觉上把共享语义暴露出来（§3.3）。

### 2.3 控件层的 3 个问题（v2 只处理前两个 + 正则）

#### 问题 1（确定性 bug）：工具栏输入会静默把面板 op 改回 `or`

```
面板 _edTag.TextChanged
  → SyncPanelToToolbar(_edTag, _tbTag)      // frmMain.cs:401
  → _tbTag.TextChanged
  → _cbTagOp.SelectedItem = "or"            // frmMain.cs:200  ← 副作用，_syncingFilter 包不住
```

[SyncPanelToToolbar](file:///d:/01.0.Code/C%23/logcat/frmMain.cs#L401-L407) 只把 `_syncingFilter` 包住「写 Text」，**不包住工具栏 `TextChanged` 里紧随其后的 `_cbTagOp.SelectedItem = "or"`**（[frmMain.cs:200](file:///d:/01.0.Code/C%23/logcat/frmMain.cs#L200)、[204](file:///d:/01.0.Code/C%23/logcat/frmMain.cs#L204)）。后果：

- 用户在面板把 Tag 的 op 选成 `and`，之后**每次改 Tag 文本，op 都被强制改回 `or`**；
- Message 同理从 `and` 被改回 `or`。

> 这就是 v2 保留镜像后仍需修的那一处：**删除这两个 `= "or"` 副作用即可**，不必删除镜像。

#### 问题 2（设计缺陷，v2 用颜色缓解）：工具栏输入实际套用面板的高级设置

`CollectSpec` 只读面板控件，工具栏文本靠镜像写进面板文本框，因此工具栏「快速过滤」套用的是**面板当前的正则 / 大小写 / 排除 / op**。工具栏 UI 上无任何指示。

- v2 通过 §3.3 的颜色提示，把 **op** 与 **大小写** 这两个「会改变匹配结果、但工具栏原先完全看不见」的开关显式暴露；
- **排除** 是否也纳入颜色提示，见 §5.2；
- 正则整条移除后，问题 2 中「被当正则执行」的隐患自然消失。

#### 问题 3（次要）：双向镜像导致重复触发

`SyncToolbarToPanel` 末尾显式调用 `OnFilterChanged()`，写入目标 TextBox 的 `TextChanged` 又会再调一次；`ResetFilter`（[frmMain.cs:748-758](file:///d:/01.0.Code/C%23/logcat/frmMain.cs#L748-L758)）逐控件赋值时也会反复触发自动应用。功能上被 `_autoTimer.Stop()` / `WorkerGen` 挡住不影响正确性，v2 视为可选优化（见 §3.5）。

---

## 3. 改造方案

### 3.1 目标架构（镜像保留版）

```
工具栏（快捷入口）
  Tag 文本框 ──┐  文本双向镜像
  Message 文本框 ──┤   （op / 大小写 / 排除 以面板控件为准，用颜色提示）
  分钟 文本框 ──┘
         ⇅ SyncToolbarToPanel / SyncPanelToToolbar（仅同步 Text）
面板（完整组）
  级别 / Tag(op·大小写·排除·收藏) / Message(同) / PID·TID(+排除) / 分钟 / 仅标记行
        │
        └── CollectSpec() ──→ 唯一一份 FilterSpec ──→ FilterEngine（全量 / 增量）
```

要点：

1. **保留** `SyncToolbarToPanel` / `SyncPanelToToolbar` / `_syncingFilter`；
2. **只删副作用**：工具栏 `TextChanged` 不再改写 `_cbTagOp` / `_cbMsgOp`（问题 1 根除）；
3. op / 大小写 / 排除保持「面板控件为唯一来源」，工具栏框用颜色把它们显示出来（§3.3）；
4. **移除正则**（§3.4）；
5. 不新增 `FilterSpec` 字段、不改 `FilterEngine` 的算法结构。

### 3.2 修复静默改写 op（1 行级改动）

| 位置                                                     | 现状                                                         | 改为                                              |
| -------------------------------------------------------- | ------------------------------------------------------------ | ------------------------------------------------- |
| [_tbTag.TextChanged](file:///d:/01.0.Code/C%23/logcat/frmMain.cs#L200) | `SyncToolbarToPanel(_tbTag,_edTag); _cbTagOp.SelectedItem="or";` | `SyncToolbarToPanel(_tbTag,_edTag);`（删副作用）  |
| [_tbMsg.TextChanged](file:///d:/01.0.Code/C%23/logcat/frmMain.cs#L204) | `SyncToolbarToPanel(_tbMsg,_edMsg); _cbMsgOp.SelectedItem="or";` | `SyncToolbarToPanel(_tbMsg,_edMsg);`（删副作用）  |

删除后：工具栏输入不再篡改面板 op；两框文本仍互相镜像，行为可预期。

**配套（v2 修订）**：删除副作用后，工具栏 op 完全**继承**面板设定、自身**不具备修改能力**。为保持工具栏快速过滤的默认语义仍为 `or`，把 Message 面板 op 默认值由 `and` 改为 `or`（[frmMain.cs:358](file:///d:/01.0.Code/C%23/logcat/frmMain.cs#L358)）。此改动与 `TOOLBAR-FAVORITES-DESIGN.md` §2.4 统一为「删除副作用」写法，两文档不再冲突。

> **v3 补充**：除 358 外，[frmMain.cs:735](file:///d:/01.0.Code/C%23/logcat/frmMain.cs#L735)（`CollectSpec` 的 Msg 侧兜底）与
> [989/992](file:///d:/01.0.Code/C%23/logcat/frmMain.cs#L989)（`FindStep`）的 `?? "and"` 也要一起改成 `?? "or"`，
> 以及 `README.zh-CN.md:216` 的「Message 默认 `and`」——见 §0.1 坑 4。

### 3.3 输入框颜色提示（新增）

给「Tag」「Message」两组输入框（工具栏 `_tbTag/_tbMsg` + 面板 `_edTag/_edMsg`）加载统一的着色函数，用两个正交维度表达不可见语义：

| 维度                       | 载体         | 取值                                        |
| -------------------------- | ------------ | ------------------------------------------- |
| 匹配关系 op                | **背景色**   | `or` → 浅蓝 `#E8F1FB`；`and` → 浅橙 `#FDF0E3` |
| 是否区分大小写             | **文字色**   | 忽略大小写 → 常规 `#1F1F1F`；区分大小写 → 暗红 `#B00020` |

设计约束：

- **空框不着色**：文本框为空时恢复默认背景/文字色，避免大面积色块造成视觉噪声；
- **同一函数统一样式**：`ApplyTermBoxStyle(TextBoxBase box, string op, bool caseSensitive)`，面板与工具栏共用，保证两处颜色完全一致；
- 工具栏 `ToolStripTextBox` 的着色通过其内层 `TextBox`（`_tbTag.TextBox.BackColor/ForeColor`）设置，面板 `TextBox` 直接设属性；
- **触发时机**：`_cbTagOp/_cbMsgOp.SelectedIndexChanged`、`_ckTagCase/_ckMsgCase.CheckedChanged`、以及镜像写入文本后，各调用一次着色函数；
- **Tooltip 同步**：工具栏 tooltip 补一句当前语义（如「包含匹配；or；忽略大小写」），颜色 + 文案双重指示；
- 该着色**纯 UI**，不进入 `CollectSpec`、不进入 `FilterSpec`，不影响过滤正确性。

> 分钟框无 op / 大小写语义，v2 不着色（见 §5.3）。

> **深色主题限制**：项目在 `Program.cs` 固定「微软雅黑 UI」，但**没有做深色主题适配**
> （无 `Application.SetDefaultTheme`、无 `UserPreference` 分支）。这组浅色在深色背景下会显脏。
> 本轮按浅色实现，README 里注明；将来若加深色主题，这里需要改成跟随 `SystemColors`。

### 3.4 移除正则

一次性删除整条正则路径（UI → 模型 → 引擎 → 测试 → 文档）：

| 层     | 位置 / 符号                                                                                                                    | 处理 |
| ------ | ----------------------------------------------------------------------------------------------------------------------------- | ---- |
| UI     | `_ckTagRe` / `_ckMsgRe` 字段（[frmMain.cs:58-59](file:///d:/01.0.Code/C%23/logcat/frmMain.cs#L58-L59)）；`BuildTermControls` 的 `ckRe` 参数与 `正则` 复选框（[frmMain.cs:439-441](file:///d:/01.0.Code/C%23/logcat/frmMain.cs#L439-L441)）；两处调用实参（[351](file:///d:/01.0.Code/C%23/logcat/frmMain.cs#L351)、[357](file:///d:/01.0.Code/C%23/logcat/frmMain.cs#L357)）；`CollectSpec` 赋值（[731](file:///d:/01.0.Code/C%23/logcat/frmMain.cs#L731)、[736](file:///d:/01.0.Code/C%23/logcat/frmMain.cs#L736)）；`ResetFilter` 复位（[753-754](file:///d:/01.0.Code/C%23/logcat/frmMain.cs#L753-L754)）；F3 正则拦截（[987](file:///d:/01.0.Code/C%23/logcat/frmMain.cs#L987)） | 删除 |
| 模型   | `FilterSpec.TagRegex` / `MsgRegex`（[FilterSpec.cs:11](file:///d:/01.0.Code/C%23/logcat/Models/FilterSpec.cs#L11)、[17](file:///d:/01.0.Code/C%23/logcat/Models/FilterSpec.cs#L17)） | 删除 |
| 引擎   | `_reCache` + `CompileRegex`（[FilterEngine.cs:16-27](file:///d:/01.0.Code/C%23/logcat/Services/FilterEngine.cs#L16-L27)）；`TagIds` 的 `pats` 分支（[103-139](file:///d:/01.0.Code/C%23/logcat/Services/FilterEngine.cs#L103-L139)）；`MsgFilter` 的 `pats` 分支（[171-230](file:///d:/01.0.Code/C%23/logcat/Services/FilterEngine.cs#L171-L230)） | 删除后回归纯子串匹配 |
| 测试   | [FilterEngineTests.cs](file:///d:/01.0.Code/C%23/logcat/tests/logcat.Tests/FilterEngineTests.cs)：**5 个纯正则用例整删**（224 `tag开正则模式`、246 `tag正则模式下多值and与or语义`、305 `message开正则模式`、673 `正则模式的大小写开关与多值语义`、962 `正则忽略大小写_与预先折叠等价`）；**3 个混合用例只删正则断言**（:894-895、:952-953、:1005 的 `foreach` 改单值） | **整删 5 个 + 局部删 3 个**，见 §0.1 坑 1 |
| 文档   | README.md 的 34/152/164、README.zh-CN.md 的 59/205/217 | **只删这几行**；24/51 的 "regex fallback" 是 LogParser 的，必须保留（§0.1 坑 2） |
| 脚本   | `scripts/mutation-test.py:208-211` 的 `CompileRegex` 变异 | 删除该条，否则 `匹配数=0` 噪音 |

移除后的简化：`MsgFilter` 的 `needFold` 由 `!spec.MsgCase && pats == null` 简化为 `!spec.MsgCase`；`TagIds` 只保留字节级 contains 路径；`FoldLowerAsciiInPlace` 与非 ASCII 回退逻辑**保留不变**（仍服务于忽略大小写）。

> **`using System.Text.RegularExpressions;` 保留**（`ParseMinutes` / `ParseInts` 仍用 `Regex.Split`），见 §0.1 坑 3。

### 3.5 界面层：`frmMain.cs`（汇总）

| 改动                                                                              | 说明                                                                          |
| --------------------------------------------------------------------------------- | ----------------------------------------------------------------------------- |
| 工具栏 `TextChanged` 去掉 `_cbTagOp.SelectedItem="or"` / `_cbMsgOp...` 副作用      | [199-205](file:///d:/01.0.Code/C%23/logcat/frmMain.cs#L199-L205)，§3.2        |
| 新增 `ApplyTermBoxStyle(...)` 并接入 op/大小写/文本变更                            | §3.3                                                                          |
| 移除正则：字段、`BuildTermControls` 参数与复选框、`CollectSpec`、`ResetFilter`、F3 | §3.4                                                                          |
| 工具栏 tooltip 补当前 op/大小写文案                                                | 消除问题 2 的「无指示」                                                       |
| （可选）`ResetFilter` 清理重复触发，只应用一次                                     | [748-758](file:///d:/01.0.Code/C%23/logcat/frmMain.cs#L748-L758)              |
| 镜像机制本身**保留**                                                               | [391-407](file:///d:/01.0.Code/C%23/logcat/frmMain.cs#L391-L407)              |

### 3.6 测试：`tests/logcat.Tests/FilterEngineTests.cs`

1. **整删 5 个纯正则用例**（224/ 246 / 305 / 673 / 962）；
2. **3 个混合用例只删正则断言**（:894-895、:952-953、:1005 改单值），**主体断言保留** —— 否则丢掉非 ASCII 折叠的回归保护（详见 §0.1 坑 1）；
3. 保留并确认：Tag/Message 子串匹配、多词 OR/AND、忽略大小写与 `TagCase/MsgCase` 区分的既有用例仍通过；
4. （若 §3.5 的可选优化落地）补 `CollectSpec`/镜像相关的轻量测试或手测清单。

> **`dotnet test` 在本机跑不起来**（testhost 加载 hostfxr.dll 报 0x80070005，见 MEMORY.md）。
> 验证走`%TEMP%/tstrun` 反射 runner；覆盖率仍需在你自己终端按 [`TESTING.md`](TESTING.md) 出。
> 改完引擎后建议再跑一轮 `python scripts/mutation-test.py FilterEngine`（注意 `docs/UT-AUDIT.md:185` 的行号会漂移）。

### 3.7 文档

README.md 与 README.zh-CN.md：**更新过滤交互说明** —— 工具栏与面板文本框互相镜像（输入即可同步）；op 用输入框**背景色**、大小写用**文字色**提示；移除正则说明（**只删 §0.1 坑 2 表格里列的行**）；`README.zh-CN.md:216` 的 Message 默认 op 改为 `or`。

### 3.8 执行顺序（按风险递增，每步可独立验证）

| 步 | 动作                | 风险 | 独立验证方式                                     |
| -- | ------------------- | ---- | ------------------------------------------------ |
| 1  | 删 op副作用        | 低   | 面板设 `and` → 在工具栏改文本，op 不再被改回     |
| 2  | 移除正则            | 中   | 编译通过 + 反射 runner 全量用例绿 + 跑变异测试   |
| 3  | 输入框颜色提示      | 低   | 手测：切or/and、切大小写、清空文本，四处框同步变色 |

> 把「删正则」放中间、UI 着色放最后：着色是纯新增代码，放最后能让前两步的回归验证不被干扰；
> 而正则移除涉及面最广，单独成步便于定位问题。

---

## 4. 改动影响面一览

| 文件                                        | 改动性质                                                     |
| ------------------------------------------- | ------------------------------------------------------------ |
| `frmMain.cs`                                | 删 2 处 op 副作用；Message 默认 op 及3 处 `?? "and"` 改 `or`；新增颜色提示；移除正则 UI/收集/复位/F3 |
| `Models/FilterSpec.cs`                      | 删除 `TagRegex` / `MsgRegex`                                 |
| `Services/FilterEngine.cs`                  | 删除 `CompileRegex` / `_reCache` 与正则分支，回归纯子串匹配（**using 保留**） |
| `tests/logcat.Tests/FilterEngineTests.cs`   | 整删 5 个纯正则用例 + 3 个混合用例局部删正则断言               |
| `scripts/mutation-test.py`                  | 删掉 `CompileRegex` 那条变异                                  |
| `README.md` / `README.zh-CN.md`             | 交互说明更新（镜像 + 颜色 + 去正则），**保留 LogParser 的 regex fallback 说明** |

**不改动**：`LogDocument`、`LogParser`、`LogListView`、`Forms/FilterDialog.cs`、`Forms/FilterDialog` 承载方式，以及过滤引擎的扫描/增量结构。

---

## 5. 视觉偏好（已按默认值落地，后续可调）

> 以下都是纯视觉/范围偏好，**不阻塞代码**，已按括号里的建议值实现。
> 要改只动 `ApplyTermBoxStyle` 一个函数 + `TermTip`，不涉及过滤逻辑。

1. ~~**镜像确认保留**~~ ✅ **已定：保留**（v2 结论，见 §3.1）
2. **「排除」是否也着色** → ✅ **暂不着色**。背景色已被 op 占用、文字色已被大小写占用，
   再加一个维度会让 4 个输入框的颜色语义过载。「排除」状态改由 **tooltip 文案**表达
   （`TermTip` 在勾选时追加「已排除命中项」）。
3. **分钟框是否着色** → ✅ **不着色**。分钟无 op / 大小写语义，着色只会误导。
4. **配色取值** → ✅ 采用 `or`=浅蓝 `#E8F1FB`、`and`=浅橙 `#FDF0E3`、区分大小写=暗红 `#B00020`。
   **「忽略大小写」用 `SystemColors.WindowText` 而非硬编码深色**，这样着色/未着色两态的
   字色是同一套，将来适配深色主题时不会半途变色。
   **背景色是硬编码浅色，深色主题需另做适配**（已确认「暗色稍后再考虑」，README 已注明）。
5. **`ResetFilter` 重复触发** → ✅ **顺手优化**。`ResetFilter` 现在会一并把 op 复位为 `or`
  （原先只清 CheckBox 不复位 op），并显式调一次 `ApplyTermBoxStyle`
   —— 因为 `Clear()` 在本来就为空时**不会触发 `TextChanged`**，着色会漏。

> **已确认决策**：正则能力**整体删除**（范围见 §3.4），连同 `FilterSpec` 字段、引擎缓存、
> 全部测试与 README 说明一并移除，**不保留任何兼容开关**。
> 其中测试与README 的处理方式见 §0.1 坑 1、坑 2 —— **不是「所有含 Regex=true 的用例都删」**，
> 也**不是「README 里所有 regex 字样都删」**。

---

## 6. 实现备注（执行时踩到的，源码里已留注释）

1. **`ApplyTermBoxStyle()` 必须逐个判空**。`BuildTermControls` 是
   「建 `ed` → 挂事件 → 建 `cbOp` → `cbOp.SelectedItem = defaultOp`（触发事件）→ 建 `ckCase`」顺序，
   事件会在控件建全之前就触发，只判 `_edTag/_edMsg` 会 NRE。
2. **「忽略大小写」文字色用 `SystemColors.WindowText`**，理由见 §5.4。
3. **`_tbTag.TextBox` 着色可行**：`ToolStripTextBox` 的内层 `TextBox` 是真实控件，
   直接设 `BackColor`/`ForeColor` 生效，不需要额外 `ToolStripControlHost` 包装。
4. **`ResetFilter` 要显式调着色**（`Clear()` 空框时不触发 `TextChanged`）。
5. **harness 的坑**：别用「所有框同色」的断言去测两组独立语义 —— Tag 与 Message 的
   op/大小写互相独立，且空框是默认色。要按「一组应同色的框」分别断言
   （本次 harness 首跑 6 项假失败就是这个原因）。
