using System.Text.Json;
using System.Text.Json.Nodes;

namespace logcat.Services.Logcore;

/// <summary>D15 错误码数值表（两宿主共用，不得漂移）。</summary>
public static class LogcoreErrorCodes
{
    public const int Ok = 0;
    public const int Busy = 1;
    public const int Stopped = 2;
    public const int Failed = 3;
    public const int Closed = 4;
    public const int InvalidState = 5;
    public const int InvalidArg = 6;
    public const int Internal = 7;
    public const int ProxyLaunchTimeout = 10;
    public const int ProxyDisconnect = 11;
    public const int DirectOpenFailed = 12;
    public const int NotFound = 13;
    public const int UnsupportedFormat = 14;
    public const int IoError = 15;
    public const int ProxyLaunchFailed = 16;
    public const int RegexInvalid = 17;
    public const int SubstrInvalid = 18;

    /// <summary>绑定层发布的单批推入行数上限（与 core 的 MAX_FEED_LINES 一致）。</summary>
    public const int MaxFeedLines = 65536;

    /// <summary>单页行数上限（与 core 的 MAX_PAGE_ROWS 一致）。</summary>
    public const int MaxPageRows = 4096;

    public static string Name(int code) => code switch
    {
        Ok => "OK",
        Busy => "BUSY",
        Stopped => "STOPPED",
        Failed => "FAILED",
        Closed => "CLOSED",
        InvalidState => "INVALID_STATE",
        InvalidArg => "INVALID_ARG",
        Internal => "INTERNAL",
        ProxyLaunchTimeout => "PROXY_LAUNCH_TIMEOUT",
        ProxyDisconnect => "PROXY_DISCONNECT",
        DirectOpenFailed => "DIRECT_OPEN_FAILED",
        NotFound => "NOT_FOUND",
        UnsupportedFormat => "UNSUPPORTED_FORMAT",
        IoError => "IO_ERROR",
        ProxyLaunchFailed => "PROXY_LAUNCH_FAILED",
        RegexInvalid => "REGEX_INVALID",
        SubstrInvalid => "SUBSTR_INVALID",
        _ => $"UNKNOWN_{code}",
    };
}

/// <summary>同步失败（design D15）：码 + 结构化 params；参数形状错误另带 field。</summary>
public sealed class LogcoreException : Exception
{
    public int Code { get; }
    public string CodeName => LogcoreErrorCodes.Name(Code);
    public string? Field { get; }
    public IReadOnlyDictionary<string, JsonElement> Params { get; }

    public LogcoreException(int code, IReadOnlyDictionary<string, JsonElement> parameters)
        : base(BuildMessage(code, parameters))
    {
        Code = code;
        Params = parameters;
        if (parameters.TryGetValue("field", out var field) && field.ValueKind == JsonValueKind.String)
        {
            Field = field.GetString();
        }
    }

    static string BuildMessage(int code, IReadOnlyDictionary<string, JsonElement> parameters)
    {
        var text = $"logcore 返回 {LogcoreErrorCodes.Name(code)}（{code}）";
        if (parameters.TryGetValue("field", out var field) && field.ValueKind == JsonValueKind.String)
        {
            text += $"；field={field.GetString()}";
        }
        if (parameters.TryGetValue("reason", out var reason) && reason.ValueKind == JsonValueKind.String)
        {
            text += $"；reason={reason.GetString()}";
        }
        return text;
    }

    internal static LogcoreException FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var code = root.TryGetProperty("code", out var c) ? c.GetInt32() : LogcoreErrorCodes.Internal;
        var parameters = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in p.EnumerateObject())
            {
                parameters[item.Name] = item.Value.Clone();
            }
        }
        return new LogcoreException(code, parameters);
    }
}

/// <summary>plan 结果（design D14 / D24）：路由与预估大小；采集源大小未知为 null。</summary>
public sealed record LogcorePlan(string Route, long? EstimatedSize);

public sealed record LogcoreColumn(string Id, string LabelKey, int Width);

public sealed record LogcoreIdentity(ulong Size, ulong MtimeMs, string HeadHash, string TailHash);

/// <summary>源描述（design D14 线格式，字段名与核心一致）。</summary>
public sealed class LogcoreDescriptor
{
    public string Channel { get; init; } = "";
    public string Format { get; init; } = "";
    public string Encoding { get; init; } = "";
    public IReadOnlyList<LogcoreColumn> Columns { get; init; } = Array.Empty<LogcoreColumn>();
    public LogcoreIdentity? Identity { get; init; }
    public string LoadMode { get; init; } = "";
    public ulong RowCount { get; init; }
    public ulong DocSize { get; init; }
    public string AppendMode { get; init; } = "";
    public string LeaseKind { get; init; } = "";
    public string? LeaseId { get; init; }
    public string TimeSemantics { get; init; } = "";
    public string? DisplayPath { get; init; }
    public string SourcePath { get; init; } = "";

