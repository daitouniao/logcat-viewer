using System.Text.Json;

namespace Next.Interop;

/// <summary>D15 错误码数值表（与 core 共用，不得漂移）。</summary>
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

/// <summary>同步失败：码 + 结构化 params；本地化按 <see cref="LogcoreErrorCodes.Name"/> 映射（design D4）。</summary>
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
        var text = $"logcore {LogcoreErrorCodes.Name(code)}（{code}）";
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
