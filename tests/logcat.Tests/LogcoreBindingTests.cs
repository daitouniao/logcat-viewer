using System.Text.Json;
using logcat.Services.Logcore;
using Xunit;

namespace logcat.Tests;

/// <summary>绑定层契约测试：不依赖原生库（除缺失库用例），锁定 JSON 线格式与错误映射。</summary>
public sealed class BindingMarshalingRoundtripTests
{
    [Fact]
    public void FileRequest_roundtrips_required_fields()
    {
        var request = LogcoreSourceRequest.ForFile(
            @"C:\logs\a.log",
            @"C:\temp",
            "pipe-a",
            @"C:\tools\proxy.exe",
            new[] { "--serve", @"C:\logs" },
            500,
            "Auto",
            @"C:\cache");
        var text = request.ToJson();
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        Assert.Equal("File", root.GetProperty("kind").GetString());
        Assert.Equal(@"C:\logs\a.log", root.GetProperty("path").GetString());
        Assert.Equal("Auto", root.GetProperty("mode").GetString());
        var proxy = root.GetProperty("proxy");
        Assert.Equal("pipe-a", proxy.GetProperty("pipeName").GetString());
        Assert.Equal(@"C:\tools\proxy.exe", proxy.GetProperty("launchFile").GetString());
        Assert.Equal(2, proxy.GetProperty("launchArgs").GetArrayLength());
        Assert.Equal(500, proxy.GetProperty("launchTimeoutMs").GetInt32());
        var options = root.GetProperty("options");
        Assert.Equal(@"C:\temp", options.GetProperty("tempDir").GetString());
        Assert.Equal(@"C:\cache", options.GetProperty("sidecar").GetProperty("dir").GetString());
    }

    [Fact]
    public void DeviceRequest_requires_only_temp_dir()
    {
        var text = LogcoreSourceRequest.ForDevice(@"C:\temp", "serial-1").ToJson();
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        Assert.Equal("Device", root.GetProperty("kind").GetString());
        Assert.Equal("serial-1", root.GetProperty("device").GetProperty("serial").GetString());
        Assert.False(root.TryGetProperty("proxy", out _));
        Assert.Equal(@"C:\temp", root.GetProperty("options").GetProperty("tempDir").GetString());
    }

    [Fact]
    public void Optional_fields_are_omitted_for_core_defaults()
    {
        var request = LogcoreSourceRequest.ForDevice(@"C:\temp");
        using var doc = JsonDocument.Parse(request.ToJson());
        var options = doc.RootElement.GetProperty("options");
        Assert.False(options.TryGetProperty("probeWindowBytes", out _));
        Assert.False(options.TryGetProperty("probeThreshold", out _));
        Assert.False(options.TryGetProperty("sidecar", out _));
    }

