# 探索结论：文件上传/下载「进度没有显示」

- 状态：**探索完成，4/4 已拍板（2026-10-21）→ 已转 OpenSpec change `file-transfer-progress`**（proposal + spec delta + design + tasks 四件齐备，`openspec validate --strict` 通过，待评审）
- 日期：2026-10-21
- 关联提交：无（探索阶段，未改动代码）
- 验收结论：待定

## 结论摘要

用户诉求：设备 ↔ 本机 文件上传/下载时**看不到进度**（首轮还提到「传输完没刷新目录」，一并查了）。

三点结论：

1. **进度条不是没做，是全程停在空档**。`FileBrowserDialog` 底栏确有 `ProgressBar`，但它只在**文件粒度**上更新，且更新点写在每个文件**开始之前**（值 = 已完成数）。单文件传输（最常见）时 `Maximum=1 / Value=0`，填充比恒为 0 → 一条空条挂到底；多文件最高停在 `(N-1)/N`，**N/N 那一帧从不渲染**，随后 `finally` 直接隐藏。静态确证，与用户描述吻合。
2. **字节级进度是现成的、零新依赖**。`AdvancedSharpAdbClient 3.6.16` 的 `SyncService.Push/Pull` 都带 `Action<SyncProgressChangedEventArgs>` 回调（adbprobe `syncapi` 实测反射：`ReceivedBytesSize` / `TotalBytesToReceive` / `ProgressPercentage`），同步版另有 `ref bool isCancelled`。我们只是没传这个回调。项目已有成熟消费范式可抄：`IProgress<(double pct, string msg)>` + `_pbar.Value = (int)(pct * 100)`（`frmMain.cs:1403`）。
3. **「没刷新目录」是另一码事**：刷新其实已接（传输尾部刷目标栏当前目录），但**只刷当前目录**——拖放落到**子目录**时新文件不在当前视图里，用户看到的就是「没刷新」。此外 root/run-as 中转路径下每个文件还串了 stat / mkdir / cp / chmod / chown / rm / sync 一串**全程无反馈**的 shell 步骤，大头时间可能根本不在 push/pull 上。

## 现状：进度条到底做了什么

```
入口（四条，全部汇到同一个 RunTransferAsync）
  底栏「⬅ 下载到本机」/「上传到设备 ➡」  ──┐
  面板按钮「⬅ 下载到本机」「上传文件到此处…」┤
  面板↔面板拖放 / 资源管理器拖入          ┼──► RunTransferAsync  (FileBrowserDialog.cs:224)
  右键菜单                                ┘
                                              │
  _transferring=true; 禁用两个按钮; _pbar.Visible=true      :239-242
  展开文件列表（目录递归 find / GetFiles）                  :248-250
  _pbar.Maximum = files.Count; _pbar.Value = 0;            :264-265   ← 分母 = 文件数
  foreach (src in files) {
      _pbar.Value = ok + failed;        ← 写的是「已完成数」，在传本文件之前  :271
      SetStatus($"({n}/{total}) 文件名")                    :272
      PushFileAsync / PullFileAsync                          :276-279
  }
  上传后 sync 刷盘（≤120s，静默）                            :290-295
  SetStatus("传输完成：成功 N 个…")                          :297
  刷目标栏（上传刷 _remote / 下载刷 _local）                  :302-303
  finally: _pbar.Visible=false                              :310-316
```

底栏进度条本体：`FileBrowserDialog.cs:100-109`（180×18，`Visible=false` 起手）。

## 根因：为什么「进度没有显示」

```
单文件传输（Maximum=1）
  传输开始  Value=0   [                    ]  ← 填充比 0/1 = 0%，肉眼全空
  传输中    一直是 0（循环内没有第二次赋值）
  传输完    循环直接结束，Value 仍为 0，从未到 1
  finally   Visible=false
  ⇒ 用户全程只看到一条不动的空条 + 一行灰色小字

N 文件传输（Maximum=N）
  第 i 个文件开始时 Value=i（i 从 0 到 N-1）
  ⇒ 最高只能走到 (N-1)/N，最后一格永不点亮，也没有「100%」的瞬间
```

叠加放大它的两个事实：

