using Avalonia;
using Next.App.Storage;

namespace Next.App;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // 持久化根与设置服务在 Avalonia 启动前装配一次（design D10），启动期失败即可留痕
        var roots = StorageRootResolver.ResolveDefault();
        AppStorage.Initialize(roots, NextSettingsService.Create(roots));
        StartupLog.Initialize(roots.LogPath);
        StartupLog.Write("INFO",
            $"start root={roots.Root ?? "<session-only>"} sessionOnly={roots.SessionOnly} cache={roots.CacheDir}");

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions());
}
