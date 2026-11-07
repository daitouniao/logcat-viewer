# Design

## Context

`DeviceSelector` 同时被两个线程使用：

- **写**：`AdbManager.DevicesChanged`（后台监控线程）→ `ApplyDevices`；
- **读 + 通知**：UI 线程订阅 `DevicesChanged` 回填下拉框；`HealFailed` 分支在 UI 线程调 `Clear()`。

现状 `ApplyDevices` 把内部 `_devices`（活 `List<DeviceInfo>`）直接作为事件参数发出。UI 线程拿到的引用与后台线程下次写入的是**同一个对象**，于是 `foreach` 期间一旦发生 `Clear()`/`AddRange` 就抛
`InvalidOperationException: Collection was modified`。这是接口契约缺陷（泄漏可变内部状态），不是某个调用点忘了 copy。

## Decisions

### D1 事件一律交付快照，签名不变

`DevicesChanged?.Invoke(_devices.ToArray())`。 `DeviceInfo[]` 实现 `IReadOnlyList<DeviceInfo>`，订阅方（`frmMain`、工具箱）零改动，只是拿到的再也不是活列表。

- 否决「订阅方自己 copy」：每个订阅方都得记得，且 copy 时读的仍是活列表，不安全。
- 否决 `ImmutableList<T>`：引入新依赖收益不抵成本，快照已经满足需求。

### D2 单锁保护 `_devices` 与 `_selectedIndex`

两者必须一起变化（列表重建 + 重选首台是一次原子语义），所以共用一把 `_gate`。临界区只做 `Clear`/`AddRange`/索引赋值/`ToArray`，无 IO、无回调。

- 否决 `ConcurrentBag`/`ConcurrentQueue`：需要按序选择与一致快照，并发容器给不了「一致」。
- 读侧（`Devices`/`SelectedIndex`/`CurrentSerial`/`CurrentRootMode`）同样进锁，避免读到「已清空、未重建」的中间态。
- 写侧的 `SelectedIndex` 属性 setter 也进锁：UI 回写下标与后台重建列表会交错，未加锁时可能出现「下标已更新、列表还是旧的」的读数。

### D3 通知在锁外发出

`ApplyDevices` 在锁内产出快照，出锁后再 `Invoke`。订阅方回调里可能间接触发 `Clear()`（自愈失败路径）或等 UI 工作，持锁回调会跨线程死锁（`lock` 只对同线程重入友好）。

### D4 `Devices` 属性返回快照

对外暴露「当前设备集合」的语义应是值而非句柄。设备数是**个位数**，每次调用一次小数组拷贝的成本可忽略；换来的是调用方（`AdbRefresh` 的 `.Count`、文件传输窗口的枚举）永远不会踩到活列表。

### D5 测试策略：确定性断言 + 固定次数压力

- 确定性：一次变更后保留通知参数，再触发变更/清空，断言先前参数内容不变。
- 并发：后台线程反复 `ApplyDevices`，主线程反复枚举通知参数并查询 `CurrentSerial`，固定迭代次数、只断言「不抛异常」——不对时序做断言，避免慢机上的抖动。
- 范围声明：并发用例**不构成对 `_gate` 的守护**。撤掉锁时，枚举独立快照与读 `Devices.Count` 仍不会抛，只有 `CurrentSerial` 的「判界 → 索引」之间存在窄窗口，5000 次迭代大概率撞不上。锁的正确性由 D2/D3 的临界区设计（派生快照、临界区不跨 IO/回调）与代码评审保证，该用例的定位是固化崩溃现场 + 防止退回活列表。

## Risks

- 快照语义是行为契约变化：若将来有订阅方依赖「拿到的是活列表」做增量 diff，会失效。当前仓库无此类用法（订阅方只用于重建下拉框），已在 spec 场景中固化。
- 并发用例只覆盖「不崩」，无法证明竞态彻底消失；但配合 D1/D2 的契约收敛，剩余窗口仅限「快照内容稍旧」，不影响正确性。
