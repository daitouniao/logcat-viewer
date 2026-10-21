# 探索结论：文件浏览页按设备记忆目录（默认 /sdcard）

- 状态：**已拍板**（2026-10-21），转 OpenSpec change `device-scoped-file-browser-path`
- 日期：2026-10-21
- 关联提交：无（探索阶段，未改动代码）
- 验收结论：待定

## 结论摘要

用户诉求：文件浏览页能否**按设备**记住上次目录，而不是所有设备共用一个；默认 /sdcard。

三点结论：

1. **能，且现状有一个真 bug 而非纯体验问题**：`LastRemotePath` 是全局单值，换设备时新页签读到的正是上一台设备的目录 —— `/data/local/tmp`（A 机）→ 切到 B 机 → B 机文件浏览直接落在 `/data/local/tmp`，多半权限不足弹框。
2. **「默认 sdcard」这条已经满足**，不需要新增：`AppSettings.LastRemotePath` 默认值就是 `/sdcard`，`DeviceOpsDialog` 侧还有 `?? "/sdcard"` 兜底，`FavoritesStore.SeedDefaults` 也预置了 4 条设备端收藏（`/sdcard`、`/sdcard/Download`、`/sdcard/Android/data`、`/data/local/tmp`）。本次要做的是把它从「一个全局值」变成「按设备的值」，默认值语义不变。
3. 流程上触发 AGENTS.md §一.3（改数据模型 + 跨 ≥3 文件 + 改 UI 行为）→ **必须先出 OpenSpec change 再编码**。

## 现状：路径从哪来到哪去

```
frmMain  设备下拉 _comboDevice ──SelectedIndexChanged──► _deviceOps.OnDeviceChanged()
                                                            │
                                                            ▼
                                        DeviceOpsDialog.OnDeviceChanged（逐个重建设备相关页签）
                                                            │
                             entry.Form.Close()  ──► FormClosing ──► FileBrowserDialog.SaveLastPaths()
                                                            │                        │
                                                            │                        ├─► s.LastRemotePath = _remote.CurrentPath
                                                            │                        └─► s.LastLocalPath  = _local.CurrentPath
                                                            │                             （单值，全局，不分设备）
                                                            ▼
                                        CreatePage(Files) 读同一个 s.LastRemotePath  ──► 新页签起始目录
```

三个关键事实：

- **`new FileBrowserDialog(...)` 全项目只有一处调用点**（`DeviceOpsDialog.cs:196`），改签名成本低。
- **`FileBrowserDialog` 自身不持有 serial**：serial 只传给了 `DeviceFilePane`（`_serial` 私有字段）。要在 `SaveLastPaths()` 里写对设备的键，必须让对话框自己留一份 serial —— 这是最小改动的必要前置。
- **`OnFormClosing` 是唯一的落盘时机**（`FileBrowserDialog.cs:410`）。嵌入 TabPage 的 Form 走 `Close()` 是否确实触发 `OnFormClosing`，实施时需实测确认；若不触发，则「换设备写旧值 → 新页签读新值」的泄漏链路描述需要修正（但全局单值的跨设备污染结论不变，只是写入时机不同）。

## 泄漏机制（为什么这是个 bug 而不是偏好问题）

```
        设备 A（已 root）                     设备 B（无 root）
   ┌─────────────────────────┐        ┌─────────────────────────┐
   │ 文件浏览 → /data/local/tmp│        │ 主窗切到 B              │
   │ 关掉设备操作窗口         │        │ OnDeviceChanged 重建页签 │
   │ SaveLastPaths:          │        │ CreatePage 读 LastRemotePath
   │   LastRemotePath =      │        │   = "/data/local/tmp"   │
   │   "/data/local/tmp"     │───────►│ B 机面板落在 /data/local/tmp
   └─────────────────────────┘  单值  │ → PermissionException 弹框
                                       └─────────────────────────┘
```

对照：`LastLocalPath`（本机侧）**不该**按设备记 —— 本机目录与设备无关，跟着设备分裂只会让同一台机器的下载目录在不同设备间跳变。

## 方案对比：按设备记 last path

| | A. AppSettings 加 `List<DeviceLastDir>`（推荐） | B. FavoritesStore 加按设备分组 | C. 只做「静默回落 /sdcard」，不记历史 |
|---|---|---|---|
| 落点 | `settings.json`（`LastRemotePath` 原地升级为兜底） | `favorites.json` | 无 |
| 语义匹配 | 设置类存储，与 `LastRemotePath`/`LastLocalPath` 同族 | 收藏/最近列表类存储 | — |
| 已有范式可抄 | `FavoritesStore.AddRecent` 去重插最前 + 裁尾 | 同左 | — |
| 改动面 | AppSettings + FileBrowserDialog + DeviceOpsDialog + 测试 | 再加 FavoritesStore + 两处调用 | 只改 FileBrowserDialog 回落 |
| 跨设备污染 | 消除 | 消除 | 天然没有（但用户仍要每次手动点到目标目录） |
| 评价 | **推荐** | 语义错位（收藏 ≠ 上次位置），无收益 | 治标不治本 |

### 关键设计点

