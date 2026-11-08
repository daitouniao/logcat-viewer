using System;
using System.Diagnostics;
using System.IO;

namespace logcat.Services;

/// <summary>
/// 安卓工具箱启动器（design D8，spec android-toolbox「日志查看器的启动器入口」）：
/// File.Exists 探测同目录 logcat-andtools.exe → 缺失时入口置禁用（悬停提示补齐方向，交互不异常）；
/// 存在则 Process.Start 拉起。已在运行时的前置由工具箱侧唤醒事件处理（二次启动 Set 后自退），这里不硬做。
/// 探测判定与启动调用以委托注入，存在/缺失两分支可单测。
/// </summary>
public static class AndToolsLauncher
{
    internal static Func<string, bool> FileExists = File.Exists;   // 测试注入点
    internal static Action<string> Start = LaunchProcess;          // 测试注入点

    /// <summary>工具箱主程序探测路径：与本程序同目录（发布形态两 exe 同目录）。</summary>
    public static string ExePath => Path.Combine(AppContext.BaseDirectory, "logcat-andtools.exe");

    /// <summary>入口可用性：工具箱主程序存在于同目录。</summary>
    public static bool IsAvailable() => FileExists(ExePath);

    /// <summary>拉起工具箱。存在 → 启动并返回 true；缺失 → 返回 false（调用方负责缺失提示）。</summary>
    public static bool TryLaunch()
    {
        if (!FileExists(ExePath)) return false;
        Start(ExePath);
        return true;
    }

    static void LaunchProcess(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
            WorkingDirectory = AppContext.BaseDirectory,
        });
    }
}
