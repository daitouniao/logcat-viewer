using logcat.Services;

namespace logcat.Forms;

/// <summary>
/// APK 卸载窗口。
/// 支持 <c>adb uninstall</c> 与 <c>pm uninstall</c>，包名可收藏复用。
/// </summary>
public sealed class ApkUninstallDialog : Form, ILocalizedUi
{
    readonly AdbManager _manager;
    readonly string _serial;
    readonly bool _isRoot;
    readonly FavoritesStore _fav = FavoritesStore.Default;

    readonly IProgress<string> _progress;
    CancellationTokenSource? _cts;
    bool _busy;

    // 卸载页
    ComboBox _cboPkg = null!;
    RadioButton _rbAdbUninstall = null!, _rbPmUninstall = null!;
    CheckBox _ckKeepData = null!, _ckUninstallRoot = null!;
    Button _btnUninstall = null!, _btnRefreshApps = null!;
    ListView _lvApps = null!;

    /// <summary>pm 无输出时 adb 会报 unresponsive，用哨兵确保总有回显。</summary>
    const string ListMarker = "__APK_LIST_DONE__";

    // 公共
    TextBox _txtOut = null!;
    Label _lblStat = null!, _lblDevice = null!;
    Button _btnStop = null!, _btnCopy = null!, _btnClear = null!;

    public ApkUninstallDialog(AdbManager manager, string serial, bool isRoot)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        _manager = manager;
        _serial = serial;
        _isRoot = isRoot;
        _progress = new Progress<string>(AppendOutput);

        Text = Loc.F("卸载 APK — {0}", serial);
        Size = new Size(780, 700);
        MinimumSize = new Size(700, 600);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;