    internal static LogcoreDescriptor Parse(JsonElement root) => new()
    {
        Channel = root.GetProperty("channel").GetString()!,
        Format = root.GetProperty("format").GetString()!,
        Encoding = root.GetProperty("encoding").GetString()!,
        Columns = root.GetProperty("columns").EnumerateArray()
            .Select(c => new LogcoreColumn(
                c.GetProperty("id").GetString()!,
                c.GetProperty("labelKey").GetString()!,
                c.GetProperty("width").GetInt32()))
            .ToArray(),
        Identity = root.GetProperty("identity").ValueKind == JsonValueKind.Null
            ? null
            : ParseIdentity(root.GetProperty("identity")),
        LoadMode = root.GetProperty("loadMode").GetString()!,
        RowCount = root.GetProperty("rowCount").GetUInt64(),
        DocSize = root.GetProperty("docSize").GetUInt64(),
        AppendMode = root.GetProperty("appendMode").GetString()!,
        LeaseKind = root.GetProperty("leaseKind").GetString()!,
        LeaseId = root.GetProperty("leaseId").ValueKind == JsonValueKind.Null
            ? null
            : root.GetProperty("leaseId").GetString(),
        TimeSemantics = root.GetProperty("timeSemantics").GetString()!,
        DisplayPath = root.GetProperty("displayPath").ValueKind == JsonValueKind.Null
            ? null
            : root.GetProperty("displayPath").GetString(),
        SourcePath = root.GetProperty("sourcePath").GetString()!,
    };

    static LogcoreIdentity ParseIdentity(JsonElement id) => new(
        id.GetProperty("size").GetUInt64(),
        id.GetProperty("mtimeMs").GetUInt64(),
        id.GetProperty("headHash").GetString()!,
        id.GetProperty("tailHash").GetString()!);
}

/// <summary>页内崩溃摘要（布局 v1）：仅崩溃头行携带。</summary>
public sealed record LogcoreCrashInfo(byte Kind, string? ExceptionType, string? Package);

/// <summary>页行（design D2/D3）：原文与按字节区间切片后的时间 / 消息。</summary>
public sealed class LogcorePageRow
{
    public uint DocRow { get; init; }
    public byte Level { get; init; }
    public int Pid { get; init; }
    public int Tid { get; init; }
    public string? Tag { get; init; }
    public byte[] Raw { get; init; } = Array.Empty<byte>();
    public string Time { get; init; } = "";
    public string Message { get; init; } = "";
    public LogcoreCrashInfo? Crash { get; init; }
}

/// <summary>进度快照（design D16）：stage 五值、total 未知为 null。</summary>
public sealed record LogcoreProgress(string Stage, ulong Value, ulong? Total)
{
    internal static LogcoreProgress Parse(JsonElement root) => new(
        root.GetProperty("stage").GetString()!,
        root.GetProperty("value").GetUInt64(),
        root.GetProperty("total").ValueKind == JsonValueKind.Null
            ? null
            : root.GetProperty("total").GetUInt64());
}

/// <summary>事件（design D22）：stage / appended / stopped / done / error。</summary>
public sealed class LogcoreEvent
{
    public string Type { get; init; } = "";
    public string? Stage { get; init; }
    public ulong? Value { get; init; }
    public ulong? Total { get; init; }
    public ulong? Rows { get; init; }
    public ulong? TotalRows { get; init; }
    public int? Code { get; init; }
    public IReadOnlyDictionary<string, JsonElement>? Params { get; init; }

    internal static LogcoreEvent Parse(JsonElement root)
    {
        var type = root.GetProperty("type").GetString()!;
        return type switch
        {
            "stage" => new LogcoreEvent
            {
                Type = type,
                Stage = root.GetProperty("stage").GetString(),
                Value = root.GetProperty("value").GetUInt64(),
                Total = root.GetProperty("total").ValueKind == JsonValueKind.Null
                    ? null
                    : root.GetProperty("total").GetUInt64(),
            },
            "appended" => new LogcoreEvent
            {
                Type = type,
                Rows = root.GetProperty("rows").GetUInt64(),
                TotalRows = root.GetProperty("totalRows").GetUInt64(),
            },
            "error" => new LogcoreEvent
            {
                Type = type,
                Code = root.GetProperty("code").GetInt32(),
                Params = ParseParams(root.TryGetProperty("params", out var p) ? p : default),
            },
            _ => new LogcoreEvent { Type = type },
        };
    }

    static IReadOnlyDictionary<string, JsonElement> ParseParams(JsonElement p)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (p.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in p.EnumerateObject())
            {
                result[item.Name] = item.Value.Clone();
            }
        }
        return result;
    }
}

