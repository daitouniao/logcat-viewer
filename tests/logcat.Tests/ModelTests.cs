using logcat.Models;
using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>模型层与纯静态工具（FilterSpec 描述、模型 ToString、AppInfo、AdbManager 的纯函数）。</summary>
public class ModelTests
{
    // ── FilterSpec.IsEmpty ──

    [Fact]
    public void 默认条件为空()
    {
        Assert.True(new FilterSpec().IsEmpty());
    }

    [Fact]
    public void 传全集级别仍视为空条件()
    {
        Assert.True(new FilterSpec { Levels = new[] { 7, 3, 1, 5, 2, 6, 4 } }.IsEmpty());
    }

    [Theory]
    [InlineData("levels")]
    [InlineData("tags")]
    [InlineData("msg")]
    [InlineData("pids")]
    [InlineData("tids")]
    [InlineData("minutes")]
    [InlineData("marked")]
    public void 任一条件非空则不为空(string kind)
    {
        var spec = new FilterSpec();
        switch (kind)
        {
            case "levels": spec.Levels = new[] { LogParser.LVL_E }; break;
            case "tags": spec.Tags = new[] { "a" }; break;
            case "msg": spec.Msg = new[] { "a" }; break;
            case "pids": spec.Pids = new[] { 1 }; break;
            case "tids": spec.Tids = new[] { 1 }; break;
            case "minutes": spec.Minutes = new[] { 1 }; break;
            case "marked": spec.MarkedOnly = true; break;
        }
        Assert.False(spec.IsEmpty());
    }

    // ── FilterSpec.Describe ──

    [Fact]
    public void Describe空条件()
    {
        Assert.Equal("（无过滤）", new FilterSpec().Describe());
    }

    [Fact]
    public void Describe级别按字母序拼接()
    {
        Assert.Equal("级别 VE", new FilterSpec { Levels = new[] { LogParser.LVL_E, LogParser.LVL_V } }.Describe());
    }

    [Fact]
    public void Describe各级别名()
    {
        Assert.Equal("级别 W", new FilterSpec { Levels = new[] { LogParser.LVL_W } }.Describe());
    }

    [Fact]
    public void Describetag与排除标记()
    {
        Assert.Equal("tag OR(a, b)", new FilterSpec { Tags = new[] { "a", "b" } }.Describe());
        Assert.Equal("tag! AND(a)", new FilterSpec { Tags = new[] { "a" }, TagOp = "and", TagExclude = true }.Describe());
    }

    [Fact]
    public void Describemessage与排除标记()
    {
        Assert.Equal("msg AND(x)", new FilterSpec { Msg = new[] { "x" } }.Describe());
        Assert.Equal("msg! OR(x)", new FilterSpec { Msg = new[] { "x" }, MsgOp = "or", MsgExclude = true }.Describe());
    }

    [Fact]
    public void Describepid与tid()
    {
        Assert.Equal("pid(1, 2)", new FilterSpec { Pids = new[] { 1, 2 } }.Describe());
        Assert.Equal("pid!(1)", new FilterSpec { Pids = new[] { 1 }, PidExclude = true }.Describe());
        Assert.Equal("tid(3)", new FilterSpec { Tids = new[] { 3 } }.Describe());
        Assert.Equal("tid!(3)", new FilterSpec { Tids = new[] { 3 }, TidExclude = true }.Describe());
    }

    [Fact]
    public void Describe分钟与仅标记行()
    {
        Assert.Equal("分钟(0, 1)", new FilterSpec { Minutes = new[] { 0, 1 } }.Describe());
        Assert.Equal("仅标记行", new FilterSpec { MarkedOnly = true }.Describe());
    }

    [Fact]
    public void Describe多条件用分号连接()
    {
        var spec = new FilterSpec
        {
            Levels = new[] { LogParser.LVL_E },
            Tags = new[] { "T" },
            Msg = new[] { "m" },
            Pids = new[] { 9 },
            MarkedOnly = true,
        };
        Assert.Equal("级别 E；tag OR(T)；msg AND(m)；pid(9)；仅标记行", spec.Describe());
    }

    // ── DeviceInfo ──

    [Fact]
    public void DeviceInfo在无机型时只显示序列号()
    {
        Assert.Equal("S1", new DeviceInfo { Serial = "S1" }.ToString());
        Assert.Equal("S1", new DeviceInfo { Serial = "S1", Model = "S1" }.ToString());
    }

    [Fact]
    public void DeviceInfo在有机型时带括号显示()
    {
        Assert.Equal("Pixel_7 (S1)", new DeviceInfo { Serial = "S1", Model = "Pixel_7" }.ToString());
    }

