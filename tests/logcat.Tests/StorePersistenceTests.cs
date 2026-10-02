using System.Drawing;
using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// 三个存储类（CommandStore / FavoritesStore / AppSettings）的落盘路径：
/// 首次落盘、反序列化时的缺字段兜底、种子自愈、文件损坏重建、保存回读。
/// 全部通过注入的临时路径访问，不触碰 %LOCALAPPDATA% 下的真实数据文件。
/// </summary>
public class StorePersistenceTests
{
    // ── CommandStore ──

    [Fact]
    public void CommandStore_首次加载落盘并写入内置命令库()
    {
        using var file = new TempStoreFile(null);
        Assert.False(file.Exists);

        var store = CommandStore.LoadFrom(file.Path);

        Assert.True(file.Exists);                                  // 首次加载会立刻落盘
        Assert.Equal(new[] { "shell", "dumpsys", "应用与包", "日志与异常", "adb" }, store.Categories);
        Assert.True(store.Favorites.Count > 50);
    }

    [Fact]
    public void CommandStore_保存后能读回而不是重新种子()
    {
        using var file = new TempStoreFile(null);
        var store = CommandStore.LoadFrom(file.Path);

        store.AddCategory("自定义");
        store.Save();

        var again = CommandStore.LoadFrom(file.Path);
        Assert.Contains("自定义", again.Categories);
        Assert.Equal(store.Favorites.Count, again.Favorites.Count);
    }

    /// <summary>旧版本写的文件可能缺字段；分类为空时还要把内置命令库补回去（自愈）。</summary>
    [Fact]
    public void CommandStore_旧配置缺字段时补默认且分类为空时自愈()
    {
        using var file = new TempStoreFile("""{"Categories":[],"Favorites":[]}""");

        var store = CommandStore.LoadFrom(file.Path);

        Assert.NotEmpty(store.Categories);           // Categories 为空 → Seed 自愈
        Assert.True(store.Favorites.Count > 50);
        Assert.Empty(store.History);                 // 缺失字段补成空集合而非 null
        Assert.Equal("", store.PlaceholderValue("pkg"));
    }

    [Fact]
    public void CommandStore_文件损坏时重建而不抛异常()
    {
        using var file = new TempStoreFile("{ 这不是 JSON");

        var store = CommandStore.LoadFrom(file.Path);

        Assert.NotEmpty(store.Categories);
        Assert.True(file.Exists);                    // 重建后覆盖写回
    }

    /// <summary>每个实例记住自己的路径：SaveTo 写到指定文件，另一个文件不受影响。</summary>
    [Fact]
    public void CommandStore_保存写回指定路径()
    {
        using var a = new TempStoreFile(null);
        using var b = new TempStoreFile(null);
        var store = CommandStore.LoadFrom(a.Path);

        store.AddCategory("临时分类");
        store.SaveTo(b.Path);

        Assert.Contains("临时分类", CommandStore.LoadFrom(b.Path).Categories);
        Assert.DoesNotContain("临时分类", CommandStore.LoadFrom(a.Path).Categories);
    }

    [Fact]
    public void CommandStore_保存触发Changed()
    {
        using var file = new TempStoreFile(null);
        var store = CommandStore.LoadFrom(file.Path);

        int changed = 0;
        store.Changed += (_, _) => changed++;
        store.SaveTo(file.Path);

        Assert.Equal(1, changed);
    }

    // ── FavoritesStore ──

    [Fact]
    public void FavoritesStore_首次加载落盘并写入预置收藏目录()
    {
        using var file = new TempStoreFile(null);

        var store = FavoritesStore.LoadFrom(file.Path);

        Assert.True(file.Exists);
        Assert.Equal(4, store.Get(true).Count);
        Assert.True(store.Contains(true, "/sdcard"));
        Assert.True(store.Contains(true, "/sdcard/Download"));
    }

    /// <summary>文件存在但字段缺失（早期版本 / 手工编辑）时要补成空集合，而不是返回 null 集合。</summary>
    [Fact]
    public void FavoritesStore_缺字段时补成空集合()
    {
        using var file = new TempStoreFile("{}");

        var store = FavoritesStore.LoadFrom(file.Path);

        Assert.Empty(store.Get(true));
        Assert.Empty(store.Get(false));
        Assert.Empty(store.RunAsPackages);
        Assert.Empty(store.ApkPaths);
        Assert.Empty(store.Packages);
        Assert.Empty(store.TagFilters);
        Assert.Empty(store.MsgFilters);
    }

