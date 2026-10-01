using logcat.Services;

namespace logcat.Forms;

/// <summary>
/// 设备操作总窗口：截图 / 录屏 / 文件浏览 / 安装·卸载 APK / 命令窗口 合并为一个页签式窗口。
/// 所有功能同级显示在TabControl中，每个页签承载一个原独立对话框，
/// 以 TopLevel=false 嵌入，原窗口的初始化、设置保存与关闭清理逻辑全部复用、不改行为。
///
/// 页签按需创建（第一次点对应入口时才建）；换设备时重建绑定设备的页签，
/// 命令窗口因为走回调（执行时才取设备），天然跟随主窗口当前选中设备，不需要重建。
/// </summary>
public class DeviceOpsDialog : Form
{
    public enum PageKind { Screenshot, Record, Files, ApkInstall, ApkUninstall, Command }

    readonly AdbManager _manager;
    readonly Func<string?> _serialProvider;
    readonly Func<bool> _rootProvider;

    readonly TabControl _tabs = new() { Dock = DockStyle.Fill };

    // 所有页签统一管理（截图/录屏/文件浏览/APK/命令 同级显示）
    readonly Dictionary<PageKind, (TabPage Page, Form Form)> _pages = new();

    public DeviceOpsDialog(AdbManager manager, Func<string?> serialProvider, Func<bool> rootProvider)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        _manager = manager;
        _serialProvider = serialProvider;
        _rootProvider = rootProvider;

