using logcat.Services;

namespace logcat.Forms;

/// <summary>
/// run-as 模式设置：选择应用包名（含访问过的收藏）与中转目录。
/// </summary>
public class RunAsDialog : Form, ILocalizedUi
{
    /// <summary>中转目录机制说明文案（两段）。</summary>
    const string HINT =
        "受限目录（/data/data 等）的文件先复制到中转目录，再经 sync 通道收发；" +
        "中转目录需为 shell 可读写的路径（sdcard 下均可）。";

    Label _hint = null!;

    readonly ComboBox _cboPkg;
    readonly TextBox _txtRelay;

    /// <summary>选定的应用包名。</summary>
    public string Package => _cboPkg.Text.Trim();

    /// <summary>选定的设备端中转目录。</summary>
    public string RelayDir => _txtRelay.Text.Trim();

    public RunAsDialog(IEnumerable<string> favorites, string defaultRelay)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = Loc.T("run-as 模式");
        Loc.Bind(this, "run-as 模式");   // 登记资源键，切语言时 ApplyTo 才能重设标题
        Size = new Size(480, 240);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        var lblPkg = Loc.Bind(new Label
        {
            Location = new Point(12, 14),
            Size = new Size(440, 18),
        }, "应用包名（可输入，或从访问过的包名中选择）：");
        _cboPkg = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDown,
            Location = new Point(12, 36),
            Width = 340,
        };
        foreach (var p in favorites) _cboPkg.Items.Add(p);
        // 预填最近用过的包名（收藏按最近在前排序），通常直接回车即可
        if (_cboPkg.Items.Count > 0 && _cboPkg.Items[0] is string recent)
            _cboPkg.Text = recent;

        // 绑资源键 + 按语言给宽度：英文 "Remove Favorite" 需 119px，
        // 固定 92px 会裁字（实测差 27px）
        var btnRemove = Loc.Bind(new Button
        {
            Location = new Point(360, 34),
            Size = new Size(Loc.W(92, 124), 24) }, "移除收藏");
        btnRemove.Click += (_, _) =>
        {
            var pkg = Package;
            if (pkg.Length == 0) return;
            if (FavoritesStore.Default.RemoveRunAsPackage(pkg))
                _cboPkg.Items.Remove(pkg);
        };

        var lblRelay = Loc.Bind(new Label
        {
            Location = new Point(12, 70),
            Size = new Size(440, 18),
        }, "中转目录（设备端路径，默认放 Download）：");
        _txtRelay = new TextBox
        {
            Text = defaultRelay,
            Location = new Point(12, 92),
            Width = 340,
        };

        // 同CommandEditDialog：说明 Label 高度必须随语言重算，
        // 英文这段比中文长一倍以上（实测 1269px vs 715px），固定 34px 必然截断。
        _hint = Loc.Bind(new Label
        {
            Location = new Point(12, 124),
            Size = new Size(440, 34),
            ForeColor = Color.DimGray,
        }, HINT);
        Loc.FitHeight(_hint, 440);

        // 绝对定位布局：按钮宽度按实测文本给（英文比中文宽），整对右边缘对齐
        var btnOk = Loc.SizedButton("确定");
        var btnCancel = Loc.SizedButton("取消");
        btnCancel.DialogResult = DialogResult.Cancel;
        Loc.LayoutButtonPair(this, btnOk, btnCancel, 428, 166);

        btnOk.Click += (_, _) =>
        {
            if (Package.Length == 0)
            {
                MessageBox.Show(this, Loc.T("请输入应用包名。"), "run-as", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!RelayDir.StartsWith('/') || RelayDir.Length < 2)
            {
                MessageBox.Show(this, Loc.T("中转目录必须是设备端绝对路径（以 / 开头）。"),
                    "run-as", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DialogResult = DialogResult.OK;
        };

        // btnOk / btnCancel 已由 Loc.LayoutButtonPair 加入（它按语言算右对齐位置）
        Controls.AddRange(new Control[] { lblPkg, _cboPkg, btnRemove, lblRelay, _txtRelay, _hint });
        AcceptButton = btnOk;
        CancelButton = btnCancel;
        ActiveControl = _cboPkg;
        _cboPkg.SelectAll();
    }
    /// <summary>
    /// 切语言后重算说明 Label 的高度，并把下方的按钮对整体下移，
    /// 否则英文文案变长后说明会压住确定/取消。
    /// </summary>
    void ILocalizedUi.OnLanguageChanged()
    {
        Loc.FitHeight(_hint, _hint.Width);
        int btnTop = 138 + _hint.Height + 12;
        foreach (Control c in Controls)
            if (c is Button { Text: var t } && (t == Loc.T("确定") || t == Loc.T("取消")))
                c.Top = btnTop;
        PerformLayout();
    }
}
