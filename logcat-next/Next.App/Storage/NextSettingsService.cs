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
        Value.RecentFiles ??= new List<string>();
    }

    public NextSettings Value { get; }

    public string? Lang => Value.Lang;

    public string? Theme => Value.Theme;

    /// <summary>最近打开的文件路径（最近在前）；列表变化时触发 <see cref="RecentFilesChanged"/>。</summary>
    public IReadOnlyList<string> RecentFiles => Value.RecentFiles!;

    public event EventHandler? RecentFilesChanged;

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

    /// <summary>
    /// 记录一次成功打开：大小写不敏感去重、移到最前并保留本次文本、超过上限从尾部淘汰，
    /// 随后整体落盘并通知界面（design D2/D3/D10）。
    /// </summary>
    public void AddRecentFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var list = Value.RecentFiles!;
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        while (list.Count > RecentFilesList.MaxCount)
        {
            list.RemoveAt(list.Count - 1);
        }

        Persist();
        RecentFilesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 移除指定条目（大小写不敏感，未命中的忽略），用于清理已删除或装载失败的文件；
    /// 有实际变化时整体落盘并通知界面。
    /// </summary>
    public void RemoveRecentFiles(IEnumerable<string> paths)
    {
        var targets = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        if (targets.Count == 0)
        {
            return;
        }

        if (Value.RecentFiles!.RemoveAll(targets.Contains) == 0)
        {
            return;
        }

        Persist();
        RecentFilesChanged?.Invoke(this, EventArgs.Empty);
    }

    void Persist() => _store.Save(new NextSettings
    {
        SchemaVersion = JsonNextSettingsStore.CurrentSchemaVersion,
        Lang = Value.Lang,
        Theme = Value.Theme,
        RecentFiles = Value.RecentFiles,
    });
}