    [Fact]
    public void FavoritesStore_文件损坏时重建()
    {
        using var file = new TempStoreFile("不是 JSON");

        var store = FavoritesStore.LoadFrom(file.Path);

        Assert.True(store.Contains(true, "/sdcard"));
    }

    [Fact]
    public void FavoritesStore_保存后能读回()
    {
        using var file = new TempStoreFile(null);
        var store = FavoritesStore.LoadFrom(file.Path);

        store.Add(true, "/sdcard/Test", "别名");
        store.AddRunAsPackage("com.example");
        store.AddTagFilter("Network");
        store.Save();

        var again = FavoritesStore.LoadFrom(file.Path);
        Assert.True(again.Contains(true, "/sdcard/Test"));
        Assert.Equal("别名", again.Get(true).First(f => f.Path == "/sdcard/Test").Alias);
        Assert.Contains("com.example", again.RunAsPackages);
        Assert.Contains("Network", again.TagFilters);
    }

    // ── AppSettings ──

    [Fact]
    public void AppSettings_文件不存在时返回默认值且不创建文件()
    {
        using var file = new TempStoreFile(null);

        var s = AppSettings.LoadFrom(file.Path);

        Assert.True(s.Join);
        Assert.Equal(10, s.FontPt);
        Assert.Equal("/sdcard", s.LastRemotePath);
        Assert.Equal(file.Path, s.StorePath);
        Assert.False(file.Exists);                   // 只读不写
    }

    [Fact]
    public void AppSettings_保存后能读回且路径不写进JSON()
    {
        using var file = new TempStoreFile(null);
        var s = new AppSettings(file.Path)
        {
            Join = false,
            FontPt = 14,
            WindowLocation = new Point(120, 80),
            WindowSize = new Size(1280, 800),
            LastLocalPath = "D:/logs",
        };
        s.DevicePaneColumnWidths.AddRange(new[] { 100, 200 });

        s.Save();

        Assert.DoesNotContain("StorePath", file.Read());   // 路径是运行时绑定，不进配置文件

        var again = AppSettings.LoadFrom(file.Path);
        Assert.False(again.Join);
        Assert.Equal(14, again.FontPt);
        Assert.Equal(new Point(120, 80), again.WindowLocation);
        Assert.Equal(new Size(1280, 800), again.WindowSize);
        Assert.Equal("D:/logs", again.LastLocalPath);
        Assert.Equal(new[] { 100, 200 }, again.DevicePaneColumnWidths);
        Assert.Equal(file.Path, again.StorePath);          // 反序列化后路径要重新绑到实际加载的文件
    }

    [Fact]
    public void AppSettings_文件损坏时回退默认值()
    {
        using var file = new TempStoreFile("{ 坏掉的 json");

        var s = AppSettings.LoadFrom(file.Path);

        Assert.True(s.Join);
        Assert.Equal(10, s.FontPt);
    }

    [Fact]
    public void AppSettings_SaveTo写回指定路径()
    {
        using var a = new TempStoreFile(null);
        using var b = new TempStoreFile(null);
        var s = AppSettings.LoadFrom(a.Path);

        s.FontPt = 16;
        s.SaveTo(b.Path);

        Assert.Equal(16, AppSettings.LoadFrom(b.Path).FontPt);
        Assert.False(File.Exists(a.Path));                 // a 从未被写过
    }

    // ── 写入失败（磁盘不可写）──

    /// <summary>
    /// 三个 Store 的 Save 都吞掉 IO 异常（磁盘不可写时数据仅在当前会话生效），
    /// 但事件仍要触发——界面据此知道内存里的数据变了。
    /// </summary>
    [Fact]
    public void 保存到不可写路径时不抛异常但事件仍触发()
    {
        using var commandFile = new TempStoreFile(null);
        using var favoriteFile = new TempStoreFile(null);
        using var settingsFile = new TempStoreFile(null);

        var commands = CommandStore.LoadFrom(commandFile.Path);
        int commandChanged = 0;
        commands.Changed += (_, _) => commandChanged++;
        Assert.Null(Record.Exception(() => commands.SaveTo("\0")));
        Assert.Equal(1, commandChanged);

        var favorites = FavoritesStore.LoadFrom(favoriteFile.Path);
        int favoriteChanged = 0;
        favorites.Changed += (_, _) => favoriteChanged++;
        Assert.Null(Record.Exception(() => favorites.SaveTo("\0")));
        Assert.Equal(1, favoriteChanged);

        var settings = AppSettings.LoadFrom(settingsFile.Path);
        Assert.Null(Record.Exception(() => settings.SaveTo("\0")));
    }
}