    [Fact]
    public void DeviceInfo带root标记()
    {
        Assert.Equal("Pixel_7 (S1) [root]", new DeviceInfo { Serial = "S1", Model = "Pixel_7", IsRoot = true }.ToString());
    }

    // ── FavoriteDir ──

    [Fact]
    public void FavoriteDir无别名时显示路径()
    {
        var dir = new FavoriteDir { Path = "/sdcard" };
        Assert.Equal("/sdcard", dir.Display);
        Assert.Equal("/sdcard", dir.ToString());
    }

    [Fact]
    public void FavoriteDir有别名时显示别名与路径()
    {
        var dir = new FavoriteDir { Path = "/sdcard", Alias = "存储" };
        Assert.Equal("存储   [/sdcard]", dir.Display);
    }

    [Fact]
    public void FavoriteDir别名全空白视为无别名()
    {
        Assert.Equal("/sdcard", new FavoriteDir { Path = "/sdcard", Alias = "   " }.Display);
    }

    // ── CommandEntry / CommandKinds ──

    [Fact]
    public void CommandEntry的ToString是命令正文()
    {
        Assert.Equal("ls -l", new CommandEntry { Command = "ls -l" }.ToString());
        Assert.Equal("ls -l", new CommandRun { Command = "ls -l" }.ToString());
    }

    [Fact]
    public void CommandKind显示名()
    {
        Assert.Equal("设备 shell", CommandKind.Shell.Text());
        Assert.Equal("本机 adb", CommandKind.Adb.Text());
        Assert.Equal(CommandKind.Adb, CommandKinds.Of(CommandKinds.AdbText));
        Assert.Equal(CommandKind.Shell, CommandKinds.Of(CommandKinds.ShellText));
        Assert.Equal(CommandKind.Shell, CommandKinds.Of(null));
        Assert.Equal(CommandKind.Shell, CommandKinds.Of("无法识别的通道"));
    }

    [Fact]
    public void CommandKindsDescribe展示完整调用形式()
    {
        Assert.Equal("adb devices", CommandKinds.Describe(CommandKind.Adb, "devices", false));
        Assert.Equal("adb devices", CommandKinds.Describe(CommandKind.Adb, "devices", true));   // adb 通道不看 root
        Assert.Equal("adb shell ls", CommandKinds.Describe(CommandKind.Shell, "ls", false));
        Assert.Equal("adb shell su -c ls", CommandKinds.Describe(CommandKind.Shell, "ls", true));
    }

    // ── AppSettings 默认值 ──

    [Fact]
    public void AppSettings默认值()
    {
        var s = new AppSettings();

        Assert.True(s.Join);
        Assert.False(s.SingleLineExport);
        Assert.True(s.AutoApply);
        Assert.Equal("↵", s.NewlineVis);
        Assert.Equal(10, s.FontPt);

        Assert.Equal("/sdcard", s.LastRemotePath);
        Assert.Equal("", s.LastLocalPath);
        Assert.Equal("/sdcard/Download", s.LastRunAsRelayDir);

        Assert.False(s.ApkInstallViaPm);
        Assert.Equal("-r", s.ApkInstallFlags);
        Assert.Equal("/data/local/tmp", s.ApkTmpDir);
        Assert.False(s.ApkUninstallViaPm);
        Assert.False(s.ApkKeepData);
        Assert.False(s.ApkUseRoot);

        Assert.Equal(0, s.WindowState);
    }

    [Fact]
    public void AppSettings属性可读写()
    {
        var s = new AppSettings
        {
            Join = false,
            FontPt = 14,
            WindowLocation = new System.Drawing.Point(10, 20),
            WindowSize = new System.Drawing.Size(800, 600),
            ApkUseRoot = true,
        };

        Assert.False(s.Join);
        Assert.Equal(14, s.FontPt);
        Assert.Equal(new System.Drawing.Point(10, 20), s.WindowLocation);
        Assert.Equal(new System.Drawing.Size(800, 600), s.WindowSize);
        Assert.True(s.ApkUseRoot);
    }

    // ── FileEntry ──

    [Fact]
    public void FileEntry默认值()
    {
        var e = new FileEntry();

        Assert.Equal("", e.Name);
        Assert.Equal("", e.Path);
        Assert.False(e.IsDir);
        Assert.Equal(0, e.Size);
        Assert.Equal("", e.Permissions);
        Assert.Equal("", e.Owner);
        Assert.Equal("", e.DateStr);
    }

    [Fact]
    public void FileEntry字段可读写()
    {
        var e = new FileEntry
        {
            Name = "app.log",
            Path = "/sdcard/app.log",
            IsDir = false,
            Size = 2048,
            Permissions = "-rw-r--r--",
            Owner = "root",
            DateStr = "2026-09-23 18:00",
        };

        Assert.Equal("app.log", e.Name);
        Assert.Equal("/sdcard/app.log", e.Path);
        Assert.False(e.IsDir);
        Assert.Equal(2048, e.Size);
        Assert.Equal("-rw-r--r--", e.Permissions);
        Assert.Equal("root", e.Owner);
        Assert.Equal("2026-09-23 18:00", e.DateStr);
    }

