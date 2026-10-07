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

    public LocalFilePane() : base(Loc.T("本机电脑")) { }

    // ── 列与行 ──

    protected override void CreateColumns(ListView list)
    {
        Loc.BindCol(new ColumnHeader { Text = Loc.T("名称"), Width = 250 }, "名称");
        Loc.BindCol(new ColumnHeader { Text = Loc.T("大小"), Width = 90, TextAlign = HorizontalAlignment.Right }, "大小");
        Loc.BindCol(new ColumnHeader { Text = Loc.T("类型"), Width = 100 }, "类型");
        Loc.BindCol(new ColumnHeader { Text = Loc.T("修改日期"), Width = 140 }, "修改日期");
    }

    protected override string[] GetRow(FileEntry e) => new[]
    {
        (e.IsDir ? "📁 " : "📄 ") + e.Name,
        e.IsDir ? "" : HumanSize(e.Size),
        e.IsDir ? Loc.T("文件夹") : TypeText(e.Name),
        e.DateStr,
    };

    static string TypeText(string name)
    {
        var ext = Path.GetExtension(name);
        return string.IsNullOrEmpty(ext) ? "文件" : Loc.F("{0} 文件", ext.TrimStart('.').ToUpperInvariant());
    }

    // ── 路径 ──

    protected override string NormalizePath(string path)
    {
        var p = (path ?? "").Trim().Trim('"');
        // 不用 `p is "" or Loc.T(...)` 形式：C# 会把 Loc.T(...) 当成「类型模式」而非方法调用，
        // 编译报 CS0426。两种语言的「此电脑」都要认——切语言后 DisplayPath 的输出语言会变。
        if (p.Length == 0 || p == "此电脑" || p == "我的电脑"
            || p == Loc.T("此电脑") || p == Loc.T("我的电脑")) return "";

        p = p.Replace('/', '\\');

        // 单个盘符（"C:"）不能直接交给 GetFullPath，否则会解析成该盘的当前目录
        if (p.Length == 2 && p[1] == ':') return p.ToUpperInvariant() + "\\";

        try { p = Path.GetFullPath(p); }
        catch { return ""; }

        if (p.Length > 3) p = p.TrimEnd('\\');
        return p;
    }

    protected override string DisplayPath(string path) => path.Length == 0 ? Loc.T("此电脑") : path;

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
                        Permissions = d.IsReady ? DriveLabel(d) : Loc.T("未就绪"),
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
        DriveType.Fixed => Loc.T("本地磁盘"),
        DriveType.Removable => Loc.T("可移动磁盘"),
        DriveType.CDRom => Loc.T("光盘驱动器"),
        DriveType.Network => Loc.T("网络驱动器"),
        DriveType.Ram => Loc.T("RAM 磁盘"),
        _ => Loc.T("驱动器"),
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
        ShowError(Loc.F("无法打开 {0}\n\n{1}", path, ex.Message));

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

        var btnRename = NewBarButton("重命名", 76);
        btnRename.Click += async (_, _) => await RenameSelectedAsync();
        bar.Controls.Add(btnRename);
    }

    // ── 右键菜单 ──

    protected override void OnBuildContextMenu(ContextMenuStrip menu)
    {
        menu.Items.Add(Loc.T("➡ 上传到设备"), null, (_, _) => RequestTransferSelection());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Loc.T("新建文件夹"), null, OnNewFolder);
        menu.Items.Add(Loc.T("在资源管理器打开"), null, (_, _) => OpenInExplorer(CurrentPath));
        menu.Items.Add(Loc.T("删除选中"), null, async (_, _) => await DeleteSelectedAsync());
        menu.Items.Add(new ToolStripSeparator());
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
            ShowError(Loc.T("请先进入一个具体目录，再新建文件夹。"));
            return;
        }

        var name = SimpleInputBox.Show(this, Loc.T("新建文件夹"), Loc.T("文件夹名称："), Loc.T("新建文件夹"));
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            var created = Directory.CreateDirectory(Path.Combine(CurrentPath, name.Trim()));
            await RefreshAsync();
            SelectByName(created.Name);
        }
        catch (Exception ex)
        {
            ShowError(Loc.F("创建失败：{0}", ex.Message));
        }
    }

    public async Task DeleteSelectedAsync()
    {
        var entries = SelectedEntries;
        if (entries.Count == 0) return;

        var names = string.Join(", ", entries.Take(5).Select(e => e.Name));
        if (entries.Count > 5) names += Loc.F(" …等 {0} 项", entries.Count);
        if (!Confirm(Loc.F("确定要删除本机上的以下文件/目录吗？\n{0}\n\n（此操作不可撤销）", names), "确认删除")) return;

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

        if (failed > 0) ShowError(Loc.F("{0} 项删除失败：\n{1}", failed, errors));
        else SetStatus(Loc.F("已删除 {0} 项", entries.Count));

        await RefreshAsync();
    }

    // ── 重命名（本机端走 File/Directory.Move）──

    protected override Task RenameAsync(FileEntry e, string newName)
    {
        var dir = Path.GetDirectoryName(e.Path) ?? "";
        var newPath = Path.Combine(dir, newName);
        if (File.Exists(newPath) || Directory.Exists(newPath))
            throw new IOException($"目标已存在：{newName}");
        if (e.IsDir) Directory.Move(e.Path, newPath);
        else File.Move(e.Path, newPath);
        return Task.CompletedTask;
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
