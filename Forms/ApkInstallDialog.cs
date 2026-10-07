using logcat.Services;

namespace logcat.Forms;

/// <summary>
/// APK 安装窗口。
/// 支持两条通道：本机 <c>adb install</c>（可带 -r -d -g -t 参数），
/// 或在 adb install 被禁用时改用设备端 <c>pm install</c>（先推送 APK 到临时目录再安装）。
/// APK 路径可收藏复用。
/// </summary>
public sealed class ApkInstallDialog : Form, ILocalizedUi
{
    readonly AdbManager _manager;
    readonly string _serial;
    // root 开关要参与标题/设备标签的本地化重算（Loc.F 带参数），故留字段
    readonly bool _isRoot;
    readonly FavoritesStore _fav = FavoritesStore.Default;

    readonly IProgress<string> _progress;
    CancellationTokenSource? _cts;
    bool _busy;

    // 安装页
    ComboBox _cboApk = null!;
    RadioButton _rbAdbInstall = null!, _rbPmInstall = null!;
    CheckBox _ckR = null!, _ckD = null!, _ckG = null!, _ckT = null!, _ckInstallRoot = null!;
    TextBox _txtTmp = null!;
    Button _btnInstall = null!;

    // 公共
    TextBox _txtOut = null!;
    Label _lblStat = null!, _lblDevice = null!;
    Button _btnStop = null!, _btnCopy = null!, _btnClear = null!;

    public ApkInstallDialog(AdbManager manager, string serial, bool isRoot)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        _manager = manager;
        _serial = serial;
        _progress = new Progress<string>(AppendOutput);

        _isRoot = isRoot;
        Text = Loc.F("安装 APK — {0}", serial);
        Size = new Size(780, 700);
        MinimumSize = new Size(700, 600);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;