**存储形状用 List 而非 Dictionary**：`Dictionary<string,string>` 序列化没问题，但**淘汰策略无处安放** —— 模拟器 serial（`emulator-5554`/`5556`…）和网络设备 serial（`192.168.x.x:5555`）都会随重启/IP 变动而增殖，无上限会无限膨胀。`List<DeviceLastDir>(Serial, Path)` 按「最近使用插最前、超限裁尾」天然有序，与 `FavoritesStore.AddRecent` 完全同构（上限参照 `MaxPerList = 60` 的量级，建议 16）。

**三级回落链**（记忆目录可能已失效：被卸载的数据目录、退出 run-as 后不可见、换了身份模式）：

```
perDevice[serial]  ──►  s.LastRemotePath（全局兜底，理论 = /sdcard）  ──►  硬编码 "/sdcard"
        │                        │                                        │
     命中最优先              升级后首访的旧行为                      最终保底
```

关键：**回落必须静默**。现在起始目录打不开会走 `OnLoadFailed` → `MessageBox`（`DeviceFilePane.cs:143`）。记忆目录失效是常态而非错误，弹框等于每次开窗口都打扰用户；只有连 `/sdcard` 都打不开才走原有错误路径。若想给一点反馈，走状态栏一行（`LblStat`，面板内已有此通道），需要新增 `LocTable.FileUi.cs` 键。

**旧字段 `LastRemotePath` 不删**：保留作全局兜底。升级后首次打开某设备查表 miss → 回落旧值（用户原来的目录），行为不退化；之后写入按设备记录。这也让 `ModelTests.AppSettings默认值` / `StorePersistenceTests` 的现有断言继续成立。

**身份模式（root / run-as）不参与键**：同一 serial 在三种身份下目录可见性不同，若按 `(serial, mode)` 记，键会翻倍且用户几乎不会预期「切 root 模式后目录变了」。交给失效回落处理更简单，也更符合直觉。

## 待用户拍板

1. **每设备记「一个」还是「一条历史列表」？** 字面「历史目录」可解读为多条。若要多条（例如该设备最近访问的 5 个目录），落点应改到 `FavoritesStore` 并复用已有的收藏下拉框（`FilePane.CboFav`），否则多个 last path 没有 UI 出口、等于死数据。**推荐先做「一个 last path」**：解决 90% 痛点、UI 零改动；真需要多条时再叠加，届时存储形状（List）已可直接复用。

2. **静默回落要不要给状态栏提示？** 「记忆目录不可用，已回到 /sdcard」这类一行提示需要新增 i18n 键（`LocTable.FileUi.cs`）。倾向加 —— 静默回落到一个用户没预期的目录，同样让人困惑，而状态栏不打断操作。

3. **容量上限取 16 是否合适？** 16 台设备的 last path ≈ 几 KB JSON，量级无压力。真正的取舍是「模拟器反复变 serial」时是否要更激进地清 —— 需要的话可加「设备重新出现时若距上次记录超 N 天则重置」这类策略，但目前看是过度设计。

## 拍板结果（2026-10-21，维护者决策）

1. **每设备记「一个 last path」** —— 多条历史列表不做。落点定 `AppSettings`（`settings.json`），存储形状 `List<DeviceLastDir>`，上限 **16**（插最前 + 裁尾）。`FavoritesStore` 不动。
2. **静默回落，不给任何提示** —— 记忆目录失效时直接换目录，不加状态栏文案、不加 i18n 键。这条同时消掉了「回落提示需要新 Loc 键」这一处 i18n 负担，实施面更小。
3. **上限 16 台够用** —— 不加过期清理策略。

**由决策 2 派生的实施约束**：回落必须是「不打断用户」的一路 —— 记忆目录打不开时**不能**走 `DeviceFilePane.OnLoadFailed` 的 `MessageBox` 路径，否则每次开窗口都弹框。需要在打开起始目录这一环自行捕获失败并改用下一级候选目录，直到成功或候选耗尽；只有连 `/sdcard` 都打不开才走原有错误提示（此时弹框是合理的，因为那是真故障）。

## 测试面（tstrun 口径）

- `ModelTests.AppSettings默认值`：加新集合字段默认值断言（空列表）+ `LastRemotePath` 仍为 `/sdcard`。
- `StorePersistenceTests`：加「按设备 last path 保存后能读回」「超限裁尾保最新」「旧配置文件缺该字段时不报错（`??=` 只兜缺字段，显式 null 需单独用例）」。
- 新增一个专门测试类覆盖回落链：命中本设备 → 未命中回落全局 → 全局空回落 `/sdcard`。
- ⚠️ 反序列化 `??=` 坑：初始化器只兜「JSON 缺字段」，只有显式 null 才走 `??=`，须单写 `{"LastRemotePaths":null}` 用例（沿用既有教训）。

## 实施顺序（若转 change）

1. `AppSettings` 加 `List<DeviceLastDir> LastRemotePaths` + 记录/查询/裁尾方法（纯数据，先测）
2. `FileBrowserDialog` 持有 serial；`SaveLastPaths` 写按设备记录 + 保留写全局兜底（或改为只在查表 miss 时用）
3. `DeviceOpsDialog.CreatePage` 改传「按设备查得的起始路径」；补三级回落与静默失败
4. 若采纳提示：`LocTable.FileUi.cs` 加键 + 状态栏一行
5. 文档：`docs/FEATURES.md` 功能条目 + TRACKER 状态同步

> 第 4 步已按拍板结果 2 取消（静默回落，无提示、无新 i18n 键）。