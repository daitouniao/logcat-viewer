using System.Reflection;

namespace logcat.Services;

/// <summary>
/// 应用级信息。版本号以 logcat.csproj 的 &lt;Version&gt; 为唯一来源，
/// 编译期写入程序集 InformationalVersion，运行时在此读取，发布时只需改 csproj 一处。
/// </summary>
public static class AppInfo
{
    public const string ProductName = "logcat viewer";

    /// <summary>纯版本号，如 "0.02"（来自程序集 InformationalVersion）。</summary>
    public static string Version { get; } =
        Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? "0.00";

    /// <summary>带前缀的显示版本号，如 "V0.02"。</summary>
    public static string DisplayVersion => "V" + Version;

    /// <summary>应用标题（不含文件名），如 "logcat viewer V0.02"。</summary>
    public static string Title => $"{ProductName} {DisplayVersion}";
}
