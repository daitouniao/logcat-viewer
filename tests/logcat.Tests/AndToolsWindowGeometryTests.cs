using System.Drawing;
using System.Windows.Forms;
using logcat.AndTools;
using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// 工具箱窗口几何持久化（tasks 2.3 / design D7 / N11，spec android-toolbox「工具箱窗口几何持久化」）：
/// 保存口径（最大化存 RestoreBounds、最小化不持久化）与恢复校验（屏内、宽度合理）。
/// 与日志查看器几何互不干扰由「三键落 andtools.json」保证（LogcatSettings 无这些键的同名存储路径）。
/// </summary>
public class AndToolsWindowGeometryTests
{
    static readonly Rectangle Primary = new(0, 0, 1920, 1040);
    static readonly Rectangle[] Screens = { Primary, new(1920, 0, 1920, 1040) };

    // ── 保存口径 ──

    [Fact]
    public void 正常态关闭_保存当前Bounds与Normal()
    {
        var bounds = new Rectangle(100, 50, 1200, 800);
        var restore = new Rectangle(100, 50, 1200, 800);

        var (loc, size, state) = AndToolsWindowGeometry.Capture(
            bounds, restore, FormWindowState.Normal, Primary);

        Assert.Equal(new Point(100, 50), loc);
        Assert.Equal(new Size(1200, 800), size);
        Assert.Equal((int)FormWindowState.Normal, state);
    }

    [Fact]
    public void 最大化关闭_保存RestoreBounds与Maximized()
    {
        var restore = new Rectangle(30, 40, 1100, 700);

        var (loc, size, state) = AndToolsWindowGeometry.Capture(
            new Rectangle(-8, -8, 1936, 1056),   // 最大化时 Bounds 是虚拟桌面包围盒
            restore, FormWindowState.Maximized, Primary);

        Assert.Equal(new Point(30, 40), loc);
        Assert.Equal(new Size(1100, 700), size);
        Assert.Equal((int)FormWindowState.Maximized, state);
    }

    [Fact]
    public void 最小化关闭_不持久化_按主屏三分之二默认()
    {
        var (loc, size, state) = AndToolsWindowGeometry.Capture(
            new Rectangle(0, 0, 100, 100), new Rectangle(0, 0, 100, 100),
            FormWindowState.Minimized, Primary);

        Assert.Equal((int)FormWindowState.Normal, state);
        Assert.Equal((int)(1920 * 0.667), size.Width);
        Assert.Equal((int)(1040 * 0.667), size.Height);
        Assert.Equal(Primary.Left + (Primary.Width - size.Width) / 2, loc.X);
    }

    // ── 恢复校验 ──

    [Fact]
    public void 恢复校验_几何落在已知屏内且宽度合理_通过()
    {
        var ok = AndToolsWindowGeometry.TryRead(
            new Point(100, 50), new Size(1200, 800), (int)FormWindowState.Normal,
            Screens, Primary, out var bounds, out var state);

        Assert.True(ok);
        Assert.Equal(new Rectangle(100, 50, 1200, 800), bounds);
        Assert.Equal(FormWindowState.Normal, state);
    }

    [Fact]
    public void 恢复校验_副屏已断开_几何弃用()
    {
        // 上次在右侧副屏，这次该屏未连接：仅剩主屏，相交校验不成立
        var ok = AndToolsWindowGeometry.TryRead(
            new Point(2100, 50), new Size(1200, 800), (int)FormWindowState.Normal,
            new[] { Primary }, Primary, out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void 恢复校验_状态值越界_弃用()
    {
        var ok = AndToolsWindowGeometry.TryRead(
            new Point(100, 50), new Size(1200, 800), stateValue: 3,
            Screens, Primary, out _, out _);

        Assert.False(ok);
    }
}
