using logcat.Services;

namespace logcat.Forms;

/// <summary>
/// 截图对话框：通过 GetFrameBuffer 获取设备屏幕快照，预览并保存。
/// </summary>
public class ScreenShotDialog : Form
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
        Text = $"截图 — {serial}";
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
        _btnShot = new Button { Text = "截图", Width = 80 };
        _btnShot.Click += async (_, _) => await TakeScreenshot();
        _btnSaveShot = new Button { Text = "保存…", Width = 80, Enabled = false };
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
            if (img == null) throw new Exception("截图数据为空");
            _screenshotImage = img;
            _picShot.Image = img;
            _btnSaveShot.Enabled = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"截图失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            Title = "保存截图",
            FileName = $"screenshot_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.png",
            Filter = "PNG 图片 (*.png)|*.png|JPEG 图片 (*.jpg)|*.jpg|所有文件 (*.*)|*.*"
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
}
