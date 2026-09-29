namespace logcat.Forms;

/// <summary>单行文本输入框。</summary>
public static class SimpleInputBox
{
    /// <summary>显示输入框，返回用户输入的文本；取消时返回 null。</summary>
    public static string? Show(IWin32Window? owner, string title, string prompt, string defaultValue = "")
    {
        var form = new Form
        {
            AutoScaleDimensions = new SizeF(96F, 96F),
            AutoScaleMode = AutoScaleMode.Dpi,
            Text = title,
            Size = new Size(460, 175),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
        };

        var lbl = new Label
        {
            Text = prompt,
            Location = new Point(12, 12),
            Size = new Size(420, 45),
        };
        var txt = new TextBox
        {
            Text = defaultValue,
            Location = new Point(12, 62),
            Width = 420,
        };
        var btnOk = new Button
        {
            Text = "确定",
            DialogResult = DialogResult.OK,
            Location = new Point(276, 98),
            Width = 75,
        };
        var btnCancel = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Location = new Point(357, 98),
            Width = 75,
        };

        form.Controls.AddRange(new Control[] { lbl, txt, btnOk, btnCancel });
        form.AcceptButton = btnOk;
        form.CancelButton = btnCancel;

        return form.ShowDialog(owner) == DialogResult.OK ? txt.Text : null;
    }
}
