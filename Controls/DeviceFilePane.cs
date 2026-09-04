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
        Task.Run(() => Ls(path), ct);

    List<FileEntry> Ls(string path)
    {
        var dir = path.EndsWith('/') ? path : path + "/";
        var output = Shell($"ls -la '{dir}'", 15);

        if (output.Contains("Permission denied") || output.Contains("No such file or directory"))
            throw new PermissionException($"无法访问 {path}，权限不足或目录不存在");

        var prefix = path == "/" ? "" : path;
        var entries = new List<FileEntry>();
        foreach (var raw in output.Split('\n'))
        {
            // 必须去掉行尾的 \r：否则文件名会带上不可见的 \r，
            // 界面显示正常，但 adb pull/push 会报 "No such file or directory"
            var line = raw.TrimEnd('\r');
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

    string ShellCmd(string inner)
    {
        if (_isRoot) return $"su -c '{inner}'";
        if (RunAsPackage != null) return $"run-as {RunAsPackage} {inner}";
        return inner;
    }

    string Shell(string cmd, int timeoutSec = 15) => _manager.Shell(_serial, ShellCmd(cmd), timeoutSec);

    Task<string> ShellAsync(string cmd, int timeoutSec = 15) =>
        _manager.ShellAsync(_serial, ShellCmd(cmd), timeoutSec);

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
        SetStatus("正在扫描可调试应用…");
        var debuggable = await FindDebuggablePackages();

        string? pkg;
        if (debuggable.Count > 0)
        {
            pkg = SimpleInputBox.Show(this, "run-as",
                $"找到 {debuggable.Count} 个可调试应用，输入包名：", debuggable[0]);
        }
        else
        {
            MessageBox.Show(this,
                "未找到可调试应用。\n\n可调试应用需要 android:debuggable=\"true\"\n" +
                "（通常是开发版/debug 构建的应用）\n\n你也可以手动输入包名。",
                "run-as", MessageBoxButtons.OK, MessageBoxIcon.Information);
            pkg = SimpleInputBox.Show(this, "run-as", "手动输入应用包名：", "");
        }

        SetStatus("");
        if (string.IsNullOrWhiteSpace(pkg)) return;

        RunAsPackage = pkg.Trim();
        _btnRunAs.Text = $"run-as: {RunAsPackage}";
        NavigateTo($"/data/data/{RunAsPackage}");
    }

    async Task<List<string>> FindDebuggablePackages()
    {
        var result = new List<string>();
        try
        {
            var outText = await _manager.ShellAsync(_serial, "pm list packages", 15);
            var pkgs = outText.Split('\n')
                .Where(l => l.StartsWith("package:"))
                .Select(l => l["package:".Length..].Trim())
                .Take(200)
                .ToList();

            foreach (var p in pkgs)
            {
                try
                {
                    var info = await _manager.ShellAsync(_serial,
                        $"dumpsys package {p} | grep -i 'flags=' | head -1", 5);
                    if (info.Contains("DEBUGGABLE")) result.Add(p);
                }
                catch { }
                if (result.Count >= 20) break;
            }
        }
        catch { }
        return result;
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
                await ShellAsync(inner, 20);
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
            var outText = await ShellAsync($"test -d '{remotePath}' && echo DIR", 10);
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
                output = await ShellAsync($"find '{p}' -type f 2>/dev/null", 60);
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
        await ShellAsync($"mkdir -p '{remoteDir}'", 15);
    }

    /// <summary>下载设备文件到本机指定文件。</summary>
    public async Task PullFileAsync(string remotePath, string localFile)
    {
        var dir = Path.GetDirectoryName(localFile);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        if (RunAsPackage != null && !_isRoot)
        {
            var name = remotePath.Split('/').Last();
            var tmp = $"/data/local/tmp/_adb_pull_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_{name}";
            await ShellAsync($"cp '{remotePath}' '{tmp}'", 60);
            using (var fs = File.Create(localFile))
                await _manager.PullAsync(_serial, tmp, fs);
            await ShellAsync($"rm -f '{tmp}'", 10);
        }
        else
        {
            using var fs = File.Create(localFile);
            await _manager.PullAsync(_serial, remotePath, fs);
        }
    }

    /// <summary>上传本机文件到设备指定路径。</summary>
    public async Task PushFileAsync(string localFile, string remotePath)
    {
        var dir = remotePath[..remotePath.LastIndexOf('/')];
        await MkdirPAsync(dir);

        if (RunAsPackage != null && !_isRoot)
        {
            var name = remotePath.Split('/').Last();
            var tmp = $"/data/local/tmp/_adb_push_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_{name}";
            await _manager.PushAsync(_serial, localFile, tmp);
            await ShellAsync($"cp '{tmp}' '{remotePath}'", 60);
            await ShellAsync($"rm -f '{tmp}'", 10);
        }
        else
        {
            await _manager.PushAsync(_serial, localFile, remotePath);
        }
    }
}

/// <summary>权限不足异常。</summary>
public class PermissionException : Exception
{
    public PermissionException(string message) : base(message) { }
}
