using logcat.Models;
using logcat.Services;

namespace logcat.Forms;

/// <summary>
/// 新建 / 编辑收藏命令：分类、命令正文、备注、执行通道、是否 root。
/// </summary>
public class CommandEditDialog : Form, ILocalizedUi
{
    /// <summary>参数占位符说明文案（两段）。</summary>
    const string HINT =
        "命令里的 {名称} 是参数占位符，执行前会提示填值并记住上次输入，" +
        "如 {pkg}、{path}。本机 adb 通道只写 adb 后面的部分（install -r xx.apk）。";

    Label _hint = null!;

    /// <summary>确定 / 取消按钮。存成字段是为了让 <see cref="ILocalizedUi.OnLanguageChanged"/>
    /// 能按引用定位——早先靠 <c>c.Text == Loc.T("确定")</c> 反查控件，
    /// 一旦译文与中文原文同形（或日后换文案）就静默失效，按钮不再随说明 Label 下移。</summary>
    Button _btnOk = null!;
    Button _btnCancel = null!;

    readonly CommandStore _store = CommandStore.Default;
    readonly ComboBox _cboCat;
    readonly TextBox _txtCmd;
    readonly TextBox _txtRemark;
    readonly ComboBox _cboKind;
    readonly CheckBox _ckRoot;

    public string Category => CommandStore.CategoryFromDisplay(_cboCat.Text.Trim());
    public string Command => _txtCmd.Text.Trim();
    public string Remark => _txtRemark.Text.Trim();
    public bool Root => _ckRoot.Checked;
    public CommandKind Kind => CommandKinds.Of(_cboKind.SelectedItem as string);

    public CommandEditDialog(CommandEntry? entry)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = entry == null ? Loc.T("新建收藏命令") : Loc.T("编辑收藏命令");
        // 登记资源键：三元表达式只做一次性赋值，不绑定的话 ApplyTo 遍历时查不到，
        // 切语言后标题会停留在打开时的语言（另两个对话框都绑了，别漏）。
        Loc.Bind(this, entry == null ? "新建收藏命令" : "编辑收藏命令");
        Size = new Size(620, 300);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        var store = _store;

