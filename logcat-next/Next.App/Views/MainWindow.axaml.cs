using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Next.App.Controls;
using Next.App.Localization;
using Next.App.ViewModels;

namespace Next.App.Views;

public partial class MainWindow : Window
{
    const double RowHeight = LogCanvas.RowHeightPublic;

    readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Scroller.ScrollChanged += OnScrollChanged;
        SizeChanged += (_, _) => UpdateViewport();
        KeyDown += OnKeyDown;
        LocalizationService.Instance.PropertyChanged += OnLanguageChanged;
        _vm.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(UpdateViewport);
    }

    void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        Canvas.OffsetY = Scroller.Offset.Y;
        UpdateViewport();
    }

    void UpdateViewport()
    {
        Canvas.ViewportHeight = Scroller.Viewport.Height;
        // 虚拟内容高度 = 行数 × 行高；行数变化或装载完成后重算
        ScrollSpace.Height = _vm.TotalRows > 0 ? _vm.TotalRows * RowHeight : 0;
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.End && e.KeyModifiers.HasFlag(KeyModifiers.Control) && Scroller.Extent.Height > 0)
        {
            // 跳转文件尾部：滚动到最大偏移，画布按新视口取尾页
            Scroller.Offset = new Vector(Scroller.Offset.X, Scroller.Extent.Height - Scroller.Viewport.Height);
            e.Handled = true;
        }
    }

    void OnLanguageClick(object? sender, RoutedEventArgs e)
    {
        if ((e.Source as Control)?.Tag is string lang)
        {
            LocalizationService.Instance.SetLanguage(lang);   // 持久化随 SetLanguage（spec ui-localization）
            _vm.RefreshStatusOnLanguageChanged();
        }
    }

    void OnThemeClick(object? sender, RoutedEventArgs e)
    {
        if ((e.Source as Control)?.Tag is string theme)
        {
            App.ApplyTheme(theme);
            NextSettingsStore.Save(new Localization.NextSettings
            {
                Lang = LocalizationService.Instance.CurrentLanguage,
                Theme = theme,
            });
        }
    }

    async void OnOpenClick(object? sender, RoutedEventArgs e) => await _vm.OpenFileAsync(this);

    void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 索引器通知（"Item"）触发文案刷新；状态栏非绑定路径由 VM 自刷
        if (e.PropertyName == "Item")
        {
            Dispatcher.UIThread.Post(() => _vm.RefreshStatusOnLanguageChanged());
        }
    }
}
