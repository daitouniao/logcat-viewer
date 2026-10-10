# 单元测试与覆盖率实操指南

> 最后更新：2026-10-07
> 本文是「怎么把测试跑起来、覆盖率怎么出」的**唯一操作入口**。
> 有效性审计（变异测试）见 [`UT-AUDIT.md`](UT-AUDIT.md)。

## 0. 当前基线（2026-10-07）

| 项 | 值 |
| --- | --- |
| 用例数 | **412**，全过（含 `LocTableTests` 的 18 条本地化一致性测试） |
| 行覆盖率 | **99.38%** (2,226 / 2,240) |
| 分支覆盖率 | **87.56%** (1,028 / 1,174) |
| 方法覆盖率 | **99.23%** |
| 统计口径 | 仅 Services + Models（UI 与设备/系统集成层已排除，见 §4） |

> 本轮口径变化：新增的 `Services/Loc.cs`（本地化运行时）**被排除**，理由同 `DpiFix`——
> 它一半的代码是控件树遍历、字体替换与文本像素测量，没有真实窗体与消息泵无从验证。
> 与它同目录的 `LocTable.*`（纯数据译文表）**不排除**，是 i18n 的核心验证面。
>
> 覆盖率是「代码被执行过」的证明，**不等于**「断言咬得住」。
> 本项目做过一轮变异测试审计，发现 6 处「覆盖率统计覆盖、却一个用例都抓不到」的代码，详见 [`UT-AUDIT.md`](UT-AUDIT.md)。

---

## 1. 两条路径，先选对再往下看

| | 路径 A：人终端 | 路径 B：智能体沙箱 |
| --- | --- | --- |
| 跑测试 | `dotnet test`（直接可用） | `tstrun` 反射 runner（§3） |
| 出覆盖率 | `dotnet test --collect`（§2.1） | `coverlet.console` 驱动 tstrun（§2.2） |
| 为什么 | 正常路径 | **`dotnet test` 在沙箱里起不来**，见 §1.1 |

### 1.1 为什么有两条路径

`dotnet test` 会拉起 `testhost.exe`（apphost），它在沙箱里加载 `hostfxr.dll` 时报 **`0x80070005`**（拒绝访问）。
沙箱开关、`TEMP` 指向、进程位数、目录 ACL、是否管理员 —— 全部试过并排除，**不再深挖**。

连带影响：`dotnet test --collect:"XPlat Code Coverage"` 走的是同一个 testhost，所以**覆盖率也一起起不来**。
解法不是修 testhost，而是**换掉执行器**（§2.2 / §3）。你自己在终端跑则不受影响，走路径 A。

---

## 2. 出覆盖率

### 2.1 路径 A：终端 collector 模式

```powershell
dotnet test tests/logcat.Tests/logcat.Tests.csproj `
  --collect:"XPlat Code Coverage" `
  --settings tests/logcat.Tests/coverlet.runsettings
python tests/coverage-report.py
```

产物在 `tests/logcat.Tests/TestResults/<guid>/coverage.cobertura.xml`，
`coverage-report.py` 会自动取**最新**那个 xml，输出 `tests/coverage-report.html`。

> **不需要**再设 `$env:TMP` / `$env:TEMP`。临时目录早已迁到 `tests/logcat.Tests/tmp/`（`TempRoot`），
> 无需任何环境变量设置。

### 2.2 路径 B：coverlet console 驱动 tstrun（绕开 testhost）

用 `coverlet.console` 全局工具，把 tstrun 当 runner 注入 Profiler。

```bash
dotnet tool install -g coverlet.console        # 需 ≥ 10.1.0，见 §5.3
```

