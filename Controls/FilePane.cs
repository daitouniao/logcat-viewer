using logcat.Forms;
using logcat.Models;
using logcat.Services;

namespace logcat.Controls;

/// <summary>跨面板拖放时携带的路径信息。</summary>
public sealed class PathsDroppedArgs : EventArgs
{
    /// <summary>被拖动的源路径集合。</summary>
    public IReadOnlyList<string> Paths { get; }

    /// <summary>源路径所在的基准目录，用于计算相对路径。</summary>
    public string SourceDirectory { get; }

    /// <summary>拖放的目标目录。</summary>
    public string TargetDirectory { get; }

    public PathsDroppedArgs(IReadOnlyList<string> paths, string sourceDirectory, string targetDirectory)
    {
        Paths = paths;
        SourceDirectory = sourceDirectory;
        TargetDirectory = targetDirectory;
    }
}

/// <summary>
/// 文件浏览面板基类：左侧为 Android 设备目录，右侧为本机目录。
/// 两者共用导航、列表渲染、收藏与拖放逻辑，仅目录枚举方式不同。
/// </summary>
public abstract class FilePane : UserControl
{
    protected const string DeviceDragFormat = "logcat.DevicePaths";
    protected const string LocalDragFormat = "logcat.LocalPaths";
    /// <summary>跨面板拖放时附带源基准目录的格式（后缀拼接在各面板格式之后）。</summary>
    protected const string BaseDirFormatSuffix = ".BaseDir";

    protected readonly ListView List = null!;
    protected readonly ComboBox CboFav = null!;
    protected readonly TextBox EdPath = null!;
    protected readonly Label LblStat = null!;
    protected readonly ProgressBar Pbar = null!;
    protected readonly Button BtnFav = null!;
    protected readonly Button BtnUnfav = null!;
    /// <summary>面板外框，子类可改标题以展示当前模式（如 run-as 包名）。</summary>
    protected readonly GroupBox Box = null!;
    readonly TableLayoutPanel TlpGrid = null!;
    readonly FlowLayoutPanel FavBar = null!;
    readonly FlowLayoutPanel PathBar = null!;
    readonly FlowLayoutPanel BtnBar = null!;

    CancellationTokenSource? _cts;
    bool _favEventSuppressed;

    /// <summary>是否为设备端（远端）面板。</summary>
    public abstract bool IsRemote { get; }

    /// <summary>当前目录。</summary>
    public string CurrentPath { get; private set; } = "";

    public FavoritesStore Favorites => FavoritesStore.Default;

    /// <summary>本面板拖出数据时使用的格式。</summary>
    protected abstract string DragFormat { get; }

    /// <summary>来自另一侧面板的数据格式。</summary>
    protected abstract string PeerDragFormat { get; }

    public event EventHandler? PathChanged;
    public event EventHandler<string>? StatusChanged;

    /// <summary>另一侧面板的文件拖入本面板，需要跨设备传输。</summary>
    public event EventHandler<PathsDroppedArgs>? PeerPathsDropped;

    /// <summary>系统文件管理器拖入本面板的文件。</summary>
    public event EventHandler<PathsDroppedArgs>? FilesDropped;

    /// <summary>请求把本面板选中的文件传送到另一侧。</summary>
    public event EventHandler? TransferSelectionRequested;

    /// <summary>双击文件。</summary>
    public event EventHandler<string>? FileActivated;

