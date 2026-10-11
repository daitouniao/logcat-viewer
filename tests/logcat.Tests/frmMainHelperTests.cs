using System.Globalization;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// <c>frmMain</c> 里的纯函数：<c>IsNetworkPath</c> / <c>EllipsizeMiddle</c> / <c>HumanSize</c>。
///
/// <para>
/// <b>为什么要测frmMain 里的东西</b>：这三个都是无窗体、无状态的纯函数，被覆盖率口径
/// （coverlet.runsettings 排除整个 <c>Forms.*</c> / <c>frmMain*</c>）排除在行覆盖率之外，
/// 但它们是有明确契约的逻辑：<c>IsNetworkPath</c> 决定要不要对路径做 <c>File.Exists</c>
/// 探测（错了会让「最近打开」菜单对不可达UNC 阻塞 UI 数秒），<c>EllipsizeMiddle</c> 决定
/// 路径显示会不会丢首尾，<c>HumanSize</c> 决定状态栏体积文案。判据是「能否在无窗体条件下
/// 被有意义地验证」——能，就该有测试。
/// </para>
///
/// <para>
/// <b>对应关系</b>：<c>RebuildRecentMenu</c>（菜单重建）、<c>_lblFile.Text</c>（状态栏路径+体积）
/// 是它们仅有的两个消费点，都是 UI 胶水，不测。
/// </para>
///
/// <para>
/// 三个函数都是 <c>internal static</c>（主工程已配<code>InternalsVisibleTo("logcat.Tests")</code>），
/// 不构造窗体、不碰 STA，因此不需要 UI harness。改动可见性时记得同步这里的调用。
/// </para>
/// </summary>
public class frmMainHelperTests
{
    // ── IsNetworkPath ──

    /// <summary>
    /// UNC 前缀（含 <c>\\?\</c> 扩展前缀）一律判为网络路径——这是核心契约：
    /// 判错会对 SMB 路径调 <c>File.Exists</c>，不可达时 UI 阻塞数秒。
    /// <c>\\.\PIPE\</c> 也归入此类：它同样以 <c>\\</c> 开头，行为上等价（不做存在性探测）。
    /// </summary>
    [Theory]
    [InlineData(@"\\server\share\a.log")]
    [InlineData(@"\\?\UNC\server\share\a.log")]
    [InlineData(@"\\.\PIPE\x")]
    [InlineData(@"\\")]
    public void IsNetworkPath_UNC前缀判为网络(string path)
    {
        Assert.True(frmMain.IsNetworkPath(path));
    }

    /// <summary>
    /// <c>\\?\C:\x.log</c>（扩展前缀 + 本地盘）按前缀规则判为网络。
    /// 记录的是<b>当前实现行为</b>：判为网络只是「不做存在性探测」，不会误开文件，
    /// 代价至多是菜单里少显示一条本机文件，可接受，不值得为它加特判。
    /// </summary>
    [Fact]
    public void IsNetworkPath_扩展前缀加本地盘按UNC规则判为网络()
    {
        Assert.True(frmMain.IsNetworkPath(@"\\?\C:\logs\a.log"));
    }

    /// <summary>
    /// 空/空白/null 一律 false。<b>必须</b>先短路：<see cref="Path.GetPathRoot"/> 对
    /// null 会抛 <c>ArgumentNullException</c>，不短路就会把空白路径变成异常而不是「非网络」。
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void IsNetworkPath_空白路径判为本地(string path)
    {
        Assert.False(frmMain.IsNetworkPath(path));
    }

    [Fact]
    public void IsNetworkPath_null判为本地()
    {
        Assert.False(frmMain.IsNetworkPath(null!));
    }

    /// <summary>相对路径无盘根（<c>GetPathRoot</c> 返回空串）→ 按本地处理，仍会做存在性探测。</summary>
    [Theory]
    [InlineData(@"logs\a.log")]
    [InlineData("a.log")]
    [InlineData(@"sub\dir\a.log")]
    public void IsNetworkPath_无盘根相对路径判为本地(string path)
    {
        Assert.False(frmMain.IsNetworkPath(path));
    }

    /// <summary>真实存在的本机绝对路径 → 非网络。这是「本机日志文件走正常探测路径」的判据。</summary>
    [Fact]
    public void IsNetworkPath_本机绝对路径判为本地()
    {
        using var tmp = new TempLogFile("x");
        Assert.False(frmMain.IsNetworkPath(tmp.Path));
    }

