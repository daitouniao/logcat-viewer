using logcat.Models;
using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// CommandStore：内置命令库、分类 CRUD、收藏、最近使用历史、占位符记忆。
/// 全部用例都直接构造内存实例，不触碰 %LOCALAPPDATA% 下的真实数据文件。
/// </summary>
public class CommandStoreTests
{
    static CommandStore NewStore(out CommandStore.StoreData data)
    {
        data = new CommandStore.StoreData();
        return new CommandStore(data);
    }

    static CommandStore Seeded()
    {
        var data = new CommandStore.StoreData();
        CommandStore.Seed(data);
        return new CommandStore(data);
    }

    // ── 内置命令库 ──

    [Fact]
    public void Seed写入内置分类与命令()
    {
        var data = new CommandStore.StoreData();
        CommandStore.Seed(data);

        Assert.Equal(new[] { "shell", "dumpsys", "应用与包", "日志与异常", "adb" }, data.Categories);
        Assert.True(data.Favorites.Count > 50);
        Assert.Contains(data.Favorites, f => f.Kind == CommandKind.Adb);
        Assert.Contains(data.Favorites, f => f.Kind == CommandKind.Shell && f.Root);
        Assert.All(data.Favorites, f => Assert.Contains(f.Category, data.Categories));
    }

    [Fact]
    public void Seed生成的收藏带备注且分类合法()
    {
        var data = new CommandStore.StoreData();
        CommandStore.Seed(data);

        var entry = data.Favorites.First(f => f.Command == "logcat -c");
        Assert.Equal("日志与异常", entry.Category);
        Assert.Equal("清空日志缓冲区", entry.Remark);
        Assert.Equal(CommandKind.Shell, entry.Kind);
        Assert.False(entry.Root);
    }

    // ── 分类 ──