```bash
cd %TEMP%/tstrun/bin/Debug/net10.0-windows
coverlet logcat.Tests.dll \
  --target tstrun.exe --targetargs "" \
  --include "[logcat]*" \
  --exclude "[logcat]logcat.ApplicationConfiguration" \
  --exclude "[logcat]logcat.Forms.*" \
  --exclude "[logcat]logcat.Controls.*" \
  --exclude "[logcat]logcat.frmMain" \
  --exclude "[logcat]logcat.frmMain.*" \
  --exclude "[logcat]logcat.Program" \
  --exclude "[logcat]logcat.Services.AdbManager" \
  --exclude "[logcat]logcat.Services.AdbManager.*" \
  --exclude "[logcat]logcat.Services.LogcatStream" \
  --exclude "[logcat]logcat.Services.LogcatStream.*" \
  --exclude "[logcat]logcat.Services.ClipboardHelper" \
  --exclude "[logcat]logcat.Services.StartupLog" \
  --exclude "[logcat]logcat.Services.DpiFix" \
  --exclude "[logcat]logcat.Services.DpiDiag" \
  --exclude-byfile "**/Forms/*.cs" \
  --exclude-byfile "**/Controls/*.cs" \
  --exclude-byfile "**/frmMain.cs" \
  --exclude-byfile "**/frmMain.Designer.cs" \
  --exclude-byfile "**/Program.cs" \
  --exclude-byfile "**/Services/DpiFix.cs" \
  --exclude-byfile "**/Services/DpiDiag.cs" \
  --format cobertura \
  --output <仓库>/tests/logcat.Tests/TestResults/coverlet-tstrun/coverage.cobertura.xml
```

然后同样跑 `python tests/coverage-report.py` 出 HTML（它按 mtime 取最新 xml，不关心是哪个 collector 产的）。

> **⚠️ 头号坑：`--exclude` 不接受逗号分隔。**
> 上面写成了 **14 个 `--exclude` + 7 个 `--exclude-byfile`**，这不是啰嗦，是**必须**这么写。
> 把 `coverlet.runsettings` 里 `<Exclude>` 的逗号串原样塞进单个 `--exclude`，coverlet console
> 会把它当成**一个**无效表达式，然后**全部静默失效** —— 不报错、不警告，只是 UI / 集成层全被算进
> valid，行覆盖率假降到 **23.85%**。详见 §5.1。

---

## 3. 智能体沙箱怎么跑测试：tstrun 反射 runner

`%TEMP%/tstrun` 是一个**临时**工程（不进 git，%TEMP% 被清就没了）。
它是控制台工程，通过 `ProjectReference` 引用 `logcat.Tests.csproj`，
反射扫出所有 `[Fact]` / `[Theory]` 方法直接调用，绕开 testhost。

### 3.1 重建方法

`tstrun.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseWindowsForms>true</UseWindowsForms>
    <AssemblyName>tstrun</AssemblyName>
    <NoWarn>$(NoWarn);CS8600;CS8602;CS8604;CS8625;CS0618;xUnit1004</NoWarn>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Runner.cs" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="<仓库绝对路径>/tests/logcat.Tests/logcat.Tests.csproj" />
  </ItemGroup>
</Project>
```

`Runner.cs`（顶栏语句，**不要**写 `static int Main`）：

