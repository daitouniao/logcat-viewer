namespace logcat.Services;

/// <summary>
/// sidecar 缓存目录治理（design D1/D6/D7/D9/D10）：条目路径派生、容量上限与 LRU 淘汰、
/// 静态文件边界、原子替换与静默降级。
/// <para>
/// <b>一切异常都在本类内吞掉并只写启动日志</b>——缓存是纯加速手段，读写失败不得影响
/// 打开文件的主流程，也不弹提示、不设开关（spec「静默降级到原路径」）。
/// </para>
/// <para>
/// 可注入目录与上限（internal 构造）：生产用 exe 同级 <c>cache/</c>，测试用临时目录与小上限。
/// </para>
/// </summary>
internal sealed class SidecarCacheStore
{
    /// <summary>单份 sidecar 体积上限（design D9，spec「缓存容量上限」）。</summary>
    internal const long MaxEntryBytes = 1L << 30;

    /// <summary>缓存目录总占用触发线（design D9 拍板的固定值）。</summary>
    internal const long DirectoryLimitBytes = 4L << 30;

    /// <summary>单行索引体积预估（8 段定宽合计，design D3）：用于落盘前预判，不做实际序列化。</summary>
    internal const long EstimatedBytesPerRow = 30;

    /// <summary>临时文件前缀标记：淘汰与遍历时一律跳过，避免把半截写入当成条目。</summary>
    const string TempInfix = ".tmp-";

    static readonly object _defaultLock = new();
    static SidecarCacheStore? _default;

    /// <summary>生产单例：<c>&lt;exe 同级&gt;/cache</c>，与既有 <c>Log/</c>、<c>temp/</c> 惯例一致（design D1）。</summary>
    internal static SidecarCacheStore Default
    {
        get
        {
            lock (_defaultLock)
                return _default ??= new SidecarCacheStore(
                    Path.Combine(AppContext.BaseDirectory, "cache"));
        }
    }

    readonly string _dir;
    readonly long _maxEntryBytes;
    readonly long _dirLimitBytes;
    /// <summary>当前会话使用中的条目名（淘汰必须跳过，spec「使用中的缓存不被误删」）。</summary>
    readonly HashSet<string> _inUse = new(StringComparer.OrdinalIgnoreCase);
    readonly object _gate = new();

    internal SidecarCacheStore(string dir,
        long maxEntryBytes = MaxEntryBytes, long dirLimitBytes = DirectoryLimitBytes)
    {
        _dir = dir;
        _maxEntryBytes = maxEntryBytes;
        _dirLimitBytes = dirLimitBytes;
    }

    internal string DirPath => _dir;

    /// <summary>源文件 → 条目完整路径。</summary>
    internal string EntryPath(string sourcePath) =>
        Path.Combine(_dir, SidecarKey.DeriveName(sourcePath));

    /// <summary>登记「本会话正在使用」（命中装载或刚落盘时）。</summary>
    internal void MarkInUse(string sourcePath)
    {
        var name = SafeName(sourcePath);
        if (name == null) return;
        lock (_gate) _inUse.Add(name);
    }

    /// <summary>解除登记（换文档、关文档时调用）。</summary>
    internal void UnmarkInUse(string sourcePath)
    {
        var name = SafeName(sourcePath);
        if (name == null) return;
        lock (_gate) _inUse.Remove(name);
    }

    static string? SafeName(string sourcePath)
    {
        try { return SidecarKey.DeriveName(sourcePath); }
        catch { return null; }
    }

    bool IsInUse(string entryName)
    {
        lock (_gate) return _inUse.Contains(entryName);
    }

