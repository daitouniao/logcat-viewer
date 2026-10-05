using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// FavoritesStore：收藏目录、run-as 包名、APK 路径、应用包名、tag/msg 过滤词。
/// 直接构造内存实例，不落盘。
/// </summary>
public class FavoritesStoreTests
{
    static FavoritesStore NewStore() => new(new FavoritesStore.StoreData());

    // ── 预置数据 ──

    [Fact]
    public void SeedDefaults写入设备端与本机端预置目录()
    {
        var store = NewStore();
        FavoritesStore.SeedDefaults(store);

        Assert.Equal(4, store.Get(true).Count);
        Assert.Contains(store.Get(true), f => f.Path == "/sdcard");
        Assert.Contains(store.Get(true), f => f.Path == "/data/local/tmp");

        // 本机端预置依赖用户目录（Desktop/Downloads/Documents），个别环境可能缺失，
        // 所以按实际可用的目录数断言，不写死 3
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expected = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            System.IO.Path.Combine(home, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        }.Where(p => !string.IsNullOrEmpty(p)).ToList();

        Assert.Equal(expected.Count, store.Get(false).Count);
        Assert.All(expected, p => Assert.Contains(store.Get(false), f => f.Path == p));
    }

    // ── 收藏目录 ──

    [Fact]
    public void Add插入到最前并支持别名()
    {
        var store = NewStore();
        Assert.True(store.Add(true, "/sdcard", "外置存储"));
        Assert.True(store.Add(true, "/data"));

        Assert.Equal("/data", store.Get(true)[0].Path);
        Assert.Equal("外置存储", store.Get(true)[1].Alias);
    }

    [Fact]
    public void Add拒绝空路径与重复路径()
    {
        var store = NewStore();
        Assert.False(store.Add(true, ""));
        Assert.False(store.Add(true, "   "));

        Assert.True(store.Add(true, "/sdcard"));
        Assert.False(store.Add(true, "/sdcard"));
        Assert.Single(store.Get(true));
    }

    [Fact]
    public void 设备端路径忽略末尾斜杠()
    {
        var store = NewStore();
        store.Add(true, "/sdcard");

        Assert.True(store.Contains(true, "/sdcard/"));
        Assert.False(store.Add(true, "/sdcard/"));
    }

    [Fact]
    public void 设备端路径区分大小写_本机端不区分()
    {
        var store = NewStore();
        store.Add(true, "/SDCard");
        store.Add(false, @"C:\Foo");

        Assert.False(store.Contains(true, "/sdcard"));
        Assert.True(store.Contains(false, @"c:\foo"));
    }

    [Fact]
    public void 别名空白归一为null()
    {
        var store = NewStore();
        store.Add(true, "/a", "   ");
        Assert.Null(store.Get(true)[0].Alias);
    }

    [Fact]
    public void SetAlias与Remove()
    {
        var store = NewStore();
        store.Add(true, "/a");

        Assert.True(store.SetAlias(true, "/a", "别名"));
        Assert.Equal("别名", store.Get(true)[0].Alias);
        Assert.False(store.SetAlias(true, "/missing", "x"));

        Assert.False(store.Remove(true, "/missing"));
        Assert.True(store.Remove(true, "/a"));
        Assert.Empty(store.Get(true));
    }

    [Fact]
    public void Clear只清空指定端()
    {
        var store = NewStore();
        store.Add(true, "/a");
        store.Add(false, @"C:\b");

        store.Clear(true);

        Assert.Empty(store.Get(true));
        Assert.Single(store.Get(false));
    }

    [Fact]
    public void 收藏目录数量不超过上限且淘汰最旧()
    {
        var store = NewStore();
        for (int i = 0; i < 65; i++)
            store.Add(true, $"/dir{i}");

        Assert.Equal(60, store.Get(true).Count);
        // 只断言数量抓不住「淘汰方向反了」：AddRecent 丢最旧才是本功能的意义
        Assert.Equal("/dir64", store.Get(true)[0].Path);
        Assert.DoesNotContain(store.Get(true), f => f.Path == "/dir0");
    }

    // ── run-as 包名 ──

