using System.Text.Json;

namespace Next.Interop;

/// <summary>plan 结果：路由与预估大小；采集源大小未知为 null。</summary>
public sealed record LogcorePlan(string Route, long? EstimatedSize);

/// <summary>源描述（线格式字段名与 core 一致）。新壳只消费其中展示所需字段。</summary>
public sealed class LogcoreDescriptor
{
    public string Channel { get; init; } = "";
    public string Format { get; init; } = "";
    public string Encoding { get; init; } = "";
    public ulong RowCount { get; init; }
    public ulong DocSize { get; init; }
    public string LoadMode { get; init; } = "";
    public string? DisplayPath { get; init; }
    public string SourcePath { get; init; } = "";

    public static LogcoreDescriptor Parse(JsonElement root) => new()
    {
        Channel = root.GetProperty("channel").GetString()!,
        Format = root.GetProperty("format").GetString()!,
        Encoding = root.GetProperty("encoding").GetString()!,
        RowCount = root.GetProperty("rowCount").GetUInt64(),
        DocSize = root.GetProperty("docSize").GetUInt64(),
        LoadMode = root.GetProperty("loadMode").GetString()!,
        DisplayPath = root.GetProperty("displayPath").ValueKind == JsonValueKind.Null
            ? null
            : root.GetProperty("displayPath").GetString(),
        SourcePath = root.GetProperty("sourcePath").GetString()!,
    };
}

/// <summary>进度快照：stage 五值、total 未知为 null。</summary>
public sealed record LogcoreProgress(string Stage, ulong Value, ulong? Total)
{
    public static LogcoreProgress Parse(JsonElement root) => new(
        root.GetProperty("stage").GetString()!,
        root.GetProperty("value").GetUInt64(),
        root.GetProperty("total").ValueKind == JsonValueKind.Null
            ? null
            : root.GetProperty("total").GetUInt64());
}

/// <summary>事件：stage / appended / stopped / done / error。</summary>
public sealed class LogcoreEvent
{
    public string Type { get; init; } = "";
    public string? Stage { get; init; }
    public ulong? Value { get; init; }
    public ulong? Total { get; init; }
    public ulong? Rows { get; init; }
    public ulong? TotalRows { get; init; }
    public int? Code { get; init; }

    public static LogcoreEvent Parse(JsonElement root)
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
            },
            _ => new LogcoreEvent { Type = type },
        };
    }
}

/// <summary>页行（布局 v1）：原文与按字节区间切片后的时间 / 消息。</summary>
public sealed class LogcorePageRow
{
    public uint Ord { get; init; }
    public byte Level { get; init; }
    public int Pid { get; init; }
    public int Tid { get; init; }
    public string? Tag { get; init; }
    public byte[] Raw { get; init; } = Array.Empty<byte>();
    public string Time { get; init; } = "";
    public string Message { get; init; } = "";
}