- **字节维度完全没有观测点**：`AdbManager.Pull/Push`（`Services/AdbManager.cs:387-418`）是 `Task.Run(() => Push(...))` 一刀切，`SyncService` 的进度回调参数留空；上层 `DeviceFilePane.PushFileAsync/PullFileAsync`（`:545-619`）同样是一串 `await`，没有 `IProgress` 形参。
- **中转路径的时间黑洞**（root / run-as 且路径非 `/sdcard` 系）：`NearestOwnerAsync`(stat) → `mkdir -p` → `cp` → `chmod` → push/pull → `chown`×2 → `rm`，上传后还要 `sync`（`:578`，超时 120s，`ShellBestEffortAsync` 全吞异常）。这些步骤进度条纹丝不动、状态文案也不换。
- **唯一真正在动的信号**是底栏那行 `DimGray` 小字 `({n}/{total}) 文件名`，无速度、无剩余时间，且传输一结束立刻被「传输完成：成功 N 个」覆盖——面板自己的状态栏（`LblStat`）此刻又切回「N 项」，用户很容易以为什么都没发生。

## 字节级进度：现成的（adbprobe `syncapi` 实测）

```
AdvancedSharpAdbClient.SyncService (3.6.16)
  Void Push(Stream, String remotePath, UnixFileStatus, DateTimeOffset,
            Action<SyncProgressChangedEventArgs> callback, Boolean useV2, ref Boolean isCancelled)
  Void Pull(String remoteFilePath, Stream, Action<SyncProgressChangedEventArgs> callback,
            Boolean useV2, ref Boolean isCancelled)
  Task PushAsync(..., Action<...>, Boolean useV2, CancellationToken)
  Task PullAsync(String, Stream, Action<...>, Boolean useV2, CancellationToken)

回调实参 AdvancedSharpAdbClient.Models.SyncProgressChangedEventArgs
  prop UInt64 ReceivedBytesSize
  prop UInt64 TotalBytesToReceive
  prop Double  ProgressPercentage
```

即：**不需要换库、不需要自造 chunk 协议**，只要把回调接上，push/pull 立刻有真实百分比，且同步版顺带白送一个取消开关。

（探针已加进 `.tmp-test/adbprobe`：`dotnet .tmp-test/adbprobe/bin/Debug/net10.0/adbprobe.dll syncapi`，不入库。）

## 「传输完没刷新目录」查证

刷新**已实现**（`FileBrowserDialog.cs:302-303`），但有真实缺口：

| 缺口 | 位置 | 表现 |
|---|---|---|
| 只刷**当前目录** | `:302-303` | 拖放到**子目录项**上（`FilePane.DropTargetPath` 返回子目录路径，`:759-765`）时新文件不在当前视图，刷新了也看不出变化 |
| 早期 return 不刷新 | `:255-262` | `files.Count == 0`（选中空目录、设备不支持 find）直接返回，不刷新（此时也无变更，可接受） |
| 外层异常不刷新 | `:305-309` | 列举阶段整体抛错时，已落盘的文件不会出现在列表里 |
| 本机复制无守卫 | `:319-364` | `CopyLocalFilesAsync` **不设 `_transferring`**、不禁用按钮、**`_pbar` 无 try/finally**（`:333`/`:357`）→ 与设备传输并发时抢同一条进度条；异常时进度条永久可见 |

## 其它确认的缺口（一并记下）

1. **传输不可取消**：`FilePane.CancelLoading()` 只取消目录列举（`_cts` 属 `LoadIntoAsync`）。传输中没有取消入口；关窗时 `OnFormClosing`（`:398-406`）只弹「文件正在传输中，请等待完成后再关闭」。设备中途掉线 = 只能干等。
2. **i18n 违规两处**：`SetStatus($"({ok+failed+1}/{files.Count}) {Shorten(name, 32)}")`（`:272`）与 `CopyLocalFilesAsync` 内同名一行（`:342`）是硬编码插值，未走 `Loc.F`，`LocTable.FileUi.cs` 也没有对应键——切英文时这两行仍是中文。
3. **`AdbManager` 传输路径没有注入缝**：`new SyncService(new AdbSocket(), device)` 写死（`:390`/`:410`），字节级回调无法用假 socket 单测。修法可复用 `fix-adb-server-self-heal` 已建的 `_socketFactory` 缝（`SyncService` 有 `ctor(IAdbSocket, DeviceData)`），或更轻：把「回调 → `IProgress<(double,string)>`」抽成纯函数单测。
4. **APK 安装/卸载连进度条都没有**（只有 `IProgress<string>` 输出日志，`ApkInstallDialog.cs` 无 `ProgressBar`），横向不一致——本次是否一并做，建议明确划出范围。

## 「相位」是什么意思（提问补充，2026-10-21）

