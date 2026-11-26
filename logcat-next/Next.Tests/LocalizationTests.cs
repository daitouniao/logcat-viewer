using Xunit;
using System.Text.Json;
using Next.App.Localization;
using Next.App.Storage;

namespace Next.Tests;

/// <summary>双语目录契约（tasks 4.1 / 4.2）：键集合、占位符、非空、启动来源、缺译回退。</summary>
public class LocalizationTests
{
    static (IReadOnlyDictionary<string, string> Zh, IReadOnlyDictionary<string, string> En) RealStrings()
    {
        var dir = Path.GetDirectoryName(typeof(LocalizationService).Assembly.Location)!;
        var zh = LocalizationService.LoadStrings(dir, "Strings.zh-CN.json");
        var en = LocalizationService.LoadStrings(dir, "Strings.en.json");
        return (zh, en);
    }

    static LocalizationService MakeService(
        IReadOnlyDictionary<string, string>? zh = null,
        IReadOnlyDictionary<string, string>? en = null,
        NextSettings? settings = null,
        string systemLang = "zh-CN") =>
        new(
            zh ?? new Dictionary<string, string>(),
            en ?? new Dictionary<string, string>(),
            new FixedSettingsService(settings ?? new NextSettings()),
            () => systemLang);

    sealed class FixedSettingsService(NextSettings value) : ISettingsService
    {
        public string? Lang => value.Lang;

        public void SetLanguage(string langCode) => value.Lang = langCode;
    }

    [Fact]
    public void KeySetsMatch()
    {
        var (zh, en) = RealStrings();
        Assert.NotEmpty(zh);
        Assert.Equal(zh.Keys.OrderBy(k => k, StringComparer.Ordinal), en.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    /// <summary>占位符索引集合一致（{0:N0} 与 {0} 视为同一索引），避免格式错位。</summary>
    [Fact]
    public void PlaceholdersMatch()
    {
        var (zh, en) = RealStrings();
        foreach (var (key, zhText) in zh)
        {
            Assert.Equal(Indices(zhText), Indices(en[key]));
        }
    }

    static ISet<int> Indices(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text, @"\{(\d+)(?::[^}]+)?\}")
            .Select(m => int.Parse(m.Groups[1].Value))
            .ToHashSet();

    [Fact]
    public void NoEmptyTranslations()
    {
        var (zh, en) = RealStrings();
        Assert.All(zh, kv => Assert.NotEqual("", kv.Value.Trim()));
        Assert.All(en, kv => Assert.NotEqual("", kv.Value.Trim()));
    }

    /// <summary>启动语言：上次保存的选择优先于系统区域。</summary>
    [Theory]
    [InlineData("en", "ja-JP")]
    [InlineData("zh-CN", "en-US")]
    public void StartupPrefersSavedChoice(string saved, string system)
    {
        var svc = MakeService(settings: new NextSettings { Lang = saved }, systemLang: system);
        svc.Initialize(new NextSettings { Lang = saved });
        Assert.Equal(saved == "en", svc.IsEnglish);
    }

    /// <summary>无保存记录：中文系统取中文，其余一律英文。</summary>
    [Theory]
    [InlineData("zh-CN", false)]
    [InlineData("en-US", true)]
    [InlineData("ja-JP", true)]
    [InlineData("", true)]
    public void NonChineseEnglishSystemFallsBackToEnglish(string system, bool expectEnglish)
    {
        var svc = MakeService(systemLang: system);
        svc.Initialize(new NextSettings());
        Assert.Equal(expectEnglish, svc.IsEnglish);
    }

    /// <summary>缺译回退中文原文；两语言都没有的键兜底返回键本身（运行时防线，完整性由 KeySetsMatch 把关）。</summary>
    [Fact]
    public void MissingKeyFallsBackToChineseThenKey()
    {
        var svc = MakeService(
            zh: new Dictionary<string, string> { ["A"] = "中文" },
            en: new Dictionary<string, string>());
        svc.Initialize(new NextSettings { Lang = "en" });
        Assert.Equal("中文", svc["A"]);
        Assert.Equal("NotA.Key", svc["NotA.Key"]);
    }

    /// <summary>切换语言触发索引器变更通知（绑定自动刷新的机制基础）。
    /// Avalonia 11.3 只认 "Item" 作为索引器失效名，空名不会刷新绑定。</summary>
    [Fact]
    public void SetLanguageNotifiesAllBindings()
    {
        var svc = MakeService(settings: new NextSettings());
        svc.Initialize(new NextSettings());
        var notifications = new List<string?>();
        svc.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        svc.SetLanguage("en");
        Assert.Contains("Item", notifications);
        Assert.True(svc.IsEnglish);
    }

    /// <summary>切换语言不得清掉已持久化的主题选择（单写入入口整体落盘）。</summary>
    [Fact]
    public void SetLanguageKeepsSavedTheme()
    {
        var store = new FixedStore(new NextSettings { SchemaVersion = 1, Lang = "zh-CN", Theme = "dark" });
        var svc = new LocalizationService(
            new Dictionary<string, string> { ["A"] = "甲" },
            new Dictionary<string, string> { ["A"] = "A" },
            new NextSettingsService(store),
            () => "zh-CN");
        svc.Initialize(new NextSettings());
        svc.SetLanguage("en");
        Assert.Equal("en", store.Value?.Lang);
        Assert.Equal("dark", store.Value?.Theme);
    }

    sealed class FixedStore(NextSettings value) : INextSettingsStore
    {
        public NextSettings? Value { get; private set; } = value;

        public NextSettings? Load() => Value;

        public void Save(NextSettings settings) => Value = settings;
    }
}