/// <summary>启动维护统计（design D19）。</summary>
public sealed record LogcoreMaintainResult(
    string Status,
    ulong RemovedCorrupt,
    ulong RemovedTempWrites,
    ulong RemovedOrphans,
    ulong SkippedInUse,
    ulong Evicted,
    ulong Kept,
    ulong FreedBytes)
{
    public bool IsPartial => Status == "Partial";

    internal static LogcoreMaintainResult Parse(JsonElement root) => new(
        root.GetProperty("status").GetString()!,
        root.GetProperty("removedCorrupt").GetUInt64(),
        root.GetProperty("removedTempWrites").GetUInt64(),
        root.GetProperty("removedOrphans").GetUInt64(),
        root.GetProperty("skippedInUse").GetUInt64(),
        root.GetProperty("evicted").GetUInt64(),
        root.GetProperty("kept").GetUInt64(),
        root.GetProperty("freedBytes").GetUInt64());
}

/// <summary>plan 请求构造（design D21）。null 的可选项不落 JSON，由 core 套默认值。</summary>
public sealed class LogcoreSourceRequest
{
    public string Kind { get; init; } = "File";
    public string? Path { get; init; }
    public string? Serial { get; init; }
    public string? Mode { get; init; } = "Auto";
    public string? PipeName { get; init; }
    public string? LaunchFile { get; init; }
    public IReadOnlyList<string>? LaunchArgs { get; init; }
    public int? LaunchTimeoutMs { get; init; }
    public required string TempDir { get; init; }
    public int? ProbeWindowBytes { get; init; }
    public double? ProbeThreshold { get; init; }
    public string? SidecarDir { get; init; }
    public long? SidecarEntryLimitBytes { get; init; }
    public long? SidecarDirLimitBytes { get; init; }

    public static LogcoreSourceRequest ForFile(
        string path,
        string tempDir,
        string pipeName,
        string launchFile,
        IReadOnlyList<string>? launchArgs = null,
        int launchTimeoutMs = 3000,
        string mode = "Auto",
        string? sidecarDir = null) => new()
        {
            Kind = "File",
            Path = path,
            TempDir = tempDir,
            PipeName = pipeName,
            LaunchFile = launchFile,
            LaunchArgs = launchArgs,
            LaunchTimeoutMs = launchTimeoutMs,
            Mode = mode,
            SidecarDir = sidecarDir,
        };

    public static LogcoreSourceRequest ForDevice(
        string tempDir, string? serial = null, string mode = "Auto") => new()
        {
            Kind = "Device",
            Serial = serial,
            Mode = mode,
            TempDir = tempDir,
        };

    internal string ToJson()
    {
        var root = new JsonObject
        {
            ["kind"] = Kind,
            ["mode"] = Mode,
        };
        if (Path != null) root["path"] = Path;
        if (Serial != null)
        {
            root["device"] = new JsonObject { ["serial"] = Serial };
        }
        if (Kind == "File")
        {
            var proxy = new JsonObject { ["pipeName"] = PipeName };
            if (LaunchFile != null) proxy["launchFile"] = LaunchFile;
            if (LaunchArgs != null) proxy["launchArgs"] = new JsonArray(LaunchArgs.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());
            if (LaunchTimeoutMs.HasValue) proxy["launchTimeoutMs"] = LaunchTimeoutMs.Value;
            root["proxy"] = proxy;
        }
        var options = new JsonObject { ["tempDir"] = TempDir };
        if (ProbeWindowBytes.HasValue) options["probeWindowBytes"] = ProbeWindowBytes.Value;
        if (ProbeThreshold.HasValue) options["probeThreshold"] = ProbeThreshold.Value;
        if (SidecarDir != null)
        {
            var sidecar = new JsonObject { ["dir"] = SidecarDir };
            if (SidecarEntryLimitBytes.HasValue) sidecar["entryLimitBytes"] = SidecarEntryLimitBytes.Value;
            if (SidecarDirLimitBytes.HasValue) sidecar["dirLimitBytes"] = SidecarDirLimitBytes.Value;
            options["sidecar"] = sidecar;
        }
        root["options"] = options;
        return root.ToJsonString();
    }
}

/// <summary>启动维护请求（design D19）：缓存目录与临时目录必填。</summary>
public sealed class LogcoreMaintainRequest
{
    public required string CacheDir { get; init; }
    public required string TempDir { get; init; }
    public long? EntryLimitBytes { get; init; }
    public long? DirLimitBytes { get; init; }

    internal string ToJson()
    {
        var root = new JsonObject
        {
            ["cacheDir"] = CacheDir,
            ["tempDir"] = TempDir,
        };
        if (EntryLimitBytes.HasValue) root["entryLimitBytes"] = EntryLimitBytes.Value;
        if (DirLimitBytes.HasValue) root["dirLimitBytes"] = DirLimitBytes.Value;
        return root.ToJsonString();
    }
}