        Text = "设备操作";
        Size = new Size(1160, 760);
        MinimumSize = new Size(920, 600);
        // CenterParent 只对模态 ShowDialog 生效，Show(owner) 非模态会落到系统默认位置（常在主屏），
        // 改为 Manual 并在 OnLoad 按主窗口所在屏幕居中，保证弹出窗口跟随主窗口
        StartPosition = FormStartPosition.Manual;
        Controls.Add(_tabs);
        FormClosing += OnHostFormClosing;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        CenterOverOwner();
    }

    /// <summary>以主窗口为中心居中显示，并夹取到主窗口所在屏幕的工作区内（多屏时跟随主窗口所在屏）。</summary>
    void CenterOverOwner()
    {
        if (Owner == null) return;
        var b = Owner.Bounds;
        int x = b.X + (b.Width - Width) / 2;
        int y = b.Y + (b.Height - Height) / 2;
        var wa = Screen.FromControl(Owner).WorkingArea;
        x = Math.Clamp(x, wa.Left, Math.Max(wa.Left, wa.Right - Width));
        y = Math.Clamp(y, wa.Top, Math.Max(wa.Top, wa.Bottom - Height));
        Location = new Point(x, y);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // WinForms 不会随 DPI 放大 TabControl 的页签内边距，高缩放屏上文字会贴边甚至被裁，
        // 这里按当前 DPI 显式补齐（默认值 6,3 是 96 DPI 基准）
        _tabs.Padding = new Point(6 * _tabs.DeviceDpi / 96, 3 * _tabs.DeviceDpi / 96);

        // 窗口首次打开就把所有页签建齐，无设备时跳过设备相关页（等入口再建，会提示选设备）。
        EnsurePage(PageKind.Command);   // 命令窗口不要求选设备，始终可建
        if (!string.IsNullOrEmpty(_serialProvider()))
        {
            EnsurePage(PageKind.Screenshot);
            EnsurePage(PageKind.Record);
            EnsurePage(PageKind.Files);
            EnsurePage(PageKind.ApkInstall);
            EnsurePage(PageKind.ApkUninstall);
        }
    }

    static string Title(PageKind kind) => kind switch
    {
        PageKind.Screenshot => "截图",
        PageKind.Record => "录屏",
        PageKind.Files => "文件浏览",
        PageKind.ApkInstall => "安装 APK",
        PageKind.ApkUninstall => "卸载 APK",
        PageKind.Command => "命令窗口",
        _ => "?"
    };

    /// <summary>
    /// 打开（或切换到）某页。设备相关页需要主窗口已选设备，失败弹提示并返回 false。
    /// 所有页签同级显示，截图和文件浏览各自独立。
    /// </summary>
    public bool OpenPage(PageKind kind)
    {
        if (!_pages.TryGetValue(kind, out var entry) || entry.Form.IsDisposed)
        {
            var form = CreatePage(kind);
            if (form == null) return false;
            entry = _pages[kind];
        }
        _tabs.SelectedTab = entry.Page;
        return true;
    }

    /// <summary>确保某页签存在，不存在则创建。</summary>
    bool EnsurePage(PageKind kind)
    {
        if (_pages.TryGetValue(kind, out var entry) && !entry.Form.IsDisposed)
            return true;
        return CreatePage(kind) != null;
    }



    // ── 页签创建 ──

    /// <summary>把原对话框改造成可嵌入的页面控件，放进一个新 TabPage。</summary>
    static TabPage Embed(Form form, string title)
    {
        form.TopLevel = false;
        form.FormBorderStyle = FormBorderStyle.None;
        form.Dock = DockStyle.Fill;
        var page = new TabPage(title);
        page.Controls.Add(form);
        return page;
    }

    Form? CreatePage(PageKind kind)
    {
        var serial = _serialProvider();
        if (kind != PageKind.Command && string.IsNullOrEmpty(serial))
        {
            MessageBox.Show(this, "请先在主窗口选择设备", "设备操作",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }

        Form form = kind switch
        {
            PageKind.Screenshot => new ScreenShotDialog(_manager, serial!),
            PageKind.Record => new ScreenRecordDialog(_manager, serial!),
            PageKind.Files => new FileBrowserDialog(_manager, serial!, _rootProvider(),
                logcat.Services.AppSettings.Default.LastRemotePath ?? "/sdcard",
                logcat.Services.AppSettings.Default.LastLocalPath ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
            PageKind.ApkInstall => new ApkInstallDialog(_manager, serial!, _rootProvider()),
            PageKind.ApkUninstall => new ApkUninstallDialog(_manager, serial!, _rootProvider()),
            _ => new CommandDialog(_manager, _serialProvider, _rootProvider),
        };

        var page = Embed(form, Title(kind));
        _tabs.TabPages.Add(page);
        _pages[kind] = (page, form);
        form.FormClosed += (_, _) => RemovePage(kind);
        form.Show();
        return form;
    }

    void RemovePage(PageKind kind)
    {
        if (!_pages.Remove(kind, out var entry)) return;
        if (!entry.Page.IsDisposed)
        {
            _tabs.TabPages.Remove(entry.Page);
            entry.Page.Dispose();
        }
    }

    // ── 设备切换 / 关窗 ──

    /// <summary>
    /// 主窗口选中的设备变了：重建绑定设备的页签，保证页签永远对着当前设备；
    /// 用户停留的页优先恢复。没有设备时只关闭不重建，等下次从入口打开。
    /// </summary>
    public void OnDeviceChanged()
    {
        var selectedPage = _tabs.SelectedTab;
        PageKind? current = null;   // 用户当前停留的页，最后恢复（OpenPage 会切换选中）
        var reopen = new List<PageKind>();

        // 收集需要重建的页签（命令窗口通过回调取设备，不需要重建）
        foreach (var (kind, entry) in _pages)
        {
            if (kind == PageKind.Command) continue;  // 命令窗口不重建
            if (entry.Form is { IsDisposed: false })
            {
                reopen.Add(kind);
                if (selectedPage == entry.Page) current = kind;
                entry.Form.Close();
            }
        }

        if (current == null && reopen.Count == 0) return;
        if (string.IsNullOrEmpty(_serialProvider())) return;

        // 先重建非当前页，最后重建当前页（OpenPage 会切换选中）
        foreach (var kind in reopen.Where(k => k != current)) OpenPage(kind);
        if (current != null) OpenPage(current.Value);
    }

    /// <summary>
    /// 关窗时逐个关掉嵌入页，让它们走自己的关闭清理（保存设置、停止录屏、取消任务等）；
    /// 用户主动关窗时如有页面拒绝关闭（如文件传输进行中），取消整个窗口的关闭；
    /// 应用退出（关主窗口）则不阻止、也不弹确认框。
    /// </summary>
    void OnHostFormClosing(object? sender, FormClosingEventArgs e)
    {
        bool userClose = e.CloseReason == CloseReason.UserClosing;
        foreach (var (_, form) in _pages.Values.ToArray())
        {
            if (form.IsDisposed || form.Disposing) continue;
            // 文件浏览页需要特殊处理：应用退出时不阻止关闭
            if (form is FileBrowserDialog fb)
                fb.BlockCloseWhileTransferring = !userClose;
            form.Close();
        }

        if (userClose && AnyPageAlive())
            e.Cancel = true;
    }

    bool AnyPageAlive() =>
        _pages.Values.Any(p => !p.Form.IsDisposed && !p.Form.Disposing);
}
