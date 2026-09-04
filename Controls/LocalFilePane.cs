using logcat.Forms;
using logcat.Models;
using logcat.Services;

namespace logcat.Controls;

/// <summary>
/// 右侧面板：本机电脑目录。空路径表示「此电脑」，列出所有可用驱动器。
/// </summary>
public sealed class LocalFilePane : FilePane
{
    public override bool IsRemote => false;
    protected override string DragFormat => LocalDragFormat;
    protected override string PeerDragFormat => DeviceDragFormat;

    public LocalFilePane() : base("本机电脑") { }

    // ── 列与行 ──

    protected override void CreateColumns(ListView list)
    {
        list.Columns.Add("名称", 250);
        list.Columns.Add("大小", 90, HorizontalAlignment.Right);
        list.Columns.Add("类型", 100);
        list.Columns.Add("修改日期", 140);
    }

    protected override string[] GetRow(FileEntry e) => new[]
    {
        (e.IsDir ? "📁 " : "📄 ") + e.Name,
        e.IsDir ? "" : HumanSize(e.Size),
        e.IsDir ? "文件夹" : TypeText(e.Name),
        e.DateStr,
    };

    static string TypeText(string name)
    {
        var ext = Path.GetExtension(name);
        return string.IsNullOrEmpty(ext) ? "文件" : $"{ext.TrimStart('.').ToUpperInvariant()} 文件";
    }

    // ── 路径 ──

    protected override string NormalizePath(string path)
    {
        var p = (path ?? "").Trim().Trim('"');
        if (p is "" or "此电脑" or "我的电脑") return "";

        p = p.Replace('/', '\\');

        // 单个盘符（"C:"）不能直接交给 GetFullPath，否则会解析成该盘的当前目录
        if (p.Length == 2 && p[1] == ':') return p.ToUpperInvariant() + "\\";

        try { p = Path.GetFullPath(p); }
        catch { return ""; }

        if (p.Length > 3) p = p.TrimEnd('\\');
        return p;
    }

    protected override string DisplayPath(string path) => path.Length == 0 ? "此电脑" : path;

    protected override string? GetParentPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (path.TrimEnd('\\').Length <= 3) return "";

        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent)) return "";
        return parent.Length > 3 ? parent.TrimEnd('\\') : parent;
    }

    // ── 列目录 ──

    protected override Task<List<FileEntry>> ListAsync(string path, CancellationToken ct) =>
        Task.Run(() => ListLocal(path), ct);

    static List<FileEntry> ListLocal(string path)
    {
        var list = new List<FileEntry>();

        if (string.IsNullOrEmpty(path))
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                FileEntry entry;
                try
                {
                    entry = new FileEntry
                    {
                        Name = d.Name,
                        Path = d.Name,
                        IsDir = true,
                        Permissions = d.IsReady ? DriveLabel(d) : "未就绪",
                    };
                }
                catch
                {
                    continue;
                }
                list.Add(entry);
            }
            return list;
        }

        var dir = new DirectoryInfo(path);
        foreach (var d in dir.EnumerateDirectories())
        {
            try { list.Add(FromFs(d)); }
            catch { /* 无权限访问的子目录跳过 */ }
        }
        foreach (var f in dir.EnumerateFiles())
        {
            try { list.Add(FromFs(f)); }
            catch { }
        }
        return list;
    }

    static string DriveLabel(DriveInfo d) =>
        string.IsNullOrEmpty(d.VolumeLabel) ? DescribeDriveType(d.DriveType) : d.VolumeLabel;

    static string DescribeDriveType(DriveType t) => t switch
    {
        DriveType.Fixed => "本地磁盘",
        DriveType.Removable => "可移动磁盘",
        DriveType.CDRom => "光盘驱动器",
        DriveType.Network => "网络驱动器",
        DriveType.Ram => "RAM 磁盘",
        _ => "驱动器",
    };

    static FileEntry FromFs(FileSystemInfo info)
    {
        bool isDir = (info.Attributes & FileAttributes.Directory) != 0;
        var entry = new FileEntry
        {
            Name = info.Name,
            Path = info.FullName,
            IsDir = isDir,
            DateStr = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
        };
        if (!isDir && info is FileInfo fi) entry.Size = fi.Length;
        return entry;
    }

    protected override void OnLoadFailed(string path, Exception ex) =>
        ShowError($"无法打开 {path}\n\n{ex.Message}");

    // ── 按钮 ──

    protected override void BuildButtons(FlowLayoutPanel bar)
    {
        var btnSend = NewBarButton("上传到设备 ➡", 106);
        btnSend.Click += (_, _) => RequestTransferSelection();
        bar.Controls.Add(btnSend);

        var btnNewDir = NewBarButton("新建文件夹", 86);
        btnNewDir.Click += OnNewFolder;
        bar.Controls.Add(btnNewDir);

        var btnExplore = NewBarButton("在资源管理器打开", 128);
        btnExplore.Click += (_, _) => OpenInExplorer(CurrentPath);
        bar.Controls.Add(btnExplore);

        var btnDel = NewBarButton("删除选中", 76);
        btnDel.Click += async (_, _) => await DeleteSelectedAsync();
        bar.Controls.Add(btnDel);
    }

    static void OpenInExplorer(string path)
    {
        if (string.IsNullOrEmpty(path)) path = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", path)
            {
                UseShellExecute = true,
            });
        }
        catch { /* 忽略 */ }
    }

    async void OnNewFolder(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(CurrentPath))
        {
            ShowError("请先进入一个具体目录，再新建文件夹。");
            return;
        }

        var name = SimpleInputBox.Show(this, "新建文件夹", "文件夹名称：", "新建文件夹");
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            var created = Directory.CreateDirectory(Path.Combine(CurrentPath, name.Trim()));
            await RefreshAsync();
            SelectByName(created.Name);
        }
        catch (Exception ex)
        {
            ShowError($"创建失败：{ex.Message}");
        }
    }

    public async Task DeleteSelectedAsync()
    {
        var entries = SelectedEntries;
        if (entries.Count == 0) return;

        var names = string.Join(", ", entries.Take(5).Select(e => e.Name));
        if (entries.Count > 5) names += $" …等 {entries.Count} 项";
        if (!Confirm($"确定要删除本机上的以下文件/目录吗？\n{names}\n\n（此操作不可撤销）", "确认删除")) return;

        int failed = 0;
        var errors = new System.Text.StringBuilder();
        foreach (var e in entries)
        {
            try
            {
                if (e.IsDir) Directory.Delete(e.Path, true);
                else File.Delete(e.Path);
            }
            catch (Exception ex)
            {
                failed++;
                if (errors.Length < 800) errors.AppendLine($"{e.Name}: {ex.Message}");
            }
        }

        if (failed > 0) ShowError($"{failed} 项删除失败：\n{errors}");
        else SetStatus($"已删除 {entries.Count} 项");

        await RefreshAsync();
    }

    /// <summary>把本机目录递归展开为文件列表。</summary>
    public static List<string> ExpandFiles(IEnumerable<string> paths)
    {
        var result = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                try { result.AddRange(Directory.GetFiles(p, "*", SearchOption.AllDirectories)); }
                catch { }
            }
            else if (File.Exists(p))
            {
                result.Add(p);
            }
        }
        return result;
    }
}
