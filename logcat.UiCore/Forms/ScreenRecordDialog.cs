using logcat.Services;

namespace logcat.Forms;

/// <summary>
/// 录屏对话框：通过 screenrecord 录制设备屏幕，完成后拉取到本地。
/// </summary>
public class ScreenRecordDialog : Form, ILocalizedUi
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
        Text = Loc.F("录屏 — {0}", serial);
        Size = new Size(720, 580);
        StartPosition = FormStartPosition.CenterParent;

        // 绑资源键：切语言时重设文案 + FitHeight 按新文案重算高度
        _lblRecStat = Loc.Bind(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.FromArgb(240, 240, 240),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font(Font.FontFamily, 12)
        }, "点击「开始录屏」录制设备屏幕");
        var recBtnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            FlowDirection = FlowDirection.LeftToRight
        };
        _btnRecStart = Loc.Bind(new Button { AutoSize = true, MinimumSize = new Size(100, 25) }, "开始录屏");
        _btnRecStart.Click += async (_, _) => await StartRecording();
        _btnRecStop = Loc.Bind(new Button { AutoSize = true, MinimumSize = new Size(100, 25), Enabled = false }, "停止录屏");
        _btnRecStop.Click += async (_, _) => await StopRecording();
        _btnRecPull = Loc.Bind(new Button { AutoSize = true, MinimumSize = new Size(120, 25), Enabled = false }, "拉取到本地…");
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
        _lblRecStat.Text = Loc.T("录屏中…");

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
        _lblRecStat.Text = Loc.F("录屏完成（{0:F1} 秒），可拉取到本地", elapsed);
        _btnRecStart.Enabled = true;
        _btnRecPull.Enabled = true;
    }

    void OnPullRecording(object? sender, EventArgs e)
    {
        using var dlg = new SaveFileDialog
        {
            Title = Loc.T("保存录屏"),
            FileName = $"recording_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.mp4",
            Filter = Loc.T("MP4 视频 (*.mp4)|*.mp4|所有文件 (*.*)|*.*")
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        try
        {
            using var fs = File.Create(dlg.FileName);
            _manager.Pull(_serial, REMOTE_RECORD_PATH, fs);
            _lblRecStat.Text = Loc.F("已拉取 → {0}", dlg.FileName);
        }
        catch (Exception ex)
        {
            _lblRecStat.Text = Loc.F("拉取失败：{0}", ex.Message);
        }
    }

    void UpdateRecTimer()
    {
        var elapsed = (DateTime.Now - _recordStartTime).TotalSeconds;
        int mins = (int)(elapsed / 60);
        int secs = (int)(elapsed % 60);
        _lblRecStat.Text = Loc.F("● 录屏中  {0:D2}:{1:D2}", mins, secs);
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
    /// <summary>
    /// 切语言后按当前状态重算状态标签——三种状态（录屏中/已停止/拉取结果）
    /// 是运行时算出来的，没有静态键可绑，只能这里重算。
    /// </summary>
    void ILocalizedUi.OnLanguageChanged()
    {
        if (_recordStartTime == default) _lblRecStat.Text = Loc.T("点击「开始录屏」录制设备屏幕");
        else UpdateRecTimer();
        PerformLayout();
    }
}
