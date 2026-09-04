using logcat.Services;

namespace logcat.Forms;

/// <summary>
/// run-as 模式设置：选择应用包名（含访问过的收藏）与中转目录。
/// </summary>
public class RunAsDialog : Form
{
    readonly ComboBox _cboPkg;
    readonly TextBox _txtRelay;

    /// <summary>选定的应用包名。</summary>
    public string Package => _cboPkg.Text.Trim();

    /// <summary>选定的设备端中转目录。</summary>
    public string RelayDir => _txtRelay.Text.Trim();

    public RunAsDialog(IEnumerable<string> favorites, string defaultRelay)
    {
        Text = "run-as 模式";
        Size = new Size(480, 240);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        var lblPkg = new Label
        {
            Text = "应用包名（可输入，或从访问过的包名中选择）：",
            Location = new Point(12, 14),
            Size = new Size(440, 18),
        };
        _cboPkg = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDown,
            Location = new Point(12, 36),
            Width = 340,
        };
        foreach (var p in favorites) _cboPkg.Items.Add(p);

        var btnRemove = new Button
        {
            Text = "移除收藏",
            Location = new Point(360, 34),
            Size = new Size(92, 24),
        };
        btnRemove.Click += (_, _) =>
        {
            var pkg = Package;
            if (pkg.Length == 0) return;
            if (FavoritesStore.Default.RemoveRunAsPackage(pkg))
                _cboPkg.Items.Remove(pkg);
        };

        var lblRelay = new Label
        {
            Text = "中转目录（设备端路径，默认放 Download）：",
            Location = new Point(12, 70),
            Size = new Size(440, 18),
        };
        _txtRelay = new TextBox
        {
            Text = defaultRelay,
            Location = new Point(12, 92),
            Width = 340,
        };

        var hint = new Label
        {
            Text = "受限目录（/data/data 等）的文件先复制到中转目录，再经 sync 通道收发；" +
                   "中转目录需为 shell 可读写的路径（sdcard 下均可）。",
            Location = new Point(12, 124),
            Size = new Size(440, 34),
            ForeColor = Color.DimGray,
        };

        var btnOk = new Button { Text = "确定", Location = new Point(276, 166), Size = new Size(75, 24) };
        var btnCancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(357, 166), Size = new Size(75, 24) };

        btnOk.Click += (_, _) =>
        {
            if (Package.Length == 0)
            {
                MessageBox.Show(this, "请输入应用包名。", "run-as", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!RelayDir.StartsWith('/') || RelayDir.Length < 2)
            {
                MessageBox.Show(this, "中转目录必须是设备端绝对路径（以 / 开头）。",
                    "run-as", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DialogResult = DialogResult.OK;
        };

        Controls.AddRange(new Control[] { lblPkg, _cboPkg, btnRemove, lblRelay, _txtRelay, hint, btnOk, btnCancel });
        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }
}
