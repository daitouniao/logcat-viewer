using System.Text.Json;
using System.Text.Json.Nodes;
using Next.App.Storage;
using Next.Interop;

namespace Next.App.Services;

/// <summary>装载结果：完成后的句柄与行数；失败 / 取消时由装载流程自行释放句柄。</summary>
public sealed record LogLoadOutcome(LogcoreSource Source, ulong Rows);

/// <summary>
/// 文件装载流程（design D4）：plan → commit → 轮询事件至 done。
/// 只在 done 分支回调 onLoaded（记录最近文件），失败与被取消都不回调。
/// </summary>
public static class LogFileLoader
{
    public static async Task<LogLoadOutcome> LoadAsync(
        string path,
        StorageRoots roots,
        Action<string>? onLoaded,
        CancellationToken token)
    {
        var source = LogcoreSource.Plan(BuildRequest(path, roots));
        try
        {
            source.Commit();

            while (true)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(100, token);
                foreach (var e in source.PollEvents())
                {
                    if (e.Type == "error")
                    {
                        throw new LogcoreException(e.Code ?? LogcoreErrorCodes.Failed,
                            new Dictionary<string, JsonElement>(StringComparer.Ordinal));
                    }
                    if (e.Type == "done")
                    {
                        var rows = source.RowCount();
                        onLoaded?.Invoke(path);
                        return new LogLoadOutcome(source, rows);
                    }
                }
            }
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 组装 plan 请求：临时目录与索引缓存目录都由持久化根提供（design D8/D9）。
    /// 目录创建交给调用方尽力而为，失败只留日志——缓存不可用必须降级而不是阻断打开。
    /// </summary>
    public static string BuildRequest(string path, StorageRoots roots)
    {
        EnsureDirectory(roots.TempDir);
        EnsureDirectory(roots.CacheDir);
        var root = new JsonObject
        {
            ["kind"] = "File",
            ["mode"] = "Auto",
            ["path"] = path,
            // proxy 节对 File 源必填（core request.rs），本地直开不启动代理；
            // proxy 存在又要求 options.sidecar，故一并给足形状
            ["proxy"] = new JsonObject
            {
                ["pipeName"] = $"logcat-next-{Guid.NewGuid():N}",
                ["launchFile"] = "logcat-next-sidecar.exe",
                ["launchTimeoutMs"] = 3000,
            },
            ["options"] = new JsonObject
            {
                ["tempDir"] = roots.TempDir,
                ["sidecar"] = new JsonObject { ["dir"] = roots.CacheDir },
            },
        };
        return root.ToJsonString();
    }

    static void EnsureDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception error)
        {
            StartupLog.Write("ERROR", $"目录不可用：{directory}", error);
        }
    }
}
