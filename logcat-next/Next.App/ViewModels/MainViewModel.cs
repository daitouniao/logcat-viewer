using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Threading;
using Next.App.Localization;
using Next.App.Services;
using Next.App.Storage;
using Next.Interop;

namespace Next.App.ViewModels;

/// <summary>
/// 主窗状态：打开文件 → plan/commit → 轮询事件至装载完成 → 行数与页供给就绪。
/// 文案经 LocalizationService 取（Error.<CODE> 映射，design D4），语言切换时重建。
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    LogcoreSource? _source;
    LogPageProvider? _provider;
    string _statusKey = "Status.Ready";
    object?[] _statusArgs = [];
    ulong _totalRows;
    CancellationTokenSource? _loadCts;

    public event PropertyChangedEventHandler? PropertyChanged;

    public LogPageProvider? Provider
    {
        get => _provider;
        private set { _provider = value; Raise(); }
    }

    public ulong TotalRows
    {
        get => _totalRows;
        private set { _totalRows = value; Raise(); }
    }

    /// <summary>状态栏文案：键经本地化 + 参数格式化；语言切换时自动重建。</summary>
    public string StatusText
    {
        get
        {
            var text = LocalizationService.Instance[_statusKey];
            try
            {
                return _statusArgs.Length == 0 ? text : string.Format(text, _statusArgs);
            }
            catch (FormatException)
            {
                return text;
            }
        }
    }

    public bool HasRows => TotalRows > 0;

    /// <summary>打开文件对话框 → 装载。窗口层提供 StorageProvider。</summary>
    public async Task OpenFileAsync(TopLevel topLevel)
    {
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(
            new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                AllowMultiple = false,
                Title = LocalizationService.Instance["Menu.Open"],
            });
        if (files.Count == 0 || files[0].Path is not { } path)
        {
            return;
        }
        await LoadFileAsync(path.LocalPath);
    }

    /// <summary>装载流程：plan → commit → 轮询事件至 done；错误落在状态栏（spec 数据来源不可用）。</summary>
    public async Task LoadFileAsync(string path)
    {
        var cts = new CancellationTokenSource();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _loadCts?.Cancel();
            _loadCts = cts;
            Provider = null;
            TotalRows = 0;
            SetStatus("Status.Loading", path);
        });

        var token = cts.Token;
        try
        {
            // 装载成功才回调记录最近文件（含 0 行文件）；失败 / 取消不记录（design D4）
            var outcome = await LogFileLoader.LoadAsync(path, AppStorage.Roots, AppStorage.Settings.AddRecentFile, token);
            var old = _source;
            _source = outcome.Source;
            _ = Task.Run(() => old?.Dispose());

            var rows = outcome.Rows;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                TotalRows = rows;
                if (rows > 0)
                {
                    Provider = new LogPageProvider(outcome.Source, rows);
                    SetStatus("Status.Loaded", rows);
                }
                else
                {
                    SetStatus("Status.Empty");
                }
            });
        }
        catch (OperationCanceledException)
        {
            // 被下一次装载顶替，静默
        }
        catch (LogcoreUnavailableException e)
        {
            await SetErrorAsync("Error.LogcoreMissing", e.Message);
        }
        catch (LogcoreException e)
        {
            // 文件原因的装载失败：移除该最近条目（design D9）；取消与组件不可用不移除
            AppStorage.Settings.RemoveRecentFiles([path]);
            await SetErrorAsync("Error.LoadFailed", e.CodeName, e.Message);
        }
        catch (Exception e)
        {
            AppStorage.Settings.RemoveRecentFiles([path]);
            await SetErrorAsync("Error.OpenFailed", e.Message);
        }
    }

    async Task SetErrorAsync(string key, params object?[] args)
    {
        await Dispatcher.UIThread.InvokeAsync(() => SetStatus(key, args));
    }

    void SetStatus(string key, params object?[] args)
    {
        _statusKey = key;
        _statusArgs = args;
        Raise(nameof(StatusText));
    }

    /// <summary>复制成功：状态栏「已复制 N 行」（选中记录数，当前区域千分位）。</summary>
    public void ShowCopied(long rows) => SetStatus("Status.Copied", rows);

    /// <summary>选区内有页取数失败：整体失败，不写剪贴板。</summary>
    public void ShowCopyFetchFailed() => SetStatus("Status.CopyFetchFailed");

    /// <summary>剪贴板写入失败：应用保持可用。</summary>
    public void ShowCopyClipboardFailed() => SetStatus("Status.CopyClipboardFailed");

    void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>语言切换后重建状态栏文案（界面其余部分由绑定自动刷新）。</summary>
    public void RefreshStatusOnLanguageChanged() => Raise(nameof(StatusText));
}
