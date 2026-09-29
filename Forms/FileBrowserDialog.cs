using System.Text;
using logcat.Controls;
using logcat.Services;

namespace logcat.Forms;

/// <summary>
/// 双栏文件浏览器：左侧 Android 设备目录，右侧本机电脑目录。
/// 支持收藏目录、跨栏拖放传输、目录递归传输。
/// </summary>
public class FileBrowserDialog : Form
{
    readonly DeviceFilePane _remote;
    readonly LocalFilePane _local;
    readonly Label _lblStat;
    readonly ProgressBar _pbar;
    readonly Button _btnDownload;
    readonly Button _btnUpload;

    bool _transferring;

    /// <summary>
    /// 传输进行中是否阻止关闭并弹提示。默认 true；
    /// 嵌入设备操作窗口后，应用退出场景由宿主把它设为 false，避免关程序时弹「传输中」确认框。
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool BlockCloseWhileTransferring { get; set; } = true;

    public FileBrowserDialog(AdbManager manager, string serial, bool isRoot = false,
                             string startPath = "/sdcard", string localStartPath = "")
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = $"文件浏览 — {serial}{(isRoot ? " [root]" : "")}";
        Size = new Size(1440, 820);
        MinimumSize = new Size(920, 540);
        StartPosition = FormStartPosition.CenterParent;
        AllowDrop = true;

