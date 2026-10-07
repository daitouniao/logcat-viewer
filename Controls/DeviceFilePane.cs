using System.Text.RegularExpressions;
using logcat.Forms;
using logcat.Models;
using logcat.Services;

namespace logcat.Controls;

/// <summary>
/// 左侧面板：Android 设备目录。支持 root 与 run-as 模式。
/// </summary>
public sealed class DeviceFilePane : FilePane
{
    readonly AdbManager _manager;
    readonly string _serial;
    readonly bool _isRoot;

    Button _btnRunAs = null!;

    public override bool IsRemote => true;
    protected override string DragFormat => DeviceDragFormat;
    protected override string PeerDragFormat => LocalDragFormat;

    /// <summary>当前 run-as 包名，null 表示未启用。</summary>
    public string? RunAsPackage { get; private set; }

    /// <summary>run-as 模式的中转目录，默认放 Download，可在 run-as 对话框中修改。</summary>
    public string RunAsRelayDir { get; private set; } = logcat.Services.AppSettings.Default.LastRunAsRelayDir;

    public DeviceFilePane(AdbManager manager, string serial, bool isRoot)
        : base(Loc.F("Android 设备 — {0}{1}", serial, isRoot ? "  [root]" : ""))
    {
        _manager = manager;
        _serial = serial;
        _isRoot = isRoot;
        UpdateRunAsUi();
    }

    // ── 列与行 ──

    protected override void CreateColumns(ListView list)
    {
        Loc.BindCol(new ColumnHeader { Text = Loc.T("名称"), Width = 220 }, "名称");
        Loc.BindCol(new ColumnHeader { Text = Loc.T("大小"), Width = 80, TextAlign = HorizontalAlignment.Right }, "大小");
        Loc.BindCol(new ColumnHeader { Text = Loc.T("权限"), Width = 95 }, "权限");
        Loc.BindCol(new ColumnHeader { Text = Loc.T("所有者"), Width = 80 }, "所有者");
        Loc.BindCol(new ColumnHeader { Text = Loc.T("修改日期"), Width = 130 }, "修改日期");
    }

    protected override string[] GetRow(FileEntry e) => new[]
    {
        (e.IsDir ? "📁 " : "📄 ") + e.Name,
        e.IsDir ? "" : HumanSize(e.Size),
        e.Permissions,
        e.Owner,
        e.DateStr,
    };

    // ── 路径 ──

    protected override string NormalizePath(string path)
    {
        var p = (path ?? "").Trim().Replace('\\', '/');
        while (p.Contains("//")) p = p.Replace("//", "/");
        if (!p.StartsWith('/')) p = "/" + p;
        p = p.TrimEnd('/');
        return p.Length == 0 ? "/" : p;
    }

    protected override string? GetParentPath(string path)
    {
        if (path is "" or "/") return null;
        int i = path.LastIndexOf('/');
        return i <= 0 ? "/" : path[..i];
    }

    // ── 列目录 ──

    protected override Task<List<FileEntry>> ListAsync(string path, CancellationToken ct) =>
        LsAsync(path, ct);

    /// <summary>列目录。ls 失败时没有任何 stdout，靠哨兵区分「列不出」和「列出来是空目录」。</summary>
    async Task<List<FileEntry>> LsAsync(string path, CancellationToken ct)
    {
        var dir = path.EndsWith('/') ? path : path + "/";
        string output;
        try
        {
            output = await ShellAsync($"ls -la '{dir}' 2>/dev/null && echo {OkMarker}", 15, NeedsElevation(path));
        }
        catch (Exception ex)
        {
            throw new PermissionException($"无法访问 {path}，权限不足或目录不存在{ReasonOf(ex)}");
        }
        ct.ThrowIfCancellationRequested();
        if (!output.Contains(OkMarker))
            throw new PermissionException($"无法访问 {path}，权限不足或目录不存在");

        var prefix = path == "/" ? "" : path;
        var entries = new List<FileEntry>();
        foreach (var raw in output.Split('\n'))
        {
            // 必须去掉行尾的 \r：否则文件名会带上不可见的 \r，
            // 界面显示正常，但 adb pull/push 会报 "No such file or directory"
            var line = raw.TrimEnd('\r');
            if (line.Contains(OkMarker)) continue;
            var entry = ParseLsLine(line);
            if (entry == null) continue;
            entry.Path = prefix + "/" + entry.Name;
            entries.Add(entry);
        }
        return entries;
    }

