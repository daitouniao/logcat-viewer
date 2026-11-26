using Xunit;
using Next.App.Storage;

namespace Next.Tests;

/// <summary>设置层（tasks 2.1 / 2.2）：版本、容错读、原子写、失败降级、互不覆盖。</summary>
[Collection("startup-log")]
public class SettingsStoreTests
{
    static string TempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "logcat-next-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void RoundTripsKnownKeys()
    {
        var path = Path.Combine(TempDirectory(), "next-settings.json");
        var store = new JsonNextSettingsStore(path);

        store.Save(new NextSettings { Lang = "en", Theme = "dark" });
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal("en", loaded!.Lang);
        Assert.Equal("dark", loaded.Theme);
    }

    [Fact]
    public void SaveWritesSchemaVersionAndPascalCaseKeys()
    {
        var path = Path.Combine(TempDirectory(), "next-settings.json");
        new JsonNextSettingsStore(path).Save(new NextSettings { Lang = "zh-CN", Theme = "light" });

        var text = File.ReadAllText(path);
        Assert.Contains("\"SchemaVersion\": 1", text);
        Assert.Contains("\"Lang\":", text);
        Assert.Contains("\"Theme\":", text);
    }

    [Fact]
    public void CorruptFileYieldsDefaults()
    {
        var path = Path.Combine(TempDirectory(), "next-settings.json");
        File.WriteAllText(path, "{ not json");

        Assert.Null(new JsonNextSettingsStore(path).Load());
    }

    [Fact]
    public void UnknownVersionYieldsDefaults()
    {
        var path = Path.Combine(TempDirectory(), "next-settings.json");
        File.WriteAllText(path, """{ "SchemaVersion": 2, "Lang": "en" }""");

        Assert.Null(new JsonNextSettingsStore(path).Load());
    }

    [Fact]
    public void MissingVersionYieldsDefaults()
    {
        var path = Path.Combine(TempDirectory(), "next-settings.json");
        File.WriteAllText(path, """{ "Lang": "en", "Theme": "dark" }""");

        Assert.Null(new JsonNextSettingsStore(path).Load());
    }

    [Fact]
    public void IgnoresUnknownKeys()
    {
        var path = Path.Combine(TempDirectory(), "next-settings.json");
        File.WriteAllText(path, """{ "SchemaVersion": 1, "Lang": "en", "FutureKey": 42 }""");

        var loaded = new JsonNextSettingsStore(path).Load();
        Assert.Equal("en", loaded?.Lang);
    }

    /// <summary>中断残留的半成品不应被读取：只有完整替换后的文件才算数。</summary>
    [Fact]
    public void InterruptedWriteLeavesPreviousFile()
    {
        var dir = TempDirectory();
        var path = Path.Combine(dir, "next-settings.json");
        var store = new JsonNextSettingsStore(path);
        store.Save(new NextSettings { Lang = "zh-CN", Theme = "light" });
        File.WriteAllText(path + ".tmp", "{ broken");

        var loaded = store.Load();
        Assert.Equal("zh-CN", loaded?.Lang);
        Assert.Equal("light", loaded?.Theme);
    }

    [Fact]
    public void SaveFailureDoesNotThrow()
    {
        var dir = TempDirectory();
        var fileAsParent = Path.Combine(dir, "blocked");
        File.WriteAllText(fileAsParent, "x");
        var store = new JsonNextSettingsStore(Path.Combine(fileAsParent, "next-settings.json"));

        store.Save(new NextSettings { Lang = "en" });
    }

    [Fact]
    public void SaveFailureWritesLog()
    {
        var dir = TempDirectory();
        var logPath = Path.Combine(dir, "next-startup.log");
        var fileAsParent = Path.Combine(dir, "blocked");
        File.WriteAllText(fileAsParent, "x");
        try
        {
            StartupLog.Initialize(logPath);
            new JsonNextSettingsStore(Path.Combine(fileAsParent, "next-settings.json"))
                .Save(new NextSettings { Lang = "en" });

            Assert.Contains("设置保存失败", File.ReadAllText(logPath));
        }
        finally
        {
            StartupLog.Initialize(null);
        }
    }

    [Fact]
    public void ThemeAndLanguagePersistIndependently()
    {
        var path = Path.Combine(TempDirectory(), "next-settings.json");
        var store = new JsonNextSettingsStore(path);
        store.Save(new NextSettings { Lang = "zh-CN", Theme = "dark" });

        var service = new NextSettingsService(store);
        service.SetTheme("light");
        service.SetLanguage("en");

        var reloaded = new JsonNextSettingsStore(path).Load();
        Assert.Equal("en", reloaded?.Lang);
        Assert.Equal("light", reloaded?.Theme);
    }

    [Fact]
    public void SessionOnlyStoreKeepsValuesInMemory()
    {
        var service = new NextSettingsService(new InMemorySettingsStore());
        service.SetLanguage("en");
        service.SetTheme("dark");

        Assert.Equal("en", service.Lang);
        Assert.Equal("dark", service.Theme);
    }
}