        BuildUi();
        LoadOptions();
        UpdateInstallRootEnabled();
        RefreshApkCombo();
        _lblDevice.Text = Loc.F("设备：{0}{1}", serial, isRoot ? "  [root]" : "");
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
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));   // 设备信息
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 210));  // 安装选项
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 输出
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));   // 按钮栏
        Controls.Add(root);

        _lblDevice = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
        };
        root.Controls.Add(_lblDevice, 0, 0);

        var installPanel = BuildInstallPanel();
        root.Controls.Add(installPanel, 0, 1);

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

    Panel BuildInstallPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };

        panel.Controls.Add(Loc.Bind(new Label { Location = new Point(10, 16), AutoSize = true }, "APK 文件:"));
        // 绝对定位：下拉框宽度固定，三个按钮按实测文本宽度从右往左排（英文更宽，整排向左让位）
        const int rightEdge = 660;
        _cboApk = new ComboBox { Location = new Point(86, 12), Width = 372, DropDownStyle = ComboBoxStyle.DropDown };
        panel.Controls.Add(_cboApk);
        var btnUnfavApk = Loc.SizedButton("☆ 移除", 62, 25);
        btnUnfavApk.Location = new Point(rightEdge - btnUnfavApk.Width, 11);
        panel.Controls.Add(btnUnfavApk);
        var btnFavApk = Loc.SizedButton("★ 收藏", 62, 25);
        btnFavApk.Location = new Point(btnUnfavApk.Left - 4 - btnFavApk.Width, 11);
        panel.Controls.Add(btnFavApk);
        var btnBrowse = Loc.SizedButton("浏览…", 62, 25);
        btnBrowse.Location = new Point(btnFavApk.Left - 4 - btnBrowse.Width, 11);
        panel.Controls.Add(btnBrowse);
        btnBrowse.Click += (_, _) => BrowseApk();
        btnFavApk.Click += (_, _) => FavApk();
        btnUnfavApk.Click += (_, _) => UnfavApk();

        panel.Controls.Add(Loc.Bind(new Label { Location = new Point(10, 52), AutoSize = true }, "安装方式:"));
        _rbAdbInstall = new RadioButton { Text = "adb install", Location = new Point(86, 49), AutoSize = true, Checked = true };
        _rbPmInstall = Loc.Bind(new RadioButton { Location = new Point(200, 49), AutoSize = true }, "pm install（adb 安装被禁用时，先推送到设备再装）");
        foreach (var rb in new[] { _rbAdbInstall, _rbPmInstall })
            rb.CheckedChanged += (_, _) => UpdateInstallRootEnabled();
        panel.Controls.Add(_rbAdbInstall);
        panel.Controls.Add(_rbPmInstall);

        panel.Controls.Add(Loc.Bind(new Label { Location = new Point(10, 86), AutoSize = true }, "参数:"));
        _ckR = Loc.Bind(new CheckBox { Location = new Point(86, 83), AutoSize = true, Checked = true }, "-r 覆盖安装（保留数据）");
        _ckD = Loc.Bind(new CheckBox { Location = new Point(266, 83), AutoSize = true }, "-d 允许降级");
        _ckG = Loc.Bind(new CheckBox { Location = new Point(380, 83), AutoSize = true }, "-g 授予全部权限");
        _ckT = Loc.Bind(new CheckBox { Location = new Point(520, 83), AutoSize = true }, "-t 允许测试包");
        panel.Controls.AddRange(new Control[] { _ckR, _ckD, _ckG, _ckT });

        _ckInstallRoot = Loc.Bind(new CheckBox { Location = new Point(86, 116), AutoSize = true, Enabled = false }, "以 root 执行（su -c 包裹，仅 pm install 生效）");
        panel.Controls.Add(_ckInstallRoot);
        panel.Controls.Add(Loc.Bind(new Label { Location = new Point(400, 118), AutoSize = true }, "临时目录:"));
        _txtTmp = new TextBox { Location = new Point(470, 114), Width = 188, Text = "/data/local/tmp" };
        panel.Controls.Add(_txtTmp);

        _btnInstall = Loc.Bind(new Button { Location = new Point(86, 150), Size = new Size(120, 32) }, "安装");
        _btnInstall.Click += (_, _) => DoInstall();
        panel.Controls.Add(_btnInstall);
        panel.Controls.Add(Loc.Bind(new Label { Location = new Point(216, 158), AutoSize = true, ForeColor = Color.DimGray }, "提示：pm install 完成后会自动清理临时 APK。输出里出现 Success 即安装成功。"));

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

    void RefreshApkCombo()
    {
        var cur = _cboApk.Text;
        _cboApk.Items.Clear();
        foreach (var p in _fav.ApkPaths) _cboApk.Items.Add(p);
        _cboApk.Text = cur;
    }

    void BrowseApk()
    {
        using var dlg = new OpenFileDialog
        {
            Title = Loc.T("选择要安装的 APK"),
            Filter = Loc.T("APK 文件 (*.apk)|*.apk|所有文件 (*.*)|*.*"),
        };
        var last = _cboApk.Text.Trim();
        if (last.Length > 0)
        {
            var dir = Path.GetDirectoryName(last);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
        }
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _cboApk.Text = dlg.FileName;
    }

    void FavApk()
    {
        var path = _cboApk.Text.Trim().Trim('"');
        if (path.Length == 0) { Status(Loc.T("APK 路径为空，无法收藏")); return; }
        _fav.AddApkPath(path);
        _fav.Save();
        RefreshApkCombo();
        Status(Loc.F("已收藏：{0}", path));
    }

    void UnfavApk()
    {
        var path = _cboApk.Text.Trim().Trim('"');
        if (!_fav.RemoveApkPath(path)) { Status(Loc.T("该路径未在收藏中")); return; }
        _fav.Save();
        RefreshApkCombo();
        Status(Loc.F("已移除收藏：{0}", path));
    }

    // ── root 选项联动 ──

    void UpdateInstallRootEnabled()
    {
        _ckInstallRoot.Enabled = _rbPmInstall.Checked;
        if (!_ckInstallRoot.Enabled) _ckInstallRoot.Checked = false;
        _txtTmp.Enabled = _rbPmInstall.Checked;
    }

    // ── 选项记忆 ──

    void LoadOptions()
    {
        var s = logcat.Services.AppSettings.Default;
        _rbPmInstall.Checked = s.ApkInstallViaPm;
        _rbAdbInstall.Checked = !s.ApkInstallViaPm;
        var flags = s.ApkInstallFlags ?? "";
        _ckR.Checked = flags.Contains("-r", StringComparison.Ordinal);
        _ckD.Checked = flags.Contains("-d", StringComparison.Ordinal);
        _ckG.Checked = flags.Contains("-g", StringComparison.Ordinal);
        _ckT.Checked = flags.Contains("-t", StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(s.ApkTmpDir)) _txtTmp.Text = s.ApkTmpDir;
        _ckInstallRoot.Checked = s.ApkUseRoot;
    }

    void SaveOptions()
    {
        var s = logcat.Services.AppSettings.Default;
        s.ApkInstallViaPm = _rbPmInstall.Checked;
        s.ApkInstallFlags = InstallFlags();
        s.ApkTmpDir = _txtTmp.Text.Trim();
        s.ApkUseRoot = _ckInstallRoot.Checked;
        try { s.Save(); } catch { }
    }

    // ── 执行 ──

    string InstallFlags()
    {
        var f = new List<string>();
        if (_ckR.Checked) f.Add("-r");
        if (_ckD.Checked) f.Add("-d");
        if (_ckG.Checked) f.Add("-g");
        if (_ckT.Checked) f.Add("-t");
        return string.Join(" ", f);
    }

    async void DoInstall()
    {
        if (_busy) return;
        var apk = _cboApk.Text.Trim().Trim('"');
        if (apk.Length == 0) { Status(Loc.T("请选择或输入 APK 文件路径")); return; }
        if (!File.Exists(apk)) { Status(Loc.F("找不到文件：{0}", apk)); return; }

        var flags = InstallFlags();
        bool pm = _rbPmInstall.Checked;
        bool root = pm && _ckInstallRoot.Checked;

        if (!Begin()) return;
        try
        {
            if (!pm)
            {
                var head = flags.Length > 0 ? $"install {flags}" : "install";
                var args = AdbManager.AdbArgs($"{head} \"{apk}\"", _serial);
                AppendOutput($"> adb {args}");
                int code = await AdbManager.RunAdbAsync(args, _progress, _cts!.Token);
                AppendOutput(Loc.F("— 退出码 {0}", code));
                Status(code == 0 ? "adb install 完成，请检查输出中的 Success" : Loc.F("adb install 结束（退出码 {0}）", code));
            }
            else
            {
                var tmpDir = string.IsNullOrWhiteSpace(_txtTmp.Text) ? "/data/local/tmp" : _txtTmp.Text.Trim().TrimEnd('/');
                var remote = $"{tmpDir}/{Path.GetFileName(apk)}";
                AppendOutput(Loc.F("> 推送 {0} → {1}:{2}", apk, _serial, remote));
                await _manager.PushAsync(_serial, apk, remote);

                var cmd = $"pm install{(flags.Length > 0 ? " " + flags : "")} '{remote}'";
                AppendOutput($"> adb shell {(root ? "su -c " : "")}{cmd}");
                await _manager.ShellLinesAsync(_serial, Wrap(cmd, root), _progress, _cts!.Token);

                // 临时文件用尽力清理，失败不影响结果
                try { await _manager.ShellAsync(_serial, Wrap($"rm -f '{remote}'", root), 15); } catch { }
                Status(Loc.T("pm install 执行完毕，请检查输出中的 Success/Failure"));
            }
            _fav.AddApkPath(apk);
            _fav.Save();
            RefreshApkCombo();
        }
        catch (OperationCanceledException) { Status(Loc.T("已取消")); }
        catch (Exception ex) { AppendOutput("!! " + Friendly(ex)); Status(Loc.F("安装失败：{0}", Friendly(ex))); }
        finally { End(); }
    }

    /// <summary>按 root 选项决定是否用 su -c 包裹设备端命令。</summary>
    static string Wrap(string cmd, bool root) =>
        root ? $"su -c {AdbManager.ShellQuote(cmd)}" : cmd;

    bool Begin()
    {
        if (_busy) return false;
        _busy = true;
        _cts = new CancellationTokenSource();
        _btnInstall.Enabled = false;
        _btnStop.Enabled = true;
        Status(Loc.T("执行中…"));
        return true;
    }

    void End()
    {
        _busy = false;
        _btnStop.Enabled = false;
        _btnInstall.Enabled = true;
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
        Text = Loc.F("安装 APK — {0}", _serial);
        _lblDevice.Text = Loc.F("设备：{0}{1}", _serial, _isRoot ? "  [root]" : "");
    }
}