        // ── 左右两栏 ──
        // 注意：Panel1MinSize / Panel2MinSize / SplitterDistance 必须等布局完成后再设置，
        // 构造期控件还是默认尺寸，设置最小尺寸会让 SplitterDistance 越界并抛异常。
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 6,
        };
        _remote = new DeviceFilePane(manager, serial, isRoot);
        _local = new LocalFilePane();
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
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        root.Controls.Add(split, 0, 0);
        Controls.Add(root);

        // ── 底部操作栏 ──
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(4, 0, 0, 0),
            Margin = Padding.Empty,
        };

        _btnDownload = new Button { Text = "⬅ 下载到本机", Width = 110, Height = 26, Margin = new Padding(3, 5, 0, 0) };
        _btnDownload.Click += (_, _) => _ = TransferSelectionAsync(toDevice: false);
        bar.Controls.Add(_btnDownload);

        _btnUpload = new Button { Text = "上传到设备 ➡", Width = 110, Height = 26, Margin = new Padding(3, 5, 0, 0) };
        _btnUpload.Click += (_, _) => _ = TransferSelectionAsync(toDevice: true);
        bar.Controls.Add(_btnUpload);

        var btnRefreshAll = new Button { Text = "刷新两栏", Width = 86, Height = 26, Margin = new Padding(3, 5, 0, 0) };
        btnRefreshAll.Click += (_, _) => { _ = _remote.RefreshAsync(); _ = _local.RefreshAsync(); };
        bar.Controls.Add(btnRefreshAll);

        var btnClose = new Button { Text = "关闭", Width = 70, Height = 26, Margin = new Padding(3, 5, 0, 0) };
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
        _remote.PeerPathsDropped += (_, a) => _ = TransferPathsAsync(a, toDevice: false);
        _local.PeerPathsDropped += (_, a) => _ = TransferPathsAsync(a, toDevice: true);
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

            _remote.NavigateTo(startPath);
            _local.NavigateTo(localStartPath);
            UpdateTransferLabels();
        };
    }

    void UpdateTransferLabels()
    {
        var local = string.IsNullOrEmpty(_local.CurrentPath) ? "此电脑" : _local.CurrentPath;
        var remote = string.IsNullOrEmpty(_remote.CurrentPath) ? "/" : _remote.CurrentPath;
        _btnDownload.Text = $"⬅ 下载到 {Shorten(local, 18)}";
        _btnUpload.Text = $"上传到 {Shorten(remote, 18)} ➡";
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
            SetStatus(toDevice ? "请先在右侧本机面板选中要上传的文件" : "请先在左侧设备面板选中要下载的文件");
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

    async Task RunTransferAsync(IReadOnlyList<string> srcPaths, string srcBase, string dstDir, bool toDevice)
    {
        if (_transferring)
        {
            SetStatus("正在传输，请稍候…");
            return;
        }
        if (srcPaths.Count == 0) return;

        if (string.IsNullOrEmpty(dstDir))
        {
            SetStatus(toDevice ? "请先在左侧设备面板进入目标目录" : "请先在右侧本机面板进入目标目录");
            return;
        }

        _transferring = true;
        _btnDownload.Enabled = false;
        _btnUpload.Enabled = false;
        _pbar.Visible = true;

        var errors = new StringBuilder();
        try
        {
            var skipped = new List<string>();
            var files = toDevice
                ? LocalFilePane.ExpandFiles(srcPaths)
                : await _remote.ExpandFilesAsync(srcPaths, p => skipped.Add(p));

            foreach (var p in skipped)
                errors.AppendLine($"{p}: 目录无法递归展开（设备可能不支持 find，或目录为空/不可读）");

            if (files.Count == 0)
            {
                SetStatus("没有可传输的文件");
                if (errors.Length > 0)
                    MessageBox.Show(this, errors.ToString(), "传输结果",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _pbar.Maximum = files.Count;
            _pbar.Value = 0;

            int ok = 0, failed = 0;
            foreach (var src in files)
            {
                var rel = RelativeOf(src, srcBase, toDevice);
                _pbar.Value = ok + failed;
                SetStatus($"({ok + failed + 1}/{files.Count}) {Shorten(Path.GetFileName(src), 32)}");

                try
                {
                    if (toDevice)
                        await _remote.PushFileAsync(src, RemoteCombine(dstDir, rel));
                    else
                        await _remote.PullFileAsync(src, Path.Combine(dstDir, rel.Replace('/', '\\')));
                    ok++;
                }
                catch (Exception ex)
                {
                    failed++;
                    if (errors.Length < 1200)
                        errors.AppendLine($"{Path.GetFileName(src)}: {DescribeError(ex)}");
                }
            }

            SetStatus($"传输完成：成功 {ok} 个{(failed > 0 ? $"，失败 {failed} 个" : "")}");
            if (errors.Length > 0)
                MessageBox.Show(this, errors.ToString(), "传输结果",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);

            if (toDevice) await _remote.RefreshAsync();
            else await _local.RefreshAsync();
        }
        catch (Exception ex)
        {
            SetStatus("传输失败");
            MessageBox.Show(this, DescribeError(ex), "传输失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _pbar.Visible = false;
            _btnDownload.Enabled = true;
            _btnUpload.Enabled = true;
            _transferring = false;
        }
    }

    async Task CopyLocalFilesAsync(PathsDroppedArgs args)
    {
        var dstDir = string.IsNullOrEmpty(args.TargetDirectory) ? _local.CurrentPath : args.TargetDirectory;
        if (string.IsNullOrEmpty(dstDir))
        {
            SetStatus("请先进入一个本机目录");
            return;
        }

        var files = LocalFilePane.ExpandFiles(args.Paths);
        if (files.Count == 0) return;

        var errors = new StringBuilder();
        int ok = 0, failed = 0;
        _pbar.Visible = true;
        _pbar.Maximum = files.Count;
        _pbar.Value = 0;

        foreach (var src in files)
        {
            var rel = RelativeOf(src, args.SourceDirectory, false);
            var dst = Path.Combine(dstDir, rel.Replace('/', '\\'));
            _pbar.Value = ok + failed;
            SetStatus($"({ok + failed + 1}/{files.Count}) {Shorten(Path.GetFileName(src), 32)}");
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

        _pbar.Visible = false;
        SetStatus($"复制完成：成功 {ok} 个{(failed > 0 ? $"，失败 {failed} 个" : "")}");
        if (failed > 0)
            MessageBox.Show(this, $"{failed} 个文件复制失败：\n{errors}", "复制结果",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);

        await _local.RefreshAsync();
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
            MessageBox.Show(this, "文件正在传输中，请等待完成后再关闭。", "提示",
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
}
