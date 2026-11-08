using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace logcat.Services;

/// <summary>
/// 配置落盘共用原语：JSON 序列化选项、原子写、容错读取、schema 版本判定。
/// 三份配置（settings.json / andtools.json / language.json）的存取类共用此处的原语；
/// 「何时写、写哪份」的落盘分支在各 exe 自有工程（design D3/D4）。
/// </summary>
public static class SettingsIO
{
    /// <summary>当前拆分布局的 schema 版本。旧版单文件 settings.json 无此属性（读到 0）。</summary>
    public const int CurrentSchemaVersion = 2;

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 目标文件是否已是新布局（SchemaVersion=2）。文件缺失、损坏或旧布局一律 false——
    /// 损坏文件不能当成已迁移，否则迁移被跳过、键面丢失。
    /// </summary>
    public static bool IsSchemaV2(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("SchemaVersion", out var v)
                && v.ValueKind == JsonValueKind.Number
                && v.GetInt32() == CurrentSchemaVersion;
        }
        catch { return false; }
    }

    /// <summary>容错读取：文件缺失或损坏返回 null，不抛异常（损坏时留痕便于排查）。</summary>
    public static T? TryLoad<T>(string path) where T : class
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOpts);
        }
        catch (Exception ex)
        {
            StartupLog.Write($"[SettingsIO] 读取失败 path={path} err={ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// 原子写：先写同目录 .tmp，再 File.Move 覆盖目标。中途崩溃只留下 .tmp 或完整旧文件，
    /// 不会留下半写的新布局文件；下次启动按未迁移/未更新分支重走（design D4）。
    /// </summary>
    public static void AtomicWrite(string path, string json)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, true);
    }
}
