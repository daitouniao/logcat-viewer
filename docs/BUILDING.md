# 编译与发布

本文只列操作步骤。

## 环境

本文按作者本人的开发环境写成：**.NET 10 SDK** + Windows PowerShell。
项目内带 `NuGet.config`，已排除不可用的镜像源，无需改全局配置。

> 你的环境如果不同，按自己的情况调整即可：
> 用 PowerShell 7 或 cmd 的话命令语法要微调；SDK 版本不同需确认能编译
> `net10.0-windows` 目标框架。

## 一、改版本号

`logcat.csproj` 的 `<Version>` 是唯一来源（显示约定 `V<Version>`）。改完同步这几处：

| 位置 | 改什么 |
|---|---|
| `logcat.csproj` | `<Version>0.1.3</Version>` |
| `README.md` | 顶部 badge 里的版本文字 |
| `README.zh-CN.md` | 顶部 badge 里的版本文字 |
| `README.zh-CN.md` | "当前版本 **V0.1.3**" |

标题栏、关于对话框、exe 文件属性都从 `<Version>` 自动取，不用改。

## 二、编译

```powershell
dotnet build logcat.csproj -c Release
```

## 三、跑测试

```powershell
dotnet build tests\logcat.Tests\logcat.Tests.csproj -c Release
```

完整测试与覆盖率办法见 [`TESTING.md`](TESTING.md)。

## 四、发布

以 V0.1.3 为例，**先把版本号换掉**再执行。

每个版本发**两个包**：自包含 + 框架依赖。

| 包 | 目标机要求 | 体积 |
|---|---|---|
| **自包含** `logcat-V0.1.3-win-x64.zip` | 只需 Windows | 约 120 MB |
| **框架依赖** `logcat-V0.1.3-win-x64-fd.zip` | 须预装 .NET 10 Desktop Runtime | 约 1.8 MB |

### 1. 先删干净目标目录

⚠️ `dotnet publish` **不清空目标目录**，旧文件会被一起打进 zip。

```powershell
Remove-Item -Recurse -Force publish\Release\V0.1.3-selfcontained
Remove-Item -Recurse -Force publish\Release\V0.1.3-fdd
```

### 2. 发布自包含包

```powershell
dotnet publish logcat.csproj -c Release `
  -p:SelfContained=true -p:RuntimeIdentifier=win-x64 `
  -p:PublishDir=publish\Release\V0.1.3-selfcontained\

Compress-Archive -Path publish\Release\V0.1.3-selfcontained\* `
  -DestinationPath publish\Release\logcat-V0.1.3-win-x64.zip -Force
```

⚠️ **不要加 `-p:PublishSingleFile=true`**，单文件 exe 实测启动失败。

### 3. 发布框架依赖包

同一个 csproj，不带 `-p:SelfContained`（csproj 默认 `false`）：

```powershell
dotnet publish logcat.csproj -c Release `
  -p:RuntimeIdentifier=win-x64 `
  -p:PublishDir=publish\Release\V0.1.3-fdd\

Compress-Archive -Path publish\Release\V0.1.3-fdd\* `
  -DestinationPath publish\Release\logcat-V0.1.3-win-x64-fd.zip -Force
```

### 4. 验证产物

```powershell
# 自包含：runtimeconfig 无外部框架声明 + coreclr.dll 在包内
Select-String -Path publish\Release\V0.1.3-selfcontained\logcat.runtimeconfig.json -Pattern '"frameworks"'
Test-Path publish\Release\V0.1.3-selfcontained\coreclr.dll

# 框架依赖：runtimeconfig 应声明依赖，且包内【无】coreclr.dll
Select-String -Path publish\Release\V0.1.3-fdd\logcat.runtimeconfig.json -Pattern 'Microsoft.WindowsDesktop.App'
Test-Path publish\Release\V0.1.3-fdd\coreclr.dll   # 应为 False

# 两个包都要查有没有混进测试产物和 pdb
Select-String -Path publish\Release\V0.1.3-*\*\*.* -Pattern 'xunit|coverlet|TestPlatform' -List
```

预期：自包含约 120 MB、`frameworks` 匹配不到（字段为空）、`coreclr.dll` 存在；
框架依赖约 1.8 MB、`coreclr.dll` 不存在；两者都无测试产物匹配项。

验证**能否启动**必须在另一台没装 .NET 的机器上做（验证框架依赖包则反过来，
在一台**没装** .NET 的机器上双击应报错）。本机不适合做这个验证。

### 5. 如果用 VS 发布面板

`Properties/PublishProfiles/FolderProfile.pubxml` 不入库，改版本后需同步其中的
`<PublishDir>`，否则会发到旧目录。

## 两包的取舍

| | 自包含 | 框架依赖 |
|---|---|---|
| 体积 | 120 MB（zip 约 48 MB） | 1.8 MB |
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
| 发出去的 zip 是旧版 | `FolderProfile.pubxml` 的 `<PublishDir>` 没同步 |
| 两个包内容一样大 | 框架依赖那条命令漏了 `-p:SelfContained` 的对比检查，确认 runtimeconfig 是否声明了 frameworks |
