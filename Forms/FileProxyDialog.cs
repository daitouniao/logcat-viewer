using System;
using System.Drawing;
using System.Windows.Forms;
using logcat.Services;

namespace logcat.Forms;

/// <summary>
/// 文件代理设置对话框：模式三选一（默认停用）、管道名、启动命令（可执行文件 + 参数模板）、
/// 启动等待超时、测试连接（只发 hello，不开文件）。
/// 保存写 settings.json 并立即生效——装载判定每次打开文件时现读，不需要重启（spec「保存后立即生效」）。
/// </summary>
public sealed class FileProxyDialog : Form, ILocalizedUi
{
    RadioButton _rbOff = null!, _rbOn = null!, _rbAuto = null!;
    TextBox _txtPipe = null!, _txtLaunchFile = null!, _txtLaunchArgs = null!;
    NumericUpDown _numTimeout = null!;
    Button _btnTest = null!;
    Label _lblTestResult = null!;

    // 测试结果 Label 的「资源键 + 参数」：文案随语言切换重算（禁 static readonly，同 _statusKey 口径）
    string? _testKey;
    object?[]? _testArgs;

    public FileProxyDialog()
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = Loc.T("文件代理设置");
        Size = new Size(560, 400);
        MinimumSize = new Size(520, 380);
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

        // ── 启动命令（可执行文件 + 参数模板两个独立字段，不做整行解析）──
        var grpLaunch = Loc.Bind(new GroupBox { Location = new Point(10, 108), Size = new Size(524, 112) }, "启动命令");
        grpLaunch.Controls.Add(Loc.Bind(new Label { Location = new Point(10, 20), AutoSize = true }, "可执行文件（解释器或程序的完整路径）"));
        _txtLaunchFile = new TextBox { Location = new Point(10, 40), Width = 500 };
        grpLaunch.Controls.Add(_txtLaunchFile);
        grpLaunch.Controls.Add(Loc.Bind(new Label { Location = new Point(10, 70), AutoSize = true }, "参数（{pipe} 为管道名占位符）"));
        _txtLaunchArgs = new TextBox { Location = new Point(10, 90), Width = 500 };
        grpLaunch.Controls.Add(_txtLaunchArgs);
        Controls.Add(grpLaunch);

        // ── 启动等待超时 ──
        Controls.Add(Loc.Bind(new Label { Location = new Point(12, 232), AutoSize = true }, "启动等待超时（毫秒）"));
        _numTimeout = new NumericUpDown
        {
            Location = new Point(220, 228),
            Width = 100,
            Minimum = 200,
            Maximum = 60000,
            Increment = 500,
        };
        Controls.Add(_numTimeout);

        // ── 测试连接（只发 hello，不开任何文件）──
        _btnTest = Loc.Bind(new Button { Location = new Point(10, 262), Width = 110, Height = 26 }, "测试连接");
        _btnTest.Click += async (_, _) => await TestConnectAsync();
        Controls.Add(_btnTest);
        _lblTestResult = new Label
        {
            Location = new Point(130, 264),
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
        Loc.LayoutButtonPair(this, btnSave, btnCancel, right: 522, y: 330);
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
        _txtLaunchArgs.Text = s.FileProxyLaunchArgs;
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
            using var client = await Services.FileProxyClient.ConnectAsync(pipe, 2000).ConfigureAwait(true);
            SetTestResult("测试连接成功：{0}（协议版本 {1}）",
                new object?[] { client.ProxyName ?? "", Services.FileProxyClient.ProtoVersion });
        }
        catch (Exception ex)
        {
            SetTestResult("测试连接失败：{0}", new object?[] { ex.Message });
        }
        finally { _btnTest.Enabled = true; }
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
