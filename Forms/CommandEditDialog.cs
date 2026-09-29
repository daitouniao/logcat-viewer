using logcat.Models;
using logcat.Services;

namespace logcat.Forms;

/// <summary>
/// 新建 / 编辑收藏命令：分类、命令正文、备注、执行通道、是否 root。
/// </summary>
public class CommandEditDialog : Form
{
    readonly ComboBox _cboCat;
    readonly TextBox _txtCmd;
    readonly TextBox _txtRemark;
    readonly ComboBox _cboKind;
    readonly CheckBox _ckRoot;

    public string Category => _cboCat.Text.Trim();
    public string Command => _txtCmd.Text.Trim();
    public string Remark => _txtRemark.Text.Trim();
    public bool Root => _ckRoot.Checked;
    public CommandKind Kind => CommandKinds.Of(_cboKind.SelectedItem as string);

    public CommandEditDialog(CommandEntry? entry)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = entry == null ? "新建收藏命令" : "编辑收藏命令";
        Size = new Size(620, 300);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        var store = CommandStore.Default;

        var lblCat = new Label { Text = "分类：", Location = new Point(12, 16), Size = new Size(60, 18) };
        _cboCat = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDown,
            Location = new Point(74, 12),
            Width = 240,
        };
        foreach (var c in store.Categories) _cboCat.Items.Add(c);

        var btnNewCat = new Button { Text = "新建分类…", Location = new Point(322, 11), Size = new Size(90, 24) };
        btnNewCat.Click += (_, _) =>
        {
            var name = SimpleInputBox.Show(this, "新建分类", "分类名称：", "");
            if (string.IsNullOrWhiteSpace(name)) return;
            if (!store.HasCategory(name))
            {
                _cboCat.Items.Add(name.Trim());
                _cboCat.SelectedIndex = _cboCat.Items.Count - 1;
            }
            else
            {
                _cboCat.Text = name.Trim();
            }
        };

        var lblKind = new Label { Text = "通道：", Location = new Point(424, 16), Size = new Size(48, 18) };
        _cboKind = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(474, 12),
            Width = 108,
        };
        _cboKind.Items.AddRange(new object[] { CommandKinds.ShellText, CommandKinds.AdbText });
        _cboKind.SelectedIndex = 0;
        _cboKind.SelectedIndexChanged += (_, _) => UpdateRootEnabled();

        _ckRoot = new CheckBox { Text = "以 root 执行（su -c 包裹）", Location = new Point(74, 44), AutoSize = true };

        var lblCmd = new Label { Text = "命令：", Location = new Point(12, 78), Size = new Size(60, 18) };
        _txtCmd = new TextBox
        {
            Location = new Point(74, 74),
            Width = 508,
            PlaceholderText = "如 dumpsys meminfo {pkg}",
        };

        var lblRemark = new Label { Text = "备注：", Location = new Point(12, 110), Size = new Size(60, 18) };
        _txtRemark = new TextBox { Location = new Point(74, 106), Width = 508 };

        var hint = new Label
        {
            Text = "命令里的 {名称} 是参数占位符，执行前会提示填值并记住上次输入，" +
                   "如 {pkg}、{path}。本机 adb 通道只写 adb 后面的部分（install -r xx.apk）。",
            Location = new Point(74, 138),
            Size = new Size(508, 40),
            ForeColor = Color.DimGray,
        };

        var btnOk = new Button { Text = "确定", Location = new Point(426, 200), Size = new Size(75, 24) };
        var btnCancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(507, 200), Size = new Size(75, 24) };
        btnOk.Click += (_, _) =>
        {
            if (Command.Length == 0)
            {
                MessageBox.Show(this, "请输入命令。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (Category.Length == 0)
            {
                MessageBox.Show(this, "请选择或输入分类。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DialogResult = DialogResult.OK;
        };

        if (entry != null)
        {
            _cboCat.Text = entry.Category;
            _txtCmd.Text = entry.Command;
            _txtRemark.Text = entry.Remark ?? "";
            _ckRoot.Checked = entry.Root;
            _cboKind.SelectedItem = entry.Kind.Text();
        }
        else
        {
            _cboCat.Text = store.Categories.FirstOrDefault() ?? CommandStore.FallbackCategory;
        }
        UpdateRootEnabled();

        Controls.AddRange(new Control[]
        {
            lblCat, _cboCat, btnNewCat, lblKind, _cboKind, _ckRoot,
            lblCmd, _txtCmd, lblRemark, _txtRemark, hint, btnOk, btnCancel,
        });
        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }

    /// <summary>root 只对设备 shell 通道有意义。</summary>
    void UpdateRootEnabled()
    {
        _ckRoot.Enabled = Kind == CommandKind.Shell;
        if (!_ckRoot.Enabled) _ckRoot.Checked = false;
    }
}