        // 绝对定位的 Label：绑资源键（切语言时文本能刷新）+ 按语言给宽度
        // （英文 "Category:" 66px 比中文 "分类：" 60px 宽，固定 60 会裁字）
        var lblCat = Loc.Bind(new Label { Location = new Point(12, 16), Size = new Size(Loc.W(60, 68), 18) }, "分类：");
        _cboCat = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDown,
            Location = new Point(74, 12),
            Width = 240,
        };
        // 分类下拉用显示文本（内置的「应用与包」「日志与异常」按语言翻译），
        // 取值走 CategoryFromDisplay 还原成存储用的中文原文（见 Category 属性）
        foreach (var c in store.Categories) _cboCat.Items.Add(CommandStore.CategoryDisplay(c));

        var btnNewCat = Loc.SizedButton("新建分类…", zhMinWidth: 90);
        btnNewCat.Click += (_, _) =>
        {
            var name = SimpleInputBox.Show(this, Loc.T("新建分类"), Loc.T("分类名称："), "");
            if (string.IsNullOrWhiteSpace(name)) return;
            if (!store.HasCategory(name))
            {
                _cboCat.Items.Add(name.Trim());
                _cboCat.SelectedIndex = _cboCat.Items.Count - 1;
            }
            else
            {
                _cboCat.Text = CommandStore.CategoryDisplay(name.Trim());
            }
        };

        var lblKind = Loc.Bind(new Label { Location = new Point(424, 16), Size = new Size(Loc.W(48, 62), 18) }, "通道：");
        _cboKind = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(474, 12),
            Width = 108,
        };
        _cboKind.Items.AddRange(new object[] { CommandKinds.ShellText, CommandKinds.AdbText });
        _cboKind.SelectedIndex = 0;
        _cboKind.SelectedIndexChanged += (_, _) => UpdateRootEnabled();

        _ckRoot = Loc.Bind(new CheckBox { Location = new Point(74, 44), AutoSize = true }, "以 root 执行（su -c 包裹）");

        var lblCmd = Loc.Bind(new Label { Location = new Point(12, 78), Size = new Size(Loc.W(60, 76), 18) }, "命令：");
        _txtCmd = Loc.BindHint(new TextBox
        {
            Location = new Point(74, 74),
            Width = 508,
        }, "如 dumpsys meminfo {pkg}");

        var lblRemark = Loc.Bind(new Label { Location = new Point(12, 110), Size = new Size(Loc.W(60, 72), 18) }, "备注：");
        _txtRemark = new TextBox { Location = new Point(74, 106), Width = 508 };

        // 说明 Label：高度必须随语言重算。中文 2 行放得下的 508px宽，
        // 英文要 4 行（实测需1111px 文本宽→3 行），固定Height=40 会把说明截掉半截。
        // 宽度固定、高度按内容算，所以不能用 AutoSize（那会把宽度也一起撑坏布局）。
        _hint = Loc.Bind(new Label
        {
            Location = new Point(74, 138),
            Size = new Size(508, 40),
            ForeColor = Color.DimGray,
        }, HINT);
        Loc.FitHeight(_hint, 508);

        // 绝对定位布局：宽度按实测文本给（英文比中文宽），整对按钮右边缘对齐
        _btnOk = Loc.SizedButton("确定");
        _btnCancel = Loc.SizedButton("取消");
        _btnCancel.DialogResult = DialogResult.Cancel;
        Loc.LayoutButtonPair(this, _btnOk, _btnCancel, 582, 200);
        _btnOk.Click += (_, _) =>
        {
            if (Command.Length == 0)
            {
                MessageBox.Show(this, Loc.T("请输入命令。"), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (Category.Length == 0)
            {
                MessageBox.Show(this, Loc.T("请选择或输入分类。"), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DialogResult = DialogResult.OK;
        };

        if (entry != null)
        {
            _cboCat.Text = CommandStore.CategoryDisplay(entry.Category);
            _txtCmd.Text = entry.Command;
            _txtRemark.Text = entry.Remark ?? "";
            _ckRoot.Checked = entry.Root;
            _cboKind.SelectedItem = entry.Kind.Text();
        }
        else
        {
            var first = store.Categories.FirstOrDefault() ?? CommandStore.FallbackCategory;
            _cboCat.Text = CommandStore.CategoryDisplay(first);
        }
        UpdateRootEnabled();

        Controls.AddRange(new Control[]
        {
            // btnOk / btnCancel 已由 Loc.LayoutButtonPair 加入（它还要按语言算右对齐位置）
            lblCat, _cboCat, btnNewCat, lblKind, _cboKind, _ckRoot,
            lblCmd, _txtCmd, lblRemark, _txtRemark, _hint,
        });
        AcceptButton = _btnOk;
        CancelButton = _btnCancel;
    }

    /// <summary>root 只对设备 shell 通道有意义。</summary>
    void UpdateRootEnabled()
    {
        _ckRoot.Enabled = Kind == CommandKind.Shell;
        if (!_ckRoot.Enabled) _ckRoot.Checked = false;
    }

    /// <summary>
    /// 切语言后重算说明 Label 的高度，并把下方的按钮对整体下移，
    /// 否则英文文案变长后说明会压住确定/取消。
    /// </summary>
    void ILocalizedUi.OnLanguageChanged()
    {
        Loc.FitHeight(_hint, _hint.Width);
        int btnTop = 138 + _hint.Height + 12;
        _btnOk.Top = _btnCancel.Top = btnTop;
        // 英文态按钮更宽，右边缘对齐的位置要跟着重排，否则会越过父容器右界
        Loc.LayoutButtonPair(this, _btnOk, _btnCancel, 582, btnTop);
        // 下拉框项是构造那一刻的译文成品串（ComboBox.Items 不在控件树绑定范围内），
        // 不重填就会停在打开时的语言
        RefreshKindCombo();
        RefreshCategoryCombo();
        PerformLayout();
    }

    /// <summary>
    /// 重填通道下拉框并保住当前通道。
    /// <c>Items</c> 里存的是 <c>Loc.T</c> 求值后的成品串，<c>Loc.ApplyTo</c> 扫不到
    /// （它只认 <c>Loc.Bind</c> 登记过的控件文本），必须自己重填。
    /// </summary>
    void RefreshKindCombo()
    {
        var keep = Kind;
        _cboKind.Items.Clear();
        _cboKind.Items.AddRange(new object[] { CommandKinds.ShellText, CommandKinds.AdbText });
        _cboKind.SelectedIndex = keep == CommandKind.Adb ? 1 : 0;
    }

    /// <summary>重填分类下拉框（显示文本随语言变），按还原后的分类名保住选中项。</summary>
    void RefreshCategoryCombo()
    {
        var keep = CommandStore.CategoryFromDisplay(_cboCat.Text.Trim());
        _cboCat.Items.Clear();
        foreach (var c in _store.Categories) _cboCat.Items.Add(CommandStore.CategoryDisplay(c));
        _cboCat.Text = CommandStore.CategoryDisplay(keep);
    }
}
