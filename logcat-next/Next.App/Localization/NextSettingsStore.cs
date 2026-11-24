using System.Text.Json;

namespace Next.App.Localization;

/// <summary>最小壳的设置：语言与主题两项。存应用目录旁 next-settings.json，尽力而为不抛错。</summary>
public sealed class NextSettings
{
    public string? Lang { get; set; }
    public string? Theme { get; set; }
}

public interface INextSettingsStore
{
    NextSettings? Load();
    void Save(NextSettings settings);
}

/// <summary>JSON 文件实现；损坏或不可写时按无设置处理。</summary>
public sealed class JsonNextSettingsStore : INextSettingsStore
{
    readonly string _path;

    public JsonNextSettingsStore(string path) => _path = path;

    public NextSettings? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }
            return JsonSerializer.Deserialize<NextSettings>(File.ReadAllText(_path));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Save(NextSettings settings)
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(settings));
        }
        catch (Exception)
        {
            // 设置写失败不致命：下次启动回到探测默认
        }
    }
}

public static class NextSettingsStore
{
    /// <summary>应用目录旁的设置文件（与旧查看器 settings.json 同一存放策略）。</summary>
    public static string DefaultPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "next-settings.json");

    public static INextSettingsStore Default { get; } = new JsonNextSettingsStore(DefaultPath);

    public static NextSettings Load() => Default.Load() ?? new NextSettings();

    public static void Save(NextSettings settings) => Default.Save(settings);
}