        BuildUi();
        LoadOptions();
        UpdateUninstallRootEnabled();
        RefreshPkgCombo();
        _lblDevice.Text = Loc.F("设备：{0}{1}", serial, isRoot ? "  [root]" : "");
        Shown += async (_, _) => await RefreshAppsAsync();
        FormClosed += (_, _) => { SaveOptions(); try { _cts?.Cancel(); } catch { } _cts?.Dispose(); };
    }

    // ── 界面 ──

    void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(6),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        Controls.Add(root);

        _lblDevice = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
        };
        root.Controls.Add(_lblDevice, 0, 0);

        var uninstallPanel = BuildUninstallPanel();
        root.Controls.Add(uninstallPanel, 0, 1);

        var outGroup = Loc.Bind(new GroupBox { Dock = DockStyle.Fill, Padding = new Padding(6, 4, 6, 6) }, "输出");
        _txtOut = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
            Font = new Font("Consolas", 9.5F),
            BackColor = Color.White,
        };
        outGroup.Controls.Add(_txtOut);
        root.Controls.Add(outGroup, 0, 2);

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
        };
        _lblStat = new Label
        {
            AutoSize = false,
            Width = 360,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 6, 6, 0),
        };
        bar.Controls.Add(_lblStat);
        _btnStop = BarButton("停止", 64);
        _btnStop.Enabled = false;
        _btnStop.Click += (_, _) => Stop();
        bar.Controls.Add(_btnStop);
        _btnCopy = BarButton("复制输出", 78);
        _btnCopy.Click += (_, _) => CopyOutput();
        bar.Controls.Add(_btnCopy);
        _btnClear = BarButton("清空", 60);
        _btnClear.Click += (_, _) => _txtOut.Clear();
        bar.Controls.Add(_btnClear);
        root.Controls.Add(bar, 0, 3);
    }

    Panel BuildUninstallPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Margin = Padding.Empty,
        };
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));   // 包名输入
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));   // 卸载方式
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));   // 选项
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));   // 卸载按钮+提示
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));    // 应用列表
        panel.Controls.Add(grid);

        // 行0：包名输入 + 收藏 + 刷新列表
        var rowPkg = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        rowPkg.Controls.Add(Loc.Bind(new Label { AutoSize = true, Margin = new Padding(0, 7, 4, 0) }, "应用包名:"));
        _cboPkg = new ComboBox { Width = 300, DropDownStyle = ComboBoxStyle.DropDown, Margin = new Padding(0, 4, 4, 0) };
        rowPkg.Controls.Add(_cboPkg);
        var btnFavPkg = Loc.Bind(new Button { AutoSize = true, MinimumSize = new Size(60, 25), Margin = new Padding(0, 3, 4, 0) }, "★ 收藏");
        btnFavPkg.Click += (_, _) => FavPkg();
        rowPkg.Controls.Add(btnFavPkg);
        var btnUnfavPkg = Loc.Bind(new Button { AutoSize = true, MinimumSize = new Size(60, 25), Margin = new Padding(0, 3, 4, 0) }, "☆ 移除");
        btnUnfavPkg.Click += (_, _) => UnfavPkg();
        rowPkg.Controls.Add(btnUnfavPkg);
        _btnRefreshApps = Loc.Bind(new Button { AutoSize = true, MinimumSize = new Size(92, 25), Margin = new Padding(8, 3, 0, 0) }, "↻ 刷新列表");
        _btnRefreshApps.Click += async (_, _) => await RefreshAppsAsync();
        rowPkg.Controls.Add(_btnRefreshApps);
        grid.Controls.Add(rowPkg, 0, 0);

        // 行1：卸载方式
        var rowMode = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        rowMode.Controls.Add(Loc.Bind(new Label { AutoSize = true, Margin = new Padding(0, 7, 4, 0) }, "卸载方式:"));
        _rbAdbUninstall = new RadioButton { Text = "adb uninstall", AutoSize = true, Checked = true, Margin = new Padding(0, 5, 14, 0) };
        _rbPmUninstall = Loc.Bind(new RadioButton { AutoSize = true, Margin = new Padding(0, 5, 0, 0) }, "pm uninstall（adb 卸载被禁用时）");
        foreach (var rb in new[] { _rbAdbUninstall, _rbPmUninstall })
            rb.CheckedChanged += (_, _) => UpdateUninstallRootEnabled();
        rowMode.Controls.Add(_rbAdbUninstall);
        rowMode.Controls.Add(_rbPmUninstall);
        grid.Controls.Add(rowMode, 0, 1);

        // 行2：选项
        var rowOpt = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        _ckKeepData = Loc.Bind(new CheckBox { AutoSize = true, Margin = new Padding(0, 5, 20, 0) }, "-k 保留数据和缓存");
        _ckUninstallRoot = Loc.Bind(new CheckBox { AutoSize = true, Enabled = false, Margin = new Padding(0, 5, 0, 0) }, "以 root 执行（su -c 包裹，仅 pm 生效）");
        rowOpt.Controls.Add(_ckKeepData);
        rowOpt.Controls.Add(_ckUninstallRoot);
        grid.Controls.Add(rowOpt, 0, 2);

        // 行3：卸载按钮 + 提示
        var rowAct = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        _btnUninstall = Loc.Bind(new Button { AutoSize = true, MinimumSize = new Size(150, 30), Margin = new Padding(0, 5, 10, 0) }, "卸载输入的包名");
        _btnUninstall.Click += (_, _) => DoUninstall();
        rowAct.Controls.Add(_btnUninstall);
        rowAct.Controls.Add(Loc.Bind(new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 13, 0, 0) }, "提示：双击下方列表项可直接卸载；卸载系统预装应用通常需 root。"));
        grid.Controls.Add(rowAct, 0, 3);

        // 列表
        // 列表
        _lvApps = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            HideSelection = false,
            MultiSelect = false,
            Font = new Font("Consolas", 9.5F),
            Margin = Padding.Empty,
        };
        _lvApps.Columns.Add(Loc.BindCol(new ColumnHeader { Text = Loc.T("应用名"), Width = 180 }, "应用名"));
        _lvApps.Columns.Add(Loc.BindCol(new ColumnHeader { Text = Loc.T("包名"), Width = 280 }, "包名"));
        _lvApps.Columns.Add(Loc.BindCol(new ColumnHeader { Text = Loc.T("APK 路径"), Width = -2 }, "APK 路径")); // -2 自动填充剩余宽度
        _lvApps.DoubleClick += (_, _) => OnAppDoubleClicked();
        _lvApps.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { OnAppDoubleClicked(); e.Handled = true; } };
        grid.Controls.Add(_lvApps, 0, 4);

        return panel;
    }

    /// <summary>
    /// 工具栏按钮。宽度不能用固定值：<paramref name="width"/> 只是<b>中文态</b>的宽度，
    /// 英文文案普遍更长（"复制输出" 78px→"Copy Output" 96px），固定宽度必裁字。
    /// 改用 AutoSize +中文宽度下限，并走 <c>Loc.Bind</c> 绑定资源键——
    /// 只用 <c>Text = Loc.T(…)</c> 赋值的话，切语言时文本与宽度都停在旧值上。
    /// </summary>
    static Button BarButton(string text, int zhWidth) => Loc.Bind(new Button
    {
        AutoSize = true,
        MinimumSize = new Size(zhWidth, 25),
        Margin = new Padding(2, 4, 0, 0),
    }, text);

    // ── 收藏 ──

    void RefreshPkgCombo()
    {
        var cur = _cboPkg.Text;
        _cboPkg.Items.Clear();
        foreach (var p in _fav.Packages) _cboPkg.Items.Add(p);
        _cboPkg.Text = cur;
    }

    void FavPkg()
    {
        var pkg = _cboPkg.Text.Trim();
        if (pkg.Length == 0) { Status(Loc.T("包名为空，无法收藏")); return; }
        _fav.AddPackage(pkg);
        _fav.Save();
        RefreshPkgCombo();
        Status(Loc.F("已收藏：{0}", pkg));
    }

    void UnfavPkg()
    {
        var pkg = _cboPkg.Text.Trim();
        if (!_fav.RemovePackage(pkg)) { Status(Loc.T("该包名未在收藏中")); return; }
        _fav.Save();
        RefreshPkgCombo();
        Status(Loc.F("已移除收藏：{0}", pkg));
    }

    // ── root 选项联动 ──

    void UpdateUninstallRootEnabled()
    {
        _ckUninstallRoot.Enabled = _rbPmUninstall.Checked;
        if (!_ckUninstallRoot.Enabled) _ckUninstallRoot.Checked = false;
    }

    // ── 选项记忆 ──

    void LoadOptions()
    {
        var s = logcat.Services.AppSettings.Default;
        _rbPmUninstall.Checked = s.ApkUninstallViaPm;
        _rbAdbUninstall.Checked = !s.ApkUninstallViaPm;
        _ckKeepData.Checked = s.ApkKeepData;
        _ckUninstallRoot.Checked = s.ApkUseRoot;
    }

    void SaveOptions()
    {
        var s = logcat.Services.AppSettings.Default;
        s.ApkUninstallViaPm = _rbPmUninstall.Checked;
        s.ApkKeepData = _ckKeepData.Checked;
        s.ApkUseRoot = _ckUninstallRoot.Checked;
        try { s.Save(); } catch { }
    }

    // ── 执行 ──

    async void DoUninstall()
    {
        if (_busy) return;
        var pkg = _cboPkg.Text.Trim();
        if (pkg.Length == 0) { Status(Loc.T("请输入或选择要卸载的应用包名")); return; }
        if (!Confirm(Loc.F("确定要卸载应用 {0} 吗？", pkg), "确认卸载")) return;
        await UninstallAsync(pkg, refreshList: true);
    }

    void OnAppDoubleClicked()
    {
        if (_busy || _lvApps.SelectedItems.Count == 0) return;
        var item = _lvApps.SelectedItems[0];
        var pkg = item.SubItems[1].Text;
        var name = item.SubItems[0].Text;
        if (pkg.Length == 0) return;
        if (!Confirm(Loc.F("确定要卸载「{0}」吗？\n\n包名：{1}\n此操作不可恢复。", name, pkg), "确认卸载")) return;
        _cboPkg.Text = pkg;
        _ = UninstallAsync(pkg, refreshList: true);
    }

    async Task UninstallAsync(string pkg, bool refreshList)
    {
        if (_busy) return;
        bool pm = _rbPmUninstall.Checked;
        bool root = pm && _ckUninstallRoot.Checked;
        var keep = _ckKeepData.Checked ? "-k " : "";

        if (!Begin()) return;
        try
        {
            if (!pm)
            {
                var args = AdbManager.AdbArgs($"uninstall {keep}{pkg}", _serial);
                AppendOutput($"> adb {args}");
                int code = await AdbManager.RunAdbAsync(args, _progress, _cts!.Token);
                AppendOutput(Loc.F("— 退出码 {0}", code));
                Status(code == 0 ? "adb uninstall 完成，请检查输出中的 Success" : Loc.F("adb uninstall 结束（退出码 {0}）", code));
            }
            else
            {
                var cmd = $"pm uninstall {keep}{pkg}";
                AppendOutput($"> adb shell {(root ? "su -c " : "")}{cmd}");
                await _manager.ShellLinesAsync(_serial, Wrap(cmd, root), _progress, _cts!.Token);
                Status(Loc.T("pm uninstall 执行完毕，请检查输出中的 Success/Failure"));
            }
            _fav.AddPackage(pkg);
            _fav.Save();
            RefreshPkgCombo();
            if (refreshList) await FetchAppsAsync();
        }
        catch (OperationCanceledException) { Status(Loc.T("已取消")); }
        catch (Exception ex) { AppendOutput("!! " + Friendly(ex)); Status(Loc.F("卸载失败：{0}", Friendly(ex))); }
        finally { End(); }
    }

    async Task RefreshAppsAsync()
    {
        if (_busy) return;
        if (!Begin()) return;
        try { await FetchAppsAsync(); }
        catch (OperationCanceledException) { Status(Loc.T("已取消")); }
        catch (Exception ex) { AppendOutput("!! " + Friendly(ex)); Status(Loc.F("读取应用列表失败：{0}", Friendly(ex))); }
        finally { End(); }
    }

    async Task FetchAppsAsync()
    {
        AppendOutput("> adb shell pm list packages -3 -f");
        var raw = await _manager.ShellAsync(_serial, $"pm list packages -3 -f 2>/dev/null; echo {ListMarker}", 40);
        var apps = new List<(string Pkg, string Path)>();
        foreach (var line in raw.Split('\n'))
        {
            var l = line.Trim().TrimEnd('\r');
            if (!l.StartsWith("package:", StringComparison.Ordinal)) continue;
            var rest = l["package:".Length..];
            int eq = rest.LastIndexOf('=');
            if (eq <= 0 || eq >= rest.Length - 1) continue;
            apps.Add((rest[(eq + 1)..], rest[..eq]));
        }
        if (apps.Count == 0 && !raw.Contains(ListMarker, StringComparison.Ordinal))
            throw new InvalidOperationException(Loc.T("设备未返回应用列表（可能无三方应用或权限不足）"));

        var labels = await FetchLabelsAsync();
        _lvApps.BeginUpdate();
        _lvApps.Items.Clear();
        foreach (var (pkg, path) in apps.OrderBy(a => a.Pkg, StringComparer.OrdinalIgnoreCase))
        {
            var name = labels.TryGetValue(pkg, out var lb) && lb.Length > 0 ? lb : FallbackName(pkg);
            var it = new ListViewItem(name);
            it.SubItems.Add(pkg);
            it.SubItems.Add(path);
            _lvApps.Items.Add(it);
        }
        _lvApps.EndUpdate();
        Status(Loc.F("已加载 {0} 个三方应用", apps.Count) + (labels.Count == 0 ? Loc.T("（未取到应用名，已用包名末段代替）") : ""));
    }

    async Task<Dictionary<string, string>> FetchLabelsAsync()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            const string cmd =
                "command -v aapt >/dev/null 2>&1 && pm list packages -3 | tr -d '\\r' | sed s/^package:// | " +
                "while read p; do ap=$(pm path \"$p\" 2>/dev/null | head -1 | tr -d '\\r' | sed s/^package://); " +
                "l=$(aapt dump badging \"$ap\" 2>/dev/null | grep ^application-label: | head -1 | cut -d\"'\" -f2); " +
                "printf \"%s\\t%s\\n\" \"$p\" \"$l\"; done; echo " + ListMarker;
            var outp = await _manager.ShellAsync(_serial, cmd, 90);
            foreach (var line in outp.Split('\n'))
            {
                var l = line.TrimEnd('\r');
                if (l.Contains(ListMarker, StringComparison.Ordinal)) continue;
                int t = l.IndexOf('\t');
                if (t <= 0) continue;
                var lbl = l[(t + 1)..].Trim();
                if (lbl.Length > 0) map[l[..t]] = lbl;
            }
        }
        catch { }
        return map;
    }

    static string FallbackName(string pkg)
    {
        int i = pkg.LastIndexOf('.');
        var tail = i >= 0 && i < pkg.Length - 1 ? pkg[(i + 1)..] : pkg;
        return tail.Length == 0 ? pkg : tail;
    }

    static string Wrap(string cmd, bool root) =>
        root ? $"su -c {AdbManager.ShellQuote(cmd)}" : cmd;

    bool Confirm(string text, string title) =>
        MessageBox.Show(this, text, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    bool Begin()
    {
        if (_busy) return false;
        _busy = true;
        _cts = new CancellationTokenSource();
        _btnUninstall.Enabled = false;
        _btnRefreshApps.Enabled = false;
        _btnStop.Enabled = true;
        Status(Loc.T("执行中…"));
        return true;
    }

    void End()
    {
        _busy = false;
        _btnStop.Enabled = false;
        _btnUninstall.Enabled = true;
        _btnRefreshApps.Enabled = true;
        _cts?.Dispose();
        _cts = null;
    }

    void Stop()
    {
        if (!_busy) return;
        try { _cts?.Cancel(); } catch { }
        Status(Loc.T("停止中…"));
    }

    // ── 输出 ──

    void AppendOutput(string line)
    {
        if (_txtOut.TextLength > 400_000) _txtOut.Clear();
        _txtOut.AppendText(line + Environment.NewLine);
        _txtOut.SelectionStart = _txtOut.TextLength;
        _txtOut.ScrollToCaret();
    }

    void CopyOutput()
    {
        if (_txtOut.TextLength == 0) { Status(Loc.T("没有输出可复制")); return; }
        ClipboardHelper.SetText(_txtOut.Text);
        Status(Loc.T("输出已复制到剪贴板"));
    }

    void Status(string text) => _lblStat.Text = text;

    static string Friendly(Exception ex)
    {
        var msg = ex.Message ?? "";
        if (msg.Contains("unresponsive", StringComparison.OrdinalIgnoreCase))
            msg = Loc.T("命令没有返回输出：可能是命令不存在、权限不足，或被设备限制（可试试勾选 root 或改用 pm 通道）");
        var inner = ex.InnerException?.Message;
        if (!string.IsNullOrEmpty(inner) && !msg.Contains(inner, StringComparison.Ordinal))
            msg += $"（{inner}）";
        return msg;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        try { _cts?.Cancel(); } catch { }
        base.OnFormClosing(e);
    }
    /// <summary>
    /// 切语言后重算标题与设备标签——两者都是带参数的 <c>Loc.F</c>（设备序列号是变量），
    /// 没法用 <c>Loc.Bind</c> 绑静态资源键，只能在这里重算。
    /// </summary>
    void ILocalizedUi.OnLanguageChanged()
    {
        Text = Loc.F("卸载 APK — {0}", _serial);
        _lblDevice.Text = Loc.F("设备：{0}{1}", _serial, _isRoot ? "  [root]" : "");
    }
}
