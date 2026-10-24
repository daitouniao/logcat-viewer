using System.Text;
using logcat.Controls;
using logcat.Models;
using logcat.Services;

namespace logcat.Forms;

/// <summary>
/// 双栏文件浏览器：左侧 Android 设备目录，右侧本机电脑目录。
/// 支持收藏目录、跨栏拖放传输、目录递归传输。
/// </summary>
public class FileBrowserDialog : Form, ILocalizedUi
{
    readonly DeviceFilePane _remote;
    readonly LocalFilePane _local;
    readonly SplitContainer _split = null!;
    readonly Label _lblStat;
    readonly ProgressBar _pbar;
    readonly Button _btnDownload;
    readonly Button _btnUpload;

    bool _transferring;

    string _startPath = "/sdcard";
    string _localStartPath = "";

    /// <summary>
    /// 传输进行中是否阻止关闭并弹提示。默认 true；
    /// 嵌入设备操作窗口后，应用退出场景由宿主把它设为 false，避免关程序时弹「传输中」确认框。
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool BlockCloseWhileTransferring { get; set; } = true;

    public FileBrowserDialog(AdbManager manager, string serial, RootMode rootMode = RootMode.None,
                             string startPath = "/sdcard", string localStartPath = "")
        : this(new DeviceFilePane(manager, serial, rootMode), new LocalFilePane(), serial, rootMode)
    {
        _startPath = startPath;
        _localStartPath = localStartPath;
    }

    /// <summary>测试注入构造：面板可替换，便于在无设备环境下驱动传输编排（design D6）。</summary>
    internal FileBrowserDialog(DeviceFilePane remote, LocalFilePane local, string serial, RootMode rootMode = RootMode.None)
    {
        _remote = remote;
        _local = local;

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = Loc.F("文件浏览 — {0}{1}", serial, rootMode != RootMode.None ? " [root]" : "");
        Size = new Size(1440, 820);
        MinimumSize = new Size(920, 540);
        StartPosition = FormStartPosition.CenterParent;
        AllowDrop = true;

        // ── 左右两栏 ──
        // 注意：Panel1MinSize / Panel2MinSize / SplitterDistance 必须等布局完成后再设置，
        // 构造期控件还是默认尺寸，设置最小尺寸会让 SplitterDistance 越界并抛异常。
        var split = _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 6,
        };
        split.Panel1.Controls.Add(_remote);
        split.Panel2.Controls.Add(_local);

        // 用 TableLayoutPanel 定位底部操作栏，避免 Dock 顺序导致的遮挡
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        // 行高 AutoSize 而非写死：绝对行高在 150% 高 DPI / 嵌入 TabPage 缩放链路里不会被重算，会裁掉底栏
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(split, 0, 0);
        Controls.Add(root);

