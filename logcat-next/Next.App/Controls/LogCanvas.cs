using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Next.App.Selection;
using Next.App.Services;

namespace Next.App.Controls;

/// <summary>
/// 自绘日志画布（design D5）：定行高、只绘制视口行；不创建逐行控件。颜色只取
/// DesignTokens 语义角色，随主题变体切换重绘（design D3）；字体显式给出中文回退链。
/// </summary>
public class LogCanvas : Control
{
    const string LevelQuestion = "?";
    static readonly string[] LevelLetters = { LevelQuestion, "V", "D", "I", "W", "E", "F", "A" };
    static readonly Typeface TextTypeface = new("Microsoft YaHei UI, Segoe UI");
    public const double RowHeightPublic = 17;
    const double RowHeight = RowHeightPublic;
    const double PadTop = 2;
    const double FontSize = 12;

    // 列起点：# / Time / PID / TID / Lv / Tag / Message（列名与旧查看器一致，域常量不走本地化）
    // 表头与自绘共用这一份常量；Tag 列加宽到 200，避免常见长 TAG 溢出。
    public static readonly double[] ColumnStarts = { 0, 62, 190, 258, 326, 356, 556 };

    /// <summary>文字左内边距；表头定位也用它，保证与数据列同起点。</summary>
    public const double TextPadding = 8;

    public static readonly StyledProperty<LogPageProvider?> ProviderProperty =
        AvaloniaProperty.Register<LogCanvas, LogPageProvider?>(nameof(Provider));

    public static readonly StyledProperty<double> OffsetYProperty =
        AvaloniaProperty.Register<LogCanvas, double>(nameof(OffsetY));

    public static readonly StyledProperty<double> ViewportHeightProperty =
        AvaloniaProperty.Register<LogCanvas, double>(nameof(ViewportHeight));

    public static readonly StyledProperty<IRowSelection?> SelectionProperty =
        AvaloniaProperty.Register<LogCanvas, IRowSelection?>(nameof(Selection));

    public LogPageProvider? Provider
    {
        get => GetValue(ProviderProperty);
        set => SetValue(ProviderProperty, value);
    }

    public double OffsetY
    {
        get => GetValue(OffsetYProperty);
        set => SetValue(OffsetYProperty, value);
    }

    public double ViewportHeight
    {
        get => GetValue(ViewportHeightProperty);
        set => SetValue(ViewportHeightProperty, value);
    }

    /// <summary>选中判定（只读）：选择集合原地变化时重绘（design D3）。</summary>
    public IRowSelection? Selection
    {
        get => GetValue(SelectionProperty);
        set => SetValue(SelectionProperty, value);
    }

    readonly Action _providerDataChanged;
    readonly Action _selectionChanged;

    public LogCanvas()
    {
        // 自绘不在样式系统内：订阅主题变更手动重绘（design D3）
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        _providerDataChanged = () => InvalidateVisual();
        _selectionChanged = () => InvalidateVisual();
        ProviderProperty.Changed.AddClassHandler<LogCanvas>((c, e) => c.HandleProviderChanged(e));
        SelectionProperty.Changed.AddClassHandler<LogCanvas>((c, e) => c.HandleSelectionChanged(e));
    }