    static readonly Regex LsRegex = new(
        @"^([drwxlsStT\-?]{10})\s+\d+\s+(\S+)\s+\S+\s+(\d+)\s+" +
        @"(\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2})\s+(.+)$",
        RegexOptions.Compiled);

    static FileEntry? ParseLsLine(string line)
    {
        var m = LsRegex.Match(line);
        if (!m.Success) return null;
        var perms = m.Groups[1].Value;
        var owner = m.Groups[2].Value;
        var size = long.Parse(m.Groups[3].Value);
        var dateStr = m.Groups[4].Value;
        var name = m.Groups[5].Value;
        if (name is "." or "..") return null;
        if (name.Contains(" -> ")) name = name.Split(" -> ")[0];
        var isDir = perms.StartsWith('d') || perms.StartsWith('l');
        return new FileEntry
        {
            Name = name,
            IsDir = isDir,
            Size = size,
            Permissions = perms,
            Owner = owner,
            DateStr = dateStr,
        };
    }

    protected override void OnLoadFailed(string path, Exception ex)
    {
        // 只在 /data/data/*（run-as 唯一能救场的范围）引导进入 run-as，
        // 其他路径失败多半是输错或需要 root，弹 run-as 只会打扰
        if (ex is PermissionException && !_isRoot && RunAsPackage == null &&
            path.StartsWith("/data/data/", StringComparison.Ordinal))
        {
            _ = ApplyRunAsAsync(path);
            return;
        }
        base.OnLoadFailed(path, ex);
    }

    // ── Shell 包装 ──

    /// <summary>
    /// 该路径上的操作是否需要提权（su / run-as）。
    /// sync 通道能直接收发的路径（/sdcard 等）shell 自身就有权限，
    /// 不加提权包裹，避免浏览普通目录时反复触发 root 授权。
    /// </summary>
    bool NeedsElevation(string devicePath) =>
        !IsSyncDirect(devicePath) && (_isRoot || RunAsPackage != null);

    string ShellCmd(string inner, bool elevated)
    {
        if (!elevated) return inner;
        // run-as 直接 exec 命令而非交给 shell，因此不能整体加引号，也不支持 && 串联
        if (_isRoot) return $"su -c {AdbManager.ShellQuote(inner)}";
        if (RunAsPackage != null) return $"run-as {RunAsPackage} {inner}";
        return inner;
    }

    Task<string> ShellAsync(string cmd, int timeoutSec = 15, bool elevated = false) =>
        _manager.ShellAsync(_serial, ShellCmd(cmd, elevated), timeoutSec);

    /// <summary>
    /// 成功哨兵。命令末尾接 &amp;&amp; echo 该标记，只有退出码为 0 时才会输出。
    /// </summary>
    const string OkMarker = "__LC_OK__";

    /// <summary>
    /// 执行设备端命令并确认成功（靠命令末尾的哨兵判定，失败时抛 PermissionException）。
    /// 不能把 stderr 并进 stdout：只要输出里出现“No such file or directory”这类报错文本，
    /// adb 就会判定命令失联并抛异常，报错内容反而会把正常输出（如整个目录列表）一起拖没；
    /// 所以失败详情只能从 failMessage 里给出，设备端的报错则丢弃。
    /// </summary>
    async Task ShellOkAsync(string inner, string failMessage, int timeoutSec = 60, bool elevated = false)
    {
        try
        {
            var output = await ShellAsync($"{inner} 2>/dev/null && echo {OkMarker}", timeoutSec, elevated);
            if (!output.Contains(OkMarker)) throw new PermissionException(failMessage);
        }
        catch (PermissionException) { throw; }
        catch (Exception ex) { throw new PermissionException($"{failMessage}{ReasonOf(ex)}"); }
    }