```csharp
using System.Reflection;
using System.Text;

var asm = typeof(logcat.Tests.FilterEngineTests).Assembly;
var filter = args.Length > 0 ? args[0] : null;   // 可选：类名过滤子串

int pass = 0, fail = 0, skipped = 0;
var failures = new List<string>();

foreach (var type in asm.GetTypes().Where(t => t.IsClass && !t.IsAbstract).OrderBy(t => t.Name))
{
    if (filter != null && !type.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

    var tests = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Where(m => m.GetCustomAttributes().Any(a =>
               a.GetType().Name is "FactAttribute" or "TheoryAttribute"))
        .OrderBy(m => m.Name).ToList();
    if (tests.Count == 0) continue;

    foreach (var m in tests)
    {
        bool isTheory = m.GetCustomAttributes().Any(a => a.GetType().Name == "TheoryAttribute");

        var datas = new List<object[]>();
        foreach (var a in m.GetCustomAttributes())
        {
            if (a.GetType().Name != "InlineDataAttribute") continue;
            if (a.GetType().GetMethod("GetData")?.Invoke(a, new object[] { m }) is IEnumerable<object[]> rows)
                datas.AddRange(rows);
        }
        if (datas.Count == 0)
        {
            if (isTheory) { skipped++; continue; }   // MemberData/ClassData 等未支持
            datas.Add(Array.Empty<object>());
        }

        foreach (var data in datas)
        {
            var argText = string.Join(", ", (object[])data);          // 坑②：先提到变量
            string label = $"{type.Name}.{m.Name}({argText})";
            object inst = null;
            try
            {
                inst = Activator.CreateInstance(type);
                m.Invoke(inst, (object[])data);
                pass++;
            }
            catch (Exception ex)
            {
                var real = ex is TargetInvocationException tie ? tie.InnerException : ex;
                fail++;
                var msg = real is Xunit.Sdk.XunitException xe
                    ? xe.Message
                    : real.GetType().Name + ": " + real.Message;
                failures.Add(label + "\n    " + msg.Replace("\r\n", "\n    "));
            }
            finally { (inst as IDisposable)?.Dispose(); }
        }
    }
}

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine($"passed: {pass}   failed: {fail}   skipped(theory w/o InlineData): {skipped}");
if (fail > 0)
{
    Console.WriteLine("\n================ FAILURES ================");
    foreach (var f in failures) Console.WriteLine("  " + f);
    Environment.Exit(1);
}
```

跑法：

```bash
cd %TEMP%/tstrun
dotnet run                 # 全量
dotnet run FilterEngine    # 只跑类名含 FilterEngine 的
```

### 3.2 三个必须知道的坑

1. **顶栏语句文件里不能写 `static int Main`** —— 报 CS7022，Main 被忽略，程序**静默什么都不跑**，
   表现为「跑完没输出、看起来像全绿」。**假绿**比报错危险得多。
2. **插值表达式里内嵌 `"null"` 会提前闭合字符串**：`$"...{string.Join(", ", data)}..."` 在某些参数下
   会被解析成 `string.Join(", ", data) + "..."` 之类，`string.Join` 的结果要**先提到变量**再插值。
3. **只匹配 `FactAttribute` 会漏掉全部 Theory**（20 个）—— 所以要么同时匹配 `TheoryAttribute` 并按
   `[InlineData]` 展开（上面的代码已做），要么**必须**拿「用例总数」与已知基线（当前 **412**）交叉验证
   runner 本身没漏。这个交叉验证是每次改 runner 后的必做动作。

### 3.3 本地化相关的两个特殊坑（2026-10-07）

**① 源码扫描类测试必须先断言「扫到了东西」**

`LocTableTests` 要扫源码找漏翻的中文键。扫描器的成员访问解析一旦写错（`Loc.T` 后面是 `.` 不是 `(`），会返回 **0 条**，而"没有漏翻"照样成立——**假绿比报错危险**。所以必须配一条硬断言：

```csharp
int scanned = 0;
foreach (var _ in EnumerateLocCallKeys(repoRoot)) scanned++;
Assert.True(scanned > 300, $"只找到 {scanned} 个调用点，解析逻辑大概率坏了");
```

改扫描逻辑后，**务必跑一份独立 Python 脚本交叉验证条数**，别信"全绿"。

仓库路径用 csproj 的 `AssemblyMetadata("RepoRoot")` **编译期注入**，不要靠 `AppContext.BaseDirectory` 往上找 `logcat.csproj`——tstrun 从 `%TEMP%` 启动，输出目录不在仓库内，会直接找不到。

**② `LocTableTests` 只扫 `Loc.*` 调用点，不扫所有字面量**

诊断日志（`StartupLog` / `DpiDiag` / `_logger`）与 `ArgumentException` 消息按约定**保持中文不翻译**。全量扫字面量会把它们误判成漏翻，测试与设计直接冲突。

---

## 4. 统计口径（为什么不是 100%）

覆盖率**只统计可单测的业务逻辑层**，规则集中定义在 `tests/logcat.Tests/coverlet.runsettings`，
排除两类代码：

