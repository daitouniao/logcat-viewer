using logcat.Services;

namespace logcat.Forms;

/// <summary>
/// 截图对话框：通过 GetFrameBuffer 获取设备屏幕快照，预览并保存。
/// </summary>
public class ScreenShotDialog : Form, ILocalizedUi
{
    readonly AdbManager _manager;
    readonly string _serial;
    Image? _screenshotImage;

    readonly PictureBox _picShot;
    readonly Button _btnShot, _btnSaveShot;

    public ScreenShotDialog(AdbManager manager, string serial)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        _manager = manager;
        _serial = serial;
        Text = Loc.F("截图 — {0}", serial);
        Size = new Size(720, 580);
        StartPosition = FormStartPosition.CenterParent;

        _picShot = new PictureBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(240, 240, 240),
            SizeMode = PictureBoxSizeMode.Zoom,
            BorderStyle = BorderStyle.FixedSingle
        };
        var shotBtnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            FlowDirection = FlowDirection.LeftToRight
        };
        _btnShot = Loc.Bind(new Button { AutoSize = true, MinimumSize = new Size(80, 25) }, "截图");
        _btnShot.Click += async (_, _) => await TakeScreenshot();
        _btnSaveShot = Loc.Bind(new Button { AutoSize = true, MinimumSize = new Size(80, 25), Enabled = false }, "保存…");
        _btnSaveShot.Click += OnSaveScreenshot;
        shotBtnPanel.Controls.Add(_btnShot);
        shotBtnPanel.Controls.Add(_btnSaveShot);

        Controls.Add(_picShot);
        Controls.Add(shotBtnPanel);
    }

    async Task TakeScreenshot()
    {
        _btnShot.Enabled = false;
        _picShot.Image = null;
        _screenshotImage?.Dispose();
        _screenshotImage = null;
        _btnSaveShot.Enabled = false;

        try
        {
            var img = await _manager.ScreenshotAsync(_serial);
            if (img == null) throw new Exception(Loc.T("截图数据为空"));
            _screenshotImage = img;
            _picShot.Image = img;
            _btnSaveShot.Enabled = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Loc.F("截图失败：{0}", ex.Message), "错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _btnShot.Enabled = true;
        }
    }

    void OnSaveScreenshot(object? sender, EventArgs e)
    {
        if (_screenshotImage == null) return;
        using var dlg = new SaveFileDialog
        {
            Title = Loc.T("保存截图"),
            FileName = $"screenshot_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.png",
            Filter = Loc.T("PNG 图片 (*.png)|*.png|JPEG 图片 (*.jpg)|*.jpg|所有文件 (*.*)|*.*")
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        var format = dlg.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            ? System.Drawing.Imaging.ImageFormat.Jpeg
            : System.Drawing.Imaging.ImageFormat.Png;
        _screenshotImage.Save(dlg.FileName, format);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _screenshotImage?.Dispose();
        base.OnFormClosing(e);
    }
    /// <summary>切语言后重算标题——它含设备序列号（变量），没法用 Loc.Bind 绑静态键。</summary>
    void ILocalizedUi.OnLanguageChanged() => Text = Loc.F("截图 — {0}", _serial);
}
