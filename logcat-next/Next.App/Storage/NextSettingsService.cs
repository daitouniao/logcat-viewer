namespace Next.App.Storage;

/// <summary>语言设置消费者需要的最小接口，便于本地化测试注入假实现。</summary>
public interface ISettingsService
{
    string? Lang { get; }
    void SetLanguage(string langCode);
}

/// <summary>
/// 设置层的单一写入入口（design D6）：内存模型是权威值，任何一项修改都整体落盘，
/// 保证语言与主题互不覆盖；保存失败时当次运行仍按内存值生效。
/// </summary>
public sealed class NextSettingsService : ISettingsService
{
    readonly INextSettingsStore _store;

    public NextSettingsService(INextSettingsStore store)
    {
        _store = store;
        Value = store.Load() ?? new NextSettings { SchemaVersion = JsonNextSettingsStore.CurrentSchemaVersion };
    }

    public NextSettings Value { get; }

    public string? Lang => Value.Lang;

    public string? Theme => Value.Theme;

    public static NextSettingsService Create(StorageRoots roots) =>
        new(roots.SessionOnly
            ? new InMemorySettingsStore()
            : new JsonNextSettingsStore(roots.SettingsPath!));

    public void SetLanguage(string langCode)
    {
        Value.Lang = langCode;
        Persist();
    }

    public void SetTheme(string theme)
    {
        Value.Theme = theme;
        Persist();
    }

    void Persist() => _store.Save(new NextSettings
    {
        SchemaVersion = JsonNextSettingsStore.CurrentSchemaVersion,
        Lang = Value.Lang,
        Theme = Value.Theme,
    });
}