    [Fact]
    public void HasCategory区分大小写()
    {
        var store = NewStore(out _);
        Assert.True(store.AddCategory("shell"));

        Assert.True(store.HasCategory("shell"));
        Assert.False(store.HasCategory("Shell"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AddCategory拒绝空名(string name)
    {
        var store = NewStore(out _);
        Assert.False(store.AddCategory(name));
        Assert.Empty(store.Categories);
    }

    [Fact]
    public void AddCategory拒绝重名()
    {
        var store = NewStore(out _);
        Assert.True(store.AddCategory("a"));
        Assert.False(store.AddCategory("a"));
        Assert.Single(store.Categories);
    }

    [Fact]
    public void AddCategory会去掉首尾空白()
    {
        var store = NewStore(out _);
        Assert.True(store.AddCategory("  net  "));
        Assert.Equal("net", store.Categories[0]);
    }

    [Fact]
    public void RenameCategory同步改写其下收藏()
    {
        var store = NewStore(out var data);
        store.AddCategory("old");
        var entry = store.AddFavorite("old", "ls", null, CommandKind.Shell, false);

        Assert.True(store.RenameCategory("old", "new"));

        Assert.Equal("new", store.Categories[0]);
        Assert.Equal("new", entry!.Category);
        Assert.Equal(1, store.CountIn("new"));
        Assert.Equal(0, store.CountIn("old"));
    }

    [Fact]
    public void RenameCategory的各拒绝分支()
    {
        var store = NewStore(out _);
        store.AddCategory("a");
        store.AddCategory("b");

        Assert.False(store.RenameCategory("", "x"));       // 旧名为空
        Assert.False(store.RenameCategory("a", ""));       // 新名为空
        Assert.False(store.RenameCategory("a", "a"));      // 新旧相同
        Assert.False(store.RenameCategory("zzz", "x"));    // 旧名不存在
        Assert.False(store.RenameCategory("a", "b"));      // 新名已存在

        Assert.Equal(new[] { "a", "b" }, store.Categories);
    }

    [Fact]
    public void RemoveCategory连带删除其下收藏()
    {
        var store = NewStore(out _);
        store.AddCategory("a");
        store.AddCategory("b");
        store.AddFavorite("a", "cmd-a", null, CommandKind.Shell, false);
        store.AddFavorite("b", "cmd-b", null, CommandKind.Shell, false);

        Assert.True(store.RemoveCategory("a"));

        Assert.Equal(new[] { "b" }, store.Categories);
        Assert.Equal(0, store.CountIn("a"));
        Assert.Single(store.Favorites);
    }

    [Fact]
    public void RemoveCategory对不存在的分类返回假()
    {
        var store = NewStore(out _);
        Assert.False(store.RemoveCategory("nope"));
    }

    // ── 收藏查询 ──

    [Fact]
    public void FavoritesIn按分类筛选_null表示全部()
    {
        var store = NewStore(out _);
        store.AddFavorite("a", "c1", null, CommandKind.Shell, false);
        store.AddFavorite("b", "c2", null, CommandKind.Shell, false);

        Assert.Equal(2, store.FavoritesIn(null).Count);
        Assert.Equal(2, store.FavoritesIn("").Count);
        Assert.Single(store.FavoritesIn("a"));
        Assert.Empty(store.FavoritesIn("zzz"));
    }

    [Fact]
    public void FindFavorite按命令与通道匹配()
    {
        var store = NewStore(out _);
        store.AddFavorite("a", "  ls -l  ", null, CommandKind.Shell, root: false);

        Assert.NotNull(store.FindFavorite("ls -l", CommandKind.Shell));
        Assert.NotNull(store.FindFavorite(" ls -l ", CommandKind.Shell));
        Assert.True(store.IsFavorite("ls -l", CommandKind.Shell));

        // 通道不同则是另一条
        Assert.Null(store.FindFavorite("ls -l", CommandKind.Adb));
        Assert.False(store.IsFavorite("ls -l", CommandKind.Adb));
    }

    // ── 收藏增删改 ──

    [Fact]
    public void AddFavorite插入到列表最前()
    {
        var store = NewStore(out _);
        store.AddFavorite("a", "first", null, CommandKind.Shell, false);
        store.AddFavorite("a", "second", null, CommandKind.Shell, false);

        Assert.Equal("second", store.Favorites[0].Command);
        Assert.Equal("first", store.Favorites[1].Command);
    }

    [Fact]
    public void AddFavorite空命令返回空()
    {
        var store = NewStore(out _);
        Assert.Null(store.AddFavorite("a", "", null, CommandKind.Shell, false));
        Assert.Null(store.AddFavorite("a", "   ", null, CommandKind.Shell, false));
    }

    [Fact]
    public void AddFavorite重复命令返回空()
    {
        var store = NewStore(out _);
        Assert.NotNull(store.AddFavorite("a", "ls", null, CommandKind.Shell, false));
        Assert.Null(store.AddFavorite("a", "ls", "另一条", CommandKind.Shell, true));
        Assert.Single(store.Favorites);
    }

    [Fact]
    public void AddFavorite空分类落到默认分类()
    {
        var store = NewStore(out _);
        var entry = store.AddFavorite("", "ls", null, CommandKind.Shell, false);

        // 写死字面量，不能拿 CommandStore.FallbackCategory 当期望值——那是断言自证：
        // 常量从 "shell" 改成 "Shell" 时两边一起变，测试照样通过（变异实测全绿）。
        // "shell" 是 UI 依赖的分类名（CommandKinds.ShellText 同值），写错会直接影响界面。
        Assert.Equal("shell", entry!.Category);
        Assert.Contains("shell", store.Categories);
    }

    [Fact]
    public void AddFavorite会自动创建未登记的分类()
    {
        var store = NewStore(out _);
        Assert.Empty(store.Categories);

        store.AddFavorite("新分类", "ls", null, CommandKind.Shell, false);

        Assert.Equal(new[] { "新分类" }, store.Categories);
    }

    [Fact]
    public void AddFavorite的空白备注归一为null()
    {
        var store = NewStore(out _);
        Assert.Null(store.AddFavorite("a", "c1", "   ", CommandKind.Shell, false)!.Remark);
        Assert.Equal("备注", store.AddFavorite("a", "c2", "  备注  ", CommandKind.Shell, false)!.Remark);
    }

    [Fact]
    public void UpdateFavorite改写字段()
    {
        var store = NewStore(out _);
        var entry = store.AddFavorite("a", "old", null, CommandKind.Shell, false)!;

        Assert.True(store.UpdateFavorite(entry, "b", "new", "r", CommandKind.Adb, true));

        Assert.Equal("b", entry.Category);
        Assert.Equal("new", entry.Command);
        Assert.Equal("r", entry.Remark);
        Assert.Equal(CommandKind.Adb, entry.Kind);
        Assert.True(entry.Root);
        Assert.Contains("b", store.Categories);
    }

    [Fact]
    public void UpdateFavorite的各拒绝分支()
    {
        var store = NewStore(out _);
        var entry = store.AddFavorite("a", "cmd", null, CommandKind.Shell, false)!;
        store.AddFavorite("a", "other", null, CommandKind.Shell, false);

        Assert.False(store.UpdateFavorite(null!, "a", "x", null, CommandKind.Shell, false));
        Assert.False(store.UpdateFavorite(entry, "a", "", null, CommandKind.Shell, false));
        // 命令与另一条已存在的收藏重复
        Assert.False(store.UpdateFavorite(entry, "a", "other", null, CommandKind.Shell, false));
        Assert.Equal("cmd", entry.Command);
    }

    [Fact]
    public void UpdateFavorite允许保留自身命令()
    {
        var store = NewStore(out _);
        var entry = store.AddFavorite("a", "cmd", null, CommandKind.Shell, false)!;

        Assert.True(store.UpdateFavorite(entry, "a", "cmd", "改备注", CommandKind.Shell, false));
        Assert.Equal("改备注", entry.Remark);
    }

    [Fact]
    public void RemoveFavorite与ClearFavorites()
    {
        var store = NewStore(out _);
        var a = store.AddFavorite("c", "a", null, CommandKind.Shell, false)!;
        store.AddFavorite("c", "b", null, CommandKind.Shell, false);

        Assert.False(store.RemoveFavorite(null!));
        Assert.True(store.RemoveFavorite(a));
        Assert.Single(store.Favorites);

        store.ClearFavorites();
        Assert.Empty(store.Favorites);
    }

    [Fact]
    public void 收藏数量不超过上限()
    {
        var store = NewStore(out _);
        for (int i = 0; i < 305; i++)
            store.AddFavorite("c", $"cmd{i}", null, CommandKind.Shell, false);

        Assert.Equal(300, store.Favorites.Count);
        Assert.Equal("cmd304", store.Favorites[0].Command);   // 最新的在最前
    }

    // ── 历史 ──

    [Fact]
    public void RecordRun首次记录次数为一()
    {
        var store = NewStore(out _);
        var run = store.RecordRun("ls", CommandKind.Shell, false);

        Assert.Equal(1, run.UseCount);
        Assert.Equal("ls", run.Command);
        Assert.Same(run, store.History[0]);
    }

    [Fact]
    public void RecordRun重复执行累加次数并提到最前()
    {
        var store = NewStore(out _);
        store.RecordRun("a", CommandKind.Shell, false);
        store.RecordRun("b", CommandKind.Shell, false);
        var run = store.RecordRun("a", CommandKind.Shell, true);

        Assert.Equal(2, run.UseCount);
        Assert.True(run.Root);
        Assert.Same(run, store.History[0]);
        Assert.Equal(2, store.History.Count);
    }

    [Fact]
    public void RecordRun忽略root差异但区分通道()
    {
        var store = NewStore(out _);
        store.RecordRun("ls", CommandKind.Shell, false);
        store.RecordRun("ls", CommandKind.Adb, false);

        Assert.Equal(2, store.History.Count);
    }

    [Fact]
    public void RecordRun去掉命令首尾空白()
    {
        var store = NewStore(out _);
        var run = store.RecordRun("  ls  ", CommandKind.Shell, false);

        Assert.Equal("ls", run.Command);
    }

    [Fact]
    public void 历史数量不超过上限()
    {
        var store = NewStore(out _);
        for (int i = 0; i < 125; i++)
            store.RecordRun($"cmd{i}", CommandKind.Shell, false);

        Assert.Equal(120, store.History.Count);
        Assert.Equal("cmd124", store.History[0].Command);
    }

    [Fact]
    public void RemoveRun与ClearHistory()
    {
        var store = NewStore(out _);
        var run = store.RecordRun("ls", CommandKind.Shell, false);

        Assert.False(store.RemoveRun(null!));
        Assert.True(store.RemoveRun(run));
        Assert.Empty(store.History);

        store.RecordRun("ls", CommandKind.Shell, false);
        store.ClearHistory();
        Assert.Empty(store.History);
    }

    // ── 占位符 ──

    [Fact]
    public void 占位符值可记忆可读取()
    {
        var store = NewStore(out _);

        Assert.Equal("", store.PlaceholderValue("pkg"));       // 未记录时为空串
        Assert.Equal("", store.PlaceholderValue(null!));

        store.RememberPlaceholder("pkg", "com.example.app");
        Assert.Equal("com.example.app", store.PlaceholderValue("pkg"));

        store.RememberPlaceholder("  tag  ", "v");
        Assert.Equal("v", store.PlaceholderValue("tag"));
    }

    [Fact]
    public void 占位符空名被忽略()
    {
        var store = NewStore(out var data);

        store.RememberPlaceholder("", "x");
        store.RememberPlaceholder("   ", "x");

        Assert.Empty(data.Placeholders);
    }

    [Fact]
    public void 占位符值允许为null并落空串()
    {
        var store = NewStore(out _);
        store.RememberPlaceholder("p", null!);
        Assert.Equal("", store.PlaceholderValue("p"));
    }
}
