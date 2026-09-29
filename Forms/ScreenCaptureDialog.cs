using logcat.Services;

namespace logcat.Forms;

/// <summary>
/// 截图 / 录屏对话框。
/// 截图：通过 GetFrameBuffer 获取设备屏幕快照，预览并保存。
/// 录屏：通过 screenrecord 录制设备屏幕，完成后拉取到本地。
/// </summary>
public class ScreenCaptureDialog : Form
{
    readonly AdbManager _manager;
    readonly string _serial;
    bool _recording;
    DateTime _recordStartTime;
    Image? _screenshotImage;

    readonly TabControl _tabs;
    readonly PictureBox _picShot;
    readonly Button _btnShot, _btnSaveShot;
    readonly Label _lblRecStat;
    readonly Button _btnRecStart, _btnRecStop, _btnRecPull;
    readonly System.Windows.Forms.Timer _recTimer = new() { Interval = 1000 };

    const string REMOTE_RECORD_PATH = "/sdcard/logcat_viewer_recording.mp4";

    public ScreenCaptureDialog(AdbManager manager, string serial, bool startRecording = false)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        _manager = manager;
        _serial = serial;
        Text = $"屏幕捕获 — {serial}";
        Size = new Size(720, 580);
        StartPosition = FormStartPosition.CenterParent;

        _tabs = new TabControl { Dock = DockStyle.Fill };
        Controls.Add(_tabs);

        // ── 截图标签页 ──
        var tabShot = new TabPage("截图");
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
        tabShot.Controls.Add(_picShot);
        tabShot.Controls.Add(shotBtnPanel);
        _tabs.TabPages.Add(tabShot);

        // ── 录屏标签页 ──
        var tabRec = new TabPage("录屏");
        _lblRecStat = new Label
        {
            Dock = DockStyle.Fill,
            Text = "点击「开始录屏」录制设备屏幕",
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.FromArgb(240, 240, 240),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font(Font.FontFamily, 12)
        };
        var recBtnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            FlowDirection = FlowDirection.LeftToRight
        };
        _btnRecStart = new Button { Text = "开始录屏", Width = 100 };
        _btnRecStart.Click += async (_, _) => await StartRecording();
        _btnRecStop = new Button { Text = "停止录屏", Width = 100, Enabled = false };
        _btnRecStop.Click += async (_, _) => await StopRecording();
        _btnRecPull = new Button { Text = "拉取到本地…", Width = 120, Enabled = false };
        _btnRecPull.Click += OnPullRecording;
        recBtnPanel.Controls.Add(_btnRecStart);
        recBtnPanel.Controls.Add(_btnRecStop);
        recBtnPanel.Controls.Add(_btnRecPull);
        tabRec.Controls.Add(_lblRecStat);
        tabRec.Controls.Add(recBtnPanel);
        _tabs.TabPages.Add(tabRec);

        _recTimer.Tick += (_, _) => UpdateRecTimer();

        if (startRecording)
        {
            _tabs.SelectedTab = tabRec;
            Load += async (_, _) => await StartRecording();
        }
    }

    // ── 截图 ──

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

    // ── 录屏 ──

    async Task StartRecording()
    {
        if (_recording) return;
        _recording = true;
        _recordStartTime = DateTime.Now;
        _btnRecStart.Enabled = false;
        _btnRecStop.Enabled = true;
        _btnRecPull.Enabled = false;
        _lblRecStat.Text = "录屏中…";

        // 先删除旧的录屏文件
        try { await _manager.ShellAsync(_serial, $"rm -f {REMOTE_RECORD_PATH}", 5); }
        catch { }

        // 在后台启动 screenrecord
        _ = Task.Run(async () =>
        {
            try { await _manager.ShellAsync(_serial, $"screenrecord {REMOTE_RECORD_PATH}", 0); }
            catch { }
        });

        _recTimer.Start();
    }

    async Task StopRecording()
    {
        if (!_recording) return;
        _recording = false;
        _recTimer.Stop();
        _btnRecStop.Enabled = false;

        try { await _manager.ShellAsync(_serial, "pkill -INT screenrecord", 5); }
        catch { }

        // 等待文件写入完成
        await Task.Delay(1000);

        var elapsed = (DateTime.Now - _recordStartTime).TotalSeconds;
        _lblRecStat.Text = $"录屏完成（{elapsed:F1} 秒），可拉取到本地";
        _btnRecStart.Enabled = true;
        _btnRecPull.Enabled = true;
    }

    void OnPullRecording(object? sender, EventArgs e)
    {
        using var dlg = new SaveFileDialog
        {
            Title = "保存录屏",
            FileName = $"recording_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.mp4",
            Filter = "MP4 视频 (*.mp4)|*.mp4|所有文件 (*.*)|*.*"
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        try
        {
            using var fs = File.Create(dlg.FileName);
            _manager.Pull(_serial, REMOTE_RECORD_PATH, fs);
            _lblRecStat.Text = $"已拉取 → {dlg.FileName}";
        }
        catch (Exception ex)
        {
            _lblRecStat.Text = $"拉取失败：{ex.Message}";
        }
    }

    void UpdateRecTimer()
    {
        var elapsed = (DateTime.Now - _recordStartTime).TotalSeconds;
        int mins = (int)(elapsed / 60);
        int secs = (int)(elapsed % 60);
        _lblRecStat.Text = $"● 录屏中  {mins:D2}:{secs:D2}";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_recording)
        {
            _recording = false;
            _recTimer.Stop();
            try { _manager.Shell(_serial, "pkill -INT screenrecord", 5); } catch { }
        }
        _screenshotImage?.Dispose();
        _recTimer.Dispose();
        base.OnFormClosing(e);
    }
}
