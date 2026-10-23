using System.Globalization;
using System.Runtime.CompilerServices;

namespace logcat.Tests;

/// <summary>
/// 按 <c>LOGCAT_FORCE_UI_CULTURE</c> 在测试进程内钉住区域，用于本地复现 CI 的英文界面。
///
/// <para>
/// 为什么不靠 <c>LANG</c>/<c>LC_ALL</c>：2026-10-24 实测，Windows 上这两个变量以及
/// <c>DOTNET_CLI_UI_LANGUAGE</c> 都不改变 <c>CultureInfo.CurrentUICulture</c>
/// （不设 zh-CN，设了仍 zh-CN），只在 Unix 生效。若沿用环境变量方案，Windows 上跑全量
/// 实际仍是中文界面却显示全绿——正是本 change 要消灭的假绿。
/// </para>
///
/// <para>
/// 模块初始化在测试程序集加载时执行，早于任何用例访问 <c>Loc</c>（<c>Loc</c> 的语言
/// 只在静态构造期探测一次），因此设在这里能可靠生效。设完再做一次自检，不一致立即抛错，
/// 让整轮测试变红而不是静默退回中文。
/// </para>
/// </summary>
internal static class TestCultureBootstrap
{
    /// <summary>脚本 / CI 约定使用的环境变量名；未设置时保持系统默认区域。</summary>
    internal const string CultureVariable = "LOGCAT_FORCE_UI_CULTURE";

    [ModuleInitializer]
    internal static void ApplyForcedCulture()
    {
        var name = Environment.GetEnvironmentVariable(CultureVariable);
        if (string.IsNullOrWhiteSpace(name))
            return;

        var culture = CultureInfo.GetCultureInfo(name.Trim());

        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        if (!string.Equals(CultureInfo.CurrentUICulture.Name, culture.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"无法把测试进程的界面语言切到 {culture.Name}（实际 {CultureInfo.CurrentUICulture.Name}）");
        }
    }
}
