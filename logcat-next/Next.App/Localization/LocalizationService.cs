using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Next.App.Storage;

namespace Next.App.Localization;

/// <summary>
/// 界面字符串本地化（design D4）：稳定键 + 可观察索引器。XAML 经 {loc Key=...}
/// 绑定索引器；切换语言只触发一次索引器变更通知（"Item"），绑定自动刷新——不遍历控件树。
/// 取值链：英文态查 en 表，缺译回退中文原文；两语言都没有的键由测试判定为失败
/// （KeySetsMatch / NoEmptyTranslations），运行时兜底返回键本身。
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    public const string ZhCn = "zh-CN";
    public const string En = "en";

    readonly IReadOnlyDictionary<string, string> _zh;
    readonly IReadOnlyDictionary<string, string> _en;
    readonly ISettingsService _settings;
    readonly Func<string> _systemLanguage;
    string _current;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>测试入口：注入双语言目录、设置存储与系统语言探测。</summary>
    public LocalizationService(
        IReadOnlyDictionary<string, string> zh,
        IReadOnlyDictionary<string, string> en,
        ISettingsService settings,
        Func<string> systemLanguage)
    {
        _zh = zh;
        _en = en;
        _settings = settings;
        _systemLanguage = systemLanguage;
        _current = ZhCn;
    }

    /// <summary>程序级实例：输出目录旁的双语目录 + 应用目录设置 + 系统区域。</summary>
    public static LocalizationService Instance { get; } = new(
        LoadStrings(GetAssemblyDirectory(), "Strings.zh-CN.json"),
        LoadStrings(GetAssemblyDirectory(), "Strings.en.json"),
        AppStorage.Settings,
        () =>
        {
            var name = CultureInfo.CurrentUICulture.Name;
            return name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? ZhCn : En;
        });

    static string GetAssemblyDirectory() =>
        Path.GetDirectoryName(typeof(LocalizationService).Assembly.Location) ?? ".";

    /// <summary>启动语言来源（spec next/ui-localization）：上次保存的选择 → 系统区域。</summary>
    public void Initialize(NextSettings settings)
    {
        var saved = settings.Lang;
        if (string.IsNullOrWhiteSpace(saved))
        {
            // 系统区域归一化：zh* 取中文，其余一律英文
            var sys = _systemLanguage();
            saved = sys.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? ZhCn : En;
        }
        _current = saved == En ? En : ZhCn;
    }

    public string CurrentLanguage => _current;

    public bool IsEnglish => _current == En;

    /// <summary>切换语言（save=false 仅供测试，不落盘）。相同语言不通知。</summary>
    public void SetLanguage(string langCode, bool save = true)
    {
        var target = langCode == En ? En : ZhCn;
        if (_current == target)
        {
            return;
        }
        _current = target;
        if (save)
        {
            // 单一写入入口（design D6）：只改语言，主题由设置服务自身保留
            _settings.SetLanguage(_current);
        }
        // Avalonia 11.3 的索引器绑定只认 "Item" 作为失效通知名（空名与 "Item[]" 均不刷新）
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
    }

    /// <summary>取文案。占位符 {0} 风格；缺译回退中文原文，不呈现空白或键名。</summary>
    public string this[string key] =>
        _current == En && _en.TryGetValue(key, out var en) ? en
        : _zh.TryGetValue(key, out var zh) ? zh
        : key;

    /// <summary>从输出目录 Strings\ 下加载语言目录（csproj 以 Link 复制到此）。</summary>
    public static IReadOnlyDictionary<string, string> LoadStrings(string assemblyDirectory, string fileName)
    {
        var path = Path.Combine(assemblyDirectory, "Strings", fileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"语言目录缺失：{path}");
        }
        var doc = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
        return doc is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(doc, StringComparer.Ordinal);
    }
}
