using logcat.Controls;
using logcat.Forms;
using logcat.Models;
using logcat.Services;
using Microsoft.Extensions.Logging;

namespace logcat;

public partial class frmMain : Form
{
    // ── 数据 ──
    LogDocument? _doc;
    FilterSpec _spec = new();
    HashSet<int> _marked = new();
    double _indexMs, _filterMs;
    bool _join = true;
    int _fontPt = 10;
    string _newlineVis = "↵";
    bool _singleLineExport;

    // ── 异步 ──
    CancellationTokenSource? _ctsWorker;
    int _workerGen;
    bool _programScroll;
    int _lastVsb = -1;

    // ── ADB ──
    AdbManager? _adbManager;
    LogcatStream? _streamThread;
    string? _adbTempPath;
    bool _adbCapturing;
    bool _adbReloading;
    bool _adbIndexing;
    int _adbGen;
    int _adbLiveCount;
    readonly List<DeviceInfo> _adbDevices = new();

    // ── 控件 ──
    LogListView _listView = null!;
    ToolStrip _toolStrip = null!;
    ToolStrip _toolStrip2 = null!;
    StatusStrip _statusStrip = null!;
    ToolStripStatusLabel _lblFile = null!, _lblStat = null!, _lblPos = null!, _lblMsg = null!;
    ToolStripProgressBar _pbar = null!;
    ToolStripComboBox _comboDevice = null!;

    // 过滤面板控件
    CheckBox[] _lvlBoxes = new CheckBox[8];
    TextBox _edTag = null!, _edMsg = null!, _edPid = null!, _edTid = null!, _edMin = null!;
    ComboBox _cbTagOp = null!, _cbMsgOp = null!;
    CheckBox _ckTagRe = null!, _ckTagCase = null!, _ckTagEx = null!;
    CheckBox _ckMsgRe = null!, _ckMsgCase = null!, _ckMsgEx = null!;
    CheckBox _ckPidEx = null!, _ckTidEx = null!;
    CheckBox _chkAuto = null!;
    // 工具栏上的过滤控件（与过滤窗口双向同步）
    ToolStripTextBox _tbMin = null!, _tbTag = null!, _tbMsg = null!;
    CheckBox _chkToolbarMarkedOnly = null!, _chkToolbarFollow = null!;
    bool _syncingFilter;
    Label _lblSpec = null!;
    GroupBox _panel = null!;

    // 定时器
    readonly System.Windows.Forms.Timer _autoTimer = new() { Interval = 400 };
    readonly System.Windows.Forms.Timer _flushTimer = new() { Interval = 250 };
    readonly System.Windows.Forms.Timer _adbReloadTimer = new() { Interval = 1000 };
    readonly System.Windows.Forms.Timer _adbStatTimer = new() { Interval = 300 };

    // 菜单项
    ToolStripMenuItem _actJoin = null!, _actSingleExport = null!, _actSaveLog = null!;

    // 设备操作总窗口（截图 / 录屏 / 文件浏览 / 安装·卸载 APK / 命令 合并为一个页签式窗口，非模态）
    DeviceOpsDialog? _deviceOps;

    // 过滤设置窗口（非模态，悬浮在主窗口之上）
    FilterDialog? _filterDialog;

    readonly ILogger _logger;

    // ── 窗口几何（多屏安全）──
    // 保存的“正常态矩形”与窗口状态；在 Load 事件中应用，
    // 最大化需等窗体句柄就绪才能可靠落到原屏。
    Rectangle _savedBounds;
    FormWindowState _savedState = FormWindowState.Normal;
    bool _hasSavedGeometry;

