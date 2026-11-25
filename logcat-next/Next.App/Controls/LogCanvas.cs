using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
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
    const double PadLeft = 8;
    const double PadTop = 2;
    const double FontSize = 12;

    // 列起点：# / Time / PID / TID / Lv / Tag / Message（列名与旧查看器一致，域常量不走本地化）
    static readonly double[] ColumnX = { 0, 62, 190, 258, 326, 356, 510 };

    public static readonly StyledProperty<LogPageProvider?> ProviderProperty =
        AvaloniaProperty.Register<LogCanvas, LogPageProvider?>(nameof(Provider));

    public static readonly StyledProperty<double> OffsetYProperty =
        AvaloniaProperty.Register<LogCanvas, double>(nameof(OffsetY));

    public static readonly StyledProperty<double> ViewportHeightProperty =
        AvaloniaProperty.Register<LogCanvas, double>(nameof(ViewportHeight));

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

    readonly Action _providerDataChanged;

    public LogCanvas()
    {
        // 自绘不在样式系统内：订阅主题变更手动重绘（design D3）
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        _providerDataChanged = () => InvalidateVisual();
        ProviderProperty.Changed.AddClassHandler<LogCanvas>((c, e) => c.HandleProviderChanged(e));
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
            var row = provider.TryGetRow(index);
            if (row is null)
            {
                // 页未就绪：画占位行号，数据到达后经 Changed 重绘
                Draw(context, (index + 1).ToString(), ColumnX[0], y, muted);
                continue;
            }

            Draw(context, (row.Ord + 1).ToString(), ColumnX[0], y, muted);
            Draw(context, row.Time, ColumnX[1], y, primary);
            Draw(context, row.Pid.ToString(), ColumnX[2], y, muted);
            Draw(context, row.Tid.ToString(), ColumnX[3], y, muted);
            var levelBrush = TryBrush("Level." + LevelName(row.Level)) ?? muted;
            Draw(context, LevelLetter(row.Level), ColumnX[4], y, levelBrush);
            Draw(context, row.Tag ?? "", ColumnX[5], y, primary);
            Draw(context, row.Message, ColumnX[6], y, primary);
        }
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
        context.DrawText(formatted, new Point(PadLeft + x, y + PadTop));
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