    [Fact]
    public void AddRunAsPackage去重且最近在前()
    {
        var store = NewStore();

        Assert.True(store.AddRunAsPackage("com.a"));
        Assert.True(store.AddRunAsPackage("  com.b  "));
        Assert.False(store.AddRunAsPackage("com.a"));
        Assert.False(store.AddRunAsPackage("   "));
        Assert.False(store.AddRunAsPackage(null!));

        Assert.Equal(new[] { "com.b", "com.a" }, store.RunAsPackages);
    }

    [Fact]
    public void RemoveRunAsPackage()
    {
        var store = NewStore();
        store.AddRunAsPackage("com.a");

        Assert.False(store.RemoveRunAsPackage("com.b"));
        Assert.True(store.RemoveRunAsPackage("com.a"));
        Assert.Empty(store.RunAsPackages);
    }

    [Fact]
    public void runAs包名数量不超过上限()
    {
        var store = NewStore();
        for (int i = 0; i < 65; i++)
            store.AddRunAsPackage($"com.p{i}");

        Assert.Equal(60, store.RunAsPackages.Count);
    }

    // ── APK 路径 / 应用包名 / 过滤词（共用 AddRecent）──

    [Fact]
    public void APK路径收藏去重且最近在前()
    {
        var store = NewStore();

        Assert.True(store.AddApkPath(@"D:\a.apk"));
        Assert.True(store.AddApkPath(@"D:\b.apk"));
        Assert.True(store.AddApkPath(@"D:\a.apk"));      // 重复：去重后提到最前，仍返回 true

        Assert.Equal(new[] { @"D:\a.apk", @"D:\b.apk" }, store.ApkPaths);
        Assert.True(store.RemoveApkPath(@"D:\a.apk"));
        Assert.False(store.RemoveApkPath(@"D:\a.apk"));
    }

    [Fact]
    public void APK路径空值被忽略()
    {
        var store = NewStore();

        Assert.False(store.AddApkPath(""));
        Assert.False(store.AddApkPath("   "));
        Assert.Empty(store.ApkPaths);
    }

    [Fact]
    public void 应用包名收藏()
    {
        var store = NewStore();

        Assert.True(store.AddPackage("com.a"));
        Assert.True(store.AddPackage("com.b"));
        Assert.Equal(new[] { "com.b", "com.a" }, store.Packages);
        Assert.True(store.RemovePackage("com.a"));
        Assert.Single(store.Packages);
    }

    [Fact]
    public void tag过滤词收藏去重时忽略大小写()
    {
        var store = NewStore();

        store.AddTagFilter("ActivityManager");
        store.AddTagFilter("activitymanager");   // 去重后提到最前，值取最后一次

        Assert.Single(store.TagFilters);
        Assert.Equal("activitymanager", store.TagFilters[0]);
    }

    [Fact]
    public void tag过滤词删除是大小写敏感的()
    {
        var store = NewStore();
        store.AddTagFilter("activitymanager");

        Assert.False(store.RemoveTagFilter("ACTIVITYMANAGER"));
        Assert.True(store.RemoveTagFilter("activitymanager"));
        Assert.Empty(store.TagFilters);
    }

    [Fact]
    public void msg过滤词收藏()
    {
        var store = NewStore();

        Assert.True(store.AddMsgFilter("timeout"));
        Assert.False(store.AddMsgFilter("  "));
        Assert.Equal(new[] { "timeout" }, store.MsgFilters);

        Assert.True(store.RemoveMsgFilter("timeout"));
        Assert.Empty(store.MsgFilters);
    }

    [Fact]
    public void ContainsTagFilter大小写不敏感()
    {
        var store = NewStore();
        store.AddTagFilter("ActivityManager");

        Assert.True(store.ContainsTagFilter("ActivityManager"));
        Assert.True(store.ContainsTagFilter("activitymanager"));
        Assert.True(store.ContainsTagFilter("ACTIVITYMANAGER"));
        Assert.False(store.ContainsTagFilter("nonexistent"));
    }

