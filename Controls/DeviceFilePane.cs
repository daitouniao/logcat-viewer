using System.Text.RegularExpressions;
using logcat.Forms;
using logcat.Models;
using logcat.Properties;
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
    public string RunAsRelayDir { get; private set; } = Settings.Default.LastRunAsRelayDir;

    public DeviceFilePane(AdbManager manager, string serial, bool isRoot)
        : base($"Android 设备 — {serial}{(isRoot ? "  [root]" : "")}")
    {
        _manager = manager;
        _serial = serial;
        _isRoot = isRoot;
    }

    // ── 列与行 ──

    protected override void CreateColumns(ListView list)
    {
        list.Columns.Add("名称", 220);
        list.Columns.Add("大小", 80, HorizontalAlignment.Right);
        list.Columns.Add("权限", 95);
        list.Columns.Add("所有者", 80);
        list.Columns.Add("修改日期", 130);
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
        if (ex is PermissionException && !_isRoot && RunAsPackage == null)
        {
            var r = MessageBox.Show(this,
                $"无法访问 {path}\n\n是否使用 run-as 模式？\n（需要目标应用为可调试版本）",
                "权限不足", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r == DialogResult.Yes)
            {
                _ = ApplyRunAsAsync();
                return;
            }
        }
        base.OnLoadFailed(path, ex);
    }

    // ── Shell 包装 ──

    /// <summary>
    /// 用单引号包裹，内部的单引号按 '\'' 规则转义。
    /// root 模式下内层命令自身也带单引号（路径引用），不转义的话 su -c 只会收到被截断的前半段，
    /// 路径含空格时直接失效。
    /// </summary>
    static string Quote(string value) => "'" + value.Replace("'", @"'\''") + "'";

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
        if (_isRoot) return $"su -c {Quote(inner)}";
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

        _btnRunAs = NewBarButton("run-as…", 72);
        _btnRunAs.Click += (_, _) => _ = OnRunAsAsync();
        bar.Controls.Add(_btnRunAs);
    }

    void OnPickFilesToUpload(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "选择要上传到设备的文件",
            Multiselect = true,
            Filter = "所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        RaiseFilesDropped(dlg.FileNames, "", CurrentPath);
    }

    // ── 右键菜单 ──

    protected override void OnBuildContextMenu(ContextMenuStrip menu)
    {
        menu.Items.Add("⬅ 下载到本机", null, (_, _) => RequestTransferSelection());
        menu.Items.Add("上传文件到此处…", null, (_, _) => OnPickFilesToUpload(this, EventArgs.Empty));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("新建文件夹", null, (_, _) => _ = CreateFolderAsync());
        menu.Items.Add("删除选中", null, (_, _) => _ = DeleteSelectedAsync());
        menu.Items.Add(new ToolStripSeparator());
    }

    async Task CreateFolderAsync()
    {
        if (string.IsNullOrEmpty(CurrentPath)) return;
        var name = SimpleInputBox.Show(this, "新建文件夹", "文件夹名称：", "新建文件夹");
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
            ShowError($"创建失败：{ex.Message}");
        }
    }

    // ── run-as ──

    async Task OnRunAsAsync()
    {
        if (RunAsPackage != null)
        {
            if (!Confirm($"当前 run-as 包：{RunAsPackage}\n\n是否清除 run-as 模式？", "run-as")) return;
            RunAsPackage = null;
            _btnRunAs.Text = "run-as…";
            NavigateTo("/sdcard");
            return;
        }
        await ApplyRunAsAsync();
    }

    async Task ApplyRunAsAsync()
    {
        // 不再主动扫描可调试应用（逐包 dumpsys 又慢又打扰），
        // 直接弹出对话框：从访问过的包名收藏中选择或手动输入，并设置中转目录。
        using var dlg = new RunAsDialog(FavoritesStore.Default.RunAsPackages, RunAsRelayDir);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        RunAsPackage = dlg.Package;
        RunAsRelayDir = dlg.RelayDir;
        _btnRunAs.Text = $"run-as: {RunAsPackage}";

        // 记住包名收藏与中转目录，下次直接复用
        if (FavoritesStore.Default.AddRunAsPackage(RunAsPackage))
            FavoritesStore.Default.Save();
        var s = Settings.Default;
        if (s.LastRunAsRelayDir != RunAsRelayDir)
        {
            s.LastRunAsRelayDir = RunAsRelayDir;
            s.Save();
        }

        NavigateTo($"/data/data/{RunAsPackage}");
    }

    // ── 文件操作 ──

    public async Task DeleteSelectedAsync()
    {
        var entries = SelectedEntries;
        if (entries.Count == 0) return;

        var names = string.Join(", ", entries.Take(5).Select(e => e.Name));
        if (entries.Count > 5) names += $" …等 {entries.Count} 项";
        if (!Confirm($"确定要删除设备上的以下文件/目录吗？\n{names}", "确认删除")) return;

        Pbar.Visible = true;
        try
        {
            foreach (var e in entries)
            {
                var inner = e.IsDir ? $"rm -rf '{e.Path}'" : $"rm -f '{e.Path}'";
                SetStatus($"删除 {e.Name}…");
                await ShellOkAsync(inner, $"删除 {e.Name} 失败", 20, NeedsElevation(e.Path));
            }
            SetStatus($"已删除 {entries.Count} 项");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ShowError($"删除失败：{ex.Message}");
        }
        finally { Pbar.Visible = false; }
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
        await ShellOkAsync($"mkdir -p '{remoteDir}'", $"无法创建 {remoteDir}", 15, NeedsElevation(remoteDir));
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

    /// <summary>下载设备文件到本机指定文件。</summary>
    public async Task PullFileAsync(string remotePath, string localFile)
    {
        var dir = Path.GetDirectoryName(localFile);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        if (!NeedsRelay(remotePath))
        {
            using var fs = File.Create(localFile);
            await _manager.PullAsync(_serial, remotePath, fs);
            return;
        }

        var tmp = NextRelayPath(remotePath);
        try
        {
            await ShellOkAsync($"cp '{remotePath}' '{tmp}'", $"无法读取 {remotePath}", elevated: true);
            // cp 落地的中转文件沿用源权限（常为 0600），属主是提权身份（root 或应用），
            // 不放开读权限的话 sync 依旧取不到；chmod 与清理也必须用同样的身份执行。
            await ShellBestEffortAsync($"chmod a+r '{tmp}'", elevated: true);
            using (var fs = File.Create(localFile))
                await _manager.PullAsync(_serial, tmp, fs);
        }
        finally
        {
            await ShellBestEffortAsync($"rm -f '{tmp}'", elevated: true);
        }
    }

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
            await ShellOkAsync($"cp '{tmp}' '{remotePath}'", $"无法写入 {remotePath}", elevated: true);
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
