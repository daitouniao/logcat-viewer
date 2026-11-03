# 编译与发布

本文只列操作步骤。

## 环境

本文按作者本人的开发环境写成：**.NET 10 SDK** + Windows PowerShell。
项目内带 `NuGet.config`，已排除不可用的镜像源，无需改全局配置。

> 你的环境如果不同，按自己的情况调整即可：
> 用 PowerShell 7 或 cmd 的话命令语法要微调；SDK 版本不同需确认能编译
> `net10.0-windows` 目标框架。

## 一、改版本号

仓库根 `Directory.Build.props` 的 `<Version>` 是唯一来源（显示约定 `V<Version>`；
`logcat.exe`、`logcat-andtools.exe` 与共享库 `logcat.UiCore.dll` 一并继承）。改完同步这几处：

| 位置 | 改什么 |
|---|---|
| `Directory.Build.props` | `<Version>0.1.7</Version>` |
| `README.md` | 顶部 badge 里的版本文字 |
| `README.md` | "Current version **V0.1.4**" |
| `README.zh-CN.md` | 顶部 badge 里的版本文字 |
| `README.zh-CN.md` | "当前版本 **V0.1.4**" |

标题栏、关于对话框、exe 文件属性都从 `<Version>` 自动取，不用改。

## 二、编译

```powershell
dotnet build logcat.slnx -c Release
```

## 三、跑测试

```powershell
dotnet test tests\logcat.Tests\logcat.Tests.csproj
```

`dotnet test` 在本机（受限沙箱）跑不起来时，编译测试工程改用：

```powershell
dotnet build tests\logcat.Tests\logcat.Tests.csproj -c Release
```

完整测试与覆盖率办法见 [`TESTING.md`](TESTING.md)。

## 四、发布

以 V0.1.4 为例，**先把版本号换掉**再执行。

每个版本发**两个包**：自包含 + 框架依赖。

| 包 | 目标机要求 | 体积 |
|---|---|---|
| **自包含** `logcat-V0.1.4-win-x64.zip` | 只需 Windows | 约 120 MB |
| **框架依赖** `logcat-V0.1.4-win-x64-fd.zip` | 须预装 .NET 10 Desktop Runtime | 约 2 MB |

### 1. 先删干净目标目录

⚠️ `dotnet publish` **不清空目标目录**，旧文件会被一起打进 zip。

```powershell
Remove-Item -Recurse -Force publish\Release\V0.1.4-selfcontained
Remove-Item -Recurse -Force publish\Release\V0.1.4-fdd
```

### 2. 发布自包含包

两个 exe 工程**先后 publish 到同一目标目录**（SDK 原生合并；单工程 publish 不会带出
另一个 exe，第二条命令必须执行）。zip 名沿用 `logcat-V<版本>-win-x64.zip`：

```powershell
dotnet publish logcat.csproj -c Release `
  -p:SelfContained=true -p:RuntimeIdentifier=win-x64 `
  -p:PublishDir=publish\Release\V0.1.4-selfcontained\

dotnet publish logcat.AndTools\logcat.AndTools.csproj -c Release `
  -p:SelfContained=true -p:RuntimeIdentifier=win-x64 `
  -p:PublishDir=publish\Release\V0.1.4-selfcontained\

Compress-Archive -Path publish\Release\V0.1.4-selfcontained\* `
  -DestinationPath publish\Release\logcat-V0.1.4-win-x64.zip -Force
```

⚠️ **不要加 `-p:PublishSingleFile=true`**，单文件 exe 实测启动失败。
⚠️ 不要改用 `dotnet publish logcat.slnx -o`：会把测试工程一并 publish 进包。

### 3. 发布框架依赖包

两个工程都不带 `-p:SelfContained`（csproj 默认 `false`）：

```powershell
dotnet publish logcat.csproj -c Release `
  -p:RuntimeIdentifier=win-x64 `
  -p:PublishDir=publish\Release\V0.1.4-fdd\

dotnet publish logcat.AndTools\logcat.AndTools.csproj -c Release `
  -p:RuntimeIdentifier=win-x64 `
  -p:PublishDir=publish\Release\V0.1.4-fdd\

Compress-Archive -Path publish\Release\V0.1.4-fdd\* `
  -DestinationPath publish\Release\logcat-V0.1.4-win-x64-fd.zip -Force
```

### 4. 验证产物