    /// <summary>
    /// 尝试命中外层缓存：身份校验通过且格式有效才返回 true。
    /// 任何失败（缺失 / 损坏 / 版本不识别 / 目录不可写）返回 false，调用方走既有全量路径。
    /// </summary>
    internal bool TryLoad(string sourcePath, out SidecarPayload? payload)
    {
        payload = null;
        try
        {
            var entry = EntryPath(sourcePath);
            if (!File.Exists(entry)) return false;

            var identity = SidecarIdentity.Capture(sourcePath);
            using var fs = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var loaded = SidecarFile.Read(fs, identity, out var reason);
            if (loaded == null)
            {
                StartupLog.Write($"[Sidecar] 缓存无效，走全量加载：{reason}");
                return false;
            }

            payload = loaded;
            MarkInUse(sourcePath);
            Touch(entry);          // 「最近使用」标记（design D9：以条目自身文件时间，不新增状态文件）
            return true;
        }
        catch (Exception ex)
        {
            StartupLog.Write($"[Sidecar] 读取缓存失败，走全量加载：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 落盘一份 sidecar。返回 false 表示「没有写」（超上限 / 文件在解析期间变化 / 目录不可写 / 写失败），
    /// 一律静默——调用方不感知差异。
    /// </summary>
    /// <param name="before">解析开始前的身份取样。</param>
    /// <param name="after">解析结束时的身份取样；与 <paramref name="before"/> 不等即放弃（静态文件边界）。</param>
    internal bool Save(string sourcePath, SidecarIdentity? before, SidecarIdentity after,
        SidecarPayload payload)
    {
        try
        {
            // 静态文件边界（design D7）：解析期间文件被追加/改写 → 不落盘
            if (before is null || !before.Value.Matches(after))
            {
                StartupLog.Write("[Sidecar] 解析期间文件已变化，不落盘缓存");
                return false;
            }

            long estimated = payload.RowCount * EstimatedBytesPerRow;
            if (estimated > _maxEntryBytes)
            {
                // 超大索引不落盘，且不触发淘汰（spec「超大文件索引不落盘」）
                StartupLog.Write($"[Sidecar] 索引预估 {estimated} 字节超过单份上限 {_maxEntryBytes}，不落盘");
                return false;
            }

            Directory.CreateDirectory(_dir);
            var entry = EntryPath(sourcePath);
            // 新条目必留由 EvictForSpace 显式跳过目标路径保证；不在此登记「使用中」——
            // 使用中 = 本会话经命中装载、文档仍打开的条目，否则整个会话都不会淘汰（见 D9）
            EvictForSpace(estimated, entry);

            var temp = entry + TempInfix + Guid.NewGuid().ToString("N");
            try
            {
                using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    SidecarFile.Write(fs, after, payload);
                // 原子替换：目标已存在用覆盖式 Move（File.Replace 在目标缺失时会抛，需先探测）
                if (File.Exists(entry)) File.Move(temp, entry, overwrite: true);
                else File.Move(temp, entry);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }

            Touch(entry);
            StartupLog.Write($"[Sidecar] 已落盘缓存：{payload.RowCount:N0} 行，预估 {estimated:N0} 字节");
            return true;
        }
        catch (Exception ex)
        {
            StartupLog.Write($"[Sidecar] 落盘失败（静默）：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 目录超限时按最近使用时间从旧到新淘汰，直至容纳 <paramref name="incomingBytes"/>。
    /// 新条目自身与使用中的条目跳过；删不掉只留痕不抛（下一轮再试）。
    /// </summary>
    void EvictForSpace(long incomingBytes, string newEntryPath)
    {
        var entries = new List<(string path, string name, DateTime used, long length)>();
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(_dir, "*" + SidecarKey.Extension))
        {
            try
            {
                var info = new FileInfo(file);
                long length = info.Length;
                total += length;
                entries.Add((file, info.Name, LastUsed(info), length));
            }
            catch { /* 单个条目读不到元数据：跳过，不算占用 */ }
        }

        if (total + incomingBytes <= _dirLimitBytes) return;

        entries.Sort((a, b) => a.used.CompareTo(b.used));
        foreach (var e in entries)
        {
            if (total + incomingBytes <= _dirLimitBytes) break;
            if (string.Equals(e.path, newEntryPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (IsInUse(e.name)) continue;
            if (!TryDelete(e.path)) continue;
            total -= e.length;
            StartupLog.Write($"[Sidecar] 目录超限淘汰条目：{e.name}");
        }
    }

    /// <summary>
    /// 「最近使用」= 访问时间；访问时间不可靠（不更新 / 早于写入）时以修改时间兜底（design D9）。
    /// </summary>
    static DateTime LastUsed(FileInfo info)
    {
        var used = info.LastAccessTimeUtc;
        var modified = info.LastWriteTimeUtc;
        return used < modified ? modified : used;
    }

    /// <summary>刷新条目的「最近使用」标记。</summary>
    static void Touch(string entryPath)
    {
        try { File.SetLastAccessTimeUtc(entryPath, DateTime.UtcNow); }
        catch { /* 只读介质 / 特殊挂载：标记失效，LRU 退化为修改时间 */ }
    }

    static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch { return false; }
    }
}