    /// <summary>
    /// 把底层异常转成可拼接的补充说明。
    /// adb 对「没拿到输出的失败命令」一律报 The shell command has become unresponsive，
    /// 这种描述对用户没有信息量，直接丢掉，失败原因由调用方传入的 failMessage 说明。
    /// </summary>
    static string ReasonOf(Exception ex) =>
        ex.Message.Contains("unresponsive", StringComparison.OrdinalIgnoreCase) ? "" : $"：{ex.Message}";

    /// <summary>尽力而为地执行设备端命令：失败只忽略，用于 chmod / chown / rm 这类收尾动作。</summary>
    async Task ShellBestEffortAsync(string inner, int timeoutSec = 15, bool elevated = false)
    {
        try { await ShellAsync(inner, timeoutSec, elevated); } catch { }
    }

    // ── 按钮 ──

    protected override void BuildButtons(FlowLayoutPanel bar)
    {
        var btnUp = NewBarButton("⬅ 下载到本机", 106);
        btnUp.Click += (_, _) => RequestTransferSelection();
        bar.Controls.Add(btnUp);

        var btnPush = NewBarButton("上传文件到此处…", 122);
        btnPush.Click += OnPickFilesToUpload;
        bar.Controls.Add(btnPush);

        var btnDel = NewBarButton("删除选中", 76);
        btnDel.Click += async (_, _) => await DeleteSelectedAsync();
        bar.Controls.Add(btnDel);

        var btnRename = NewBarButton("重命名", 76);
        btnRename.Click += async (_, _) => await RenameSelectedAsync();
        bar.Controls.Add(btnRename);

        _btnRunAs = NewBarButton("run-as…", 72);
        _btnRunAs.AutoSize = true;   // 显示当前包名时宽度可变
        _btnRunAs.Click += (_, _) => OnModeButtonClicked();
        bar.Controls.Add(_btnRunAs);
    }

    void OnPickFilesToUpload(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Title = Loc.T("选择要上传到设备的文件"),
            Multiselect = true,
            Filter = Loc.T("所有文件 (*.*)|*.*"),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        RaiseFilesDropped(dlg.FileNames, "", CurrentPath);
    }

    // ── 右键菜单 ──