中转路径（root / run-as，且目标不在 `/sdcard` 系）下，**传一个文件并不是"把字节推过去"这一件事**，而是一条多步流水线。实测代码路径（`DeviceFilePane.PushFileAsync` / `PullFileAsync`）：

```
上传到 /data/data/pkg/f.bin（run-as 模式）
  ① stat  查目标目录链上最近祖先的 uid:gid      NearestOwnerAsync  ← adb shell 往返
  ② mkdir -p 建目标目录                         MkdirPAsync         ← adb shell 往返
  ③ push  本机文件 → 设备中转目录               _manager.PushAsync  ← ★只有这一步有字节进度
  ④ cp    中转文件 → 最终路径（设备端拷贝）      ShellOkAsync        ← adb shell 往返，设备侧真实 I/O
  ⑤ chmod a+r 放开读权限                        ShellBestEffort     ← adb shell 往返
  ⑥ chown 目录 + 文件改回属主                   ShellBestEffort     ← adb shell 往返
  ⑦ rm    删中转文件                            ShellBestEffort     ← adb shell 往返
  ⑧ sync  刷盘（全设备脏页，单次可达数十秒）     SyncAsync           ← adb shell 往返，超时 120s
```

**问题**：整条流水线里，**只有 ③ 能拿到字节级进度**（库的回调只覆盖 push/pull 本身）。① ② ④ ⑤ ⑥ ⑦ ⑧ 全是 adb shell 往返 —— 有真实耗时（尤其 ④ 设备端拷贝 和 ⑧ sync 刷盘），但**一个字节回调都没有**。而直连路径（目标是 `/sdcard` 系）= 纯 ③，百分比完全准确。

所以「中转 shell 相位要不要入进度」= **进度条怎么表示 ③ 之外那段「知道在忙、但不知道进度」的黑时间**。三种做法：

| 做法 | 条的表现 | 代价 / 风险 |
|---|---|---|
| **a. 不入**：条只管 ③ 的字节，shell 阶段条停在原处 | 条在 ② 之后卡住不动，直到 ③ 开始才跳 | ⑧ sync 那段（可能几十秒）条完全静止 —— 正是用户当下最想看到"还在动"的时刻 |
| **b. 假权重**：按经验给各步分份额（准备 10% / 传输 60% / 收尾 30%），每步完成就 +权重 | 条连续、不卡死 | **权重是拍的**，与真实字节无关；本项目对"假进度"敏感（fileproxy 规范明写「禁止静态文案 + 静止进度条组合」） |
| **c. 不确定态**：shell 阶段把条切成 marquee（滚动），进入 ③ 再切回 Blocks 显示真实百分比；shell 阶段的具体动作写进状态栏（"正在复制到设备…"） | 条一直"在动"，③ 阶段给精确百分比 | WinForms `ProgressBar.Style = Marquee` 原生支持，实现最轻；语义诚实（不假装知道百分比） |

> 直连路径（`/sdcard` 系，无中转）三选一都无差别 —— 只有 ③，进度条天然准确。**相位问题只在 root/run-as 中转路径上出现**，而那恰恰是权限最受限、最容易让人觉得"卡住了"的场景。

## 方案对比

| | A. 只修计数口径 | B. 字节级进度（推荐） | C. 独立传输窗口/浮动进度 |
|---|---|---|---|
| 改动面 | `FileBrowserDialog` 数行 | `AdbManager` 传回调 + `DeviceFilePane` 透传 `IProgress` + `FileBrowserDialog` 改 0–100 百分比（+ 速度/剩余可选） | 新窗体 + 生命周期 + 多传输队列 |
| 单文件大文件体感 | 仍无（只是结束时跳满） | 实时百分比 | 好 |
| 中转路径（root/run-as） | 无改善 | 需给 shell 步骤分配相位权重，否则大量时间条不动 | 好（可逐条列步骤） |
| 取消能力 | 无 | 同步版可用 `ref isCancelled`；或整体换 `CancellationToken` | 好 |
| i18n 负担 | 低（顺手补 2 键） | 中（速度/剩余/已取消/相位文案） | 高 |
| 风险 | 低，但治标 | 中：改传输签名，波及 4 个文件 | 高，且与「单活动文档」的轻量取向不符 |

**倾向 B**（已拍板，见下）。

## 拍板结果（2026-10-21，维护者决策）

