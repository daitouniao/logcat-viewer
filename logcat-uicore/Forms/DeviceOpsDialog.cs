using System.Runtime.InteropServices;
using logcat.Models;
using logcat.Services;

namespace logcat.Forms;

/// <summary>
/// 固定页签的设备跟随约定：宿主在选中设备变化后逐个通知，实现方刷新标题/设备标签等文案，
/// 页签本身不重建（执行时才取当前设备，见 <see cref="DeviceOpsDialog"/> 的页签分类）。
/// </summary>
public interface IDeviceFollowUi
{
    /// <summary>当前选中设备（或其 root 形态）变化后由宿主调用；可能在页签不可见时触发。</summary>
    void OnDeviceSerialChanged();
}

/// <summary>
/// 设备操作总窗口：截图 / 录屏 / 文件浏览 / 安装·卸载 APK / 命令窗口 合并为一个页签式窗口。
/// 所有功能同级显示在TabControl中，每个页签承载一个原独立对话框，
/// 以 TopLevel=false 嵌入，原窗口的初始化、设置保存与关闭清理逻辑全部复用、不改行为。
///
/// 页签按需创建（第一次点对应入口时才建）；换设备时重建绑定设备的页签，
/// 命令窗口因为走回调（执行时才取设备），天然跟随主窗口当前选中设备，不需要重建。
/// </summary>
public class DeviceOpsDialog : Form, ILocalizedUi
{
    public enum PageKind { Screenshot, Record, Files, ApkInstall, ApkUninstall, Command }

    readonly AdbManager _manager;
    readonly Func<string?> _serialProvider;
    readonly Func<RootMode> _rootProvider;

    // 上次重建页签时的设备序列号与 root 形态：设备监控每次推送都会走到 OnDeviceChanged，
    // 设备与 root 都没变时直接跳过，否则页签会被反复关了又建（表现为不停闪）
    string? _lastSerial;
    RootMode _lastRoot;

    readonly SinglePageTabs _tabs = new() { Dock = DockStyle.Fill };

