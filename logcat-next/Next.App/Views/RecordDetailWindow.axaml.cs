using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Next.App.Views;

/// <summary>
/// 只读「完整记录」窗口（design D7）：模态、标题为记录 # 源文件行号，内容为该行原始文本并保留换行。
/// 默认 1000×620、居中主窗、可拉伸；Esc 与关闭按钮均可关闭。
/// </summary>
public partial class RecordDetailWindow : Window
{
    public RecordDetailWindow()
    {
        InitializeComponent();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
    }

    public RecordDetailWindow(string title, string text) : this()
    {
        Title = title;
        Body.Text = text;
        Body.CaretIndex = 0;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Body.Focus();
    }

    void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
