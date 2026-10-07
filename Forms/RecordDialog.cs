using logcat.Services;

namespace logcat.Forms;

/// <summary>
/// 单条记录的完整内容弹窗（保留原始换行），便于查看异常堆栈。
/// </summary>
public class RecordDialog : Form
{
    readonly RichTextBox _text;
    readonly CheckBox _ckLiteral;
    readonly string _originalText;

    public RecordDialog(string text, string title)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = title;
        Size = new Size(1000, 620);
        StartPosition = FormStartPosition.CenterParent;
        _originalText = text;

        _text = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            WordWrap = false,
            Font = new Font("Consolas", 10),
            Text = text
        };

        // 绑资源键：切语言时重设文案（而不是停留在构造时的 Loc.T 结果）
        _ckLiteral = Loc.Bind(new CheckBox
        {
            Dock = DockStyle.Bottom,
            Height = 30
        }, @"把字面的 \n 也拆成真实换行");
        _ckLiteral.CheckedChanged += (_, _) => Refresh();

        var btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            FlowDirection = FlowDirection.RightToLeft
        };

        var btnClose = Loc.Bind(new Button { DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(80, 25) }, "关闭");
        var btnCopy = Loc.Bind(new Button { AutoSize = true, MinimumSize = new Size(80, 25) }, "复制");
        btnCopy.Click += (_, _) => logcat.Services.ClipboardHelper.SetText(_text.Text);

        btnPanel.Controls.Add(btnClose);
        btnPanel.Controls.Add(btnCopy);

        AcceptButton = btnClose;
        Controls.Add(_text);
        Controls.Add(_ckLiteral);
        Controls.Add(btnPanel);

        void Refresh()
        {
            _text.Text = _ckLiteral.Checked
                ? _originalText.Replace("\\n", "\n")
                : _originalText;
        }
    }
}