    /// <summary>
    /// 不存在的盘符（<c>DriveInfo.DriveType == NoRootDirectory</c>）→ 非网络。
    /// 用 <c>Z:</c>：测试机上是空槽（实测 <c>NoRootDirectory</c>）。
    /// </summary>
    [Theory]
    [InlineData(@"Z:\nope\a.log")]
    [InlineData(@"Z:")]
    public void IsNetworkPath_无根盘符判为本地(string path)
    {
        Assert.False(frmMain.IsNetworkPath(path));
    }

    /// <summary>
    /// 单个反斜杠 <c>\"</c> 让 <see cref="Path.GetPathRoot"/> 抛 <c>ArgumentException</c>——
    /// 覆盖 <c>catch { return false; }</c> 那条兜底分支。删掉 try/catch 这条用例会红。
    /// </summary>
    [Fact]
    public void IsNetworkPath_非法根抛异常时兜底判为本地()
    {
        Assert.False(frmMain.IsNetworkPath(@"\"));
    }

    // ── EllipsizeMiddle ──

    /// <summary>长度 ≤ max 原样返回（不做任何裁剪，也不引入省略号）。</summary>
    [Theory]
    [InlineData("", 80)]
    [InlineData("a", 80)]
    [InlineData(@"C:\logs", 80)]
    public void EllipsizeMiddle_不超长时原样返回(string s, int max)
    {
        Assert.Equal(s, frmMain.EllipsizeMiddle(s, max));
    }

    /// <summary>
    /// 边界：长度<b>恰好等于</b> max 不裁剪。写成 <c>&lt;</c> 而非 <c>&lt;=</c> 的变异
    /// （会在等长时多插一个省略号）会被这条抓住。
    /// </summary>
    [Fact]
    public void EllipsizeMiddle_恰好等于上限时不裁剪()
    {
        var s = new string('x', 80);
        Assert.Equal(s, frmMain.EllipsizeMiddle(s, 80));
    }

    /// <summary>
    /// 超长时输出长度<b>恒等于</b> max：<c>keep = max - 1</c>（给省略号留 1 位），
    /// 头尾再瓜分 keep。这是菜单显示不撑破布局的核心约束。
    /// </summary>
    [Theory]
    [InlineData(81, 80)]
    [InlineData(200, 80)]
    [InlineData(10, 4)]
    [InlineData(5, 2)]
    [InlineData(200, 3)]
    public void EllipsizeMiddle_超长时输出长度恒等于上限(int len, int max)
    {
        var s = new string('x', len);

        var r = frmMain.EllipsizeMiddle(s, max);

        Assert.Equal(max, r.Length);
        Assert.Equal(1, r.Count(c => c == '…'));       // 恰好一个省略号
    }

    /// <summary>
    /// 首尾各保留一半（<c>head = keep / 2</c> 取整除，多出来的那 1 个字符归尾部）。
    /// 用可辨识字符断言具体切分位置，而不只是断言长度——长度对但切错位置（只留头不留尾）
    /// 在菜单里是「看不出文件名」的实际问题。
    /// </summary>
    [Fact]
    public void EllipsizeMiddle_保留首尾两端()
    {
        var s = "HEAD" + new string('x', 100) + "TAIL";

        var r = frmMain.EllipsizeMiddle(s, 80);

        Assert.Equal(80, r.Length);
        Assert.StartsWith("HEAD", r);
        Assert.EndsWith("TAIL", r);
        Assert.Equal(39, r.IndexOf('…'));              // keep=79，head=79/2=39，尾 40
    }

    /// <summary>尾部比头部多 1 个字符时（keep 为奇数），多的那个归尾部。</summary>
    [Fact]
    public void EllipsizeMiddle_奇数容量时尾部多留一位()
    {
        var s = new string('h', 10) + new string('t', 90);

        var r = frmMain.EllipsizeMiddle(s, 80);

        Assert.Equal(80, r.Length);
        Assert.Equal('…', r[39]);
        Assert.Equal(new string('t', 40), r[40..]);    // 头 39 + 省略号 1 + 尾 40 = 80
    }

    /// <summary>max=1 时 keep=0，输出就是一个省略号（不留任何原字符）。</summary>
    [Fact]
    public void EllipsizeMiddle_上限为1时只留省略号()
    {
        Assert.Equal("…", frmMain.EllipsizeMiddle("abcdef", 1));
    }

    /// <summary>
    /// max=0 且串非空时抛 <c>ArgumentOutOfRangeException</c>：<c>keep = -1</c> →
    /// <c>head = -1/2 = 0</c>，<c>AsSpan(s.Length + 1)</c> 起点越界。
    /// <para>
    /// <b>固化这个行为是有意的</b>：它记录「当前实现没有防御 max=0」，调用点固定传 80 所以
    /// 不可达。若将来给 <c>max=0</c> 加了防御，这条会红——那时该改的是实现而不是删断言。
    /// 空串与 max=0 不抛（走 <c>Length &lt;= max</c> 早退）。
    /// </para>
    /// </summary>
    [Fact]
    public void EllipsizeMiddle_上限为0且非空时抛越界异常()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => frmMain.EllipsizeMiddle("ab", 0));
        Assert.Equal("", frmMain.EllipsizeMiddle("", 0));    // 空串早退，不抛
    }

