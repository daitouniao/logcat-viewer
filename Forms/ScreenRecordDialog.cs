using logcat.Services;

namespace logcat.Forms;

/// <summary>
/// 录屏对话框：通过 screenrecord 录制设备屏幕，完成后拉取到本地。
/// </summary>
public class ScreenRecordDialog : Form
{
    readonly AdbManager _manager;
    readonly string _serial;
    bool _recording;
    DateTime _recordStartTime;

    readonly Label _lblRecStat;
    readonly Button _btnRecStart, _btnRecStop, _btnRecPull;
    readonly System.Windows.Forms.Timer _recTimer = new() { Interval = 1000 };

    const string REMOTE_RECORD_PATH = "/sdcard/logcat_viewer_recording.mp4";

    public ScreenRecordDialog(AdbManager manager, string serial, bool startRecording = false)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        _manager = manager;
        _serial = serial;
        Text = $"录屏 — {serial}";
        Size = new Size(720, 580);
        StartPosition = FormStartPosition.CenterParent;

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

        Controls.Add(_lblRecStat);
        Controls.Add(recBtnPanel);

        _recTimer.Tick += (_, _) => UpdateRecTimer();

        if (startRecording)
        {
            Load += async (_, _) => await StartRecording();
        }
    }

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
        _recTimer.Dispose();
        base.OnFormClosing(e);
    }
}