    [Fact]
    public void Descriptor_parses_wire_names_and_nulls()
    {
        const string json = """
        {
          "channel":"FileProxy","format":"Logcat","encoding":"Utf8",
          "columns":[{"id":"time","labelKey":"column.time","width":150}],
          "identity":null,"loadMode":"Progressive","rowCount":3,"docSize":42,
          "appendMode":"ReloadOnly","leaseKind":"Ephemeral","leaseId":"spool",
          "timeSemantics":"Original","displayPath":null,"sourcePath":"C:\\spool\\a.log"
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var desc = LogcoreDescriptor.Parse(doc.RootElement);
        Assert.Equal("FileProxy", desc.Channel);
        Assert.Null(desc.Identity);
        Assert.Null(desc.DisplayPath);
        Assert.Equal("Ephemeral", desc.LeaseKind);
        Assert.Equal((ulong)3, desc.RowCount);
    }
}

public sealed class BindingErrorCodeMappingTests
{
    [Fact]
    public void Code_names_match_d15_table()
    {
        Assert.Equal("OK", LogcoreErrorCodes.Name(0));
        Assert.Equal("CLOSED", LogcoreErrorCodes.Name(4));
        Assert.Equal("INVALID_ARG", LogcoreErrorCodes.Name(6));
        Assert.Equal("PROXY_LAUNCH_TIMEOUT", LogcoreErrorCodes.Name(10));
        Assert.Equal("PROXY_LAUNCH_FAILED", LogcoreErrorCodes.Name(16));
        Assert.Equal(LogcoreErrorCodes.MaxFeedLines, 65536);
    }

    [Fact]
    public void Error_params_and_field_are_readable()
    {
        var error = LogcoreException.FromJson("""
        {"code":6,"params":{"field":"proxy.launchFile"}}
        """);
        Assert.Equal(LogcoreErrorCodes.InvalidArg, error.Code);
        Assert.Equal("proxy.launchFile", error.Field);
        Assert.Contains("INVALID_ARG", error.Message);
        Assert.Contains("proxy.launchFile", error.Message);
    }

    [Fact]
    public void Params_without_field_are_preserved()
    {
        var error = LogcoreException.FromJson("""
        {"code":16,"params":{"proxyPath":"C:\\missing.exe","osError":2}}
        """);
        Assert.Null(error.Field);
        Assert.Equal(@"C:\missing.exe", error.Params["proxyPath"].GetString());
        Assert.Equal(2, error.Params["osError"].GetInt32());
    }
}

public sealed class BindingCommitShapeTests
{
    [Fact]
    public void Commit_accept_descriptor_has_zero_rows_and_static_channel()
    {
        const string json = """
        {
          "channel":"Local","format":"Logcat","encoding":"Utf8","columns":[],
          "identity":{"size":10,"mtimeMs":1,"headHash":"0000000000000001","tailHash":"0000000000000001"},
          "loadMode":"Progressive","rowCount":0,"docSize":0,"appendMode":"ReloadOnly",
          "leaseKind":"None","leaseId":null,"timeSemantics":"Original",
          "displayPath":"C:\\a.log","sourcePath":"C:\\a.log"
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var desc = LogcoreDescriptor.Parse(doc.RootElement);
        Assert.Equal((ulong)0, desc.RowCount);
        Assert.Equal("Local", desc.Channel);
        Assert.Equal("Progressive", desc.LoadMode);
        Assert.NotNull(desc.Identity);
        Assert.Equal(16, desc.Identity!.HeadHash.Length);
    }

    [Fact]
    public void Plan_json_shape_is_route_and_estimated_size()
    {
        using var doc = JsonDocument.Parse("""{"route":"DeviceStream","estimatedSize":null}""");
        Assert.Equal("DeviceStream", doc.RootElement.GetProperty("route").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("estimatedSize").ValueKind);
    }
}

public sealed class BindingEventsMarshalingTests
{
    [Fact]
    public void All_five_event_types_parse()
    {
        const string json = """
        [
          {"type":"stage","stage":"Draining","value":0,"total":5},
          {"type":"appended","rows":2,"totalRows":7},
          {"type":"stopped"},
          {"type":"done"},
          {"type":"error","code":12,"params":{"path":"C:\\gone.log"}}
        ]
        """;
        using var doc = JsonDocument.Parse(json);
        var events = doc.RootElement.EnumerateArray().Select(LogcoreEvent.Parse).ToArray();
        Assert.Equal(new[] { "stage", "appended", "stopped", "done", "error" }, events.Select(e => e.Type));
        Assert.Equal("Draining", events[0].Stage);
        Assert.Equal((ulong?)5, events[0].Total);
        Assert.Equal((ulong?)2, events[1].Rows);
        Assert.Equal((ulong?)7, events[1].TotalRows);
        Assert.Equal(12, events[4].Code);
        Assert.Equal("C:\\gone.log", events[4].Params!["path"].GetString());
    }

    [Fact]
    public void Empty_event_array_is_legal()
    {
        using var doc = JsonDocument.Parse("[]");
        Assert.Empty(doc.RootElement.EnumerateArray().Select(LogcoreEvent.Parse));
    }
}

public sealed class BindingStartupMaintainTests
{
    [Fact]
    public void Maintain_request_and_stats_roundtrip()
    {
        var request = new LogcoreMaintainRequest
        {
            CacheDir = @"C:\cache",
            TempDir = @"C:\temp",
            EntryLimitBytes = 1024,
        };
        using (var doc = JsonDocument.Parse(request.ToJson()))
        {
            Assert.Equal(@"C:\cache", doc.RootElement.GetProperty("cacheDir").GetString());
            Assert.Equal(1024, doc.RootElement.GetProperty("entryLimitBytes").GetInt64());
            Assert.False(doc.RootElement.TryGetProperty("dirLimitBytes", out _));
        }

        using var stats = JsonDocument.Parse("""
        {"status":"Partial","removedCorrupt":1,"removedTempWrites":2,"removedOrphans":3,
         "skippedInUse":4,"evicted":5,"kept":6,"freedBytes":7}
        """);
        var result = LogcoreMaintainResult.Parse(stats.RootElement);
        Assert.True(result.IsPartial);
        Assert.Equal((ulong)7, result.FreedBytes);
        Assert.Equal((ulong)6, result.Kept);
    }
}

public sealed class BindingMissingLibraryThrowsClearError
{
    [Fact]
    public void Missing_library_message_names_path_and_build_step()
    {
        var missing = Path.Combine(Path.GetTempPath(), "logcore-missing-" + Guid.NewGuid().ToString("N"), "logcore.dll");
        var error = Assert.Throws<LogcoreUnavailableException>(() => Logcore.UseLibrary(missing));
        Assert.Contains("logcore.dll", error.Message);
        Assert.Contains("cargo build", error.Message);
        Assert.Contains(missing, error.Message);
    }
}

public sealed class BindingFeedLimitConstantTests
{
    [Fact]
    public void Binding_limit_equals_core_constant()
    {
        var root = LogcoreTestSupport.RepoRoot();
        var source = File.ReadAllText(Path.Combine(root, "logcore", "src", "source.rs"));
        var marker = "pub const MAX_FEED_LINES: usize = ";
        var at = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, "core 未声明 MAX_FEED_LINES");
        var rest = source[(at + marker.Length)..];
        var digits = new string(rest.TakeWhile(char.IsDigit).ToArray());
        Assert.Equal(int.Parse(digits), LogcoreErrorCodes.MaxFeedLines);
    }
}