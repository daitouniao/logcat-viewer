using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Next.App.Localization;
using Next.App.Storage;

namespace Next.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // 语言与主题在首窗前生效（spec next/ui-language、next/ui-theme：启动即呈现所选状态）
        var settings = AppStorage.Settings.Value;
        LocalizationService.Instance.Initialize(settings);
        ApplyTheme(settings.Theme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new Views.MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>主题三态：light / dark / system（跟随系统 = Default 变体）。未知值回退 Default。</summary>
    public static void ApplyTheme(string? theme)
    {
        Current!.RequestedThemeVariant = theme switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