        // ── 底部操作栏 ──
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            // 同 NewBar()：允许换行，避免状态文字变长时把右侧控件挤出可视区
            WrapContents = true,
            // 同上：AutoSize 行按实际高度测量，不打开 AutoSize 会按默认高度 100 撑开底栏
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(4, 0, 0, 0),
            Margin = Padding.Empty,
        };

        _btnDownload = Loc.Bind(new Button { AutoSize = true, MinimumSize = new Size(110, 26), Margin = new Padding(3, 5, 0, 0) }, "⬅ 下载到本机");
        _btnDownload.Click += (_, _) => _ = TransferSelectionAsync(toDevice: false);
        bar.Controls.Add(_btnDownload);

        _btnUpload = Loc.Bind(new Button { AutoSize = true, MinimumSize = new Size(110, 26), Margin = new Padding(3, 5, 0, 0) }, "上传到设备 ➡");
        _btnUpload.Click += (_, _) => _ = TransferSelectionAsync(toDevice: true);
        bar.Controls.Add(_btnUpload);

        var btnRefreshAll = Loc.Bind(new Button { AutoSize = true, MinimumSize = new Size(86, 26), Margin = new Padding(3, 5, 0, 0) }, "刷新两栏");
        btnRefreshAll.Click += (_, _) => { _ = _remote.RefreshAsync(); _ = _local.RefreshAsync(); };
        bar.Controls.Add(btnRefreshAll);

        var btnClose = Loc.Bind(new Button { AutoSize = true, MinimumSize = new Size(70, 26), Margin = new Padding(3, 5, 0, 0) }, "关闭");
        btnClose.Click += (_, _) => Close();
        bar.Controls.Add(btnClose);

        _pbar = new ProgressBar
        {
            Width = 180,
            Height = 18,
            Minimum = 0,
            Maximum = 100,
            Visible = false,
            Margin = new Padding(10, 7, 0, 0),
        };
        bar.Controls.Add(_pbar);

        _lblStat = new Label
        {
            AutoSize = true,
            ForeColor = Color.DimGray,
            Padding = new Padding(8, 10, 0, 0),
        };
        bar.Controls.Add(_lblStat);

        root.Controls.Add(bar, 0, 1);

        // ── 事件接线 ──
        _remote.TransferSelectionRequested += (_, _) => _ = TransferSelectionAsync(toDevice: false);
        _local.TransferSelectionRequested += (_, _) => _ = TransferSelectionAsync(toDevice: true);
        _remote.PeerPathsDropped += (_, a) => _ = TransferPathsAsync(a, toDevice: true);
        _local.PeerPathsDropped += (_, a) => _ = TransferPathsAsync(a, toDevice: false);
        _remote.FilesDropped += (_, a) => _ = TransferPathsAsync(a, toDevice: true);
        _local.FilesDropped += (_, a) => _ = CopyLocalFilesAsync(a);
        _remote.PathChanged += (_, _) => UpdateTransferLabels();
        _local.PathChanged += (_, _) => UpdateTransferLabels();

        Load += (_, _) =>
        {
            try
            {
                const int min = 360;
                // 只在宽度足够时收紧最小尺寸，否则保持默认的 25
                if (split.Width > min * 2 + split.SplitterWidth)
                {
                    split.Panel1MinSize = min;
                    split.Panel2MinSize = min;
                }
                split.SplitterDistance = Math.Clamp(split.Width / 2,
                    split.Panel1MinSize,
                    Math.Max(split.Panel1MinSize, split.Width - split.Panel2MinSize));
            }
            catch { /* 布局异常不应阻断打开对话框 */ }

            _remote.NavigateTo(_startPath);
            _local.NavigateTo(_localStartPath);
            UpdateTransferLabels();
        };

        Shown += (_, _) =>
        {
            DpiDiag.Log($"FileBrowserDialog.Shown({(TopLevel ? "独立窗口" : "嵌入页签")})", this);
            LogLayout("Shown");
        };
        DpiChanged += (_, e) =>
        {
            DpiDiag.Write($"FileBrowserDialog.DpiChanged dpi→{DeviceDpi} suggested={e.SuggestedRectangle} bounds={Bounds}");
            BeginInvoke(() => LogLayout("DpiChanged后"));   // 诊断日志保持中文，不本地化
        };
    }

    void LogLayout(string when)
    {
        try
        {
            DpiDiag.Write($"FileBrowserDialog.{when}: split={_split.Width}x{_split.Height} " +
                          $"panel1={_split.Panel1.Width} panel2={_split.Panel2.Width} " +
                          $"remote[{_remote.DescribeLayout()}] local[{_local.DescribeLayout()}]");
        }
        catch (Exception ex) { DpiDiag.Write($"FileBrowserDialog.LogLayout 异常: {ex.Message}"); }
    }

    void UpdateTransferLabels()
    {
        var local = string.IsNullOrEmpty(_local.CurrentPath) ? Loc.T("此电脑") : _local.CurrentPath;
        var remote = string.IsNullOrEmpty(_remote.CurrentPath) ? "/" : _remote.CurrentPath;
        _btnDownload.Text = Loc.F("⬅ 下载到 {0}", Shorten(local, 18));
        _btnUpload.Text = Loc.F("上传到 {0} ➡", Shorten(remote, 18));
    }

    static string Shorten(string s, int max)
    {
        if (s.Length <= max) return s;
        return "…" + s[^(max - 1)..];
    }

    /// <summary>把异常展开成便于定位的单行文本（含内层异常）。</summary>
    static string DescribeError(Exception ex)
    {
        var sb = new StringBuilder();
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (sb.Length > 0) sb.Append(" ← ");
            sb.Append($"{e.GetType().Name}: {e.Message}");
        }
        return sb.ToString();
    }

    // ── 传输 ──

    async Task TransferSelectionAsync(bool toDevice)
    {
        var src = toDevice ? (FilePane)_local : _remote;
        var paths = src.SelectedPaths;
        if (paths.Count == 0)
        {
            SetStatus(toDevice ? Loc.T("请先在右侧本机面板选中要上传的文件") : Loc.T("请先在左侧设备面板选中要下载的文件"));
            return;
        }
        var dstDir = toDevice ? _remote.CurrentPath : _local.CurrentPath;
        await RunTransferAsync(paths, src.CurrentPath, dstDir, toDevice);
    }

    async Task TransferPathsAsync(PathsDroppedArgs args, bool toDevice)
    {
        var dstDir = args.TargetDirectory;
        if (string.IsNullOrEmpty(dstDir)) dstDir = toDevice ? _remote.CurrentPath : _local.CurrentPath;
        await RunTransferAsync(args.Paths, args.SourceDirectory, dstDir, toDevice);
    }

    internal async Task RunTransferAsync(IReadOnlyList<string> srcPaths, string srcBase, string dstDir, bool toDevice)
    {
        if (_transferring)
        {
            SetStatus(Loc.T("正在传输，请稍候…"));
            return;
        }
        if (srcPaths.Count == 0) return;

        if (string.IsNullOrEmpty(dstDir))
        {
            SetStatus(toDevice ? Loc.T("请先在左侧设备面板进入目标目录") : Loc.T("请先在右侧本机面板进入目标目录"));
            return;
        }

        _transferring = true;
        _btnDownload.Enabled = false;
        _btnUpload.Enabled = false;
        _pbar.Style = ProgressBarStyle.Blocks;
        _pbar.Maximum = 100;
        _pbar.Value = 0;
        _pbar.Visible = true;

        var errors = new StringBuilder();
        try
        {
            var skipped = new List<string>();
            var files = toDevice
                ? LocalFilePane.ExpandFiles(srcPaths)
                : await _remote.ExpandFilesAsync(srcPaths, p => skipped.Add(p));

            foreach (var p in skipped)
                errors.AppendLine(Loc.F("{0}: 目录无法递归展开（设备可能不支持 find，或目录为空/不可读）", p));

            if (files.Count == 0)
            {
                SetStatus(Loc.T("没有可传输的文件"));
                if (errors.Length > 0)
                    MessageBox.Show(this, errors.ToString(), Loc.T("传输结果"),
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                await RefreshTargetAsync(toDevice);
                return;
            }

            // 总量在传输开始前算清（design D2）：上传侧取本机文件长度，下载侧取目录列表中的大小；
            // 任一项不可知则分母不可信，进度条退为不确定态而不是显示错的百分比
            var totalBytes = TransferProgress.TotalBytes(files.Select(f => FileSizeOf(f, toDevice)));
            var reporter = new TransferReporter(this, totalBytes);
            if (totalBytes is null)
                reporter.Report((double.NaN, ""));

            int ok = 0, failed = 0;
            for (int i = 0; i < files.Count; i++)
            {
                var src = files[i];
                var rel = RelativeOf(src, srcBase, toDevice);
                reporter.NextFile(FileSizeOf(src, toDevice) ?? 0);
                SetStatus(Loc.F("({0}/{1}) {2}", i + 1, files.Count, Shorten(Path.GetFileName(src), 32)));

                try
                {
                    if (toDevice)
                        await _remote.PushFileAsync(src, RemoteCombine(dstDir, rel), reporter);
                    else
                        await _remote.PullFileAsync(src, Path.Combine(dstDir, rel.Replace('/', '\\')), reporter);
                    ok++;
                }
                catch (Exception ex)
                {
                    failed++;
                    if (errors.Length < 1200)
                        errors.AppendLine($"{Path.GetFileName(src)}: {DescribeError(ex)}");
                }
            }

            // 上传完成后在设备端 sync 落盘，避免数据还在页缓存里就重启/拔线；失败不影响传输结果。
            // 刷盘不产生字节回调，归入非字节阶段上报（任务 2.3），避免进度条在长耗时段静止
            if (toDevice && ok > 0)
            {
                var syncMsg = Loc.F("({0}/{1}) 正在 sync 刷写到存储…", files.Count, files.Count);
                SetStatus(syncMsg);
                reporter.Report((double.NaN, syncMsg));
                await _remote.SyncAsync();
            }

            SetStatus(TransferResultText(ok, failed, dstDir, toDevice));
            if (errors.Length > 0)
                MessageBox.Show(this, errors.ToString(), Loc.T("传输结果"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);

            await RefreshTargetAsync(toDevice);
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("传输失败"));
            MessageBox.Show(this, DescribeError(ex), Loc.T("传输失败"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            // 异常路径同样刷新（design D7）：已落盘的文件不能在列表里缺席
            try { await RefreshTargetAsync(toDevice); } catch { /* 刷新失败不掩盖原始异常提示 */ }
        }
        finally
        {
            // 终态满格再隐藏（任务 3.3）：异常终止也不停留在中间态
            _pbar.Style = ProgressBarStyle.Blocks;
            _pbar.Value = _pbar.Maximum;
            _pbar.Visible = false;
            _btnDownload.Enabled = true;
            _btnUpload.Enabled = true;
            _transferring = false;
        }
    }

    /// <summary>
    /// 传输结果文案（任务 3.4/4.4）：成功/失败统计；目标不是目标栏当前目录（如拖放到子目录项）时说明落点，
    /// 否则用户看到的是「刷新了却看不到」。
    /// </summary>
    string TransferResultText(int ok, int failed, string dstDir, bool toDevice)
    {
        var text = Loc.F("传输完成：成功 {0} 个{1}", ok, failed > 0 ? Loc.F("，失败 {0} 个", failed) : "");
        var target = toDevice ? (FilePane)_remote : _local;
        if (!string.Equals(dstDir, target.CurrentPath, StringComparison.OrdinalIgnoreCase))
            text += Loc.F("，文件已放入 {0}", Shorten(dstDir, 40));
        return text;
    }

    /// <summary>刷新目标栏当前目录：上传刷设备栏、下载刷本机栏（design D7）。</summary>
    Task RefreshTargetAsync(bool toDevice) => toDevice ? _remote.RefreshAsync() : _local.RefreshAsync();

    /// <summary>取参与总字节计算的单文件大小：上传侧取本机文件长度，下载侧取目录列表中已显示的大小（design D2）。</summary>
    long? FileSizeOf(string path, bool toDevice)
    {
        if (toDevice)
        {
            try { return new FileInfo(path).Length; }
            catch { return null; }
        }
        return _remote.TryGetRemoteSize(path);
    }

    /// <summary>
    /// 传输期进度上报器（design D2/D5/Risks）：把每文件的字节百分比折算成本次传输整体百分比，
    /// 并对控件刷新做节流——幅度不足 1 个百分点且间隔不足 200ms 时只更新内存值，不刷控件，
    /// 避免大文件传输时字节回调过密拖卡 UI 线程。pct=NaN 表示非字节阶段（不确定态），msg 为动作说明。
    /// </summary>
    sealed class TransferReporter : IProgress<(double pct, string msg)>
    {
        const double FlushDeltaPct = 1.0;
        static readonly long FlushIntervalTicks = TimeSpan.FromMilliseconds(200).Ticks;

        readonly FileBrowserDialog _dlg;
        readonly long _total;
        readonly object _lock = new();
        long _offset;
        long _fileSize;
        double? _anchor;                 // 整体百分比单调锚点：防进度回退
        double _shownPct = -1;           // 控件已显示的整体百分比（-1 = 还没刷过）
        long _lastFlush = DateTime.UtcNow.Ticks;

        public TransferReporter(FileBrowserDialog dlg, long? total)
        {
            _dlg = dlg;
            _total = total ?? 0;
        }

        /// <summary>进入下一个文件：把当前文件计入累计字节，重设字节窗口。</summary>
        public void NextFile(long fileSize)
        {
            lock (_lock)
            {
                _offset += _fileSize;
                _fileSize = fileSize;
            }
        }

        public void Report((double pct, string msg) value)
        {
            if (double.IsNaN(value.pct))
            {
                _dlg.BeginUi(() => _dlg.ShowIndeterminate(value.msg));
                return;
            }

            double? overall;
            lock (_lock)
            {
                if (_total <= 0) return;   // 分母不可信：不冒充百分比（不确定态由阶段上报驱动）
                var intra = (long)Math.Round(value.pct / 100.0 * _fileSize);
                overall = TransferProgress.PercentOf(_offset + intra, _total, _anchor);
                if (overall is null) return;
                _anchor = overall;
                var now = DateTime.UtcNow.Ticks;
                var flush = _shownPct < 0 ||
                            overall.Value - _shownPct >= FlushDeltaPct ||
                            now - _lastFlush >= FlushIntervalTicks;
                if (!flush) return;        // 幅度与间隔都不足：只留在内存里
                _shownPct = overall.Value;
                _lastFlush = now;
            }
            var pct = overall.Value;
            _dlg.BeginUi(() => _dlg.ShowPercent(pct));
        }
    }

    internal void ShowPercent(double pct)
    {
        if (_pbar.Style != ProgressBarStyle.Blocks)
        {
            _pbar.Style = ProgressBarStyle.Blocks;
            _pbar.Maximum = 100;
        }
        _pbar.Value = (int)Math.Clamp(Math.Round(pct), 0, 100);
    }

    internal void ShowIndeterminate(string message)
    {
        if (_pbar.Style != ProgressBarStyle.Marquee)
        {
            _pbar.Style = ProgressBarStyle.Marquee;
            _pbar.MarqueeAnimationSpeed = 30;
        }
        if (message.Length > 0) SetStatus(message);
    }

    void BeginUi(Action action)
    {
        if (IsHandleCreated) BeginInvoke(action);
        else action();
    }

    internal async Task CopyLocalFilesAsync(PathsDroppedArgs args)
    {
        if (_transferring)
        {
            SetStatus(Loc.T("正在传输，请稍候…"));
            return;
        }

        var dstDir = string.IsNullOrEmpty(args.TargetDirectory) ? _local.CurrentPath : args.TargetDirectory;
        if (string.IsNullOrEmpty(dstDir))
        {
            SetStatus(Loc.T("请先进入一个本机目录"));
            return;
        }

        var files = LocalFilePane.ExpandFiles(args.Paths);
        if (files.Count == 0) return;

        // 本机内复制与设备传输共用同一条进度条，纳入同一占用闸门（design D9）；
        // 进度条生命周期收口到 try/finally，异常路径同样收起（任务 4.2）
        _transferring = true;
        _btnDownload.Enabled = false;
        _btnUpload.Enabled = false;
        _pbar.Style = ProgressBarStyle.Blocks;
        _pbar.Maximum = Math.Max(files.Count, 1);
        _pbar.Value = 0;
        _pbar.Visible = true;

        var errors = new StringBuilder();
        int ok = 0, failed = 0;
        try
        {
            for (int i = 0; i < files.Count; i++)
            {
                var src = files[i];
                var rel = RelativeOf(src, args.SourceDirectory, false);
                var dst = Path.Combine(dstDir, rel.Replace('/', '\\'));
                _pbar.Value = i;
                SetStatus(Loc.F("({0}/{1}) {2}", i + 1, files.Count, Shorten(Path.GetFileName(src), 32)));
                try
                {
                    var dir = Path.GetDirectoryName(dst);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.Copy(src, dst, true);
                    ok++;
                }
                catch (Exception ex)
                {
                    failed++;
                    if (errors.Length < 1200) errors.AppendLine($"{Path.GetFileName(src)}: {ex.Message}");
                }
            }

            _pbar.Value = _pbar.Maximum;
            SetStatus(Loc.F("复制完成：成功 {0} 个{1}", ok, failed > 0 ? Loc.F("，失败 {0} 个", failed) : ""));
            if (failed > 0)
                MessageBox.Show(this, Loc.F("{0} 个文件复制失败：\n{1}", failed, errors), Loc.T("复制结果"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);

            await _local.RefreshAsync();
        }
        catch (Exception ex)
        {
            SetStatus(Loc.F("复制失败：{0}", ex.Message));
        }
        finally
        {
            _pbar.Style = ProgressBarStyle.Blocks;
            _pbar.Value = _pbar.Maximum;
            _pbar.Visible = false;
            _btnDownload.Enabled = true;
            _btnUpload.Enabled = true;
            _transferring = false;
        }
    }

    // ── 路径工具 ──

    static string RelativeOf(string path, string baseDir, bool toDevice)
    {
        if (string.IsNullOrEmpty(baseDir)) return FileNameOf(path, toDevice);

        var b = baseDir.TrimEnd('/', '\\');
        if (path.Length > b.Length && path.StartsWith(b, StringComparison.OrdinalIgnoreCase))
        {
            var rel = path[b.Length..].TrimStart('/', '\\');
            if (rel.Length > 0) return rel;
        }
        return FileNameOf(path, toDevice);
    }

    static string FileNameOf(string path, bool toDevice)
    {
        var name = toDevice ? path.Split('\\').Last() : path.Split('/').Last();
        return toDevice ? name.Replace('\\', '/') : name;
    }

    static string RemoteCombine(string dir, string rel)
    {
        dir = dir.TrimEnd('/');
        rel = rel.Replace('\\', '/').TrimStart('/');
        return dir.Length == 0 ? "/" + rel : dir + "/" + rel;
    }

    void SetStatus(string text) => _lblStat.Text = text;

    // ── 关闭 ──

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_transferring && BlockCloseWhileTransferring && e.CloseReason == CloseReason.UserClosing)
        {
            MessageBox.Show(this, Loc.T("文件正在传输中，请等待完成后再关闭。"), Loc.T("提示"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            e.Cancel = true;
            return;
        }

        _remote.CancelLoading();
        _local.CancelLoading();
        SaveLastPaths();
        base.OnFormClosing(e);
    }

    void SaveLastPaths()
    {
        try
        {
            var s = logcat.Services.AppSettings.Default;
            if (!string.IsNullOrEmpty(_remote.CurrentPath)) s.LastRemotePath = _remote.CurrentPath;
            if (!string.IsNullOrEmpty(_local.CurrentPath)) s.LastLocalPath = _local.CurrentPath;
            s.Save();
        }
        catch { /* 设置不可用时忽略 */ }
    }
    /// <summary>
    /// 切语言后重算两个传输按钮的文案（内含路径，是运行时算出来的）。
    /// </summary>
    void ILocalizedUi.OnLanguageChanged()
    {
        UpdateTransferLabels();
        PerformLayout();
    }
}