    protected override void OnBuildContextMenu(ContextMenuStrip menu)
    {
        menu.Items.Add(Loc.T("⬅ 下载到本机"), null, (_, _) => RequestTransferSelection());
        menu.Items.Add(Loc.T("上传文件到此处…"), null, (_, _) => OnPickFilesToUpload(this, EventArgs.Empty));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Loc.T("新建文件夹"), null, (_, _) => _ = CreateFolderAsync());
        menu.Items.Add(Loc.T("删除选中"), null, (_, _) => _ = DeleteSelectedAsync());
        menu.Items.Add(new ToolStripSeparator());
    }

    async Task CreateFolderAsync()
    {
        if (string.IsNullOrEmpty(CurrentPath)) return;
        var name = SimpleInputBox.Show(this, Loc.T("新建文件夹"), Loc.T("文件夹名称："), Loc.T("新建文件夹"));
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            var dir = CurrentPath == "/" ? "/" + name.Trim() : $"{CurrentPath}/{name.Trim()}";
            await MkdirPAsync(dir);
            await RefreshAsync();
            SelectByName(name.Trim());
        }
        catch (Exception ex)
        {
            ShowError(Loc.F("创建失败：{0}", ex.Message));
        }
    }

    // ── 模式切换（root / run-as）──

    /// <summary>
    /// run-as 按钮点击：未启用时直接弹设置对话框；已启用时弹模式下拉菜单
    /// （退出 / 切换包名），免确认框，root 机退出即回到 root 模式。
    /// </summary>
    void OnModeButtonClicked()
    {
        if (RunAsPackage == null)
        {
            _ = ApplyRunAsAsync(null);
            return;
        }

        var menu = new ContextMenuStrip();
        menu.Items.Add(_isRoot ? Loc.T("回到 root 模式") : Loc.T("退出 run-as 模式"), null,
            (_, _) => ExitRunAs());
        menu.Items.Add(Loc.T("切换包名…"), null, (_, _) => _ = ApplyRunAsAsync(null));

        var visited = FavoritesStore.Default.RunAsPackages;
        if (visited.Count > 0)
        {
            menu.Items.Add(new ToolStripSeparator());
            foreach (var pkg in visited)
            {
                if (pkg == RunAsPackage) continue;
                var p = pkg;   // 闭包捕获
                menu.Items.Add(p, null, (_, _) => SwitchRunAsPackage(p));
            }
        }
        menu.Show(_btnRunAs, new Point(0, _btnRunAs.Height));
    }

    /// <summary>退出 run-as：不弹确认框。受限目录回 /sdcard，其余原地刷新。</summary>
    void ExitRunAs()
    {
        RunAsPackage = null;
        UpdateRunAsUi();
        if (CurrentPath == "/data/data" ||
            CurrentPath.StartsWith("/data/data/", StringComparison.Ordinal))
            NavigateTo("/sdcard");
        else
            _ = RefreshAsync();
    }

    /// <summary>免弹窗直接切换到某个访问过的包名。</summary>
    void SwitchRunAsPackage(string pkg)
    {
        RunAsPackage = pkg;
        RememberRunAs(pkg);
        UpdateRunAsUi();
        NavigateTo($"/data/data/{pkg}");
    }

    /// <summary>更新按钮文字与面板标题，让当前身份模式一目了然。</summary>
    void UpdateRunAsUi()
    {
        _btnRunAs.Text = RunAsPackage == null ? "run-as…" : $"run-as: {RunAsPackage}";
        Box.Text = Loc.F("Android 设备 — {0}{1}", _serial, _isRoot ? "  [root]" : "") +
                   (RunAsPackage == null ? "" : $"  [run-as: {RunAsPackage}]");
    }

    /// <summary>
    /// 进入 run-as 模式。targetPath 非空表示由权限失败触发，成功后直接打开原路径
    /// （而不是固定跳 /data/data/包名）；弹出前用最近用过的包名预填，回车即生效。
    /// </summary>
    async Task ApplyRunAsAsync(string? targetPath)
    {
        using var dlg = new RunAsDialog(FavoritesStore.Default.RunAsPackages, RunAsRelayDir);
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;

        RunAsPackage = dlg.Package;
        RunAsRelayDir = dlg.RelayDir;
        RememberRunAs(dlg.Package);
        UpdateRunAsUi();
        NavigateTo(targetPath ?? $"/data/data/{dlg.Package}");
    }

    /// <summary>记住包名收藏（置顶）与中转目录，下次直接复用。</summary>
    void RememberRunAs(string pkg)
    {
        var fav = FavoritesStore.Default;
        fav.RemoveRunAsPackage(pkg);   // 先移除再插入，让最近用的包排最前
        if (fav.AddRunAsPackage(pkg))
            fav.Save();
        var s = logcat.Services.AppSettings.Default;
        if (s.LastRunAsRelayDir != RunAsRelayDir)
        {
            s.LastRunAsRelayDir = RunAsRelayDir;
            s.Save();
        }
    }

    // ── 文件操作 ──

    public async Task DeleteSelectedAsync()
    {
        var entries = SelectedEntries;
        if (entries.Count == 0) return;

        var names = string.Join(", ", entries.Take(5).Select(e => e.Name));
        if (entries.Count > 5) names += Loc.F(" …等 {0} 项", entries.Count);
        if (!Confirm(Loc.F("确定要删除设备上的以下文件/目录吗？\n{0}", names), "确认删除")) return;

        Pbar.Visible = true;
        try
        {
            foreach (var e in entries)
            {
                var inner = e.IsDir ? $"rm -rf '{e.Path}'" : $"rm -f '{e.Path}'";
                SetStatus(Loc.F("删除 {0}…", e.Name));
                await ShellOkAsync(inner, Loc.F("删除 {0} 失败", e.Name), 20, NeedsElevation(e.Path));
            }
            SetStatus(Loc.F("已删除 {0} 项", entries.Count));
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ShowError(Loc.F("删除失败：{0}", ex.Message));
        }
        finally { Pbar.Visible = false; }
    }

    // ── 重命名（设备端走 shell mv）──

    protected override async Task RenameAsync(FileEntry e, string newName)
    {
        var parent = GetParentPath(e.Path) ?? "/";
        var newPath = parent is "" or "/" ? "/" + newName : parent + "/" + newName;
        // 同目录内移动即重命名；提权判定以源路径为准（目标与源同目录）
        await ShellOkAsync($"mv -f '{e.Path}' '{newPath}'", Loc.F("重命名 {0} 失败", e.Name), 30, NeedsElevation(e.Path));
    }

    /// <summary>判断设备端路径是否为目录。</summary>
    public async Task<bool> IsDirAsync(string remotePath)
    {
        try
        {
            var outText = await ShellAsync($"test -d '{remotePath}' && echo DIR", 10, NeedsElevation(remotePath));
            return outText.Contains("DIR");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 把设备端路径递归展开为文件列表。
    /// 文件原样返回；目录用 find 展开，若设备不支持 find 则跳过并通过 onSkip 回报。
    /// </summary>
    public async Task<List<string>> ExpandFilesAsync(IEnumerable<string> paths, Action<string>? onSkip = null)
    {
        var result = new List<string>();
        foreach (var p in paths)
        {
            if (!await IsDirAsync(p))
            {
                result.Add(p);
                continue;
            }

            string output = "";
            try
            {
                output = await ShellAsync($"find '{p}' -type f 2>/dev/null", 60, NeedsElevation(p));
            }
            catch { }

            var found = output.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.StartsWith('/'))
                .ToList();

            if (found.Count > 0) result.AddRange(found);
            else onSkip?.Invoke(p);   // 目录但无法展开（设备缺少 find 或目录为空/不可读）
        }
        return result;
    }

    public async Task MkdirPAsync(string remoteDir)
    {
        if (string.IsNullOrEmpty(remoteDir) || remoteDir == "/") return;
        await ShellOkAsync($"mkdir -p '{remoteDir}'", Loc.F("无法创建 {0}", remoteDir), 15, NeedsElevation(remoteDir));
    }

    // ── 传输通道 ──

    /// <summary>root 模式的中转目录：shell 用户必定可读写。</summary>
    const string RootRelayDir = "/data/local/tmp";

    /// <summary>
    /// 当前生效的中转目录。run-as 模式下应用无法写 /data/local/tmp（SELinux 拒绝），
    /// 改用 sdcard 下用户指定的目录（默认 Download）：应用属主可写，sync 通道也能直接收发。
    /// </summary>
    string RelayDir => RunAsPackage != null ? RunAsRelayDir : RootRelayDir;

    /// <summary>
    /// sync 通道（push/pull）能直接收发的路径前缀。
    /// 其他位置（/data/data、/system 等）shell 连目录遍历权限都没有，
    /// 必须先由 su / run-as 复制到中转目录，否则必然报 Permission denied。
    /// </summary>
    static readonly string[] SyncDirectPrefixes = ["/sdcard", "/storage", RootRelayDir];

    int _relaySeq;

    static bool IsSyncDirect(string remotePath) =>
        SyncDirectPrefixes.Any(p => remotePath.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                                    remotePath.StartsWith(p + "/", StringComparison.Ordinal));

    /// <summary>
    /// adb 的 sync 服务固定以 shell 身份运行，与设备上是否已 root 无关：
    /// su 只能提升 shell 命令的权限，提升不了 push/pull。
    /// </summary>
    bool NeedsRelay(string remotePath) =>
        !IsSyncDirect(remotePath) && (_isRoot || RunAsPackage != null);

    string NextRelayPath(string remotePath)
    {
        var name = remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrEmpty(name)) name = "file";
        return $"{RelayDir}/_adb_{Interlocked.Increment(ref _relaySeq)}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_{name}";
    }

    /// <summary>
    /// 上传目标目录链上最近一个已存在祖先的 uid:gid，用于把新建的目录/文件改回应用身份。
    /// stat 失败（目录不存在）时返回 null，逐级向上查找。
    /// </summary>
    async Task<string?> NearestOwnerAsync(string remoteDir)
    {
        var dir = remoteDir;
        while (dir.Length > 1)
        {
            try
            {
                var outText = await ShellAsync($"stat -c '%u:%g' '{dir}'", 10, NeedsElevation(dir));
                var line = outText.Split('\n')
                    .Select(l => l.Trim())
                    .FirstOrDefault(l => OwnerRegex.IsMatch(l));
                if (line != null) return line;
            }
            catch { }
            int i = dir.LastIndexOf('/');
            dir = i <= 0 ? "/" : dir[..i];
            if (dir == "/") break;
        }
        return null;
    }

    static readonly Regex OwnerRegex = new(@"^\d+:\d+$", RegexOptions.Compiled);

    /// <summary>
    /// 打开下载落地的本地文件：File.Create 默认 FileShare.None，外部进程正在读该文件时会报占用，
    /// 这里放开共享读（FileShare.Read），写入仍独占。
    /// </summary>
    static FileStream OpenDownloadTarget(string localFile) =>
        new(localFile, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);

    /// <summary>下载设备文件到本机指定文件。</summary>
    public async Task PullFileAsync(string remotePath, string localFile)
    {
        var dir = Path.GetDirectoryName(localFile);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        if (!NeedsRelay(remotePath))
        {
            using var fs = OpenDownloadTarget(localFile);
            await _manager.PullAsync(_serial, remotePath, fs);
            return;
        }

        var tmp = NextRelayPath(remotePath);
        try
        {
            await ShellOkAsync($"cp '{remotePath}' '{tmp}'", Loc.F("无法读取 {0}", remotePath), elevated: true);
            // cp 落地的中转文件沿用源权限（常为 0600），属主是提权身份（root 或应用），
            // 不放开读权限的话 sync 依旧取不到；chmod 与清理也必须用同样的身份执行。
            await ShellBestEffortAsync($"chmod a+r '{tmp}'", elevated: true);
            using (var fs = OpenDownloadTarget(localFile))
                await _manager.PullAsync(_serial, tmp, fs);
        }
        finally
        {
            await ShellBestEffortAsync($"rm -f '{tmp}'", elevated: true);
        }
    }

    /// <summary>
    /// 在设备上执行 sync，把页缓存里的数据刷写到存储。
    /// push 完成只代表数据到达设备内核，掉电/重启/拔线前未必已落盘。
    /// 全局刷写无需提权；失败仅忽略（部分设备 shell 无 sync 或超时），由调用方决定是否提示。
    /// </summary>
    public Task SyncAsync() => ShellBestEffortAsync("sync", 120);

    /// <summary>上传本机文件到设备指定路径。</summary>
    public async Task PushFileAsync(string localFile, string remotePath)
    {
        int cut = remotePath.LastIndexOf('/');
        if (cut <= 0)
        {
            await _manager.PushAsync(_serial, localFile, remotePath);
            return;
        }
        var dir = remotePath[..cut];
        var relay = NeedsRelay(remotePath);
        // 属主必须在 mkdir 之前取：目录是新建的话它自己就是 root:root，没有参考价值
        var owner = relay ? await NearestOwnerAsync(dir) : null;
        await MkdirPAsync(dir);

        if (!relay)
        {
            await _manager.PushAsync(_serial, localFile, remotePath);
            return;
        }

        var tmp = NextRelayPath(remotePath);
        try
        {
            await _manager.PushAsync(_serial, localFile, tmp);
            await ShellOkAsync($"cp '{tmp}' '{remotePath}'", Loc.F("无法写入 {0}", remotePath), elevated: true);
            // sync 推送的文件属主是 shell，直接落到应用数据目录会让应用自身无法读写，
            // 所以再按目标目录链上最近的已有属主改回去（顺带处理本次新建的目录）。
            if (_isRoot && owner != null)
            {
                await ShellBestEffortAsync($"chown '{owner}' '{dir}'", elevated: true);
                await ShellBestEffortAsync($"chown '{owner}' '{remotePath}'", elevated: true);
            }
        }
        finally
        {
            // push 落地的中转文件属主是 shell，普通 shell 即可清理，不必提权
            await ShellBestEffortAsync($"rm -f '{tmp}'");
        }
    }
}

/// <summary>权限不足异常。</summary>
public class PermissionException : Exception
{
    public PermissionException(string message) : base(message) { }
}