    /// <summary>
    /// 真实形态回归：菜单里的实际调用是 <c>EllipsizeMiddle(path, 80)</c>。
    /// 用一条 90 字符的真路径（超过 80，确认真的触发了裁剪）确认「根目录 + 文件名」都还在
    /// ——这是用户在菜单里认路径的依据。注意 75/80 字符的路径<b>不会</b>被裁，
    /// 所以这条必须挑足够长的路径，否则断言的是早退分支。
    /// </summary>
    [Fact]
    public void EllipsizeMiddle_真实长路径保留可辨识段()
    {
        var path = @"C:\Users\me\AppData\Local\Temp\gradle-build\app\build\intermediates\verbose-2026-10-11.log";
        Assert.True(path.Length > 80);                    // 前置：确保走的是裁剪分支

        var r = frmMain.EllipsizeMiddle(path, 80);

        Assert.Equal(80, r.Length);
        Assert.Equal(1, r.Count(c => c == '…'));
        Assert.StartsWith(@"C:\Users\me", r);
        Assert.EndsWith(@"verbose-2026-10-11.log", r);      // 文件名必须完整可读
    }

    // ── HumanSize ──

    /// <summary>字节档：整数 + 无小数位，且 1024 恰好进位到 KB（不进位就永远显示不到 KB）。</summary>
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1, "1 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.00 KB")]
    public void HumanSize_字节与KB进位(long n, string expected)
    {
        CultureScope.Invariant(() => Assert.Equal(expected, frmMain.HumanSize(n)));
    }

    /// <summary>
    /// 每个进位点都要能踩到：1024 → KB、1024² → MB、1024³ → GB、1024⁴ → TB。
    /// 单位算错一档（少除/多除 1024）会让整条链全错。
    /// </summary>
    [Theory]
    [InlineData(1536L, "1.50 KB")]
    [InlineData(1048576L, "1.00 MB")]
    [InlineData(1073741824L, "1.00 GB")]
    [InlineData(1099511627776L, "1.00 TB")]
    public void HumanSize_各进位点(long n, string expected)
    {
        CultureScope.Invariant(() => Assert.Equal(expected, frmMain.HumanSize(n)));
    }

    /// <summary>
    /// 刚过一个进位点时仍按<b>旧</b>单位显示，且带.2舍入：1024² - 1 → "1024.00 KB"。
    /// 这看着别扭但是既定行为（先按字节值选单位、再格式化，不做"四舍五入后再选单位"）。
    /// 记下来是为了让改它的人知道这是个有意的选择。
    /// </summary>
    [Fact]
    public void HumanSize_刚过进位点仍用旧单位并保留两位小数()
    {
        CultureScope.Invariant(() => Assert.Equal("1024.00 KB", frmMain.HumanSize(1048575)));
    }

    /// <summary>单位上限是 TB：不溢出到 PB，超过 1 PB 仍显示 TB（long.MaxValue → 8388608.00 TB）。</summary>
    [Fact]
    public void HumanSize_超过TB不再进位()
    {
        CultureScope.Invariant(() =>
        {
            Assert.Equal("1024.00 TB", frmMain.HumanSize(1125899906842624L));   // 1 PB
            Assert.Equal("8388608.00 TB", frmMain.HumanSize(long.MaxValue));
        });
    }

    /// <summary>
    /// 小数点跟随<b>当前区域</b>：de-DE 下是逗号。固化这条是为了说明断言必须
    /// 走 <see cref="CultureScope.Invariant"/>——否则在非英文机器上整片红。
    /// </summary>
    [Fact]
    public void HumanSize_小数点跟随当前区域()
    {
        CultureScope.Invariant(() => Assert.Equal("1.50 KB", frmMain.HumanSize(1536)));
        CultureScope.In(new CultureInfo("de-DE"), () => Assert.Equal("1,50 KB", frmMain.HumanSize(1536)));
    }

    /// <summary>负数（理论不该出现）按字节档原样显示，不抛也不加负号处理。</summary>
    [Fact]
    public void HumanSize_负数按字节档显示()
    {
        CultureScope.Invariant(() => Assert.Equal("-1 B", frmMain.HumanSize(-1)));
    }
}