    protected FilePane(string caption)
    {
        Dock = DockStyle.Fill;

        var box = Box = new GroupBox
        {
            Text = caption,
            Dock = DockStyle.Fill,
            Padding = new Padding(6, 2, 6, 6),
        };
        Controls.Add(box);

        // 行高用 AutoSize 而非写死的绝对值：绝对行高在高 DPI（150%）/嵌入 TabPage 的缩放链路里
        // 不会被重算，工具栏会被裁掉一截；AutoSize 让行高始终跟随控件实测高度。
        var grid = TlpGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // 收藏栏
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // 路径栏
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));    // 文件列表
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // 操作栏
        box.Controls.Add(grid);

        // ── 收藏栏 ──
        var favBar = FavBar = NewBar();
        favBar.Controls.Add(NewBarLabel("收藏:"));
        CboFav = new ComboBox
        {
            Width = 300,
            DropDownWidth = 460,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Margin = new Padding(3, 4, 3, 0),
        };
        CboFav.SelectedIndexChanged += OnFavSelected;
        CboFav.MouseUp += OnFavMouseUp;
        favBar.Controls.Add(CboFav);

        BtnFav = NewBarButton("★ 收藏当前目录", 120);
        BtnFav.Click += (_, _) => ToggleFavorite();
        favBar.Controls.Add(BtnFav);

        BtnUnfav = NewBarButton("☆ 取消收藏", 96);
        BtnUnfav.Click += (_, _) => RemoveFavorite(CurrentPath);
        favBar.Controls.Add(BtnUnfav);
        grid.Controls.Add(favBar, 0, 0);

        // ── 路径栏 ──
        var pathBar = PathBar = NewBar();
        pathBar.Controls.Add(NewBarLabel("路径:"));
        EdPath = new TextBox
        {
            Width = 300,
            Margin = new Padding(3, 5, 3, 0),
        };
        EdPath.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            NavigateTo(EdPath.Text);
        };
        pathBar.Controls.Add(EdPath);

        var btnGo = NewBarButton("前往", 56);
        btnGo.Click += (_, _) => NavigateTo(EdPath.Text);
        pathBar.Controls.Add(btnGo);

        var btnUp = NewBarButton("上级", 56);
        btnUp.Click += (_, _) => GoUp();
        pathBar.Controls.Add(btnUp);

        var btnRefresh = NewBarButton("刷新", 56);
        btnRefresh.Click += (_, _) => _ = RefreshAsync();
        pathBar.Controls.Add(btnRefresh);
        grid.Controls.Add(pathBar, 0, 1);

        // ── 文件列表 ──
        List = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = true,
            GridLines = false,
            AllowDrop = true,
            HideSelection = false,
            LabelEdit = false,
            Font = new Font("Consolas", 10),
        };
        CreateColumns(List);
        ApplySavedColumnWidths();
        List.ColumnWidthChanged += OnColumnWidthChanged;
        List.DoubleClick += (_, _) => ActivateSelected();
        List.KeyDown += OnListKeyDown;
        List.ItemDrag += OnItemDrag;
        List.DragEnter += OnDragEnter;
        List.DragDrop += OnDragDrop;
        List.MouseUp += OnListMouseUp;
        BuildContextMenu();
        grid.Controls.Add(List, 0, 2);

        // ── 操作栏 ──
        var btnBar = BtnBar = NewBar();
        BuildButtons(btnBar);

        LblStat = new Label
        {
            AutoSize = true,
            Padding = new Padding(8, 9, 0, 0),
            ForeColor = Color.DimGray,
        };
        btnBar.Controls.Add(LblStat);

        Pbar = new ProgressBar
        {
            Width = 110,
            Height = 18,
            Visible = false,
            Margin = new Padding(8, 6, 0, 0),
        };
        btnBar.Controls.Add(Pbar);
        grid.Controls.Add(btnBar, 0, 3);

        Favorites.Changed += (_, _) => RefreshFavCombo();
        RefreshFavCombo();

        // 面板变窄（分栏拖动、小窗、高 DPI 缩放）时收缩下拉框/路径框，保证最右侧按钮不被裁出视野
        Resize += (_, _) => FitBarWidths();
    }

    /// <summary>把收藏下拉框、路径输入框的宽度钳制到本条工具栏的剩余空间内（上限仍是原设计宽度）。</summary>
    void FitBarWidths()
    {
        ClampBarChild(FavBar, CboFav, min: 100, max: 300);
        ClampBarChild(PathBar, EdPath, min: 100, max: 300);
    }

    static void ClampBarChild(FlowLayoutPanel bar, Control child, int min, int max)
    {
        if (bar.Width <= 0) return;
        int others = 0;
        foreach (Control c in bar.Controls)
            if (c != child) others += c.Width + c.Margin.Horizontal;
        int avail = bar.ClientSize.Width - bar.Padding.Horizontal - child.Margin.Horizontal - others;
        child.Width = Math.Clamp(avail, min, max);
    }

    /// <summary>诊断用：三条工具栏的实际宽度 vs 内容期望宽度、网格行高快照。</summary>
    internal string DescribeLayout()
    {
        try
        {
            var rows = string.Join(",", TlpGrid.RowStyles.Cast<RowStyle>()
                .Select(r => r.SizeType == SizeType.Absolute ? $"abs:{r.Height}" : r.SizeType.ToString().ToLower()));
            return $"pane={Width}x{Height} rows=[{rows}] {DescribeBar("收藏栏", FavBar)} {DescribeBar("路径栏", PathBar)} {DescribeBar("操作栏", BtnBar)}";
        }
        catch (Exception ex) { return "DescribeLayout异常:" + ex.Message; }
    }

    static string DescribeBar(string name, FlowLayoutPanel bar)
    {
        int prefW = 0;
        foreach (Control c in bar.Controls) prefW += c.Width + c.Margin.Horizontal;
        return $"{name}(bar={bar.Width},pref≈{prefW})";
    }

    // ── 子类扩展点 ──

    protected virtual void CreateColumns(ListView list)
    {
        list.Columns.Add("名称", 250);
        list.Columns.Add("大小", 90, HorizontalAlignment.Right);
        list.Columns.Add("修改日期", 140);
    }

    // ── 列宽持久化 ──
    // 列宽按面板类型（设备端/本机端）分别记入 settings.json，
    // 事件里只更新内存，防抖 400ms 后才写盘；对话框/主窗关闭时的
    // SaveLastPaths / SaveSettings 也会整体 s.Save()，多一层兜底。

    System.Windows.Forms.Timer? _widthSaveTimer;

    List<int> ColumnWidthsSetting
    {
        get => IsRemote ? AppSettings.Default.DevicePaneColumnWidths
                        : AppSettings.Default.LocalPaneColumnWidths;
        set
        {
            if (IsRemote) AppSettings.Default.DevicePaneColumnWidths = value;
            else AppSettings.Default.LocalPaneColumnWidths = value;
        }
    }

    void ApplySavedColumnWidths()
    {
        var saved = ColumnWidthsSetting;
        for (int i = 0; i < List.Columns.Count && i < saved.Count; i++)
            if (saved[i] > 0) List.Columns[i].Width = saved[i];
    }

    void OnColumnWidthChanged(object? sender, ColumnWidthChangedEventArgs e)
    {
        ColumnWidthsSetting = List.Columns.Cast<ColumnHeader>()
            .Select(c => c.Width).ToList();

        // 拖动调宽时事件高频触发，停顿 400ms 再落盘
        if (_widthSaveTimer == null)
        {
            _widthSaveTimer = new System.Windows.Forms.Timer { Interval = 400 };
            _widthSaveTimer.Tick += (_, _) =>
            {
                _widthSaveTimer!.Stop();
                AppSettings.Default.Save();
            };
        }
        _widthSaveTimer.Stop();
        _widthSaveTimer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _widthSaveTimer?.Dispose();
        base.Dispose(disposing);
    }

    protected virtual string[] GetRow(FileEntry e) => new[]
    {
        (e.IsDir ? "📁 " : "📄 ") + e.Name,
        e.IsDir ? "" : HumanSize(e.Size),
        e.DateStr,
    };

    /// <summary>枚举指定目录下的条目。</summary>
    protected abstract Task<List<FileEntry>> ListAsync(string path, CancellationToken ct);

    /// <summary>构建本面板特有的操作按钮。</summary>
    protected abstract void BuildButtons(FlowLayoutPanel bar);

    /// <summary>规范化路径。</summary>
    protected abstract string NormalizePath(string path);

    /// <summary>获取上级目录，已在根目录时返回 null。</summary>
    protected abstract string? GetParentPath(string path);

    /// <summary>路径栏显示文本。</summary>
    protected virtual string DisplayPath(string path) => path;

    protected virtual void OnLoadFailed(string path, Exception ex) =>
        ShowError($"无法打开 {path}\n\n{ex.Message}");

    // ── 导航 ──

    public void NavigateTo(string path)
    {
        var p = NormalizePath(path ?? "");
        CurrentPath = p;
        EdPath.Text = DisplayPath(p);
        SyncFavSelection();
        PathChanged?.Invoke(this, EventArgs.Empty);
        _ = RefreshAsync();
    }

    public void GoUp()
    {
        if (string.IsNullOrEmpty(CurrentPath)) return;
        var parent = GetParentPath(CurrentPath);
        if (parent == null) return;
        NavigateTo(parent);
    }

    public Task RefreshAsync() => LoadIntoAsync(CurrentPath);

    async Task LoadIntoAsync(string path)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        List.Items.Clear();
        SetStatus("加载中…");
        Pbar.Visible = true;

        try
        {
            var entries = await ListAsync(path, ct);
            if (ct.IsCancellationRequested) return;
            Populate(entries);
            SetStatus($"{entries.Count} 项");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            SetStatus("加载失败");
            OnLoadFailed(path, ex);
        }
        finally
        {
            if (!ct.IsCancellationRequested) Pbar.Visible = false;
        }
    }

    protected void Populate(List<FileEntry> entries)
    {
        List.BeginUpdate();
        List.Items.Clear();
        entries.Sort((a, b) =>
        {
            if (a.IsDir != b.IsDir) return a.IsDir ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        foreach (var e in entries)
        {
            var cells = GetRow(e);
            var item = new ListViewItem(cells[0]) { Tag = e };
            for (int i = 1; i < cells.Length; i++) item.SubItems.Add(cells[i]);
            List.Items.Add(item);
        }
        List.EndUpdate();
    }

    public void CancelLoading() => _cts?.Cancel();

    // ── 选中项 ──

    public List<FileEntry> SelectedEntries =>
        List.SelectedItems.Cast<ListViewItem>()
            .Select(i => i.Tag as FileEntry)
            .Where(e => e != null)
            .Select(e => e!)
            .ToList();

    public List<string> SelectedPaths => SelectedEntries.Select(e => e.Path).ToList();

    public void SelectByName(string name)
    {
        foreach (ListViewItem item in List.Items)
        {
            if (item.Tag is FileEntry e && e.Name == name)
            {
                item.Selected = true;
                item.EnsureVisible();
                List.Focus();
                return;
            }
        }
    }

    void ActivateSelected()
    {
        var entries = SelectedEntries;
        if (entries.Count != 1) return;
        var e = entries[0];
        if (e.IsDir) NavigateTo(e.Path);
        else FileActivated?.Invoke(this, e.Path);
    }

    void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            ActivateSelected();
        }
        else if (e.KeyCode == Keys.Back)
        {
            e.SuppressKeyPress = true;
            GoUp();
        }
        else if (e.KeyCode == Keys.F2)
        {
            e.SuppressKeyPress = true;
            _ = RenameSelectedAsync();
        }
        else if (e.KeyCode == Keys.C && e.Control)
        {
            e.SuppressKeyPress = true;
            CopySelectedPaths();
        }
    }

    // ── 右键菜单 ──

    /// <summary>右键某个未选中的项目时，让其成为唯一选中项（资源管理器习惯）。</summary>
    void OnListMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        var item = List.GetItemAt(e.X, e.Y);
        if (item == null || item.Selected) return;
        foreach (ListViewItem sel in List.SelectedItems) sel.Selected = false;
        item.Selected = true;
    }

    void BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        // 子类在菜单顶部追加功能项（传输、新建、删除等）
        OnBuildContextMenu(menu);
        if (menu.Items.Count > 0) menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("复制完整路径", null, (_, _) => CopySelectedPaths());
        menu.Items.Add("复制文件名", null, (_, _) =>
        {
            var names = SelectedEntries.Select(e => e.Name);
            if (names.Any()) TrySetClipboard(string.Join(Environment.NewLine, names));
        });
        menu.Items.Add("重命名", null, (_, _) => _ = RenameSelectedAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("刷新", null, (_, _) => _ = RefreshAsync());
        List.ContextMenuStrip = menu;
    }

    /// <summary>子类在文件列表右键菜单的顶部追加功能项。</summary>
    protected virtual void OnBuildContextMenu(ContextMenuStrip menu) { }

    void CopySelectedPaths()
    {
        var paths = SelectedPaths;
        if (paths.Count == 0) return;
        TrySetClipboard(string.Join(Environment.NewLine, paths));
        SetStatus($"已复制 {paths.Count} 个路径");
    }

    static void TrySetClipboard(string text)
    {
        Services.ClipboardHelper.SetText(text); // 内部已重试并吞掉最终失败
    }

    // ── 重命名 ──

    /// <summary>重命名当前选中的一个或多个条目（逐个弹出输入框）。</summary>
    public async Task RenameSelectedAsync()
    {
        var entries = SelectedEntries;
        if (entries.Count == 0) return;
        foreach (var e in entries)
            await RenameOneAsync(e);
    }

    async Task RenameOneAsync(FileEntry e)
    {
        var name = SimpleInputBox.Show(this, "重命名", $"输入新名称：\n{e.Path}", e.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        // 设备端（Linux）允许尾随空格/点，本机端（Windows）去掉尾随点以免变成隐藏名
        name = IsRemote ? name.Trim() : name.Trim().TrimEnd('.');
        if (name == e.Name) return;
        if (!ValidateNewName(name, e.Path, out var error))
        {
            ShowError(error);
            return;
        }
        try
        {
            Pbar.Visible = true;
            SetStatus($"重命名 {e.Name} → {name}…");
            await RenameAsync(e, name);
            await RefreshAsync();
            SelectByName(name);
            SetStatus($"已重命名为 {name}");
        }
        catch (Exception ex)
        {
            ShowError($"重命名失败：{ex.Message}");
            await RefreshAsync();
        }
        finally { Pbar.Visible = false; }
    }

    /// <summary>执行实际重命名：设备端走 shell mv，本机走 File/Directory.Move。</summary>
    protected abstract Task RenameAsync(FileEntry e, string newName);

    /// <summary>校验新名称是否可用（空、非法字符、与同目录现有项冲突等）。</summary>
    protected virtual bool ValidateNewName(string name, string currentPath, out string error)
    {
        error = "";
        if (name.Length == 0) { error = "名称不能为空。"; return false; }
        bool invalid = IsRemote
            ? name.Any(c => c == '/' || c == '\0')
            : name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0;
        if (invalid) { error = "名称包含非法字符。"; return false; }
        if (name is "." or "..") { error = "不能使用 . 或 .. 作为名称。"; return false; }
        if (NameExistsInCurrentDir(name, currentPath)) { error = $"当前目录已存在名为「{name}」的项。"; return false; }
        return true;
    }

    /// <summary>当前目录列表中是否已存在同名项（用于重命名冲突预检）。</summary>
    protected bool NameExistsInCurrentDir(string name, string? exceptPath = null)
    {
        foreach (ListViewItem item in List.Items)
        {
            if (item.Tag is not FileEntry e) continue;
            if (exceptPath != null && FavoritesStore.PathEquals(e.Path, exceptPath, IsRemote)) continue;
            if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ── 收藏 ──

    public void RefreshFavCombo()
    {
        var list = Favorites.Get(IsRemote);
        CboFav.BeginUpdate();
        CboFav.Items.Clear();
        CboFav.Items.Add(list.Count == 0 ? "— 暂无收藏目录 —" : $"— 收藏目录（{list.Count}）—");
        foreach (var f in list) CboFav.Items.Add(f);
        CboFav.EndUpdate();
        SyncFavSelection();
    }

    /// <summary>让下拉框选中项与当前目录保持一致。</summary>
    void SyncFavSelection()
    {
        var list = Favorites.Get(IsRemote);
        int idx = list.FindIndex(f => FavoritesStore.PathEquals(f.Path, CurrentPath, IsRemote));
        _favEventSuppressed = true;
        CboFav.SelectedIndex = idx < 0 ? 0 : idx + 1;
        _favEventSuppressed = false;
        UpdateFavButtons();
    }

    void UpdateFavButtons()
    {
        bool canFav = !string.IsNullOrEmpty(CurrentPath);
        bool has = canFav && Favorites.Contains(IsRemote, CurrentPath);
        BtnFav.Enabled = canFav && !has;
        BtnFav.Text = has ? "★ 已收藏" : "★ 收藏当前目录";
        BtnUnfav.Enabled = has;
    }

    void OnFavSelected(object? sender, EventArgs e)
    {
        if (_favEventSuppressed) return;
        // 第 0 项是提示行（"暂无收藏目录 / 收藏目录（N）"）
        if (CboFav.SelectedIndex > 0 && CboFav.SelectedItem is FavoriteDir fav)
            NavigateTo(fav.Path);
    }

    void OnFavMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        if (CboFav.SelectedIndex <= 0 || CboFav.SelectedItem is not FavoriteDir fav)
        {
            ShowError("请先在下拉框中选中一个收藏目录，再右键取消收藏。\n" +
                      "（也可以直接用左侧的「☆ 取消收藏」按钮移除当前目录。）");
            return;
        }

        var menu = new ContextMenuStrip();
        menu.Items.Add("跳转到该目录", null, (_, _) => NavigateTo(fav.Path));
        menu.Items.Add("取消收藏", null, (_, _) => RemoveFavorite(fav.Path));
        menu.Items.Add("重命名备注…", null, (_, _) => RenameFavorite(fav.Path));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("清空收藏", null, (_, _) =>
        {
            if (!Confirm($"确定要清空{(IsRemote ? "设备端" : "本机端")}的全部收藏目录吗？", "清空收藏")) return;
            Favorites.Clear(IsRemote);
            Favorites.Save();
            RefreshFavCombo();
        });
        menu.Show(CboFav, e.Location);
    }

    void ToggleFavorite()
    {
        if (string.IsNullOrEmpty(CurrentPath)) return;
        if (Favorites.Contains(IsRemote, CurrentPath))
        {
            RemoveFavorite(CurrentPath);
            return;
        }
        Favorites.Add(IsRemote, CurrentPath);
        Favorites.Save();
        RefreshFavCombo();
        SetStatus($"已收藏：{CurrentPath}");
    }

    void RemoveFavorite(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (!Favorites.Remove(IsRemote, path))
        {
            SetStatus("该目录未被收藏");
            return;
        }
        Favorites.Save();
        RefreshFavCombo();
        SetStatus($"已取消收藏：{path}");
    }

    void RenameFavorite(string path)
    {
        var cur = Favorites.Get(IsRemote)
            .FirstOrDefault(f => FavoritesStore.PathEquals(f.Path, path, IsRemote));
        var alias = SimpleInputBox.Show(this, "收藏备注",
            $"为收藏目录设置备注名（留空则直接显示路径）：\n{path}",
            cur?.Alias ?? "");
        if (alias == null) return;
        Favorites.SetAlias(IsRemote, path, alias);
        Favorites.Save();
        RefreshFavCombo();
    }

    // ── 拖放 ──

    void OnItemDrag(object? sender, ItemDragEventArgs e)
    {
        var paths = SelectedPaths;
        if (paths.Count == 0) return;
        var data = new DataObject(DragFormat, paths.ToArray());
        // 附带源基准目录，接收方才能还原拖入文件夹的相对结构
        data.SetData(DragFormat + BaseDirFormatSuffix, CurrentPath);
        List.DoDragDrop(data, DragDropEffects.Copy);
    }

    void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data == null) return;
        if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(PeerDragFormat))
            e.Effect = DragDropEffects.Copy;
    }

    void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data == null) return;
        var target = DropTargetPath(e);

        if (e.Data.GetDataPresent(PeerDragFormat) &&
            e.Data.GetData(PeerDragFormat) is string[] peer && peer.Length > 0)
        {
            var peerBase = e.Data.GetData(PeerDragFormat + BaseDirFormatSuffix) as string;
            PeerPathsDropped?.Invoke(this, new PathsDroppedArgs(peer, peerBase ?? CurrentPath, target));
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            // 资源管理器拖入时源目录取拖动文件的公共父目录，子目录结构得以保留
            FilesDropped?.Invoke(this, new PathsDroppedArgs(files, CommonLocalDir(files), target));
        }
    }

    /// <summary>计算若干本机路径的公共父目录；无法确定时返回空串（退化为只取文件名）。</summary>
    static string CommonLocalDir(IReadOnlyList<string> paths)
    {
        var dirs = paths.Select(p =>
        {
            var d = Directory.Exists(p) ? p.TrimEnd('\\', '/') : Path.GetDirectoryName(p) ?? "";
            return d.Length == 0 ? "" : d.TrimEnd('\\', '/');
        }).ToList();

        if (dirs.Count == 0) return "";
        if (dirs.Count == 1) return dirs[0];

        var common = dirs[0];
        foreach (var d in dirs.Skip(1))
        {
            int len = Math.Min(common.Length, d.Length);
            int i = 0;
            while (i < len && char.ToLowerInvariant(common[i]) == char.ToLowerInvariant(d[i])) i++;
            common = common[..i];
            if (common.Length == 0) return "";
        }
        // 截到最后一个目录分隔符，避免切在路径中间（如 "C:\Pic" 与 "C:\Pics"）
        int cut = common.LastIndexOfAny(['\\', '/']);
        return cut <= 0 ? "" : common[..cut];
    }

    /// <summary>拖放到某个目录项上时进入该目录，否则落到当前目录。</summary>
    string DropTargetPath(DragEventArgs e)
    {
        var pt = List.PointToClient(new Point(e.X, e.Y));
        var item = List.GetItemAt(pt.X, pt.Y);
        if (item?.Tag is FileEntry entry && entry.IsDir) return entry.Path;
        return CurrentPath;
    }

    /// <summary>请求把本面板选中的文件传送到另一侧。</summary>
    protected void RequestTransferSelection() => TransferSelectionRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>抛出「外部文件落入本面板」事件。</summary>
    protected void RaiseFilesDropped(IReadOnlyList<string> paths, string sourceDirectory, string targetDirectory) =>
        FilesDropped?.Invoke(this, new PathsDroppedArgs(paths, sourceDirectory, targetDirectory));

    // ── 提示与工具 ──

    protected void SetStatus(string text)
    {
        LblStat.Text = text;
        StatusChanged?.Invoke(this, text);
    }

    protected void ShowError(string msg) =>
        MessageBox.Show(this, msg, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    protected bool Confirm(string msg, string title) =>
        MessageBox.Show(this, msg, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    protected static string HumanSize(long n)
    {
        if (n < 0) return "";
        double val = n;
        foreach (var unit in new[] { "B", "KB", "MB", "GB", "TB" })
        {
            if (val < 1024 || unit == "TB")
                return unit == "B" ? $"{val:F0} {unit}" : $"{val:F1} {unit}";
            val /= 1024;
        }
        return $"{val:F1} TB";
    }

    protected static FlowLayoutPanel NewBar() => new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.LeftToRight,
        // 允许换行：面板被拖窄时，宁可工具栏变两行（AutoSize 行会跟着长高），
        // 也不能把最右侧的按钮裁掉——WrapContents=false 时超出部分直接不可见。
        WrapContents = true,
        // AutoSize 必开：TableLayoutPanel 的 AutoSize 行是按子控件的「实际高度」撑开的，
        // 未开 AutoSize 的 FlowLayoutPanel 会带着 WinForms 默认高度 100 参与测量，
        // 于是每条工具栏行都被撑成 100px（内容只需 30px），行间留下大片空白。
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Padding = new Padding(3, 0, 0, 0),
        Margin = Padding.Empty,
    };

    protected static Label NewBarLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Padding = new Padding(0, 8, 2, 0),
        Margin = new Padding(3, 0, 0, 0),
    };

    protected static Button NewBarButton(string text, int width) => new()
    {
        Text = text,
        Width = width,
        Height = 26,
        Margin = new Padding(3, 4, 0, 0),
    };
}