    public frmMain()
    {
        // 日志
        var loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Debug);
            b.AddDebug();
        });
        _logger = loggerFactory.CreateLogger("frmMain");

        InitializeComponent();
        InitializeComponent2();
        LoadSettings();
        InitAdb();
    }

    void InitializeComponent2()
    {
        Text = AppInfo.Title;
        Size = new Size(1440, 900);
        StartPosition = FormStartPosition.WindowsDefaultBounds;
        AllowDrop = true;
        KeyPreview = true;

        // ── 菜单（并入工具栏第一行）──
        var mFile = new ToolStripMenuItem("文件");
        var mSet = new ToolStripMenuItem("设置");

        var actOpen = new ToolStripMenuItem("打开…", null, (_, _) => OpenFile(), Keys.Control | Keys.O);
        var actReload = new ToolStripMenuItem("重载", null, (_, _) => Reload(), Keys.F5);
        var actExport = new ToolStripMenuItem("导出结果…", null, (_, _) => ExportRows(false), Keys.Control | Keys.E);
        var actExportMarked = new ToolStripMenuItem("导出标记行…", null, (_, _) => ExportRows(true));
        var actQuit = new ToolStripMenuItem("退出", null, (_, _) => Close(), Keys.Control | Keys.Q);
        _actSaveLog = new ToolStripMenuItem("保存日志…", null, (_, _) => AdbSaveLog()) { Enabled = false };
        mFile!.DropDownItems.AddRange(new ToolStripItem[] { actOpen, actReload, actExport, actExportMarked, _actSaveLog, new ToolStripSeparator(), actQuit });

        _actJoin = new ToolStripMenuItem("续行合并（堆栈并入上一条记录）") { Checked = true, CheckOnClick = true };
        _actJoin.Click += (_, _) => { _join = _actJoin.Checked; if (_doc != null) StartIndex(_doc.Path); };
        mSet!.DropDownItems.Add(_actJoin);

        _actSingleExport = new ToolStripMenuItem("导出时单行化（换行转 \\n）") { CheckOnClick = true };
        _actSingleExport.Click += (_, _) => _singleLineExport = _actSingleExport.Checked;
        mSet.DropDownItems.Add(_actSingleExport);
        mSet.DropDownItems.Add(new ToolStripSeparator());
        mSet.DropDownItems.Add(new ToolStripMenuItem("过滤设置…", null, (_, _) => ShowFilterDialog()));

        // ── 帮助菜单 ──
        var mHelp = new ToolStripMenuItem("帮助");
        var actAbout = new ToolStripMenuItem("关于…", null, (_, _) => ShowAbout());
        mHelp!.DropDownItems.Add(actAbout);

        // 字号子菜单
        var mFont = new ToolStripMenuItem("字号");
        var fontGroup = new ToolStripMenuItem[5];
        int[] fontSizes = { 9, 10, 11, 12, 14 };
        for (int i = 0; i < fontSizes.Length; i++)
        {
            int pt = fontSizes[i];
            fontGroup[i] = new ToolStripMenuItem(pt.ToString()) { CheckOnClick = true, Checked = pt == _fontPt };
            fontGroup[i].Click += (_, _) => { foreach (var f in fontGroup) f.Checked = false; fontGroup[i].Checked = true; ApplyFont(pt); };
        }
        mFont.DropDownItems.AddRange(fontGroup);
        mSet.DropDownItems.Add(mFont);

        // ── 主工具栏（菜单 + 文件操作 + ADB 合并为一行）──
        _toolStrip = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, ShowItemToolTips = false };
        _comboDevice = new ToolStripComboBox { Enabled = false, Width = 230, DropDownWidth = 320 };
        // 换设备时，让设备操作窗口里绑定设备的页签跟着重建
        _comboDevice.SelectedIndexChanged += (_, _) => _deviceOps?.OnDeviceChanged();
        // 设备操作总入口（截图/录屏/文件浏览/APK/命令）
        var btnDeviceOps = new ToolStripButton("设备操作", null, (_, _) => ShowDeviceOps(DeviceOpsDialog.PageKind.Command));
        var btnRefresh = new ToolStripButton("刷新设备", null, (_, _) => AdbRefresh());
        // 刷新设备紧贴设备列表左侧；开始/停止采集在左侧分组
        ToolStripItem[] adbItems =
        {
            btnDeviceOps,
            new ToolStripButton("▶ 开始采集", null, (_, _) => AdbStartCapture()) { Enabled = false },
            new ToolStripButton("■ 停止采集", null, (_, _) => AdbStopCapture()) { Enabled = false },
            new ToolStripSeparator(),
            btnRefresh,
            new ToolStripLabel("设备:"),
            _comboDevice
        };
        foreach (var it in adbItems) it.Tag = "adb";
        // 不依赖具体设备的按钮（刷新设备、设备操作总入口）单独标记，无设备时也要能点
        btnRefresh.Tag = "adb-free";
        btnDeviceOps.Tag = "adb-free";
        _toolStrip.Items.AddRange(new ToolStripItem[] {
            mFile, mSet, mHelp,
            new ToolStripSeparator()
        });
        _toolStrip.Items.AddRange(adbItems);
        Controls.Add(_toolStrip);
        _toolStrip.Resize += (_, _) => LayoutDeviceCombo();

        // ── 第二行工具栏：标记相关按钮 + 快速过滤输入框 ──
        _toolStrip2 = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, ShowItemToolTips = true };

        _chkToolbarMarkedOnly = new CheckBox { Text = "仅标记行", AutoSize = true };
        _chkToolbarMarkedOnly.CheckedChanged += (_, _) => { if (!_syncingFilter) OnFilterChanged(); };
        _chkToolbarFollow = new CheckBox { Text = "跟随尾部", AutoSize = true };
        _chkToolbarFollow.CheckedChanged += (_, _) => { if (!_syncingFilter && _chkToolbarFollow.Checked && _listView.Rows.Length > 0) ScrollBottom(); };

        _tbMin = new ToolStripTextBox { Width = 80, ToolTipText = "如 05 20（空格分隔）" };
        _tbMin.TextChanged += (_, _) => SyncToolbarToPanel(_tbMin, _edMin);
        _tbMin.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) ApplyFilter(); };

        _tbTag = new ToolStripTextBox { Width = 130, ToolTipText = "多个用空格分隔，短语用双引号包裹" };
        _tbTag.TextChanged += (_, _) => { SyncToolbarToPanel(_tbTag, _edTag); _cbTagOp.SelectedItem = "or"; };
        _tbTag.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) ApplyFilter(); };

        _tbMsg = new ToolStripTextBox { Width = 180, ToolTipText = "多词用空格分隔，短语用双引号包裹" };
        _tbMsg.TextChanged += (_, _) => { SyncToolbarToPanel(_tbMsg, _edMsg); _cbMsgOp.SelectedItem = "or"; };
        _tbMsg.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) ApplyFilter(); };

        _toolStrip2.Items.AddRange(new ToolStripItem[] {
            new ToolStripLabel("标记:"),
            new ToolStripButton("◀ 上一个", null, (_, _) => GotoMark(true)),
            new ToolStripButton("下一个 ▶", null, (_, _) => GotoMark(false)),
            new ToolStripButton("清除标记", null, (_, _) => ClearMarks()),
            new ToolStripSeparator(),
            new ToolStripControlHost(_chkToolbarMarkedOnly),
            new ToolStripControlHost(_chkToolbarFollow),
            new ToolStripSeparator(),
            new ToolStripLabel("分钟"),
            _tbMin,
            new ToolStripLabel("Tag"),
            _tbTag,
            new ToolStripLabel("Message"),
            _tbMsg,
        });
        Controls.Add(_toolStrip2);

        // ── 过滤面板 ──
        BuildFilterPanel();

        // ── 列表视图 ──
        _listView = new LogListView
        {
            Dock = DockStyle.Fill,
        };
        _listView.DoubleClick += OnDoubleClick;
        _listView.KeyDown += OnListViewKeyDown;
        _listView.MouseClick += OnListViewMouseClick;
        _listView.VirtualItemsSelectionRangeChanged += (_, e) => { };
        _listView.Scrolled += (_, _) => OnScrolled();

        // ── 状态栏 ──
        _statusStrip = new StatusStrip();
        _lblFile = new ToolStripStatusLabel("未打开文件 —— 可直接把日志文件拖进窗口") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _lblStat = new ToolStripStatusLabel("");
        _lblPos = new ToolStripStatusLabel("");
        _lblMsg = new ToolStripStatusLabel("");
        _pbar = new ToolStripProgressBar { Visible = false, AutoSize = false, Size = new Size(180, 16) };
        _statusStrip.Items.AddRange(new ToolStripItem[] { _lblFile, _lblStat, _lblPos, _lblMsg, _pbar });
        Controls.Add(_statusStrip);

        // ── 布局 ──
        // 过滤面板移到独立的非模态窗口（FilterDialog），主区域只保留日志列表填满
        _filterDialog = new FilterDialog(_panel, () => ApplyFilter());
        Controls.Add(_listView);
        Load += (_, _) =>
        {
            ApplyWindowGeometry();
            LayoutDeviceCombo();
        };
        // 停靠布局按 Z 序从后往前占位：主工具栏最先占 Top，第二行工具条占其下，状态栏占 Bottom，
        // 列表（Fill）放 Z 序最前、最后布局拿剩余空间——否则工具条会叠在列表上，把表头行（Time/Tag/Message 列头）盖住
        _toolStrip.SendToBack();
        _listView.BringToFront();

        // ── 定时器 ──
        _autoTimer.Tick += (_, _) => { _autoTimer.Stop(); ApplyFilter(); };
        _flushTimer.Tick += (_, _) => FlushModelRows();
        _adbReloadTimer.Tick += (_, _) => AdbAutoReload();
        _adbStatTimer.Tick += (_, _) => AdbUpdateStat();

        // ── 快捷键 ──
        KeyDown += OnKeyDown;
    }

    // 设备下拉框占满工具栏剩余宽度
    void LayoutDeviceCombo()
    {
        try
        {
            if (_toolStrip == null || _comboDevice == null) return;
            int others = 0;
            foreach (ToolStripItem it in _toolStrip.Items)
            {
                if (it == _comboDevice || !it.Visible) continue;
                others += it.Width + it.Margin.Horizontal;
            }
            int w = _toolStrip.Width - others - _comboDevice.Margin.Horizontal - 12;
            if (w < 150) w = 150;
            if (_comboDevice.Width != w) _comboDevice.Width = w;
        }
        catch { }
    }

    void ShowAbout() => MessageBox.Show(this,
        $"{AppInfo.ProductName} {AppInfo.DisplayVersion}\n\n" +
        "Windows 桌面端 Android 日志（logcat）查看器\n\n" +
        "许可：Apache License 2.0（详见 LICENSE）\n" +
        "第三方声明：见程序目录下 THIRD-PARTY-NOTICES.md\n" +
        "Copyright 2026 logcat viewer contributors",
        "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);

    void BuildFilterPanel()
    {
        _panel = new GroupBox { Text = "过滤", Dock = DockStyle.Fill };
        var mainLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            AutoScroll = false
        };
        mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++)
            mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // 级别行
        var lvlPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        lvlPanel.Controls.Add(new Label { Text = "级别", AutoSize = true, Padding = new Padding(0, 4, 8, 0) });
        for (int i = 0; i < 8; i++)
        {
            int code = i;
            _lvlBoxes[i] = new CheckBox
            {
                Text = i < LogParser.LEVEL_NAME.Length ? LogParser.LEVEL_NAME[i] : "?",
                AutoSize = true,
                // 默认全选：V/D/I/W/E/F/A（不含 UNKNOWN=0）
                Checked = i > 0
            };
            _lvlBoxes[i].CheckedChanged += (_, _) => OnFilterChanged();
            if (i > 0) lvlPanel.Controls.Add(_lvlBoxes[i]);
        }
        var btnAll = new Button { Text = "全选", Width = 50, Height = 25 };
        btnAll.Click += (_, _) => { for (int i = 1; i < 8; i++) _lvlBoxes[i].Checked = true; };
        var btnNone = new Button { Text = "清空", Width = 50, Height = 25 };
        btnNone.Click += (_, _) => { for (int i = 1; i < 8; i++) _lvlBoxes[i].Checked = false; };
        lvlPanel.Controls.Add(btnAll);
        lvlPanel.Controls.Add(btnNone);

        // 应用/重置 与选项合并到级别行
        var btnApply = new Button { Text = "应用  (Ctrl+Enter)", Width = 140, Height = 25 };
        btnApply.Click += (_, _) => ApplyFilter();
        var btnReset = new Button { Text = "重置", Width = 60, Height = 25 };
        btnReset.Click += (_, _) => ResetFilter();
        _chkAuto = new CheckBox { Text = "自动应用", AutoSize = true, Checked = true };
        lvlPanel.Controls.Add(new Label { Text = "    ", AutoSize = true });
        lvlPanel.Controls.Add(btnApply);
        lvlPanel.Controls.Add(btnReset);
        lvlPanel.Controls.Add(_chkAuto);
        mainLayout.Controls.Add(lvlPanel, 0, 0);

        // Tag 行（含收藏）
        var tagPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        BuildTermControls(tagPanel, "Tag", out _edTag, out _cbTagOp, out _ckTagRe, out _ckTagCase, out _ckTagEx,
            "多个用空格分隔，短语用双引号包裹", "or", forTag: true, syncBox: _tbTag);
        mainLayout.Controls.Add(tagPanel, 0, 1);

        // Message 行（含收藏）
        var msgPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        BuildTermControls(msgPanel, "Message", out _edMsg, out _cbMsgOp, out _ckMsgRe, out _ckMsgCase, out _ckMsgEx,
            "多词用空格分隔，短语用双引号包裹", "and", forTag: false, syncBox: _tbMsg);
        mainLayout.Controls.Add(msgPanel, 0, 2);

        // PID / TID / 分钟 行（含过滤条件说明）
        var pidTidPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        pidTidPanel.Controls.Add(new Label { Text = "PID", AutoSize = true, Padding = new Padding(0, 4, 4, 0) });
        _edPid = new TextBox { Width = 130, PlaceholderText = "多值用空格分隔" };
        _edPid.TextChanged += (_, _) => OnFilterChanged();
        _edPid.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) ApplyFilter(); };
        pidTidPanel.Controls.Add(_edPid);
        _ckPidEx = new CheckBox { Text = "排除", AutoSize = true };
        _ckPidEx.CheckedChanged += (_, _) => OnFilterChanged();
        pidTidPanel.Controls.Add(_ckPidEx);
        pidTidPanel.Controls.Add(new Label { Text = "TID", AutoSize = true, Padding = new Padding(8, 4, 4, 0) });
        _edTid = new TextBox { Width = 130, PlaceholderText = "多值用空格分隔" };
        _edTid.TextChanged += (_, _) => OnFilterChanged();
        _edTid.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) ApplyFilter(); };
        pidTidPanel.Controls.Add(_edTid);
        _ckTidEx = new CheckBox { Text = "排除", AutoSize = true };
        _ckTidEx.CheckedChanged += (_, _) => OnFilterChanged();
        pidTidPanel.Controls.Add(_ckTidEx);
        pidTidPanel.Controls.Add(new Label { Text = "分钟", AutoSize = true, Padding = new Padding(8, 4, 4, 0) });
        _edMin = new TextBox { Width = 160, PlaceholderText = "如 05 20（空格分隔）" };
        _edMin.TextChanged += (_, _) => { SyncPanelToToolbar(_edMin, _tbMin); OnFilterChanged(); };
        _edMin.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) ApplyFilter(); };
        pidTidPanel.Controls.Add(_edMin);
        _lblSpec = new Label { Text = "（无过滤）", AutoSize = true, Padding = new Padding(8, 6, 0, 0), ForeColor = Color.Gray };
        pidTidPanel.Controls.Add(_lblSpec);
        mainLayout.Controls.Add(pidTidPanel, 0, 3);

        _panel.Controls.Add(mainLayout);
    }

    // ── 工具栏与过滤窗口输入框双向同步 ──
    void SyncToolbarToPanel(ToolStripTextBox src, TextBox dst)
    {
        if (_syncingFilter) return;
        _syncingFilter = true;
        try { dst.Text = src.Text; }
        finally { _syncingFilter = false; }
        OnFilterChanged();
    }

    void SyncPanelToToolbar(TextBox src, ToolStripTextBox dst)
    {
        if (_syncingFilter) return;
        _syncingFilter = true;
        try { dst.Text = src.Text; }
        finally { _syncingFilter = false; }
    }

    void BuildTermControls(FlowLayoutPanel panel, string label, out TextBox ed, out ComboBox cbOp,
        out CheckBox ckRe, out CheckBox ckCase, out CheckBox ckEx,
        string placeholder, string defaultOp, bool forTag, ToolStripTextBox? syncBox = null)
    {
        panel.Controls.Add(new Label { Text = label, AutoSize = true, Width = 62, Padding = new Padding(0, 4, 0, 0) });
        ed = new TextBox { Width = 280 };
        ed.PlaceholderText = placeholder;
        var box = ed;
        box.TextChanged += (_, _) => { if (syncBox != null) SyncPanelToToolbar(box, syncBox); OnFilterChanged(); };
        box.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) ApplyFilter(); };
        panel.Controls.Add(ed);

        // 收藏：▾ 从收藏选择，★ 收藏/移除当前内容
        var favTip = new ToolTip();
        var btnFavPick = new Button { Text = "▾", Width = 28, Height = 25, Margin = new Padding(2, 0, 0, 0) };
        favTip.SetToolTip(btnFavPick, "从收藏中选择");
        btnFavPick.Click += (_, _) => ShowFilterFavMenu(btnFavPick, box, forTag);
        panel.Controls.Add(btnFavPick);
        var btnFavToggle = new Button { Text = "★", Width = 28, Height = 25, Margin = new Padding(2, 0, 0, 0) };
        favTip.SetToolTip(btnFavToggle, "收藏当前内容（已收藏则移除）");
        btnFavToggle.Click += (_, _) => ToggleFilterFav(box, forTag);
        panel.Controls.Add(btnFavToggle);

        cbOp = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 55 };
        cbOp.Items.AddRange(new[] { "or", "and" });
        cbOp.SelectedItem = defaultOp;
        cbOp.SelectedIndexChanged += (_, _) => OnFilterChanged();
        panel.Controls.Add(cbOp);
        ckRe = new CheckBox { Text = "正则", AutoSize = true };
        ckRe.CheckedChanged += (_, _) => OnFilterChanged();
        panel.Controls.Add(ckRe);
        ckCase = new CheckBox { Text = "大小写", AutoSize = true };
        ckCase.CheckedChanged += (_, _) => OnFilterChanged();
        panel.Controls.Add(ckCase);
        ckEx = new CheckBox { Text = "排除", AutoSize = true };
        ckEx.CheckedChanged += (_, _) => OnFilterChanged();
        panel.Controls.Add(ckEx);
    }

    // ── 过滤条件收藏（tag / message）──
    static List<string> FilterFavList(bool forTag) =>
        forTag ? FavoritesStore.Default.TagFilters : FavoritesStore.Default.MsgFilters;

    void ShowFilterFavMenu(Button anchor, TextBox ed, bool forTag)
    {
        var list = FilterFavList(forTag);
        var menu = new ContextMenuStrip();
        if (list.Count == 0)
        {
            menu.Items.Add(new ToolStripMenuItem("（暂无收藏，点 ★ 收藏当前内容）") { Enabled = false });
        }
        else
        {
            foreach (var item in list)
            {
                var text = item;
                var mi = new ToolStripMenuItem(text);
                mi.Click += (_, _) =>
                {
                    ed.Text = text;
                    ed.SelectionStart = text.Length;
                    ApplyFilter();
                };
                menu.Items.Add(mi);
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("清空收藏", null, (_, _) =>
            {
                FilterFavList(forTag).Clear();
                FavoritesStore.Default.Save();
                ShowStatus("已清空收藏");
            });
        }
        menu.Show(anchor, new Point(0, anchor.Height));
    }

    void ToggleFilterFav(TextBox ed, bool forTag)
    {
        var fav = FavoritesStore.Default;
        var text = ed.Text.Trim();
        if (text.Length == 0) { ShowStatus("内容为空，无法收藏"); return; }
        bool removed = forTag ? fav.RemoveTagFilter(text) : fav.RemoveMsgFilter(text);
        if (removed)
        {
            fav.Save();
            ShowStatus($"已移除收藏：{text}");
        }
        else
        {
            if (forTag) fav.AddTagFilter(text); else fav.AddMsgFilter(text);
            fav.Save();
            ShowStatus($"已收藏：{text}");
        }
    }

    // ── 过滤变更 ──
    void OnFilterChanged()
    {
        if (_chkAuto.Checked)
            _autoTimer.Start();
    }

    // ── 打开文件 ──
    public void OpenFile(string? path = null)
    {
        if (string.IsNullOrEmpty(path))
        {
            using var dlg = new OpenFileDialog
            {
                Title = "打开日志文件",
                Filter = "日志文件 (*.log *.txt)|*.log;*.txt|所有文件 (*.*)|*.*"
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            path = dlg.FileName;
        }
        StartIndex(path);
    }

    async void StartIndex(string path)
    {
        StopWorker();
        _doc?.Close();
        _doc = null;
        _marked.Clear();
        _listView.SetDocument(null);
        Text = $"{AppInfo.Title} — {System.IO.Path.GetFileName(path)}";
        _lblFile.Text = path;
        _lblStat.Text = "";

        var cts = new CancellationTokenSource();
        _ctsWorker = cts;
        int gen = ++_workerGen;
        _pbar.Visible = true; _pbar.Value = 0;
        _lblMsg.Text = "建立索引…";
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var doc = await LogDocument.BuildAsync(path, _join,
                new Progress<(double pct, string msg)>(p =>
                {
                    if (_workerGen == gen)
                    {
                        _pbar.Value = (int)(p.pct * 100);
                        _lblMsg.Text = p.msg;
                    }
                }), cts.Token);

            if (_workerGen != gen) return;
            sw.Stop();
            _indexMs = sw.Elapsed.TotalMilliseconds;
            _doc = doc;
            _listView.SetDocument(doc);
            _lblFile.Text = $"{doc.Path}   ({HumanSize(doc.Size)})";
            await ApplyFilter();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowError($"建立索引失败：{ex.Message}");
        }
        finally
        {
            if (_workerGen == gen)
            {
                _pbar.Visible = false;
                _lblMsg.Text = "";
            }
        }
    }

    // ── 重载 ──
    async void Reload()
    {
        if (_doc == null) { OpenFile(); return; }
        var cts = new CancellationTokenSource();
        _ctsWorker = cts;
        int gen = ++_workerGen;
        _pbar.Visible = true;
        _lblMsg.Text = "检查更新…";

        try
        {
            string kind = await Task.Run(() => _doc.Reload(join: _join,
                progress: new Progress<(double, string)>(p =>
                {
                    if (_workerGen == gen) { _pbar.Value = (int)(p.Item1 * 100); _lblMsg.Text = p.Item2; }
                }), ct: cts.Token), cts.Token);

            if (_workerGen != gen) return;
            if (kind == "unchanged") { ShowStatus("文件无变化"); return; }
            var snap = _listView.CaptureView();
            _listView.SetDocument(_doc);
            await ApplyFilter(snap);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError($"重载失败：{ex.Message}"); }
        finally { if (_workerGen == gen) { _pbar.Visible = false; _lblMsg.Text = ""; } }
    }

    // ── 过滤 ──
    // keep：刷新前的视图快照；quiet：静默刷新（实时采集增量同步），不显示进度条、不重置文档，避免闪屏
    async Task ApplyFilter(LogListView.ViewSnapshot? keep = null, bool quiet = false)
    {
        if (_doc == null) return;
        _autoTimer.Stop();

        FilterSpec spec;
        try { spec = CollectSpec(); }
        catch (Exception ex) { ShowError(ex.Message); return; }
        _spec = spec;
        _lblSpec.Text = spec.Describe();

        _ctsWorker?.Cancel();
        var cts = new CancellationTokenSource();
        _ctsWorker = cts;
        int gen = ++_workerGen;
        if (!quiet) { _pbar.Visible = true; _pbar.Value = 0; _lblMsg.Text = "过滤…"; }
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 普通（非重载/非静默）过滤：捕获选中行锚点，过滤后固定回原屏幕位置
        (int anchorDocRow, int anchorOffset) = keep.HasValue ? (-1, 0) : _listView.CaptureAnchor();

        try
        {
            var rows = await FilterEngine.ApplyFilterAsync(_doc, spec, _marked,
                quiet ? null : new Progress<(double, string)>(p =>
                {
                    if (_workerGen == gen) { _pbar.Value = (int)(p.Item1 * 100); _lblMsg.Text = p.Item2; }
                }), cts.Token);

            if (_workerGen != gen) return;
            sw.Stop();
            _filterMs = sw.Elapsed.TotalMilliseconds;
            _listView.SetRows(rows);
            UpdateStat();
            if (_chkToolbarFollow.Checked)
            {
                ScrollBottom();
                if (keep.HasValue) _listView.RestoreView(keep.Value, restoreTop: false);
            }
            else if (keep.HasValue)
            {
                _listView.RestoreView(keep.Value);
            }
            else if (anchorDocRow >= 0)
            {
                // 把选中行（或最近存活行）固定回原屏幕位置
                _listView.PinAnchor(anchorDocRow, anchorOffset);
                _lastVsb = TopRow();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError($"过滤失败：{ex.Message}"); }
        finally
        {
            if (_workerGen == gen && !quiet) { _pbar.Visible = false; _lblMsg.Text = ""; }
        }
    }

    FilterSpec CollectSpec()
    {
        var spec = new FilterSpec();
        spec.Levels = Enumerable.Range(1, 7).Where(i => _lvlBoxes[i].Checked).ToArray();
        spec.Tags = FilterEngine.SplitTerms(_edTag.Text, _cbTagOp.SelectedItem?.ToString() ?? "or").ToArray();
        spec.TagOp = _cbTagOp.SelectedItem?.ToString() ?? "or";
        spec.TagRegex = _ckTagRe.Checked;
        spec.TagCase = _ckTagCase.Checked;
        spec.TagExclude = _ckTagEx.Checked;
        spec.Msg = FilterEngine.SplitTerms(_edMsg.Text, _cbMsgOp.SelectedItem?.ToString() ?? "and").ToArray();
        spec.MsgOp = _cbMsgOp.SelectedItem?.ToString() ?? "and";
        spec.MsgRegex = _ckMsgRe.Checked;
        spec.MsgCase = _ckMsgCase.Checked;
        spec.MsgExclude = _ckMsgEx.Checked;
        spec.Pids = FilterEngine.ParseInts(_edPid.Text, "PID");
        spec.Tids = FilterEngine.ParseInts(_edTid.Text, "TID");
        spec.PidExclude = _ckPidEx.Checked;
        spec.TidExclude = _ckTidEx.Checked;
        spec.Minutes = FilterEngine.ParseMinutes(_edMin.Text);
        spec.MarkedOnly = _chkToolbarMarkedOnly.Checked;
        return spec;
    }

    void ResetFilter()
    {
        _autoTimer.Stop();
        for (int i = 1; i < 8; i++) _lvlBoxes[i].Checked = true;
        _edTag.Clear(); _edMsg.Clear(); _edPid.Clear(); _edTid.Clear(); _edMin.Clear();
        _ckTagRe.Checked = _ckTagCase.Checked = _ckTagEx.Checked = false;
        _ckMsgRe.Checked = _ckMsgCase.Checked = _ckMsgEx.Checked = false;
        _ckPidEx.Checked = _ckTidEx.Checked = false;
        _chkToolbarMarkedOnly.Checked = _chkToolbarFollow.Checked = false;
        ApplyFilter();
    }

    void StopWorker()
    {
        _ctsWorker?.Cancel();
        _ctsWorker = null;
        _workerGen++;
        _pbar.Visible = false;
        _lblMsg.Text = "";
    }

    // ── 标记 ──
    void ToggleMark(int docRow)
    {
        _listView.ToggleMark(docRow);
        _marked = _listView.Marked;
        UpdateStat();
        _listView.Invalidate();
    }

    void ClearMarks()
    {
        _listView.ClearMarks();
        _marked = _listView.Marked;
        UpdateStat();
        _listView.Invalidate();
        ShowStatus("已清除全部标记");
    }

    void GotoMark(bool back)
    {
        var rows = _listView.Rows;
        if (rows.Length == 0) return;
        var markedModelRows = _marked
            .Select(dr => _listView.ModelRowOf(dr))
            .Where(mr => mr >= 0)
            .OrderBy(mr => mr)
            .ToArray();
        if (markedModelRows.Length == 0) { ShowStatus("当前结果中没有标记行"); return; }

        int cur = _listView.SelectedIndices.Count > 0 ? _listView.SelectedIndices[0] : -1;
        if (cur < 0) cur = TopRow();
        if (cur < 0) cur = 0;

        int target;
        if (back)
        {
            var prev = markedModelRows.Where(m => m < cur).ToArray();
            target = prev.Length > 0 ? prev[^1] : markedModelRows[^1];
        }
        else
        {
            var next = markedModelRows.Where(m => m > cur).ToArray();
            target = next.Length > 0 ? next[0] : markedModelRows[0];
        }
        SelectRow(target);
    }

    // ── 导出 ──
    async void ExportRows(bool markedOnly)
    {
        if (_doc == null) return;
        int[] rows;
        string hint;
        if (markedOnly)
        {
            rows = _marked.OrderBy(x => x).ToArray();
            hint = "marked";
        }
        else
        {
            rows = _listView.Rows;
            hint = "filtered";
        }
        if (rows.Length == 0) { ShowError("没有可导出的行"); return; }

        string baseName = System.IO.Path.GetFileNameWithoutExtension(_doc.Path);
        using var dlg = new SaveFileDialog
        {
            Title = "导出",
            FileName = $"{baseName}_{hint}.log",
            Filter = "日志文件 (*.log *.txt)|*.log;*.txt|所有文件 (*.*)|*.*"
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        _pbar.Visible = true;
        try
        {
            int n = await FilterEngine.ExportRowsAsync(_doc, rows, dlg.FileName, _singleLineExport,
                new Progress<(double, string)>(p => { _pbar.Value = (int)(p.Item1 * 100); _lblMsg.Text = p.Item2; }));
            ShowStatus($"已导出 {n:N0} 行 → {dlg.FileName}");
        }
        catch (Exception ex) { ShowError($"导出失败：{ex.Message}"); }
        finally { _pbar.Visible = false; _lblMsg.Text = ""; }
    }

    // ── 交互 ──
    void OnDoubleClick(object? sender, EventArgs e)
    {
        if (_listView.SelectedIndices.Count == 0) return;
        int mr = _listView.SelectedIndices[0];
        int dr = _listView.DocRow(mr);
        if (dr < 0) return;

        // 双击行号列 = 切换标记
        var hit = _listView.HitTest(_listView.PointToClient(MousePosition));
        if (hit.Item != null && hit.SubItem != null && hit.Item.SubItems.IndexOf(hit.SubItem) == 0)
        {
            ToggleMark(dr);
            return;
        }
        ShowDetail(dr);
    }

    void ShowDetail(int docRow)
    {
        if (docRow < 0 || _doc == null) return;
        new RecordDialog(_listView.FullText(docRow), $"记录 #{docRow + 1}").ShowDialog(this);
    }

    void OnListViewKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.M:
                MarkCurrent();
                e.Handled = true;
                break;
            case Keys.F3:
                FindStep(e.Shift);
                e.Handled = true;
                break;
        }
    }

    void OnListViewMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            var hit = _listView.HitTest(e.Location);
            if (hit.Item != null)
            {
                int mr = hit.Item.Index;
                int dr = _listView.DocRow(mr);
                if (dr >= 0) ShowRowMenu(dr, e.Location);
            }
        }
    }

    void ShowRowMenu(int docRow, Point location)
    {
        var menu = new ContextMenuStrip();
        var selected = _listView.SelectedIndices.Cast<int>().Select(i => _listView.DocRow(i)).Where(d => d >= 0).ToArray();
        if (selected.Length == 0) selected = new[] { docRow };

        menu.Items.Add($"复制原始文本（{selected.Length} 行）", null, (_, _) => CopyRows(selected));
        menu.Items.Add("复制为表格行", null, (_, _) => CopyRowsFormatted(selected));
        menu.Items.Add(new ToolStripSeparator());

        string tag = _doc?.TagOf(docRow) ?? "";
        if (!string.IsNullOrEmpty(tag))
            menu.Items.Add($"按此 tag 过滤：{tag}", null, (_, _) => { _edTag.Text = tag; ApplyFilter(); });

        if (_doc != null)
        {
            int pid = _doc.Pid[docRow], tid = _doc.Tid[docRow];
            if (pid >= 0) menu.Items.Add($"按此 PID 过滤：{pid}", null, (_, _) => { _edPid.Text = pid.ToString(); ApplyFilter(); });
            if (tid >= 0) menu.Items.Add($"按此 TID 过滤：{tid}", null, (_, _) => { _edTid.Text = tid.ToString(); ApplyFilter(); });

            long ts = _doc.Ts[docRow];
            if (ts >= 0)
            {
                int minute = (int)((ts / 60000) % 60);
                menu.Items.Add($"按此分钟过滤：{minute:D2}", null, (_, _) => { _edMin.Text = minute.ToString(); ApplyFilter(); });
            }
        }

        menu.Items.Add(new ToolStripSeparator());
        bool isMarked = _marked.Contains(docRow);
        menu.Items.Add(isMarked ? "取消标记" : "标记（M）", null, (_, _) => ToggleMark(docRow));
        menu.Items.Add("查看完整记录", null, (_, _) => ShowDetail(docRow));

        menu.Show(_listView, location);
    }

    void MarkCurrent()
    {
        if (_listView.Focused) return; // 在输入框中不处理
        int mr = _listView.SelectedIndices.Count > 0 ? _listView.SelectedIndices[0] : TopRow();
        int dr = _listView.DocRow(mr);
        if (dr >= 0) ToggleMark(dr);
    }

    void CopyRows(int[] docRows)
    {
        if (docRows.Length == 0) return;
        var text = string.Join("\n", docRows.Select(dr => _listView.FullText(dr)));
        ShowStatus(ClipboardHelper.SetText(text) ? $"已复制 {docRows.Length} 行" : "复制失败：剪贴板被其他程序占用");
    }

    void CopyRowsFormatted(int[] modelRows)
    {
        if (modelRows.Length == 0) return;
        var lines = new List<string>();
        foreach (var dr in modelRows)
        {
            int mr = _listView.ModelRowOf(dr);
            if (mr < 0) continue;
            var item = _listView.Items[mr];
            var cells = new List<string> { item.Text };
            for (int c = 0; c < item.SubItems.Count; c++)
                cells.Add(item.SubItems[c].Text);
            lines.Add(string.Join("\t", cells));
        }
        ClipboardHelper.SetText(string.Join("\n", lines));
        ShowStatus($"已复制 {modelRows.Length} 行");
    }

    void FindStep(bool back)
    {
        if (_doc == null || _listView.Rows.Length == 0) return;
        if (_ckMsgRe.Checked) { ShowStatus("正则模式下请用过滤，F3 只支持普通文本"); return; }

        var terms = FilterEngine.SplitTerms(_edMsg.Text, _cbMsgOp.SelectedItem?.ToString() ?? "and");
        if (terms.Count == 0) { ShowStatus("请先在 Message 框里填写检索词"); return; }

        bool opAnd = (_cbMsgOp.SelectedItem?.ToString() ?? "and") == "and";
        var keys = terms.Select(t => t.ToLowerInvariant()).ToArray();
        int total = _listView.Rows.Length;
        int cur = _listView.SelectedIndices.Count > 0 ? _listView.SelectedIndices[0] : -1;
        if (cur < 0) cur = back ? total - 1 : TopRow();
        if (cur < 0) cur = 0;

        int[] indices;
        if (back)
            indices = Enumerable.Range(0, cur).Reverse().Concat(Enumerable.Range(cur, total - cur).Reverse()).ToArray();
        else
            indices = Enumerable.Range(cur + 1, total - cur - 1).Concat(Enumerable.Range(0, cur)).ToArray();

        int limit = 300_000;
        for (int i = 0; i < indices.Length && i < limit; i++)
        {
            int mr = indices[i];
            int dr = _listView.DocRow(mr);
            if (dr < 0) continue;
            string msg = _doc.Decode(_doc.MessageBytes(dr)).ToLowerInvariant();
            bool ok = opAnd ? keys.All(k => msg.Contains(k)) : keys.Any(k => msg.Contains(k));
            if (ok)
            {
                SelectRow(mr);
                ShowStatus($"命中第 {mr + 1:N0} 行");
                return;
            }
        }
        ShowStatus("没有更多匹配");
    }

    // ── 导航 ──
    int TopRow()
    {
        if (_listView.TopItem != null) return _listView.TopItem.Index;
        return -1;
    }

    void SelectRow(int modelRow, bool center = true)
    {
        if (modelRow < 0 || modelRow >= _listView.Rows.Length) return;
        _listView.SelectedIndices.Clear();
        _listView.SelectedIndices.Add(modelRow);
        _listView.EnsureVisible(modelRow);
    }

    void ScrollBottom()
    {
        if (_listView.Rows.Length == 0) return;
        _programScroll = true;
        try { _listView.EnsureVisible(_listView.Rows.Length - 1); }
        finally { _programScroll = false; }
        _lastVsb = _listView.TopItem?.Index ?? -1;
    }

    void OnScrolled()
    {
        int top = TopRow();
        if (_chkToolbarFollow.Checked && !_programScroll && top < _lastVsb - 1)
        {
            _chkToolbarFollow.Checked = false;
        }
        _lastVsb = top;
        // 更新位置显示
        if (top >= 0 && _doc != null)
        {
            int dr = _listView.DocRow(top);
            _lblPos.Text = $"视口 {top + 1:N0}/{_listView.Rows.Length:N0} (记录 #{dr + 1})";
        }
    }

    void UpdateStat()
    {
        int total = _doc?.RowCount ?? 0;
        _lblStat.Text = $"总 {total:N0} 条 | 命中 {_listView.Rows.Length:N0} | 标记 {_marked.Count:N0} | 索引 {_indexMs:F0} ms | 过滤 {_filterMs:F0} ms";
    }

    void FlushModelRows()
    {
        // 增量追加的 flush 逻辑（实时采集用）
        UpdateStat();
    }

    void ApplyFont(int pt)
    {
        _fontPt = pt;
        _listView.SetFont(pt);
    }

    // ── 快捷键 ──
    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.Shift && e.KeyCode == Keys.C) { AdbCommandWindow(); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.Enter) { ApplyFilter(); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.O) { OpenFile(); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.E) { ExportRows(false); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.F) { ShowFilterDialog(); _tbMsg.Focus(); _tbMsg.SelectAll(); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.C) { CopySelection(); e.Handled = true; }
        else if (e.KeyCode == Keys.F5) { Reload(); e.Handled = true; }
        else if (e.KeyCode == Keys.F2) { GotoMark(e.Shift); e.Handled = true; }
        else if (e.KeyCode == Keys.F3) { FindStep(e.Shift); e.Handled = true; }
        else if (e.KeyCode == Keys.Escape) { StopWorker(); _listView.SelectedIndices.Clear(); e.Handled = true; }
        else if (e.KeyCode == Keys.Enter && _listView.Focused)
        {
            int mr = _listView.SelectedIndices.Count > 0 ? _listView.SelectedIndices[0] : -1;
            if (mr >= 0) ShowDetail(_listView.DocRow(mr));
            e.Handled = true;
        }
    }

    void CopySelection()
    {
        if (_listView.Focused)
        {
            var selected = _listView.SelectedIndices.Cast<int>().Select(i => _listView.DocRow(i)).Where(d => d >= 0).ToArray();
            if (selected.Length > 0) CopyRows(selected);
        }
    }

    // ── 拖拽 ──
    protected override void OnDragEnter(DragEventArgs drgevent)
    {
        if (drgevent.Data?.GetDataPresent(DataFormats.FileDrop) == true)
            drgevent.Effect = DragDropEffects.Copy;
        else
            drgevent.Effect = DragDropEffects.None;
    }

    protected override void OnDragDrop(DragEventArgs drgevent)
    {
        if (drgevent.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            OpenFile(files[0]);
    }

    // ── 关闭 ──
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        StopWorker();
        if (_adbCapturing) AdbStopCapture();
        _adbManager?.Dispose();
        SaveSettings();
        _doc?.Close();
        base.OnFormClosing(e);
    }

    // ── 设置 ──
    void LoadSettings()
    {
        StartupLog.SessionStart("logcat");
        var screens = Screen.AllScreens;
        StartupLog.Write($"检测到屏幕数={screens.Length}");
        for (int i = 0; i < screens.Length; i++)
            StartupLog.Write($"  屏[{i}] Primary={screens[i].Primary} Bounds={screens[i].Bounds} WorkingArea={screens[i].WorkingArea}");

        var s = logcat.Services.AppSettings.Default;
        try
        {
            _join = s.Join;
            _actJoin.Checked = _join;
            _singleLineExport = s.SingleLineExport;
            _actSingleExport.Checked = _singleLineExport;
            _chkAuto.Checked = s.AutoApply;
            _newlineVis = string.IsNullOrEmpty(s.NewlineVis) ? "↵" : s.NewlineVis;
            _listView.NewlineVis = _newlineVis;
            _fontPt = s.FontPt > 0 ? s.FontPt : 10;
            ApplyFont(_fontPt);

            // 窗口几何：先校验坐标是否落在当前连接的屏幕内，
            // 避免上次在副屏、这次该屏未连接时窗口跑到屏外
            if (s.WindowState is >= 0 and <= 2)
            {
                var bounds = new Rectangle(s.WindowLocation, s.WindowSize);
                var state = (FormWindowState)s.WindowState;
                bool onScreen = bounds.Width > 100 && bounds.Height > 100 &&
                                Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(bounds));
                StartupLog.Write($"读取 settings.json：Location={s.WindowLocation} Size={s.WindowSize} State={(int)state}");
                if (onScreen)
                {
                    _savedBounds = bounds;
                    // 最小化不持久化，还原为正常态，否则下次启动直接最小化
                    _savedState = state == FormWindowState.Minimized ? FormWindowState.Normal : state;
                    _hasSavedGeometry = true;
                    int hitIdx = -1;
                    for (int i = 0; i < screens.Length; i++)
                        if (screens[i].WorkingArea.IntersectsWith(bounds)) { hitIdx = i; break; }
                    StartupLog.Write($"校验通过：坐标落在屏[{hitIdx}]，将应用几何（状态={_savedState}）");
                }
                else
                {
                    StartupLog.Write("校验失败：坐标不在任何屏幕内，放弃保存几何，使用默认布局");
                }
            }
            else
            {
                StartupLog.Write("settings.json 无有效窗口状态，使用默认布局");
            }
        }
        catch { StartupLog.Write("LoadSettings 异常，使用默认布局"); /* 首次运行，使用默认值 */ }
    }

    // 在 Load 事件中调用：此时窗体句柄已就绪，最大化能可靠落到原屏
    void ApplyWindowGeometry()
    {
        if (!_hasSavedGeometry)
        {
            StartupLog.Write("ApplyWindowGeometry：无保存几何，保持默认布局");
            return;
        }
        StartupLog.Write("ApplyWindowGeometry：开始应用保存几何");
        StartPosition = FormStartPosition.Manual;
        Location = _savedBounds.Location;
        Size = _savedBounds.Size;
        if (_savedState == FormWindowState.Maximized)
            WindowState = FormWindowState.Maximized;

        // 推迟到布局完成后再读实际落点，确保取到最终位置/所在屏
        BeginInvoke((System.Action)(() =>
        {
            var cur = Screen.FromPoint(Location);
            int idx = System.Array.IndexOf(Screen.AllScreens, cur);
            StartupLog.Write($"应用后实际：Location={Location} Size={Size} State={WindowState} 所在屏[{idx}] Primary={cur.Primary}");
        }));
    }

    void SaveSettings()
    {
        var s = logcat.Services.AppSettings.Default;
        s.Join = _join;
        s.SingleLineExport = _singleLineExport;
        s.AutoApply = _chkAuto.Checked;
        s.NewlineVis = _newlineVis;
        s.FontPt = _fontPt;
        // 始终保存“正常态矩形”：RestoreBounds 在最大/最小化时给出还原后的位置，
        // 这样下次恢复最大化时能正确回到上次的屏幕
        // 正常态用当前 Bounds：RestoreBounds 内部取 rcNormalPosition，
        // 仅在窗口被最大/最小化过才由 Windows 填充；一直正常态时为未定义值（常返回 -1,-1），
        // 会导致位置丢失。故正常态必须用 this.Bounds，只有最大/最小化时才用 RestoreBounds。
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        s.WindowLocation = bounds.Location;
        s.WindowSize = bounds.Size;
        s.WindowState = (int)(WindowState == FormWindowState.Minimized ? FormWindowState.Normal : WindowState);
        StartupLog.Write($"SaveSettings：写入 Location={bounds.Location} Size={bounds.Size} State={s.WindowState}（关闭时 WindowState={WindowState}, 采用={(WindowState == FormWindowState.Normal ? "Bounds" : "RestoreBounds")}）");
        s.Save();
    }

    // ── ADB 初始化 ──

    async void InitAdb()
    {
        try
        {
            _adbManager = new AdbManager(_logger);
            _adbManager.DevicesChanged += (_, devices) => Invoke(() => OnDevicesChanged(devices));
            _adbManager.StartMonitor();
            await AdbRefresh();
            SetAdbButtons(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("ADB 初始化失败: {0}", ex.Message);
            _lblMsg.Text = "ADB 不可用";
        }
    }

    void SetAdbButtons(bool enabled)
    {
        bool hasDevice = _comboDevice.Items.Count > 0;
        _comboDevice.Enabled = enabled;
        foreach (ToolStripItem item in _toolStrip.Items)
        {
            if (item is not ToolStripButton btn) continue;
            if (item.Tag is "adb-free") btn.Enabled = enabled;
            else if (item.Tag is "adb") btn.Enabled = enabled && hasDevice;
        }
    }

    // ── ADB 设备管理 ──

    async Task AdbRefresh()
    {
        if (_adbManager == null) return;
        _lblMsg.Text = "刷新中…";
        try
        {
            var devices = await _adbManager.ListDevicesAsync();
            _adbDevices.Clear();
            _adbDevices.AddRange(devices);
            _comboDevice.Items.Clear();
            foreach (var d in devices)
                _comboDevice.Items.Add(d.ToString());
            if (devices.Count > 0)
            {
                _comboDevice.SelectedIndex = 0;
            }
            else
            {
                _lblMsg.Text = "无设备";
            }
            SetAdbButtons(true);
        }
        catch (Exception ex)
        {
            _lblMsg.Text = $"刷新失败: {ex.Message}";
        }
    }

    void OnDevicesChanged(List<DeviceInfo> devices)
    {
        _adbDevices.Clear();
        _adbDevices.AddRange(devices);
        _comboDevice.Items.Clear();
        foreach (var d in devices)
            _comboDevice.Items.Add(d.ToString());
        if (devices.Count > 0 && _comboDevice.SelectedIndex < 0)
            _comboDevice.SelectedIndex = 0;
        SetAdbButtons(true);
    }

    string? SelectedSerial()
    {
        int idx = _comboDevice.SelectedIndex;
        return idx >= 0 && idx < _adbDevices.Count ? _adbDevices[idx].Serial : null;
    }

    // ── ADB 采集 ──

    async void AdbStartCapture()
    {
        var serial = SelectedSerial();
        if (serial == null || _adbManager == null) return;

        _adbTempPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"logcat_live_{DateTime.Now:yyyyMMdd_HHmmss}.log");

        // 重置旧文档状态：采集时 AdbAutoReload 必须走首次加载，
        // 否则会对上一个打开的文件做增量 Reload，实时内容永远不更新
        StopWorker();
        _doc?.Close();
        _doc = null;
        _marked.Clear();
        _adbIndexing = false;
        _listView.SetDocument(null);

        _streamThread = new LogcatStream(_adbManager, serial, _adbTempPath, _logger);
        _streamThread.LinesReceived += lines =>
        {
            _adbLiveCount += lines.Count;
        };
        _streamThread.ErrorOccurred += msg => Invoke(() =>
        {
            _lblMsg.Text = msg;
        });
        _streamThread.Stopped += () => Invoke(() =>
        {
            _adbCapturing = false;
            _adbReloadTimer.Stop();
            _adbStatTimer.Stop();
            UpdateAdbCaptureUI();
        });

        _adbCapturing = true;
        _adbLiveCount = 0;
        _adbGen = -1; // 确保首次 AdbAutoReload 能通过 guard
        _chkToolbarFollow.Checked = true;
        UpdateAdbCaptureUI();

        _streamThread.Start(clearFirst: true);

        // 启动定时刷新和统计
        _adbReloadTimer.Start();
        _adbStatTimer.Start();

        // 首次加载
        await Task.Delay(500);
        AdbAutoReload();
    }

    async void AdbStopCapture()
    {
        if (_streamThread != null)
        {
            await _streamThread.StopAsync();
            _streamThread = null;
        }
        _adbCapturing = false;
        _adbReloadTimer.Stop();
        _adbStatTimer.Stop();
        UpdateAdbCaptureUI();
    }

    void UpdateAdbCaptureUI()
    {
        foreach (ToolStripItem item in _toolStrip.Items)
        {
            if (item.Tag is "adb" && item is ToolStripButton btn)
            {
                if (btn.Text.Contains("开始采集")) btn.Enabled = !_adbCapturing;
                if (btn.Text.Contains("停止") && !btn.Text.Contains("开始")) btn.Enabled = _adbCapturing;
            }
        }
        _actSaveLog.Enabled = !_adbCapturing && _adbTempPath != null;
    }

    void AdbSaveLog()
    {
        if (string.IsNullOrEmpty(_adbTempPath) || !File.Exists(_adbTempPath))
        {
            ShowError("没有可保存的日志");
            return;
        }
        using var dlg = new SaveFileDialog
        {
            Title = "保存日志",
            FileName = $"logcat_{DateTime.Now:yyyyMMdd_HHmmss}.log",
            Filter = "日志文件 (*.log *.txt)|*.log;*.txt|所有文件 (*.*)|*.*"
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        File.Copy(_adbTempPath, dlg.FileName, true);
        ShowStatus($"日志已保存 → {dlg.FileName}");
    }

    // ── ADB 增量同步 ──

    async void AdbAutoReload()
    {
        if (_adbCapturing && _adbTempPath != null && File.Exists(_adbTempPath) && !_adbReloading)
        {
            _adbReloading = true;
            try
            {
                if (_doc == null)
                {
                    // 首次加载
                    if (_workerGen == _adbGen) return;
                    var cts = new CancellationTokenSource();
                    _ctsWorker = cts;
                    int gen = ++_workerGen;
                    _adbGen = gen;
                    _adbIndexing = true;

                    var doc = await LogDocument.BuildAsync(_adbTempPath, _join,
                        new Progress<(double, string)>(), cts.Token);
                    if (_workerGen != gen) { _adbIndexing = false; return; }
                    _doc = doc;
                    _listView.SetDocument(doc);
                    _lblFile.Text = $"[实时采集] {_adbTempPath}";
                    _adbIndexing = false;
                    await ApplyFilter();
                }
                else
                {
                    // 增量追加
                    if (_adbIndexing) return;
                    var cts = new CancellationTokenSource();
                    int gen = ++_workerGen;
                    _adbGen = gen;
                    string kind = await Task.Run(() =>
                        _doc.Reload(join: _join,
                            progress: new Progress<(double, string)>(),
                            ct: cts.Token), cts.Token);
                    if (_workerGen != gen) return;
                    if (kind != "unchanged")
                    {
                        // 同一文档实例，无需 SetDocument 重置列表；静默刷新避免闪屏
                        var snap = _listView.CaptureView();
                        await ApplyFilter(snap, quiet: true);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("ADB 增量同步失败: {0}", ex.Message);
            }
            finally
            {
                _adbReloading = false;
            }
        }
    }

    void AdbUpdateStat()
    {
        if (_adbCapturing)
            _lblMsg.Text = $"采集中… {_adbLiveCount:N0} 行";
    }

    // ── 设备操作总窗口（截图 / 录屏 / 文件浏览 / 安装·卸载 APK / 命令 合并为一个页签式窗口）──

    bool SelectedIsRoot()
    {
        var serial = SelectedSerial();
        return serial != null && (_adbDevices.FirstOrDefault(d => d.Serial == serial)?.IsRoot ?? false);
    }

    void AdbCommandWindow() => ShowDeviceOps(DeviceOpsDialog.PageKind.Command);

    /// <summary>
    /// 打开（或唤到前台）设备操作窗口并切到对应页签。窗口单例、非模态；
    /// 命令页通过回调实时取主窗口选中设备，其余页签在换设备时由 OnDeviceChanged 重建。
    /// </summary>
    void ShowDeviceOps(DeviceOpsDialog.PageKind page)
    {
        if (_adbManager == null)
        {
            ShowError("ADB 不可用");
            return;
        }
        if (_deviceOps == null || _deviceOps.IsDisposed)
        {
            _deviceOps = new DeviceOpsDialog(_adbManager, SelectedSerial, SelectedIsRoot);
            _deviceOps.FormClosed += (_, _) => _deviceOps = null;
            _deviceOps.Show(this);
        }
        else
        {
            if (_deviceOps.WindowState == FormWindowState.Minimized)
                _deviceOps.WindowState = FormWindowState.Normal;
            _deviceOps.BringToFront();
            _deviceOps.Activate();
        }
        _deviceOps.OpenPage(page);
    }

    // ── 过滤设置窗口（非模态，悬浮在主窗口之上）──
    void ShowFilterDialog()
    {
        if (_filterDialog == null || _filterDialog.IsDisposed)
            _filterDialog = new FilterDialog(_panel, () => ApplyFilter());

        if (!_filterDialog.Visible)
        {
            _filterDialog.Show(this);
            _filterDialog.PositionAboveOwner();
        }
        if (_filterDialog.WindowState == FormWindowState.Minimized)
            _filterDialog.WindowState = FormWindowState.Normal;
        _filterDialog.BringToFront();
        _filterDialog.Activate();
    }

    // ── 辅助 ──
    void ShowStatus(string text) => _statusStrip.Items.OfType<ToolStripStatusLabel>().First(l => l == _lblMsg).Text = text;

    void ShowError(string text)
    {
        _lblMsg.Text = text;
        MessageBox.Show(this, text, "错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    static string HumanSize(long n)
    {
        double val = n;
        foreach (var unit in new[] { "B", "KB", "MB", "GB", "TB" })
        {
            if (val < 1024 || unit == "TB")
                return unit == "B" ? $"{val:F0} {unit}" : $"{val:F2} {unit}";
            val /= 1024;
        }
        return $"{val:F2} TB";
    }
}