1. **进度口径 = B 字节级** —— 接 `SyncService` 的 `Action<SyncProgressChangedEventArgs>` 回调，进度条改 **0–100% 百分比**（不再是文件计数）。不选 A（只修计数口径）：单文件大文件仍然是"没有进度"。
2. **不做取消** —— 不加取消按钮，传输中仍不可中断；连带「取消到一半的落盘文件怎么算」这个问题一并消失（关窗时仍按现状弹「请等待完成后再关闭」）。
3. **进度条形态 = 百分比** —— 条上就是百分比，不引入速度 / 剩余时间（保持轻量；如后续要速度，是纯增量，不影响本次结构）。
4. **中转 shell 相位 = c（不确定态）** —— shell 阶段（①②④⑤⑥⑦⑧）把进度条切成 **marquee 滚动**，进到 ③ 字节阶段再切回 Blocks 显示真实百分比；shell 阶段的具体动作写进状态栏（"正在复制到设备…"）。**不造假权重**（不采用 b），也不让条静止（不采用 a）。直连路径（`/sdcard` 系）无 shell 阶段，条全程 Blocks 精确百分比。


## 若转 change：实施顺序（预估 5~7 task）

1. `AdbManager`：Push/Pull 增加可选进度回调形参（`Action<SyncProgressChangedEventArgs>` 或直接收 `IProgress<(double pct, string msg)>`），把库的回调映射成项目惯用的 `IProgress<(double, string)>`（**同时决定要不要加 `syncFactory`/`socketFactory` 缝以便可测**）。
2. `DeviceFilePane.PushFileAsync/PullFileAsync` 透传进度；把中转各步骤（stat/mkdir/cp/chmod/push·pull/chown/rm/sync）的阶段文案接上（`SetStatus` 通道现成）；直连路径不产生阶段文案。
3. `FileBrowserDialog.RunTransferAsync`：进度条改 0–100 百分比（`Maximum=100`）；多文件时按**总字节**折算整体进度（上传侧用 `FileInfo.Length`、下载侧用列表里的 `Size`，总量可在传输前算清）；结束前补一次 `Value = Maximum`（现在最后一格永不点亮）；`files.Count == 0` 与外层异常两条路径也走刷新；`CopyLocalFilesAsync` 并入 `_transferring` 守卫并给 `_pbar` 加 try/finally。
4. shell 阶段 `ProgressBar.Style = Marquee`（滚动），进字节阶段切回 `Blocks`；shell 阶段的具体动作（stat/mkdir/cp/chmod/chown/rm/sync）写进状态栏，文案键进 `LocTable.FileUi.cs`；直连路径全程 Blocks。
5. i18n：`:272`/`:342` 两处硬编码插值改 `Loc.F`，新增键入 `LocTable.FileUi.cs`。
6. 文档：`docs/FEATURES.md` 文件浏览条目 + `docs/IMPLEMENTATION-TRACKER.md` 状态同步；进度/刷新行为若要长期约束，另立 spec capability（当前 `openspec/specs/` 只有 `fileproxy`，文件浏览尚无规范条目）。


## 测试面（tstrun 口径）

- 进度映射纯函数：`ReceivedBytesSize/TotalBytesToReceive` → 百分比，含 `Total=0`、进度回退、超 100% 三条边界（沿用 `UT-AUDIT.md` N10 的边界断言习惯）。
- 计数口径回归：N 个文件 → 收尾时 `Value == Maximum`（这条现在必挂，是真 bug 的可测形式）。
- 多文件总字节折算：总量 = 各文件长度之和；单文件传完时整体百分比应落在 (0,100) 且单调不回退。
- `CopyLocalFilesAsync` 与传输互斥：并发触发时第二次应被 `_transferring` 拦下。
- 刷新覆盖：`files.Count == 0`、中途异常两条路径都应触发目标栏刷新。
- 口径声明：任何「修好了」都要带 tstrun 数字（基线见 `docs/TESTING.md`，当前 638），禁止「未验证的通过」。

## 库硬规律（复用时先看）

- `AdvancedSharpAdbClient 3.6.16`：`SyncService.Push/Pull` 的进度回调与 `ref isCancelled` **都是现成形参**，不用换库、不用自造协议。
- `SyncService` 有 `ctor(IAdbSocket, DeviceData)`，与项目已建的 `_socketFactory` 缝同构——传输层要可测时先看这条。
- WinForms `ProgressBar` 的填充比 = `(Value-Min)/(Max-Min)`：`Maximum=1 / Value=0` 就是全空，肉眼等同「没有进度条」。