```powershell
# 自包含：runtimeconfig 无外部框架声明 + coreclr.dll 在包内
Select-String -Path publish\Release\V0.1.4-selfcontained\logcat.runtimeconfig.json -Pattern '"frameworks"'
Test-Path publish\Release\V0.1.4-selfcontained\coreclr.dll

# 框架依赖：runtimeconfig 应声明依赖，且包内【无】coreclr.dll
Select-String -Path publish\Release\V0.1.4-fdd\logcat.runtimeconfig.json -Pattern 'Microsoft.WindowsDesktop.App'
Test-Path publish\Release\V0.1.4-fdd\coreclr.dll   # 应为 False

# 两个包都要查有没有混进测试产物和 pdb
Select-String -Path publish\Release\V0.1.4-*\*\*.* -Pattern 'xunit|coverlet|TestPlatform' -List

# 文件代理示例必须随包分发（由 csproj 的 <None Include="sample/**"> 负责，别删该项）
Test-Path publish\Release\V0.1.4-selfcontained\sample\fileproxy\fileproxy.py   # 应为 True
Test-Path publish\Release\V0.1.4-fdd\sample\fileproxy\fileproxy.py               # 应为 True

# README 演示图必须随包分发（<None Include="docs/images/main.gif">，路径须与 README 内相对链接一致）
Test-Path publish\Release\V0.1.4-selfcontained\docs\images\main.gif              # 应为 True
Test-Path publish\Release\V0.1.4-fdd\docs\images\main.gif                        # 应为 True

# 双 exe 与共享库必须齐全（第二条 publish 命令漏了就只有 logcat.exe）
Test-Path publish\Release\V0.1.4-selfcontained\logcat.exe                        # 应为 True
Test-Path publish\Release\V0.1.4-selfcontained\logcat-andtools.exe               # 应为 True
Test-Path publish\Release\V0.1.4-selfcontained\logcat.UiCore.dll                 # 应为 True
Test-Path publish\Release\V0.1.4-fdd\logcat-andtools.exe                         # 应为 True
```

预期：自包含约 120 MB、`frameworks` 匹配不到（字段为空）、`coreclr.dll` 存在；
框架依赖约 2 MB、`coreclr.dll` 不存在；两者都无测试产物匹配项，
且都带 `sample/fileproxy/`（4 个文件：README 双语 + Python/Node 代理示例）
与 `docs/images/main.gif`（README 演示图，按原相对路径存放）。

验证**能否启动**必须在另一台没装 .NET 的机器上做（验证框架依赖包则反过来，
在一台**没装** .NET 的机器上双击应报错）。本机不适合做这个验证。

### 5. 发布冒烟清单（双 exe 人工过一遍）

解压任一包到干净目录后逐项确认：

- [ ] `logcat.exe` 与 `logcat-andtools.exe` 都能双击启动，各自出主窗体
- [ ] 首次启动后目录里出现三份配置：`settings.json`、`andtools.json`、`language.json`
- [ ] 工具箱内改语言退出，再开日志查看器：语言一致（`language.json` 两 exe 共读）
- [ ] 旧版 0.1.x 的 `settings.json` 放进目录再启动：自动拆成三份，原键值不丢（含 APK 选项、上次路径、列宽、窗口几何）
- [ ] 日志查看器点「设备操作」拉起工具箱；工具箱已开时再点，已有窗体被前置（前置失败时任务栏闪烁）
- [ ] 工具箱二次启动不出现第二个窗体，已有实例被前置
- [ ] 两边各自拖动/最大化主窗体后退出重启：各自几何恢复，互不干扰
- [ ] 两 exe 同时开着各改各的设置，退出后 `settings.json` 与 `andtools.json` 互不覆盖

## 两包的取舍

| | 自包含 | 框架依赖 |
|---|---|---|
| 体积 | 120 MB（zip 约 48 MB） | 2.0 MB（zip 约 2 MB，多出的 1.2 MB 是 README 演示图） |
| 目标机 | 只需 Windows | 须预装 **.NET 10 Desktop Runtime** |
| 适用 | 大多数用户，双击即用 | 已装运行时的机器 / 内网分发 / 带宽敏感场景 |

装运行时要注意：**必须是 Desktop Runtime**——只装 .NET Runtime 或
ASP.NET Core Runtime 都起不来。

⚠️ **`SelfContained=true` 绝不能写进 csproj**，只能由发布命令的 `-p:` 参数打开。
它会沿 ProjectReference 传染：测试工程（框架依赖）报
`NETSDK1151 自包含的可执行文件不能由非自包含的可执行文件引用`，直接打断 CI。
用 `Condition` 判断 Configuration 也不行（实测 build 时泄漏、publish 时不生效）。

## 踩坑速查

| 现象 | 处理 |
|---|---|
| 打完的 zip 里有 `coverlet.collector.dll` 等测试产物 | 目标目录没删干净，重新删再 publish |
| `NETSDK1151 自包含的可执行文件不能由非自包含的可执行文件引用` | `SelfContained=true` 写进了 csproj，移出 |
| 发出去的 zip 是旧版 | 版本号没同步（`logcat.csproj` + 两个 README 的 badge 与当前版本行） |
| 发出去的 zip 里没有 `sample/fileproxy/` | csproj 的 `<None Include="sample/**">` 被删或改坏，恢复该 ItemGroup 后重打 |
| 解压后 README 里的演示图裂图 | csproj 的 `<None Include="docs/images/main.gif">` 被删或改了路径，恢复后重打（路径必须与 README 内相对链接一致） |
| 两个包内容一样大 | 框架依赖那条命令漏了 `-p:SelfContained` 的对比检查，确认 runtimeconfig 是否声明了 frameworks |
| 包里没有 `logcat-andtools.exe` | 第二条 publish 命令（logcat.AndTools 工程）漏执行，单工程 publish 不带出另一个 exe |
| 包里混进 `logcat.Tests.*` | 用了 `dotnet publish logcat.slnx -o`（会连测试工程一起发），改用两条 publish 命令 |