    /// <summary>
    /// 只有一个页签时隐藏页签头：工具箱启动早期设备未就绪，只建出命令窗口一个页签，
    /// 孤悬的「命令窗口」页签头没有信息量，直接吞掉；之后页签多于一个时自动恢复。
    /// 原理：拦截 <c>TCM_ADJUSTRECT</c>，把显示区上边压到 0（吞掉页签头）、其余方向外扩盖住原生 3D 边框。
    /// </summary>
    sealed class SinglePageTabs : TabControl
    {
        const int TCM_ADJUSTRECT = 0x1328;

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == TCM_ADJUSTRECT && TabCount == 1)
            {
                base.WndProc(ref m);
                var rc = Marshal.PtrToStructure<RECT>(m.LParam);
                rc.Top = 0;
                rc.Left -= 4;
                rc.Right += 4;
                rc.Bottom += 4;
                Marshal.StructureToPtr(rc, m.LParam, false);
                m.Result = (IntPtr)1;
            }
            else
            {
                base.WndProc(ref m);
            }
        }
    }

    // 所有页签统一管理（截图/录屏/文件浏览/APK/命令 同级显示）
    readonly Dictionary<PageKind, (TabPage Page, Form Form)> _pages = new();

    public DeviceOpsDialog(AdbManager manager, Func<string?> serialProvider, Func<RootMode> rootProvider)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        _manager = manager;
        _serialProvider = serialProvider;
        _rootProvider = rootProvider;

        Text = Loc.T("设备操作");   // TabPage 标题由 ForwardLanguageChanged 按 kind 重算
        Loc.Bind(this, "设备操作");   // 登记资源键，切语言时 ApplyTo 才能重设标题
        Size = new Size(1160, 760);
        MinimumSize = new Size(920, 600);
        // CenterParent 只对模态 ShowDialog 生效，Show(owner) 非模态会落到系统默认位置（常在主屏），
        // 改为 Manual 并在 OnLoad 按主窗口所在屏幕居中，保证弹出窗口跟随主窗口
        StartPosition = FormStartPosition.Manual;
        Controls.Add(_tabs);
        FormClosing += OnHostFormClosing;
        DpiChanged += (_, e) =>
        {
            DpiDiag.Write($"DeviceOpsDialog.DpiChanged dpi→{DeviceDpi} suggested={e.SuggestedRectangle} bounds={Bounds}");
            BeginInvoke(() => DpiFix.Apply(this));
        };
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
        DpiDiag.Log("DeviceOpsDialog.OnShown", this);

        // 窗口首次打开就把所有页签建齐。固定页签（命令窗口/截屏/录屏/安装 APK）
        // 执行时才取当前设备，无设备也照建；文件浏览/卸载 APK 钉住设备快照，无设备时先不建
        EnsurePage(PageKind.Command);
        EnsurePage(PageKind.Screenshot);
        EnsurePage(PageKind.Record);
        EnsurePage(PageKind.ApkInstall);
        if (!string.IsNullOrEmpty(_serialProvider()))
        {
            EnsurePage(PageKind.Files);
            EnsurePage(PageKind.ApkUninstall);
        }
        MaybeOfferRootRetry();   // 上次 su 阶段未确认时给一次重试 root 检测的机会
        DpiFix.Apply(this);

        // 记下打开时的设备形态：OnShown 建齐的页签以此为基准，
        // 之后 OnDeviceChanged 只有形态真的变了才重建（否则首次监控推送就会白重建一轮）
        _lastSerial = _serialProvider();
        _lastRoot = _rootProvider();
    }

    /// <summary>
    /// 重试 root 检测入口：当前设备上次 su 阶段未得到明确结果（授权被拒/等待超时）时，
    /// 弹窗让用户选择重试。仅此条件触发——明确无 su 的设备重测大概率还是失败，不必打扰；
    /// 确认后失效该设备的缓存重测，结果经 DevicesChanged 刷新主窗口的设备标记。
    /// </summary>
    void MaybeOfferRootRetry()
    {
        var serial = _serialProvider();
        if (string.IsNullOrEmpty(serial)) return;
        if (_manager.GetRootDetectEntry(serial) is not { Mode: RootMode.None, SuFailed: true }) return;
        if (MessageBox.Show(this,
                Loc.T("上次 root 检测未得到明确结果（su 授权可能被拒绝或等待超时）。\n是否重试 root 检测？设备端可能弹出授权提示。"),
                Loc.T("设备操作"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        _ = _manager.RedetectRootAsync(serial);
    }

    static string Title(PageKind kind) => kind switch
    {
        PageKind.Screenshot => Loc.T("截图"),
        PageKind.Record => Loc.T("录屏"),
        PageKind.Files => Loc.T("文件浏览"),
        PageKind.ApkInstall => Loc.T("安装 APK"),
        PageKind.ApkUninstall => Loc.T("卸载 APK"),
        PageKind.Command => Loc.T("命令窗口"),
        _ => "?"
    };

    /// <summary>
    /// 语言切换后刷新：页签标题 + 各嵌入页的内容。
    /// 嵌入页 <see cref="TopLevel"/>=false，**不在** <c>Application.OpenForms</c> 里，
    /// <see cref="Loc.ApplyToAllForms"/> 遍历不到，必须由本宿主显式转发。
    /// </summary>
    public void ForwardLanguageChanged()
    {
        Text = Loc.T("设备操作");
        foreach (var (kind, entry) in _pages)
        {
            if (entry.Page.IsDisposed || entry.Form.IsDisposed) continue;
            entry.Page.Text = Title(kind);
            // 嵌入页不走 OpenForms，绑定文本/字体/宽度要逐个刷
            Loc.ApplyTo(entry.Form);
            Loc.FitWidths(entry.Form);
            if (entry.Form is ILocalizedUi ui) ui.OnLanguageChanged();
            DpiFix.Apply(entry.Form);
        }
        _tabs.PerformLayout();
    }

    void ILocalizedUi.OnLanguageChanged() => ForwardLanguageChanged();

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
        // 只有钉住设备快照的页签（文件浏览/卸载 APK）要求建页时有设备；
        // 固定页签执行时才取当前设备，无设备时创建不受限、用时再提示
        var serial = _serialProvider();
        bool needsSerial = kind is PageKind.Files or PageKind.ApkUninstall;
        if (needsSerial && string.IsNullOrEmpty(serial))
        {
            MessageBox.Show(this, Loc.T("请先在主窗口选择设备"), Loc.T("设备操作"),
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }

        Form form = kind switch
        {
            PageKind.Screenshot => new ScreenShotDialog(_manager, _serialProvider),
            PageKind.Record => new ScreenRecordDialog(_manager, _serialProvider),
            PageKind.Files => new FileBrowserDialog(_manager, serial!, _rootProvider()),
            PageKind.ApkInstall => new ApkInstallDialog(_manager, _serialProvider, _rootProvider),
            PageKind.ApkUninstall => new ApkUninstallDialog(_manager, serial!, _rootProvider()),
            _ => new CommandDialog(_manager, _serialProvider, _rootProvider),
        };

        var page = Embed(form, Title(kind));
        _tabs.TabPages.Add(page);
        _pages[kind] = (page, form);
        form.FormClosed += (_, _) => RemovePage(kind);
        form.Show();
        // 嵌入页签的缩放链路不保证重算 TLP 绝对行高，显示后按内容实测兜底撑大
        DpiFix.Apply(form);
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
    /// 主窗口选中的设备变了：文件浏览 / 卸载 APK 这类钉住设备快照的页签重建，
    /// 其余页签固定不重建（命令窗口回调取设备；截屏/录屏/安装 APK 执行时取当前设备，只刷文案）。
    /// 没有设备时只关闭可重建页签，等设备就绪再补建。
    /// </summary>
    public void OnDeviceChanged()
    {
        // 设备与 root 形态都没变就不折腾：监控每次推送、下拉框回填都会走到这里，
        // 重建页签代价大（逐个关窗重建）且视觉上不停闪，只对真变化响应
        var serial = _serialProvider();
        var root = _rootProvider();
        if (serial == _lastSerial && root == _lastRoot) return;
        _lastSerial = serial;
        _lastRoot = root;

        // 固定页签：不重建，刷新标题/设备标签里的序列号即可
        foreach (var (_, entry) in _pages)
        {
            if (entry.Form is IDeviceFollowUi follow && entry.Form is { IsDisposed: false })
                follow.OnDeviceSerialChanged();
        }

        var selectedPage = _tabs.SelectedTab;   // 重建会切换选中，结束时恢复用户停留的页

        // 只关闭钉住设备快照的页签（文件浏览/卸载 APK），下面重建
        foreach (var kind in new[] { PageKind.Files, PageKind.ApkUninstall })
        {
            if (_pages.TryGetValue(kind, out var entry) && entry.Form is { IsDisposed: false })
                entry.Form.Close();
        }

        if (string.IsNullOrEmpty(serial)) return;   // 无设备时只关闭不重建，等设备就绪
        foreach (var kind in new[] { PageKind.Files, PageKind.ApkUninstall })
            OpenPage(kind);

        // 恢复用户停留的页（若还在）
        if (selectedPage != null && !selectedPage.IsDisposed && _tabs.TabPages.Contains(selectedPage))
            _tabs.SelectedTab = selectedPage;
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