| 排除类别 | 成员 | 理由 |
| --- | --- | --- |
| **UI 层** | `Forms.*` / `Controls.*` / `frmMain` / `Program` / `DpiFix` / `DpiDiag` / `Loc` / `ApplicationConfiguration` | WinForms 依赖消息泵与 STA 线程，构造/布局/事件绑定单测成本极高、收益极低；DPI 兜底要真实窗口与缩放链路才有意义；`Loc` 的控件树遍历、字体替换与像素测量同理。行业惯例交给 UI 自动化或人工验收。 |
| **系统 / 设备集成层** | `AdbManager`（只测了两个纯函数）/ `LogcatStream` / `ClipboardHelper` / `StartupLog` | 分别需要真实 adb+设备、设备流、Windows 剪贴板（共享资源+UI 线程）、固定写用户目录（会污染真实数据）。 |

> `LocTable.*`（纯数据译文表）**刻意留在口径内** —— 它没有控件依赖，且是 i18n 的核心验证面。
> 判断标准是「**这条代码能不能在无窗体条件下被有意义地验证**」，不是「这个文件新不新」。

所以报告里的 ~100% 指的是「**该测的都测到了**」，不是「整个程序集都测到了」。
看覆盖率变化要**按类对比**，别只看总数字 —— 总数字持平完全可能掩盖了某个类的塌方。

---

## 5. 踩坑清单

### 5.1 coverlet console 的 `--exclude` 不接受逗号分隔 ⚠️

**症状**：把 runsettings 的逗号串塞进单个 `--exclude`，覆盖率从 ~98% 掉到 **23.85%**，且**无任何报错**。

**根因**：coverlet **collector** 的 `<Exclude>` 配置支持逗号分隔；
但 **console 命令行**的 `--exclude` / `--exclude-byfile` 每个参数只吃**一个**表达式。
逗号串被当成单个表达式、匹配不到任何东西，于是**一条都没排除** —— 静默失效。

**正解**：拆成 14 个 `--exclude` + 7 个 `--exclude-byfile`（见 §2.2）。

**自查**：出报告后先确认分母。`lines-valid` 应该是 ~1700 量级；
若是几千，说明排除规则没生效（UI 层的 ~9400 行灌进来了）。

### 5.2 cobertura XML 里同一行号会输出两次（行数与分支数都会翻倍）

同一行号的 `<line>` 条目在 coverlet 的 cobertura 输出里**重复出现两次**。后果有两条：

- 按类直接累加**行数**会翻倍；
- 累加每个 `<line>` 的 `condition-coverage` 里的分支数，**分支数同样翻倍**
  （实测：直接累加得 1,984 / 2,244，按行号去重后是真实的 **992 / 1,122**）。

行数翻倍曾把 `1,716/1,741` 显示成 `3,432/3,482`，**百分比恰好不变**，极具迷惑性
（`README` 的「Current status」行一度长期挂着这个错数，2026-10-04 才修正）。

`coverage-report.py` 已按行号去重（2026-10-03），**直接用它的输出**。
自己解析 xml 时务必按行号去重（`line_hits[num] = max(...)`），分支同理。

### 5.3 coverlet 必须 ≥ 10.1.0

6.0.4 在 .NET 10 上覆盖率**全 0**。当前 `logcat.Tests.csproj` 引用 `coverlet.collector` 10.1.0。
若换回旧版，会看到「测试全过但覆盖率 0%」的现象。

### 5.4 跑 `scripts/mutation-test.py` 前先 commit ⚠️

该脚本用 `git checkout -- <单个文件>` 恢复被变异的文件，**会静默丢弃该文件未提交的改动**。
2026-10-04 就因此丢掉了 `FilterEngine.cs` 的全部引擎改动。
细节与完整审计见 [`UT-AUDIT.md`](UT-AUDIT.md)。

---

## 6. 一句话速查

- 我在终端 → §2.1（`dotnet test --collect` + `python tests/coverage-report.py`）。
- 智能体沙箱 → §3 建 tstrun 跑测试、§2.2 用 coverlet console 出覆盖率。
- 数字看着不对 → §5，先查分母（§5.1）。
- 数字好看但心里没底 → [`UT-AUDIT.md`](UT-AUDIT.md) 跑变异测试。