    // ── AppSettings 单例 ──

    /// <summary>
    /// Default 只读用户目录下的 settings.json（测试不写入，无副作用），
    /// 同时覆盖「文件存在 → 反序列化」与「不存在/损坏 → 回退默认值」两条路径。
    /// </summary>
    [Fact]
    public void AppSettings默认实例是进程内单例()
    {
        var a = AppSettings.Default;
        var b = AppSettings.Default;

        Assert.NotNull(a);
        Assert.Same(a, b);
        Assert.NotNull(a.LastRemotePath);
    }

    // ── AppInfo ──

    [Fact]
    public void AppInfo版本与标题由程序集版本派生()
    {
        Assert.Equal("logcat viewer", AppInfo.ProductName);
        Assert.False(string.IsNullOrWhiteSpace(AppInfo.Version));
        Assert.Equal("V" + AppInfo.Version, AppInfo.DisplayVersion);
        Assert.Equal($"{AppInfo.ProductName} {AppInfo.DisplayVersion}", AppInfo.Title);
    }
}

/// <summary>AdbManager 里不依赖真实设备的纯函数。</summary>
public class AdbManagerStaticTests
{
    [Theory]
    [InlineData("ls", "'ls'")]
    [InlineData("a b", "'a b'")]
    [InlineData("", "''")]
    [InlineData(null, "''")]
    public void ShellQuote用单引号包裹(string? input, string expected)
    {
        Assert.Equal(expected, AdbManager.ShellQuote(input!));
    }

    [Fact]
    public void ShellQuote转义内部单引号()
    {
        // 内层单引号按 '\'' 规则闭合再重开，路径含空格时才不会被截断
        Assert.Equal(@"'a'\''b'", AdbManager.ShellQuote("a'b"));
    }

    [Theory]
    [InlineData("devices -l", "S1", "devices -l")]
    [InlineData("version", "S1", "version")]
    [InlineData("help", "S1", "help")]
    [InlineData("start-server", "S1", "start-server")]
    [InlineData("kill-server", "S1", "kill-server")]
    [InlineData("connect 1.2.3.4:5555", "S1", "connect 1.2.3.4:5555")]
    [InlineData("disconnect", "S1", "disconnect")]
    [InlineData("pair", "S1", "pair")]
    public void AdbArgs对主机级子命令不补设备号(string command, string serial, string expected)
    {
        Assert.Equal(expected, AdbManager.AdbArgs(command, serial));
    }

    [Theory]
    [InlineData("shell ls", "S1", "-s S1 shell ls")]
    [InlineData("reboot", "S1", "-s S1 reboot")]
    [InlineData("install -r a.apk", "S1", "-s S1 install -r a.apk")]
    [InlineData("uninstall com.a", "S1", "-s S1 uninstall com.a")]
    [InlineData("push a b", "S1", "-s S1 push a b")]
    public void AdbArgs对设备级子命令补设备号(string command, string serial, string expected)
    {
        Assert.Equal(expected, AdbManager.AdbArgs(command, serial));
    }

    [Fact]
    public void AdbArgs无设备号时原样返回()
    {
        Assert.Equal("shell ls", AdbManager.AdbArgs("shell ls", null));
        Assert.Equal("shell ls", AdbManager.AdbArgs("shell ls", ""));
    }

    [Fact]
    public void AdbArgs只判空不判空白设备号()
    {
        // 只有 null / 空串才算「没有设备号」，纯空白会被当作设备号拼进去
        Assert.Equal("-s     shell ls", AdbManager.AdbArgs("shell ls", "   "));
    }

    [Fact]
    public void AdbArgs空命令返回空()
    {
        Assert.Equal("", AdbManager.AdbArgs("", "S1"));
        Assert.Equal("", AdbManager.AdbArgs("   ", "S1"));
        Assert.Equal("", AdbManager.AdbArgs(null!, "S1"));
    }

    [Fact]
    public void AdbArgs主机级判断忽略大小写()
    {
        Assert.Equal("DEVICES", AdbManager.AdbArgs("DEVICES", "S1"));
    }

    [Fact]
    public void AdbArgs支持制表符分隔()
    {
        Assert.Equal("-s S1 shell", AdbManager.AdbArgs("shell\tls", "S1").Split('\t')[0]);
    }

    [Fact]
    public void AdbArgs会去掉命令首尾空白()
    {
        Assert.Equal("-s S1 shell ls", AdbManager.AdbArgs("  shell ls  ", "S1"));
    }
}
