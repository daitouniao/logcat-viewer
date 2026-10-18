using System;
using System.Drawing;
using System.Windows.Forms;
using logcat.Services;

namespace logcat.Forms;

/// <summary>
/// 文件代理设置对话框：模式三选一（默认停用）、管道名、启动命令（可执行文件 + 参数模板，各附「浏览…」）、
/// 启动等待超时、测试连接（复用打开文件的完整拉起链路，design D15）。
/// 保存写 settings.json 并立即生效——装载判定每次打开文件时现读，不需要重启（spec「保存后立即生效」）。
/// </summary>
public sealed class FileProxyDialog : Form, ILocalizedUi
{
    RadioButton _rbOff = null!, _rbOn = null!, _rbAuto = null!;
    TextBox _txtPipe = null!, _txtLaunchFile = null!, _txtLaunchArgs = null!;
    Button _btnBrowseFile = null!, _btnBrowseArgs = null!;
    NumericUpDown _numTimeout = null!;
    Button _btnTest = null!;
    Label _lblTestResult = null!, _lblPreview = null!;

    // 测试结果 Label 的「资源键 + 参数」：文案随语言切换重算（禁 static readonly，同 _statusKey 口径）
    string? _testKey;
    object?[]? _testArgs;

    public FileProxyDialog()
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = Loc.T("文件代理设置");
        Size = new Size(560, 428);
        MinimumSize = new Size(520, 408);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        BuildUi();
        LoadFromSettings();
    }

    void BuildUi()
    {
        // ── 模式（三选一，三个独立绑定——三元/循环绑定是 i18n 盲区）──
        var grpMode = Loc.Bind(new GroupBox { Location = new Point(10, 8), Size = new Size(524, 62) }, "模式");
        _rbOff = Loc.Bind(new RadioButton { Location = new Point(16, 26), AutoSize = true, Checked = true }, "停用");
        _rbOn = Loc.Bind(new RadioButton { Location = new Point(140, 26), AutoSize = true }, "启用");
        _rbAuto = Loc.Bind(new RadioButton { Location = new Point(264, 26), AutoSize = true }, "自动");
        grpMode.Controls.AddRange(new Control[] { _rbOff, _rbOn, _rbAuto });
        Controls.Add(grpMode);

        // ── 管道名 ──
        Controls.Add(Loc.Bind(new Label { Location = new Point(12, 80), AutoSize = true }, "管道名"));
        _txtPipe = new TextBox { Location = new Point(120, 76), Width = 300 };
        Controls.Add(_txtPipe);

        // ── 启动命令（可执行文件 + 其余参数两个独立字段，不做整行解析；各附「浏览…」杜绝手敲路径，design D16；
        //    --pipe 管道名由服务层自动追加，见 design D17）──
        var grpLaunch = Loc.Bind(new GroupBox { Location = new Point(10, 108), Size = new Size(524, 140) }, "启动命令");
        grpLaunch.Controls.Add(Loc.Bind(new Label { Location = new Point(10, 20), AutoSize = true }, "可执行文件（解释器或程序的完整路径）"));
        _txtLaunchFile = new TextBox { Location = new Point(10, 40), Width = 405 };
        grpLaunch.Controls.Add(_txtLaunchFile);
        _btnBrowseFile = Loc.Bind(new Button { Location = new Point(423, 38), Size = new Size(90, 25) }, "浏览…");
        _btnBrowseFile.Click += (_, _) => BrowseExecutable();
        grpLaunch.Controls.Add(_btnBrowseFile);
        grpLaunch.Controls.Add(Loc.Bind(new Label { Location = new Point(10, 70), AutoSize = true }, "参数（其余参数；--pipe 管道名由应用自动追加）"));
        _txtLaunchArgs = new TextBox { Location = new Point(10, 90), Width = 405 };
        grpLaunch.Controls.Add(_txtLaunchArgs);
        _btnBrowseArgs = Loc.Bind(new Button { Location = new Point(423, 88), Size = new Size(90, 25) }, "浏览…");
        _btnBrowseArgs.Click += (_, _) => BrowseScript();
        grpLaunch.Controls.Add(_btnBrowseArgs);
        _lblPreview = new Label
        {
            Location = new Point(10, 118),
            Size = new Size(504, 18),
            AutoSize = false,
            AutoEllipsis = true,
            ForeColor = Color.DimGray,
        };
        grpLaunch.Controls.Add(_lblPreview);
        // 命令行预览实时刷新（design D17）；三个字段任一变化都重算
        _txtPipe.TextChanged += (_, _) => UpdatePreview();
        _txtLaunchFile.TextChanged += (_, _) => UpdatePreview();
        _txtLaunchArgs.TextChanged += (_, _) => UpdatePreview();
        Controls.Add(grpLaunch);

        // ── 启动等待超时 ──
        Controls.Add(Loc.Bind(new Label { Location = new Point(12, 260), AutoSize = true }, "启动等待超时（毫秒）"));
        _numTimeout = new NumericUpDown
        {
            Location = new Point(220, 256),
            Width = 100,
            Minimum = 200,
            Maximum = 60000,
            Increment = 500,
        };
        Controls.Add(_numTimeout);

        // ── 测试连接（复用打开文件的完整拉起链路，design D15；不开任何文件）──
        _btnTest = Loc.Bind(new Button { Location = new Point(10, 290), Width = 110, Height = 26 }, "测试连接");
        _btnTest.Click += async (_, _) => await TestConnectAsync();
        Controls.Add(_btnTest);
        _lblTestResult = new Label
        {
            Location = new Point(130, 292),
            Size = new Size(400, 40),
            AutoSize = false,
            ForeColor = Color.DimGray,
        };
        Controls.Add(_lblTestResult);

        // ── 保存 / 取消 ──
        // 不预设 DialogResult：保存要先过校验，非法时保持对话框打开（spec「非法管道名拒绝保存」）
        var btnSave = Loc.SizedButton(Loc.T("保存"), "保存", zhMinWidth: 80);
        var btnCancel = Loc.SizedButton(Loc.T("取消"), "取消", zhMinWidth: 80);
        btnSave.Click += (_, _) => { if (TrySave()) DialogResult = DialogResult.OK; };
        btnCancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        Loc.LayoutButtonPair(this, btnSave, btnCancel, right: 522, y: 358);
        AcceptButton = btnSave;
        CancelButton = btnCancel;
    }

    // ── 设置读写 ──

    void LoadFromSettings()
    {
        var s = AppSettings.Default;
        switch (FileProxyModes.Parse(s.FileProxyMode))
        {
            case FileProxyMode.On: _rbOn.Checked = true; break;
            case FileProxyMode.Auto: _rbAuto.Checked = true; break;
            default: _rbOff.Checked = true; break;
        }
        _txtPipe.Text = s.FileProxyPipeName;
        _txtLaunchFile.Text = s.FileProxyLaunchFile;
        // 旧配置的参数模板可能带尾部 --pipe {pipe}：载入时剥掉展示（design D17，服务层会自动追加）
        _txtLaunchArgs.Text = FileProxyService.NormalizeLegacyArgs(s.FileProxyLaunchArgs);
        _numTimeout.Value = Math.Clamp(s.FileProxyLaunchTimeoutMs, (int)_numTimeout.Minimum, (int)_numTimeout.Maximum);
    }

    FileProxyMode SelectedMode => _rbOn.Checked ? FileProxyMode.On : _rbAuto.Checked ? FileProxyMode.Auto : FileProxyMode.Off;

    /// <summary>
    /// 校验管道名：判据唯一来源在 <see cref="FileProxyService.ValidatePipeName"/>（保存与启动命令构造共用）。
    /// 此处只包一层取上限实参，不再自持第二份判据。
    /// </summary>
    static string? ValidatePipeName(string? name) => FileProxyService.ValidatePipeName(name);

    /// <summary>把资源键格式化成用户可见消息（带 {0} 的键补管道名上限实参）。</summary>
    static string PipeErrText(string key) =>
        key.Contains("{0}", StringComparison.Ordinal)
            ? Loc.F(key, FileProxyService.PipeNameMaxLen) : Loc.T(key);

    /// <summary>保存到 settings.json。管道名非法时拒绝保存（返回 false，调用方保持对话框打开）。</summary>
    public bool TrySave()
    {
        string pipe = _txtPipe.Text.Trim();
        var err = ValidatePipeName(pipe);
        if (err != null)
        {
            MessageBox.Show(this, PipeErrText(err), Loc.T("错误"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        var s = AppSettings.Default;
        s.FileProxyMode = FileProxyModes.ToRaw(SelectedMode);
        s.FileProxyPipeName = pipe;
        s.FileProxyLaunchFile = _txtLaunchFile.Text.Trim();
        s.FileProxyLaunchArgs = _txtLaunchArgs.Text.Trim();
        s.FileProxyLaunchTimeoutMs = (int)_numTimeout.Value;
        s.Save();   // 保存即生效：装载判定现读 AppSettings.Default，不需要重启
        return true;
    }

    // ── 测试连接 ──

    async System.Threading.Tasks.Task TestConnectAsync()
    {
        string pipe = _txtPipe.Text.Trim();
        var err = ValidatePipeName(pipe);
        if (err != null)
        {
            // 存键不存成品串：切语言时按新语言重算（ PipeErrText 只用于弹窗式消息）
            SetTestResult(err, err.Contains("{0}", StringComparison.Ordinal)
                ? new object?[] { FileProxyService.PipeNameMaxLen } : null);
            return;
        }

        _btnTest.Enabled = false;
        try
        {
            // 复用打开文件的完整拉起链路（design D15）：以对话框当前字段值现构 settings，
            // 测的就是所配即所得，不需要先保存；拉起进程驻留复用，应用退出统一回收
            var settings = new Services.FileProxySettings(pipe,
                _txtLaunchFile.Text.Trim(), _txtLaunchArgs.Text.Trim(), (int)_numTimeout.Value);
            using var client = await Services.FileProxyService.EnsureProxyAsync(settings).ConfigureAwait(true);
            SetTestResult("测试连接成功：{0}（协议版本 {1}）",
                new object?[] { client.ProxyName ?? "", Services.FileProxyClient.ProtoVersion });
        }
        catch (Exception ex)
        {
            SetTestResult("测试连接失败：{0}", new object?[] { ex.Message });
        }
        finally { _btnTest.Enabled = true; }
    }

    // ── 浏览：文件选择填路径，杜绝手敲整行命令与全角引号（design D16）──

    // 命令行预览实时刷新（design D17）：展示最终完整启动命令，透明代替魔法，可直接复制手动运行
    void UpdatePreview()
    {
        _lblPreview.Text = Loc.F("命令行预览：{0}",
            FileProxyService.BuildCommandLine(_txtLaunchFile.Text.Trim(), _txtLaunchArgs.Text.Trim(), _txtPipe.Text.Trim()));
    }

    void BrowseExecutable()
    {
        using var dlg = new OpenFileDialog
        {
            Title = Loc.T("选择可执行文件"),
            Filter = Loc.T("程序 (*.exe)|*.exe|所有文件 (*.*)|*.*"),
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _txtLaunchFile.Text = dlg.FileName;
    }

    void BrowseScript()
    {
        using var dlg = new OpenFileDialog
        {
            Title = Loc.T("选择脚本文件"),
            Filter = Loc.T("脚本 (*.js;*.py)|*.js;*.py|所有文件 (*.*)|*.*"),
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        // 含空格才加引号，与 FileProxyService.QuoteIfNeeded 同口径；引号一律半角
        string path = dlg.FileName.Contains(' ') ? "\"" + dlg.FileName + "\"" : dlg.FileName;
        // 智能替换而非光标插入：参数开头若已是路径 token（带引号，或含 \ / 的首段）则整体替换，
        // 避免重复选择脚本时路径互相拼接；开头不是路径（如只有 --pipe {pipe}）才从头插入
        string cur = _txtLaunchArgs.Text;
        int selStart = 0, selLen = 0;
        if (cur.Length > 0 && cur[0] == '"')
        {
            int close = cur.IndexOf('"', 1);
            if (close > 0) selLen = close + 1;
        }
        else
        {
            int sp = cur.IndexOf(' ');
            if (sp < 0) sp = cur.Length;
            if (sp > 0 && cur.IndexOfAny(new[] { '\\', '/' }, 0, sp) >= 0) selLen = sp;
        }
        _txtLaunchArgs.Select(selStart, selLen);
        _txtLaunchArgs.SelectedText = path;
    }

    void SetTestResult(string keyOrText, object?[]? args)
    {
        _testKey = keyOrText;
        _testArgs = args;
        _lblTestResult.Text = args == null ? Loc.T(keyOrText) : Loc.F(keyOrText, args);
    }

    /// <summary>语言切换：测试结果 Label 是动态文案，按当前语言重算（禁 static readonly）。</summary>
    void ILocalizedUi.OnLanguageChanged()
    {
        if (_testKey != null) SetTestResult(_testKey, _testArgs);
    }
}
