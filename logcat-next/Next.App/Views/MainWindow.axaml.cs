using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Next.App.Controls;
using Next.App.Localization;
using Next.App.Selection;
using Next.App.Services;
using Next.App.Storage;
using Next.App.ViewModels;

namespace Next.App.Views;

public partial class MainWindow : Window
{
    const double RowHeight = LogCanvas.RowHeightPublic;

    readonly MainViewModel _vm = new();
    readonly RowSelection _selection = new();
    readonly SelectionGesture _gesture;
    bool _copying;

    public MainWindow()
    {
        InitializeComponent();
        LayoutHeader();
        _gesture = new SelectionGesture(_selection);
        Canvas.Selection = _selection;
        DataContext = _vm;
        Scroller.ScrollChanged += OnScrollChanged;
        // 滚动容器会先处理指针事件（滚动 / 焦点），用 handledEventsToo 保证仍能拿到点击做选行
        Scroller.AddHandler(PointerPressedEvent, OnLogPointerPressed,
            RoutingStrategies.Bubble, handledEventsToo: true);
        Scroller.AddHandler(Gestures.DoubleTappedEvent, OnLogDoubleTapped,
            RoutingStrategies.Bubble, handledEventsToo: true);
        SizeChanged += (_, _) => UpdateViewport();
        KeyDown += OnKeyDown;
        LocalizationService.Instance.PropertyChanged += OnLanguageChanged;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    /// <summary>表头位置与自绘列起点同源，避免两套常量漂移（列宽见 LogCanvas.ColumnStarts）。</summary>
    void LayoutHeader()
    {
        TextBlock[] labels = [HeaderNum, HeaderTime, HeaderPid, HeaderTid, HeaderLevel, HeaderTag, HeaderMessage];
        for (var i = 0; i < labels.Length; i++)
        {
            Avalonia.Controls.Canvas.SetLeft(labels[i], LogCanvas.TextPadding + LogCanvas.ColumnStarts[i]);
        }
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

    void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Provider))
        {
            // 重新装载 / 结果集变化：清空选择与锚点，不残留指向旧记录的选择（spec 行选择）
            _gesture.Reset();
            if (_vm.Provider is not null)
            {
                Scroller.Focus();   // 装载完成后焦点交回日志区
            }
        }
        Dispatcher.UIThread.Post(UpdateViewport);
    }

    async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.End && e.KeyModifiers.HasFlag(KeyModifiers.Control) && Scroller.Extent.Height > 0)
        {
            // 跳转文件尾部：滚动到最大偏移，画布按新视口取尾页
            Scroller.Offset = new Vector(Scroller.Offset.X, Scroller.Extent.Height - Scroller.Viewport.Height);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            await CopySelectionAsync(e);
        }
    }

    /// <summary>Ctrl+C：无选中不消费按键；成功 / 取数失败 / 剪贴板失败各自落状态栏（design D6）。</summary>
    async Task CopySelectionAsync(KeyEventArgs e)
    {
        if (_selection.IsEmpty || _vm.Provider is not { } provider)
        {
            return;
        }
        e.Handled = true;
        if (_copying)
        {
            return;
        }
        _copying = true;
        try
        {
            var clipboard = Clipboard;
            if (clipboard is null)
            {
                _vm.ShowCopyClipboardFailed();
                return;
            }
            var result = await ClipboardCopy.CopyAsync(_selection, provider, new ClipboardSink(clipboard));
            switch (result.Outcome)
            {
                case CopyOutcome.Success:
                    _vm.ShowCopied(result.RowCount);
                    break;
                case CopyOutcome.FetchFailed:
                    _vm.ShowCopyFetchFailed();
                    break;
                case CopyOutcome.ClipboardFailed:
                    _vm.ShowCopyClipboardFailed();
                    break;
            }
        }
        finally
        {
            _copying = false;
        }
    }

    void OnLogPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (FromScrollBar(e.Source))
        {
            return;   // 滚动条 / 滑块上的按压不进入选行（design D2）
        }
        var point = e.GetCurrentPoint(Scroller);
        // 触摸与触控板轻点统一走同一指针路径；只排除右 / 中键
        if (point.Properties.PointerUpdateKind is PointerUpdateKind.RightButtonPressed
            or PointerUpdateKind.MiddleButtonPressed)
        {
            return;
        }
        var kind = SelectionClickKind.Plain;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            kind = SelectionClickKind.Toggle;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            kind = SelectionClickKind.Extend;
        }
        if (_gesture.Click(HitTestRow(e.GetPosition(Scroller)), kind))
        {
            Scroller.Focus();
        }
    }

    async void OnLogDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (FromScrollBar(e.Source))
        {
            return;
        }
        var row = HitTestRow(e.GetPosition(Scroller));
        if (!SelectionHitTest.ShouldOpenDetail(
                row,
                e.KeyModifiers.HasFlag(KeyModifiers.Control),
                e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
        {
            return;
        }
        // 只用已渲染的缓存行（不再发起取数）；标题取源文件行号（# 列）
        var record = _vm.Provider?.TryGetCachedRow(row!.Value);
        if (record is null)
        {
            return;
        }
        var title = string.Format(LocalizationService.Instance["Detail.Title"], record.Ord + 1);
        await new RecordDetailWindow(title, CopyTextBuilder.RowText(record)).ShowDialog(this);
        Scroller.Focus();
    }

    long? HitTestRow(Point point) => SelectionHitTest.RowAt(
        Scroller.Offset.Y,
        point.Y,
        RowHeight,
        _vm.TotalRows > (ulong)long.MaxValue ? long.MaxValue : (long)_vm.TotalRows,
        point.X,
        Scroller.Viewport.Width);

    /// <summary>事件是否源自滚动条（含滑块）：覆盖式滚动条不占视口宽度，需按源判断。</summary>
    static bool FromScrollBar(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is ScrollBar)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>把 Avalonia 剪贴板适配为可注入的写入探针。</summary>
    sealed class ClipboardSink(IClipboard clipboard) : IClipboardSink
    {
        public Task SetTextAsync(string text) => clipboard.SetTextAsync(text);
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
            AppStorage.Settings.SetTheme(theme);
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