    [Fact]
    public void ContainsMsgFilter大小写不敏感()
    {
        var store = NewStore();
        store.AddMsgFilter("TimeoutException");

        Assert.True(store.ContainsMsgFilter("TimeoutException"));
        Assert.True(store.ContainsMsgFilter("timeoutexception"));
        Assert.False(store.ContainsMsgFilter("timeout"));
        Assert.False(store.ContainsMsgFilter(""));
    }

    [Fact]
    public void ToggleTagFilter大小写变体移除原条目()
    {
        var store = NewStore();
        store.AddTagFilter("activitymanager");

        // 大小写变体调用 Toggle → 应找到并移除原条目
        bool result = store.ToggleTagFilter("ACTIVITYMANAGER");
        Assert.False(result); // 已被移除
        Assert.Empty(store.TagFilters);
    }

    [Fact]
    public void ToggleMsgFilter大小写变体移除原条目()
    {
        var store = NewStore();
        store.AddMsgFilter("TimeoutException");

        bool result = store.ToggleMsgFilter("TIMEOUTEXCEPTION");
        Assert.False(result);
        Assert.Empty(store.MsgFilters);
    }

    [Fact]
    public void ToggleTagFilter不存在则新增()
    {
        var store = NewStore();

        bool result = store.ToggleTagFilter("newtag");
        Assert.True(result);
        Assert.Single(store.TagFilters);
        Assert.Equal("newtag", store.TagFilters[0]);
    }

    [Fact]
    public void ToggleTagFilter空值返回false且不改变列表()
    {
        var store = NewStore();
        store.AddTagFilter("existing");

        Assert.False(store.ToggleTagFilter(""));
        Assert.False(store.ToggleTagFilter("  "));
        Assert.False(store.ToggleTagFilter(null!));
        Assert.Single(store.TagFilters);
    }

    [Fact]
    public void AddTagFilter超过60条后最旧被淘汰()
    {
        var store = NewStore();
        for (int i = 0; i < 65; i++)
            store.AddTagFilter($"tag{i}");

        Assert.Equal(60, store.TagFilters.Count);
        Assert.Equal("tag64", store.TagFilters[0]);
        Assert.Equal("tag5", store.TagFilters[59]);
        Assert.DoesNotContain("tag0", store.TagFilters);
    }

    [Fact]
    public void 过滤词数量不超过上限()
    {
        var store = NewStore();
        for (int i = 0; i < 65; i++)
            store.AddTagFilter($"tag{i}");

        Assert.Equal(60, store.TagFilters.Count);
    }

    // ── 路径工具 ──

    [Theory]
    [InlineData("/sdcard", true, "/sdcard")]
    [InlineData("/sdcard/", true, "/sdcard")]
    [InlineData("/sdcard///", true, "/sdcard")]
    [InlineData("", true, "/")]
    [InlineData("/", true, "/")]
    [InlineData("  /a  ", true, "/a")]
    public void Normalize设备端路径(string input, bool remote, string expected)
    {
        Assert.Equal(expected, FavoritesStore.Normalize(input, remote));
    }

    [Theory]
    [InlineData(@"C:\", @"C:\")]              // 长度 3，不能裁掉反斜杠
    [InlineData(@"C:\foo\", @"C:\foo")]
    [InlineData(@"C:\foo", @"C:\foo")]
    [InlineData("  ", "")]
    public void Normalize本机路径(string input, string expected)
    {
        Assert.Equal(expected, FavoritesStore.Normalize(input, remote: false));
    }

    [Fact]
    public void PathEquals按端类型决定大小写敏感性()
    {
        Assert.True(FavoritesStore.PathEquals("/a", "/a/", true));
        Assert.False(FavoritesStore.PathEquals("/A", "/a", true));

        Assert.True(FavoritesStore.PathEquals(@"C:\A", @"c:\a", false));
        Assert.True(FavoritesStore.PathEquals(@"C:\a\", @"C:\a", false));
    }

    [Fact]
    public void Get按端类型返回对应列表()
    {
        var store = NewStore();
        store.Add(true, "/a");
        store.Add(false, @"C:\b");

        Assert.Single(store.Get(true));
        Assert.Single(store.Get(false));
        Assert.Equal("/a", store.Get(true)[0].Path);
    }
}
