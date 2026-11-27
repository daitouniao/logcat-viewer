using System.Text.Encodings.Web;
using System.Text.Json;

namespace Next.App.Storage;

/// <summary>新壳设置（design D4）：带 schema 版本，键名 PascalCase。</summary>
public sealed class NextSettings
{
    public int? SchemaVersion { get; set; }
    public string? Lang { get; set; }
    public string? Theme { get; set; }

    /// <summary>最近打开的文件路径，最近在前；缺失或为空按空列表处理。</summary>
    public List<string>? RecentFiles { get; set; }
}

public interface INextSettingsStore
{
    NextSettings? Load();
    void Save(NextSettings settings);
}

/// <summary>
/// JSON 文件实现（design D4/D5）：缺失、损坏、版本不识别的文件一律按无设置处理；
/// 写入先落到同目录临时文件再整体替换；失败只留日志，不抛。
/// </summary>
public sealed class JsonNextSettingsStore : INextSettingsStore
{
    public const int CurrentSchemaVersion = 1;

    static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

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

            var settings = JsonSerializer.Deserialize<NextSettings>(File.ReadAllText(_path), SerializerOptions);
            if (settings?.SchemaVersion != CurrentSchemaVersion)
            {
                return null;
            }
            settings.RecentFiles = RecentFilesList.Normalize(settings.RecentFiles);
            return settings;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Save(NextSettings settings)
    {
        var toWrite = new NextSettings
        {
            SchemaVersion = CurrentSchemaVersion,
            Lang = settings.Lang,
            Theme = settings.Theme,
            RecentFiles = RecentFilesList.Normalize(settings.RecentFiles),
        };
        var temp = _path + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(temp, JsonSerializer.Serialize(toWrite, SerializerOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception error)
        {
            TryDelete(temp);
            StartupLog.Write("ERROR", $"设置保存失败：{_path}", error);
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 清理残留失败不影响主流程
        }
    }
}

/// <summary>仅会话内使用：读永远为空，写丢弃。</summary>
public sealed class InMemorySettingsStore : INextSettingsStore
{
    public NextSettings? Load() => null;

    public void Save(NextSettings settings)
    {
    }
}
