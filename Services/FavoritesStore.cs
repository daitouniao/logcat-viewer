using System.Text.Json;
using logcat.Models;

namespace logcat.Services;

/// <summary>
/// 目录收藏存储。设备端与本机端分开保存，
/// 数据以 JSON 形式持久化到 %LOCALAPPDATA%\logcat\favorites.json。
/// </summary>
public sealed class FavoritesStore
{
    const int MaxPerList = 60;

    /// <summary>internal：供单元测试构造不落盘的实例（见 InternalsVisibleTo）。</summary>
    internal sealed class StoreData
    {
        public List<FavoriteDir> Remote { get; set; } = new();
        public List<FavoriteDir> Local { get; set; } = new();

        /// <summary>访问过的 run-as 包名（最近在前）。</summary>
        public List<string> RunAsPackages { get; set; } = new();

        /// <summary>安装过的 APK 本机路径（最近在前）。</summary>
        public List<string> ApkPaths { get; set; } = new();

        /// <summary>安装/卸载收藏的应用包名（最近在前）。</summary>
        public List<string> Packages { get; set; } = new();

        /// <summary>收藏的 tag 过滤内容（最近在前）。</summary>
        public List<string> TagFilters { get; set; } = new();

        /// <summary>收藏的 message 过滤内容（最近在前）。</summary>
        public List<string> MsgFilters { get; set; } = new();
    }

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "logcat", "favorites.json");

    static FavoritesStore? _default;

    /// <summary>进程内共享实例（首次访问时从磁盘加载）。</summary>
    public static FavoritesStore Default => _default ??= Load();

    readonly StoreData _data;

    /// <summary>收藏内容发生变化（增删、改名、清空）后触发。</summary>
    public event EventHandler? Changed;

    /// <summary>internal：供单元测试构造不落盘的实例（见 InternalsVisibleTo）。</summary>
    internal FavoritesStore(StoreData data) => _data = data;

    // ── 加载 / 保存 ──

    static FavoritesStore Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var data = JsonSerializer.Deserialize<StoreData>(File.ReadAllText(FilePath), JsonOpts);
                if (data != null)
                {
                    data.Remote ??= new List<FavoriteDir>();
                    data.Local ??= new List<FavoriteDir>();
                    data.RunAsPackages ??= new List<string>();
                    data.ApkPaths ??= new List<string>();
                    data.Packages ??= new List<string>();
                    data.TagFilters ??= new List<string>();
                    data.MsgFilters ??= new List<string>();
                    return new FavoritesStore(data);
                }
            }
        }
        catch
        {
            // 文件损坏时重建
        }

        var store = new FavoritesStore(new StoreData());
        SeedDefaults(store);
        store.Save();
        return store;
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_data, JsonOpts));
        }
        catch
        {
            // 磁盘不可写时忽略，收藏仅在当前会话生效
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>写入预置收藏目录。internal：供单元测试直接注入初始数据。</summary>
    internal static void SeedDefaults(FavoritesStore store)
    {
        foreach (var p in new[] { "/sdcard", "/sdcard/Download", "/sdcard/Android/data", "/data/local/tmp" })
            store.Add(true, p);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var p in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                     Path.Combine(home, "Downloads"),
                     Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                 })
        {
            if (!string.IsNullOrEmpty(p)) store.Add(false, p);
        }
    }

    // ── 读写 ──

    public List<FavoriteDir> Get(bool remote) => remote ? _data.Remote : _data.Local;

    public bool Contains(bool remote, string path) => IndexOf(remote, path) >= 0;

    public bool Add(bool remote, string path, string? alias = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (Contains(remote, path)) return false;

        var list = Get(remote);
        list.Insert(0, new FavoriteDir { Path = path.Trim(), Alias = NullIfBlank(alias) });
        while (list.Count > MaxPerList) list.RemoveAt(list.Count - 1);
        return true;
    }

    public bool Remove(bool remote, string path)
    {
        int i = IndexOf(remote, path);
        if (i < 0) return false;
        Get(remote).RemoveAt(i);
        return true;
    }

    public bool SetAlias(bool remote, string path, string? alias)
    {
        int i = IndexOf(remote, path);
        if (i < 0) return false;
        Get(remote)[i].Alias = NullIfBlank(alias);
        return true;
    }

    public void Clear(bool remote) => Get(remote).Clear();

    // ── run-as 包名收藏 ──

    /// <summary>访问过的 run-as 包名（最近在前）。</summary>
    public List<string> RunAsPackages => _data.RunAsPackages;

    public bool AddRunAsPackage(string pkg)
    {
        pkg = (pkg ?? "").Trim();
        if (pkg.Length == 0) return false;
        if (_data.RunAsPackages.Contains(pkg)) return false;
        _data.RunAsPackages.Insert(0, pkg);
        while (_data.RunAsPackages.Count > MaxPerList)
            _data.RunAsPackages.RemoveAt(_data.RunAsPackages.Count - 1);
        return true;
    }

    public bool RemoveRunAsPackage(string pkg) =>
        _data.RunAsPackages.RemoveAll(p => p == pkg) > 0;

    // ── APK 路径 / 应用包名收藏（安装卸载窗口用）──

    /// <summary>收藏过的 APK 本机路径（最近在前）。</summary>
    public List<string> ApkPaths => _data.ApkPaths;

    /// <summary>收藏过的应用包名（最近在前）。</summary>
    public List<string> Packages => _data.Packages;

    public bool AddApkPath(string path) => AddRecent(_data.ApkPaths, path);

    public bool RemoveApkPath(string path) => _data.ApkPaths.RemoveAll(p => p == path) > 0;

    public bool AddPackage(string pkg) => AddRecent(_data.Packages, pkg);

    public bool RemovePackage(string pkg) => _data.Packages.RemoveAll(p => p == pkg) > 0;

    // ── 过滤条件收藏（tag / message，主窗体过滤面板用）──

    /// <summary>收藏的 tag 过滤内容（最近在前）。</summary>
    public List<string> TagFilters => _data.TagFilters;

    /// <summary>收藏的 message 过滤内容（最近在前）。</summary>
    public List<string> MsgFilters => _data.MsgFilters;

    public bool AddTagFilter(string text) => AddRecent(_data.TagFilters, text);

    public bool RemoveTagFilter(string text) => _data.TagFilters.RemoveAll(p => p == text) > 0;

    public bool AddMsgFilter(string text) => AddRecent(_data.MsgFilters, text);

    public bool RemoveMsgFilter(string text) => _data.MsgFilters.RemoveAll(p => p == text) > 0;

    /// <summary>把值插到列表最前，去重并限制长度。空值或已存在时返回 false。</summary>
    static bool AddRecent(List<string> list, string value)
    {
        value = (value ?? "").Trim();
        if (value.Length == 0) return false;
        list.RemoveAll(p => string.Equals(p, value, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, value);
        while (list.Count > MaxPerList) list.RemoveAt(list.Count - 1);
        return true;
    }

    int IndexOf(bool remote, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return -1;
        return Get(remote).FindIndex(f => PathEquals(f.Path, path, remote));
    }

    // ── 路径工具 ──

    /// <summary>设备路径区分大小写，本机路径不区分大小写。</summary>
    public static bool PathEquals(string a, string b, bool remote) =>
        string.Equals(Normalize(a, remote), Normalize(b, remote),
            remote ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string path, bool remote)
    {
        var p = (path ?? "").Trim();
        if (remote)
        {
            p = p.TrimEnd('/');
            return p.Length == 0 ? "/" : p;
        }
        // "C:\" 长度为 3，不能被裁掉反斜杠
        if (p.Length > 3) p = p.TrimEnd('\\');
        return p;
    }

    static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