    void HandleSelectionChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (e.GetOldValue<IRowSelection?>() is { } old)
        {
            old.Changed -= _selectionChanged;
        }
        if (e.GetNewValue<IRowSelection?>() is { } fresh)
        {
            fresh.Changed += _selectionChanged;
        }
        InvalidateVisual();
    }

    void HandleProviderChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (e.GetOldValue<LogPageProvider?>() is { } old)
        {
            old.Changed -= _providerDataChanged;
        }
        if (e.GetNewValue<LogPageProvider?>() is { } fresh)
        {
            fresh.Changed += _providerDataChanged;
            PrefetchViewport(fresh);
        }
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // 滚动偏移 / 视口高度变化 → 重绘；主题变化走 ActualThemeVariantChanged
        if (change.Property == OffsetYProperty || change.Property == ViewportHeightProperty)
        {
            if (change.Property == ViewportHeightProperty && Provider is { } p)
            {
                PrefetchViewport(p);
            }
            InvalidateVisual();
        }
    }

    void PrefetchViewport(LogPageProvider provider) =>
        provider.Prefetch((long)(OffsetY / RowHeight), (int)(ViewportHeight / RowHeight) + 1);

    public override void Render(DrawingContext context)
    {
        var provider = Provider;
        var background = TryBrush("Surface.Canvas");
        if (background is not null)
        {
            context.FillRectangle(background, Bounds);
        }
        if (provider is null || ViewportHeight <= 0)
        {
            return;
        }

        var muted = TryBrush("Text.Muted");
        var primary = TryBrush("Text.Primary") ?? muted;
        var selectionBrush = TryBrush("Surface.Selection");
        var selection = Selection;
        var firstRow = Math.Max(0, (long)(OffsetY / RowHeight));
        var visible = (int)(ViewportHeight / RowHeight) + 2;
        var total = (long)provider.TotalRows;

        for (var i = 0; i < visible; i++)
        {
            var index = firstRow + i;
            if (index >= total)
            {
                break;
            }
            var y = index * RowHeight - OffsetY;
            if (selectionBrush is not null && selection?.Contains(index) == true)
            {
                // 选中底色铺满整行（含 Message 右侧留白），文字颜色不变（design D5）
                context.FillRectangle(selectionBrush, new Rect(0, y, Bounds.Width, RowHeight));
            }
            var row = provider.TryGetRow(index);
            if (row is null)
            {
                // 页未就绪：画占位行号，数据到达后经 Changed 重绘
                DrawCell(context, 0, y, (index + 1).ToString(), muted);
                continue;
            }

            DrawCell(context, 0, y, (row.Ord + 1).ToString(), muted);
            DrawCell(context, 1, y, row.Time, primary);
            DrawCell(context, 2, y, row.Pid.ToString(), muted);
            DrawCell(context, 3, y, row.Tid.ToString(), muted);
            var levelBrush = TryBrush("Level." + LevelName(row.Level)) ?? muted;
            DrawCell(context, 4, y, LevelLetter(row.Level), levelBrush);
            DrawCell(context, 5, y, row.Tag ?? "", primary);
            DrawCell(context, 6, y, row.Message, primary);
        }
    }

    /// <summary>按列裁剪后绘制：超宽 TAG 截断在列内，不再覆盖 Message。</summary>
    void DrawCell(DrawingContext context, int column, double y, string text, IBrush? brush)
    {
        using (context.PushClip(CellRect(column, y)))
        {
            Draw(context, text, ColumnStarts[column], y, brush);
        }
    }

    /// <summary>列裁剪矩形；最后一列到控件右边界为止。</summary>
    Rect CellRect(int column, double y)
    {
        var start = ColumnStarts[column];
        var end = column + 1 < ColumnStarts.Length
            ? ColumnStarts[column + 1]
            : Math.Max(Bounds.Width, start);
        return new Rect(start, y, Math.Max(0, end - start), RowHeight);
    }

    static void Draw(DrawingContext context, string text, double x, double y, IBrush? brush)
    {
        if (string.IsNullOrEmpty(text) || brush is null)
        {
            return;
        }
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            TextTypeface,
            FontSize,
            brush);
        context.DrawText(formatted, new Point(TextPadding + x, y + PadTop));
    }

    static string LevelName(byte level) => level switch
    {
        1 => "Verbose",
        2 => "Debug",
        3 => "Info",
        4 => "Warn",
        5 => "Error",
        6 => "Fatal",
        7 => "Assert",
        _ => "Unknown",
    };

    static string LevelLetter(byte level) =>
        level < LevelLetters.Length ? LevelLetters[level] : LevelQuestion;

    IBrush? TryBrush(string key) =>
        Application.Current is { } app
        && app.Resources.TryGetResource(key, ActualThemeVariant, out var value)
        && value is IBrush brush ? brush : null;
}
