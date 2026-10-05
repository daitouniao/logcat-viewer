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
    // 最近一次全量过滤完成时的文档行数；实时采集增量过滤（FilterTail）从此行号起判定
    int _filterDocRows;
    bool _join = true;
    int _fontPt = 10;
    string _newlineVis = "↵";
    bool _singleLineExport;

    // ── 异步 ──
    CancellationTokenSource? _ctsWorker;
    int _workerGen;
    bool _programScroll;
    int _lastVsb = -1;

    // ── 文档互斥 ──
    // LogDocument 非线程安全：后台 Append/Reload（写）与 ApplyFilter/导出（读）必须串行，
    // 否则过滤会读到换数组瞬间的混合列存储。UI 渲染读不在锁内（由 MakeItem 的 try-catch 兜底）。
    readonly SemaphoreSlim _docBusy = new(1, 1);

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
    CheckBox _ckTagCase = null!, _ckTagEx = null!;
    CheckBox _ckMsgCase = null!, _ckMsgEx = null!;
    CheckBox _ckPidEx = null!, _ckTidEx = null!;
    CheckBox _chkAuto = null!;
    // 工具栏上的过滤控件（与过滤窗口双向同步）
    ToolStripTextBox _tbMin = null!, _tbTag = null!, _tbMsg = null!;
    // 工具栏 Tag/Message 收藏按钮
    ToolStripButton _btnFavTagMenu = null!, _btnFavTagToggle = null!;
    ToolStripButton _btnFavMsgMenu = null!, _btnFavMsgToggle = null!;
    CheckBox _chkToolbarMarkedOnly = null!, _chkToolbarFollow = null!;
    bool _syncingFilter;
    Label _lblSpec = null!;
    GroupBox _panel = null!;

    // 定时器
    readonly System.Windows.Forms.Timer _autoTimer = new() { Interval = 400 };
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
        var btnRefresh = new ToolStripButton("刷新设备", null, (_, _) => _ = AdbRefresh());
        // 刷新设备紧贴设备列表左侧；开始/停止采集在左侧分组
        ToolStripItem[] adbItems =
        {
            btnDeviceOps,
            new ToolStripButton("▶ 开始采集", null, (_, _) => AdbStartCapture()) { Enabled = false },
            new ToolStripButton("■ 停止采集", null, (_, _) => _ = AdbStopCaptureAsync()) { Enabled = false },
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
        _tbMin.TextChanged += (_, _) => { SyncToolbarToPanel(_tbMin, _edMin); RefreshFixedOrBoxStyle(); };
        _tbMin.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) _ = ApplyFilter(); };

        _tbTag = new ToolStripTextBox { Width = 130, ToolTipText = "多个用空格分隔，短语用双引号包裹" };
        // 工具栏输入永不改写面板 op：op 以面板控件为唯一来源，工具栏框只用背景色提示（ApplyTermBoxStyle）。
        // 曾经的 `_cbTagOp.SelectedItem = "or"` 是并列语句（_syncingFilter 只包住 dst.Text = src.Text），
        // 导致「在面板选 and → 再在工具栏改文本」op 被静默改回 or。
        _tbTag.TextChanged += (_, _) => SyncToolbarToPanel(_tbTag, _edTag);
        _tbTag.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) _ = ApplyFilter(); };

        _tbMsg = new ToolStripTextBox { Width = 180, ToolTipText = "多词用空格分隔，短语用双引号包裹" };
        _tbMsg.TextChanged += (_, _) => SyncToolbarToPanel(_tbMsg, _edMsg);
        _tbMsg.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) _ = ApplyFilter(); };

        // 工具栏 Tag 收藏按钮：▾ 下拉、★ 收藏/移除
        _btnFavTagMenu = new ToolStripButton("▾") { Width = 24, ToolTipText = "从收藏中选择（Tag）" };
        _btnFavTagMenu.Click += (_, _) => ShowFilterFavMenu(_btnFavTagMenu, _tbTag, forTag: true);
        _btnFavTagToggle = new ToolStripButton("☆") { Width = 24, ToolTipText = "收藏/移除当前内容（Tag）" };
        _btnFavTagToggle.Click += (_, _) => ToggleFilterFav(_tbTag, forTag: true);
        _btnFavTagToggle.Overflow = ToolStripItemOverflow.Never; // ★ 永不进 overflow

        // 工具栏 Message 收藏按钮：▾ 下拉、★ 收藏/移除
        _btnFavMsgMenu = new ToolStripButton("▾") { Width = 24, ToolTipText = "从收藏中选择（Message）" };
        _btnFavMsgMenu.Click += (_, _) => ShowFilterFavMenu(_btnFavMsgMenu, _tbMsg, forTag: false);
        _btnFavMsgToggle = new ToolStripButton("☆") { Width = 24, ToolTipText = "收藏/移除当前内容（Message）" };
        _btnFavMsgToggle.Click += (_, _) => ToggleFilterFav(_tbMsg, forTag: false);
        _btnFavMsgToggle.Overflow = ToolStripItemOverflow.Never; // ★ 永不进 overflow

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
            _btnFavTagMenu,
            _btnFavTagToggle,
            new ToolStripLabel("Message"),
            _tbMsg,
            _btnFavMsgMenu,
            _btnFavMsgToggle,
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
        _filterDialog = new FilterDialog(_panel, () => _ = ApplyFilter());
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
        _autoTimer.Tick += (_, _) => { _autoTimer.Stop(); _ = ApplyFilter(); };
        _adbReloadTimer.Tick += (_, _) => AdbAutoReload();
        _adbStatTimer.Tick += (_, _) => AdbUpdateStat();

        // ── 快捷键 ──
        KeyDown += OnKeyDown;

        // ── 收藏变更订阅 ──
        FavoritesStore.Default.Changed += (_, _) =>
        {
            if (IsHandleCreated) BeginInvoke(new Action(RefreshFavStars));
        };
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
        // AutoSize + MinimumSize：按钮宽度随文字自适应，任何 DPI/字体下都不会裁字
        var btnAll = new Button { Text = "全选", AutoSize = true, MinimumSize = new Size(50, 25) };
        btnAll.Click += (_, _) => { for (int i = 1; i < 8; i++) _lvlBoxes[i].Checked = true; };
        var btnNone = new Button { Text = "清空", AutoSize = true, MinimumSize = new Size(50, 25) };
        btnNone.Click += (_, _) => { for (int i = 1; i < 8; i++) _lvlBoxes[i].Checked = false; };
        lvlPanel.Controls.Add(btnAll);
        lvlPanel.Controls.Add(btnNone);

        // 应用/重置 与选项合并到级别行
        var btnApply = new Button { Text = "应用  (Ctrl+Enter)", AutoSize = true, MinimumSize = new Size(140, 25) };
        btnApply.Click += (_, _) => _ = ApplyFilter();
        var btnReset = new Button { Text = "重置", AutoSize = true, MinimumSize = new Size(60, 25) };
        btnReset.Click += (_, _) => ResetFilter();
        _chkAuto = new CheckBox { Text = "自动应用", AutoSize = true, Checked = true };
        lvlPanel.Controls.Add(new Label { Text = "    ", AutoSize = true });
        lvlPanel.Controls.Add(btnApply);
        lvlPanel.Controls.Add(btnReset);
        lvlPanel.Controls.Add(_chkAuto);
        mainLayout.Controls.Add(lvlPanel, 0, 0);

        // Tag 行（含收藏）
        var tagPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        BuildTermControls(tagPanel, "Tag", out _edTag, out _cbTagOp, out _ckTagCase, out _ckTagEx,
            "多个用空格分隔，短语用双引号包裹", "or", forTag: true, syncBox: _tbTag);
        mainLayout.Controls.Add(tagPanel, 0, 1);

        // Message 行（含收藏）
        var msgPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        // 默认 op = or：工具栏文本框继承面板 op 自身不能改（见上方 TextChanged 注释），
        // 若面板默认 and，工具栏快速过滤会退化成「多词全命中」，与工具栏的快捷入口定位不符。
        BuildTermControls(msgPanel, "Message", out _edMsg, out _cbMsgOp, out _ckMsgCase, out _ckMsgEx,
            "多词用空格分隔，短语用双引号包裹", "or", forTag: false, syncBox: _tbMsg);
        mainLayout.Controls.Add(msgPanel, 0, 2);

        // PID / TID / 分钟 行（含过滤条件说明）
        var pidTidPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        pidTidPanel.Controls.Add(new Label { Text = "PID", AutoSize = true, Padding = new Padding(0, 4, 4, 0) });
        _edPid = new TextBox { Width = 130, PlaceholderText = "多值用空格分隔" };
        _edPid.TextChanged += (_, _) => { RefreshFixedOrBoxStyle(); OnFilterChanged(); };
        _edPid.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) _ = ApplyFilter(); };
        pidTidPanel.Controls.Add(_edPid);
        _ckPidEx = new CheckBox { Text = "排除", AutoSize = true };
        _ckPidEx.CheckedChanged += (_, _) => { RefreshFixedOrBoxStyle(); OnFilterChanged(); };
        pidTidPanel.Controls.Add(_ckPidEx);
        pidTidPanel.Controls.Add(new Label { Text = "TID", AutoSize = true, Padding = new Padding(8, 4, 4, 0) });
        _edTid = new TextBox { Width = 130, PlaceholderText = "多值用空格分隔" };
        _edTid.TextChanged += (_, _) => { RefreshFixedOrBoxStyle(); OnFilterChanged(); };
        _edTid.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) _ = ApplyFilter(); };
        pidTidPanel.Controls.Add(_edTid);
        _ckTidEx = new CheckBox { Text = "排除", AutoSize = true };
        _ckTidEx.CheckedChanged += (_, _) => { RefreshFixedOrBoxStyle(); OnFilterChanged(); };
        pidTidPanel.Controls.Add(_ckTidEx);
        pidTidPanel.Controls.Add(new Label { Text = "分钟", AutoSize = true, Padding = new Padding(8, 4, 4, 0) });
        _edMin = new TextBox { Width = 160, PlaceholderText = "如 05 20（空格分隔）" };
        _edMin.TextChanged += (_, _) => { SyncPanelToToolbar(_edMin, _tbMin); RefreshFixedOrBoxStyle(); OnFilterChanged(); };
        _edMin.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) _ = ApplyFilter(); };
        pidTidPanel.Controls.Add(_edMin);
        _lblSpec = new Label { Text = "（无过滤）", AutoSize = true, Padding = new Padding(8, 6, 0, 0), ForeColor = Color.Gray };
        pidTidPanel.Controls.Add(_lblSpec);
        mainLayout.Controls.Add(pidTidPanel, 0, 3);

        _panel.Controls.Add(mainLayout);

        // 两组的 op / 大小写控件都已就位，做一次初始着色
        ApplyTermBoxStyle();
        RefreshFixedOrBoxStyle();
    }

    // ── 输入框颜色提示（纯 UI，不进 CollectSpec / FilterSpec）──
    // 三维正交分配：
    //   背景色 = op       → or 浅蓝 / and 浅橙
    //   字色   = 大小写   → 暗红 / 默认
    //   字体   = 排除     → 删除线 / 常规
    // 工具栏文本框与面板文本框共享同一份语义（op/大小写/排除以面板控件为唯一来源），
    // 所以两处必须用同一个函数着色，否则会出现「工具栏显示 and 蓝、面板却是 or 蓝」。
    static readonly Color BoxBgOr      = ColorTranslator.FromHtml("#E8F1FB"); // or：浅蓝
    static readonly Color BoxBgAnd     = ColorTranslator.FromHtml("#FDF0E3"); // and：浅橙
    static readonly Color BoxBgMinutes = ColorTranslator.FromHtml("#E8F1FB"); // 分钟/PID/TID 固定 or：同浅蓝
    static readonly Color BoxFgCaseOn  = ColorTranslator.FromHtml("#B00020"); // 区分大小写：暗红

    /// <summary>在保留原字体所有属性（字号/族/单位/垂直/字符集）的前提下，安全地叠加/清除 Strikeout。</summary>
    static Font ToggleStrikeout(Font font, bool strikeout)
    {
        var style = font.Style;
        if (strikeout) style |= FontStyle.Strikeout;
        else style &= ~FontStyle.Strikeout;
        if (style == font.Style) return font; // 无变化直接返回，避免不必要的 GDI+ 对象
        return new Font(font.FontFamily, font.Size, style, font.Unit, font.GdiCharSet, font.GdiVerticalFont);
    }

    static void ApplyTermBoxStyle(TextBoxBase box, string op, bool caseSensitive, bool exclude)
    {
        // 空框不着色：避免大面积色块造成视觉噪声，也让「未填写」与「已填写」一眼可分。
        // 「忽略大小写」用 SystemColors.WindowText 而非硬编码深色，这样文本框在
        // 未着色与着色两种状态下底色/字色是同一套，主题变化时不会半途变色。
        // （背景色目前是硬编码浅色，深色主题需另做适配，见 FILTER-REFACTOR-PLAN §3.3）
        bool hasText = !string.IsNullOrWhiteSpace(box.Text);
        box.BackColor = !hasText ? SystemColors.Window
                    : (op == "and" ? BoxBgAnd : BoxBgOr);
        box.ForeColor = !hasText ? SystemColors.WindowText
                    : (caseSensitive ? BoxFgCaseOn : SystemColors.WindowText);
        // 排除 → 删除线；空框不划线，避免视觉噪声
        box.Font = ToggleStrikeout(box.Font, exclude && hasText);
    }

    /// <summary>固定 or 语义、无大小写维度的输入框（PID/TID/分钟）着色：背景色浅蓝 + 可选排除删除线。</summary>
    static void ApplyFixedOrBoxStyle(TextBoxBase box, bool exclude)
    {
        bool hasText = !string.IsNullOrWhiteSpace(box.Text);
        box.BackColor = hasText ? BoxBgMinutes : SystemColors.Window;
        box.ForeColor = SystemColors.WindowText;
        box.Font = ToggleStrikeout(box.Font, exclude && hasText);
    }

    /// <summary>刷新 Tag / Message 两组共 4 个输入框的着色与工具栏 tooltip。</summary>
    void ApplyTermBoxStyle()
    {
        // BuildTermControls 是「构造控件 → 挂事件 → 建下一个控件」顺序：
        // cbOp.SelectedItem = defaultOp 与 ckCase 创建时都会触发本方法，
        // 此时另一组（或本组的 ckCase）可能仍是 null。必须逐个判空。
        if (_edTag == null || _edMsg == null
            || _ckTagCase == null || _ckMsgCase == null
            || _ckTagEx == null || _ckMsgEx == null) return;

        string tagOp = _cbTagOp.SelectedItem?.ToString() ?? "or";
        string msgOp = _cbMsgOp.SelectedItem?.ToString() ?? "or";

        ApplyTermBoxStyle(_edTag, tagOp, _ckTagCase.Checked, _ckTagEx.Checked);
        ApplyTermBoxStyle(_edMsg, msgOp, _ckMsgCase.Checked, _ckMsgEx.Checked);
        ApplyTermBoxStyle(_tbTag.TextBox, tagOp, _ckTagCase.Checked, _ckTagEx.Checked);
        ApplyTermBoxStyle(_tbMsg.TextBox, msgOp, _ckMsgCase.Checked, _ckMsgEx.Checked);

        // 颜色 + 文案双重指示：工具栏框自身没有 op / 大小写控件，只能靠 tooltip 说明
        _tbTag.ToolTipText = TermTip("Tag", tagOp, _ckTagCase.Checked, _ckTagEx.Checked);
        _tbMsg.ToolTipText = TermTip("Message", msgOp, _ckMsgCase.Checked, _ckMsgEx.Checked);
    }

    /// <summary>刷新固定 or 语义的输入框着色（PID/TID/分钟：浅蓝背景 + 可选排除删除线）。</summary>
    void RefreshFixedOrBoxStyle()
    {
        if (_edMin != null) ApplyFixedOrBoxStyle(_edMin, false);
        if (_edPid != null && _ckPidEx != null) ApplyFixedOrBoxStyle(_edPid, _ckPidEx.Checked);
        if (_edTid != null && _ckTidEx != null) ApplyFixedOrBoxStyle(_edTid, _ckTidEx.Checked);
        if (_tbMin != null) ApplyFixedOrBoxStyle(_tbMin.TextBox, false);
    }

    static string TermTip(string label, string op, bool caseSensitive, bool exclude)
    {
        var parts = new List<string>
        {
            "包含匹配",
            op == "and" ? "全部词命中（AND）" : "任一词命中（OR）",
            caseSensitive ? "区分大小写" : "忽略大小写",
        };
        if (exclude) parts.Add("已排除命中项");
        return $"{label}：" + string.Join("；", parts) + "（设置在「过滤设置」里）";
    }

    // ── 工具栏与过滤窗口输入框双向同步 ──
    void SyncToolbarToPanel(ToolStripTextBox src, TextBox dst)
    {
        if (_syncingFilter) return;
        _syncingFilter = true;
        try { dst.Text = src.Text; }
        finally { _syncingFilter = false; }
        ApplyTermBoxStyle();
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
        out CheckBox ckCase, out CheckBox ckEx,
        string placeholder, string defaultOp, bool forTag, ToolStripTextBox? syncBox = null)
    {
        // 固定列宽（AutoSize=false）：原写法 AutoSize=true 会被文字实际宽度覆盖，
        // 「Tag」比「Message」窄，导致两行的下拉框、复选框列不对齐
        panel.Controls.Add(new Label { Text = label, AutoSize = false, Width = 62, Height = 25, TextAlign = ContentAlignment.MiddleLeft });
        ed = new TextBox { Width = 280 };
        ed.PlaceholderText = placeholder;
        var box = ed;
        box.TextChanged += (_, _) =>
        {
            if (syncBox != null) SyncPanelToToolbar(box, syncBox);
            ApplyTermBoxStyle();
            OnFilterChanged();
        };
        box.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) _ = ApplyFilter(); };
        panel.Controls.Add(ed);

        // 收藏：▾ 从收藏选择，★ 收藏/移除当前内容
        var favTip = new ToolTip();
        var btnFavPick = new Button { Text = "▾", AutoSize = true, MinimumSize = new Size(28, 25), Margin = new Padding(2, 0, 0, 0) };
        favTip.SetToolTip(btnFavPick, "从收藏中选择");
        btnFavPick.Click += (_, _) => ShowFilterFavMenu(btnFavPick, box, forTag);
        panel.Controls.Add(btnFavPick);
        var btnFavToggle = new Button { Text = "★", AutoSize = true, MinimumSize = new Size(28, 25), Margin = new Padding(2, 0, 0, 0) };
        favTip.SetToolTip(btnFavToggle, "收藏当前内容（已收藏则移除）");
        btnFavToggle.Click += (_, _) => ToggleFilterFav(box, forTag);
        panel.Controls.Add(btnFavToggle);

        cbOp = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 55 };
        cbOp.Items.AddRange(new[] { "or", "and" });
        cbOp.SelectedItem = defaultOp;
        cbOp.SelectedIndexChanged += (_, _) => { ApplyTermBoxStyle(); OnFilterChanged(); };
        panel.Controls.Add(cbOp);
        ckCase = new CheckBox { Text = "大小写", AutoSize = true };
        ckCase.CheckedChanged += (_, _) => { ApplyTermBoxStyle(); OnFilterChanged(); };
        panel.Controls.Add(ckCase);
        ckEx = new CheckBox { Text = "排除", AutoSize = true };
        ckEx.CheckedChanged += (_, _) => { ApplyTermBoxStyle(); OnFilterChanged(); };
        panel.Controls.Add(ckEx);
    }

    // ── 过滤条件收藏（tag / message）──
    static List<string> FilterFavList(bool forTag) =>
        forTag ? FavoritesStore.Default.TagFilters : FavoritesStore.Default.MsgFilters;

    // 面板侧：从收藏选择（Control anchor 版本）
    void ShowFilterFavMenu(Control anchor, TextBox ed, bool forTag)
    {
        var list = FilterFavList(forTag);
        var menu = new ContextMenuStrip();
        var key = ed.Text.Trim();

        if (list.Count == 0)
        {
            menu.Items.Add(new ToolStripMenuItem("（暂无收藏，点 ★ 收藏当前内容）") { Enabled = false });
        }
        else
        {
            var filtered = string.IsNullOrEmpty(key)
                ? list
                : list.Where(x => x.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            foreach (var item in filtered)
            {
                var text = item;
                var mi = new ToolStripMenuItem($"★ {text}");
                mi.Click += (_, _) => ApplyFavText(ed, text, forTag);
                menu.Items.Add(mi);
            }
            if (menu.Items.Count == 0)
                menu.Items.Add(new ToolStripMenuItem("（无匹配收藏）") { Enabled = false });

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("清空收藏", null, (_, _) =>
            {
                if (MessageBox.Show(this, $"确定清空{(forTag ? "Tag" : "Message")}收藏？", "清空收藏",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                FilterFavList(forTag).Clear();
                FavoritesStore.Default.Save();
                ShowStatus("已清空收藏");
            });
        }
        menu.Show(anchor, new Point(0, anchor.Height));
    }

    // 工具栏侧：从收藏选择（ToolStripItem anchor 版本）
    void ShowFilterFavMenu(ToolStripItem anchor, ToolStripTextBox tb, bool forTag)
    {
        var list = FilterFavList(forTag);
        var menu = new ContextMenuStrip();
        var key = tb.Text.Trim();

        if (list.Count == 0)
        {
            menu.Items.Add(new ToolStripMenuItem("（暂无收藏，点 ★ 收藏当前内容）") { Enabled = false });
        }
        else
        {
            var filtered = string.IsNullOrEmpty(key)
                ? list
                : list.Where(x => x.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            foreach (var item in filtered)
            {
                var text = item;
                var mi = new ToolStripMenuItem($"★ {text}");
                mi.Click += (_, _) => ApplyFavText(tb.TextBox, text, forTag);
                menu.Items.Add(mi);
            }
            if (menu.Items.Count == 0)
                menu.Items.Add(new ToolStripMenuItem("（无匹配收藏）") { Enabled = false });

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("清空收藏", null, (_, _) =>
            {
                if (MessageBox.Show(this, $"确定清空{(forTag ? "Tag" : "Message")}收藏？", "清空收藏",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                FilterFavList(forTag).Clear();
                FavoritesStore.Default.Save();
                ShowStatus("已清空收藏");
            });
        }
        // 用 anchor 的父 ToolStrip 显示菜单，位置相对于 anchor
        var pt = anchor.Owner?.PointToClient(Cursor.Position) ?? new Point(0, anchor.Height);
        var screenPos = anchor.Owner?.PointToScreen(new Point(anchor.Bounds.Left, anchor.Bounds.Bottom)) ?? new Point(0, 0);
        menu.Show(screenPos);
    }

    // 应用收藏文本到输入框
    void ApplyFavText(TextBox ed, string text, bool forTag)
    {
        var tb = forTag ? _tbTag : _tbMsg;

        if (ed == tb.TextBox)
        {
            // 工具栏侧：抑制重复同步，随后手动补一次工具栏→面板同步
            _syncingFilter = true;
            try { ed.Text = text; } finally { _syncingFilter = false; }
            ed.SelectionStart = ed.TextLength;
            SyncToolbarToPanel(tb, forTag ? _edTag : _edMsg);
        }
        else
        {
            // 面板侧：走既有「面板 TextChanged → SyncPanelToToolbar」链路
            ed.Text = text;
            ed.SelectionStart = ed.TextLength;
            OnFilterChanged();
        }
        RefreshFavStars();
    }

    // 切换收藏状态（使用大小写不敏感的 Toggle* API）
    void ToggleFilterFav(ToolStripTextBox tb, bool forTag)
    {
        var fav = FavoritesStore.Default;
        var text = tb.Text.Trim();
        if (text.Length == 0) { ShowStatus("内容为空，无法收藏"); return; }

        bool added = forTag ? fav.ToggleTagFilter(text) : fav.ToggleMsgFilter(text);
        fav.Save();
        ShowStatus(added ? $"已收藏：{text}" : $"已移除收藏：{text}");
        RefreshFavStars();
    }

    // 保留面板侧重载（供 BuildTermControls 调用）
    void ToggleFilterFav(TextBox ed, bool forTag)
    {
        var fav = FavoritesStore.Default;
        var text = ed.Text.Trim();
        if (text.Length == 0) { ShowStatus("内容为空，无法收藏"); return; }

        bool added = forTag ? fav.ToggleTagFilter(text) : fav.ToggleMsgFilter(text);
        fav.Save();
        ShowStatus(added ? $"已收藏：{text}" : $"已移除收藏：{text}");
        RefreshFavStars();
    }

    // ── 刷新收藏星号状态 ──
    void RefreshFavStars()
    {
        // 面板侧 ★ 由 BuildTermControls 创建，需通过控件名查找
        RefreshPanelFavStar(_edTag, _ckTagCase?.Parent as FlowLayoutPanel);
        RefreshPanelFavStar(_edMsg, _ckMsgCase?.Parent as FlowLayoutPanel);

        // 工具栏侧
        bool tagEmpty = string.IsNullOrWhiteSpace(_tbTag.Text);
        bool msgEmpty = string.IsNullOrWhiteSpace(_tbMsg.Text);
        _btnFavTagToggle.Enabled = !tagEmpty;
        _btnFavMsgToggle.Enabled = !msgEmpty;

        var fav = FavoritesStore.Default;
        _btnFavTagToggle.Text = tagEmpty ? "☆" : (fav.ContainsTagFilter(_tbTag.Text.Trim()) ? "★" : "☆");
        _btnFavMsgToggle.Text = msgEmpty ? "☆" : (fav.ContainsMsgFilter(_tbMsg.Text.Trim()) ? "★" : "☆");
    }

    void RefreshPanelFavStar(TextBox ed, FlowLayoutPanel? panel)
    {
        if (panel == null) return;
        // 在 panel 中查找 ★ 按钮（第三个按钮：▾、★、op）
        var btns = panel.Controls.OfType<Button>().ToList();
        if (btns.Count < 2) return;
        var btnStar = btns[1]; // 第二个是 ★
        bool isTag = ed == _edTag;
        bool empty = string.IsNullOrWhiteSpace(ed.Text);
        btnStar.Enabled = !empty;
        if (!empty)
        {
            var fav = FavoritesStore.Default;
            bool isFav = isTag ? fav.ContainsTagFilter(ed.Text.Trim()) : fav.ContainsMsgFilter(ed.Text.Trim());
            btnStar.Text = isFav ? "★" : "☆";
        }
        else
        {
            btnStar.Text = "☆";
        }
    }

    // ── 过滤变更 ──
    void OnFilterChanged()
    {
        if (_chkAuto.Checked)
            _autoTimer.Start();
        RefreshFavStars();
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
        // 采集与打开文件互斥：打开新文件前先停掉采集，避免 AdbAutoReload
        // 在 _doc 置空的窗口期把采集临时文件重新顶上来
        if (_adbCapturing)
            await AdbStopCaptureAsync();

        StopWorker();
        _listView.SetDocument(null);
        Text = $"{AppInfo.Title} — {System.IO.Path.GetFileName(path)}";
        _lblFile.Text = path;
        _lblStat.Text = "";

        // 等待在途的 Append/过滤结束后再关旧文档（mmap 一关后台扫描就会炸）
        await _docBusy.WaitAsync();
        try { _doc?.Close(); _doc = null; }
        finally { _docBusy.Release(); }
        _marked.Clear();

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
            string kind;
            await _docBusy.WaitAsync(cts.Token);
            try
            {
                var doc = _doc;
                if (doc == null) return;
                kind = await Task.Run(() => doc.Reload(join: _join,
                    progress: new Progress<(double, string)>(p =>
                    {
                        if (_workerGen == gen) { _pbar.Value = (int)(p.Item1 * 100); _lblMsg.Text = p.Item2; }
                    }), ct: cts.Token), cts.Token);
            }
            finally { _docBusy.Release(); }

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
            int[] rows;
            int filteredDocRows = -1;
            await _docBusy.WaitAsync(cts.Token);
            try
            {
                var doc = _doc;
                if (doc == null) return;
                rows = await FilterEngine.ApplyFilterAsync(doc, spec, _marked,
                    quiet ? null : new Progress<(double, string)>(p =>
                    {
                        if (_workerGen == gen) { _pbar.Value = (int)(p.Item1 * 100); _lblMsg.Text = p.Item2; }
                    }), cts.Token);
                filteredDocRows = doc.RowCount;
            }
            finally { _docBusy.Release(); }

            if (_workerGen != gen) return;
            _filterDocRows = filteredDocRows;
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

    /// <summary>
    /// 实时采集增量过滤：过滤条件全部逐行独立判定、append 只在尾部加行，
    /// 旧行的匹配结果永不变化，因此只需对 [_filterDocRows, RowCount) 调 FilterTail
    /// 并 AppendRows——不重算旧行、不清虚拟列表缓存，每秒开销从 O(全量) 降到 O(新增行)。
    /// 末行被续行拉长（LastAppendCarried，message 内容可能变化）或文档重建时，调用方回退全量。
    /// </summary>
    async Task ApplyIncremental(LogDocument doc)
    {
        int start = _filterDocRows;
        int n = doc.RowCount;
        if (n < start) { await ApplyFilter(quiet: true); return; }   // 状态不一致，回退全量
        if (n == start) { UpdateStat(); return; }                    // 无新行（如整批续行之外的情况）

        int[] rows;
        await _docBusy.WaitAsync();
        try { rows = await Task.Run(() => FilterEngine.FilterTail(doc, _spec, start, _marked)); }
        finally { _docBusy.Release(); }

        _filterDocRows = n;
        if (rows.Length > 0)
        {
            _listView.AppendRows(rows);
            if (_chkToolbarFollow.Checked) ScrollBottom();
        }
        UpdateStat();
    }

    FilterSpec CollectSpec()
    {
        var spec = new FilterSpec();
        spec.Levels = Enumerable.Range(1, 7).Where(i => _lvlBoxes[i].Checked).ToArray();
        spec.Tags = FilterEngine.SplitTerms(_edTag.Text, _cbTagOp.SelectedItem?.ToString() ?? "or").ToArray();
        spec.TagOp = _cbTagOp.SelectedItem?.ToString() ?? "or";
        spec.TagCase = _ckTagCase.Checked;
        spec.TagExclude = _ckTagEx.Checked;
        spec.Msg = FilterEngine.SplitTerms(_edMsg.Text, _cbMsgOp.SelectedItem?.ToString() ?? "or").ToArray();
        spec.MsgOp = _cbMsgOp.SelectedItem?.ToString() ?? "or";
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
        _ckTagCase.Checked = _ckTagEx.Checked = false;
        _ckMsgCase.Checked = _ckMsgEx.Checked = false;
        _ckPidEx.Checked = _ckTidEx.Checked = false;
        _chkToolbarMarkedOnly.Checked = _chkToolbarFollow.Checked = false;
        // 逐控件赋值会反复触发自动应用；op 也一并回到默认（Message 与 Tag 统一为 or）
        _cbTagOp.SelectedItem = "or";
        _cbMsgOp.SelectedItem = "or";
        // Clear() 在本来就为空时不会触发 TextChanged，需显式补一次着色
        ApplyTermBoxStyle();
        RefreshFixedOrBoxStyle();
        _ = ApplyFilter();
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
            int n;
            await _docBusy.WaitAsync();
            try
            {
                var doc = _doc;
                if (doc == null) { _pbar.Visible = false; return; }
                n = await FilterEngine.ExportRowsAsync(doc, rows, dlg.FileName, _singleLineExport,
                    new Progress<(double, string)>(p => { _pbar.Value = (int)(p.Item1 * 100); _lblMsg.Text = p.Item2; }));
            }
            finally { _docBusy.Release(); }
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
            menu.Items.Add($"按此 tag 过滤：{tag}", null, (_, _) => { _edTag.Text = tag; _ = ApplyFilter(); });

        if (_doc != null)
        {
            int pid = _doc.Pid[docRow], tid = _doc.Tid[docRow];
            if (pid >= 0) menu.Items.Add($"按此 PID 过滤：{pid}", null, (_, _) => { _edPid.Text = pid.ToString(); _ = ApplyFilter(); });
            if (tid >= 0) menu.Items.Add($"按此 TID 过滤：{tid}", null, (_, _) => { _edTid.Text = tid.ToString(); _ = ApplyFilter(); });

            long ts = _doc.Ts[docRow];
            if (ts >= 0)
            {
                int minute = (int)((ts / 60000) % 60);
                menu.Items.Add($"按此分钟过滤：{minute:D2}", null, (_, _) => { _edMin.Text = minute.ToString(); _ = ApplyFilter(); });
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

        var terms = FilterEngine.SplitTerms(_edMsg.Text, _cbMsgOp.SelectedItem?.ToString() ?? "or");
        if (terms.Count == 0) { ShowStatus("请先在 Message 框里填写检索词"); return; }

        bool opAnd = (_cbMsgOp.SelectedItem?.ToString() ?? "or") == "and";
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

    void ApplyFont(int pt)
    {
        _fontPt = pt;
        _listView.SetFont(pt);
    }

    // ── 快捷键 ──
    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.Shift && e.KeyCode == Keys.C) { AdbCommandWindow(); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.Enter) { _ = ApplyFilter(); e.Handled = true; }
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
        // 同步等待采集任务退出（上限 2s），否则 SaveSettings/关窗先走，
        // LogcatStream 的 Stopped 回调会打在已释放的句柄上
        try { AdbStopCaptureAsync().Wait(TimeSpan.FromSeconds(2)); }
        catch { /* 超时/包装异常不阻断关窗 */ }
        // 等待在途的 Append/过滤结束（上限 2s），再关 mmap
        try { _docBusy.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _adbManager?.Dispose();
        SaveSettings();
        _doc?.Close();
        _docBusy.Dispose();
        base.OnFormClosing(e);
    }

    // ── 设置 ──
    void LoadSettings()
    {
        StartupLog.Write("[窗口状态] LoadSettings 开始");
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
                // 同时校验尺寸合理性：宽度 < 主屏 1/2 视为异常残留值（如拖动到边缘或某次保存损坏）
                bool reasonable = bounds.Width >= Screen.PrimaryScreen!.WorkingArea.Width / 2;
                StartupLog.Write($"读取 settings.json：Location={s.WindowLocation} Size={s.WindowSize} State={(int)state} onScreen={onScreen} reasonable={reasonable}");
                if (onScreen && reasonable)
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
                    StartupLog.Write($"校验失败：onScreen={onScreen} reasonable={reasonable}，放弃保存几何，使用默认布局");
                }
            }
            else
            {
                StartupLog.Write("settings.json 无有效窗口状态，使用默认布局");
            }
        }
        catch { StartupLog.Write("LoadSettings 异常，使用默认布局"); /* 首次运行，使用默认值 */ }
    }

    // 在 Load 事件中调用：此时窗体句柄已就绪，DPI/屏幕信息都正确，最大化能可靠落到原屏
    void ApplyWindowGeometry()
    {
        // 默认策略：主屏幕 WorkingArea 的 2/3 宽高，居中
        var wa = Screen.PrimaryScreen!.WorkingArea;
        var defaultSize = new Size((int)(wa.Width * 0.667), (int)(wa.Height * 0.667));
        var defaultLoc = new Point(wa.Left + (wa.Width - defaultSize.Width) / 2,
                                   wa.Top + (wa.Height - defaultSize.Height) / 2);

        if (!_hasSavedGeometry)
        {
            StartupLog.Write($"[窗口状态] 无保存几何，使用默认 {defaultSize.Width}x{defaultSize.Height}");
            StartPosition = FormStartPosition.Manual;
            Location = defaultLoc;
            Size = defaultSize;
            StartupLog.Write($"[窗口状态] 已设置默认几何 Location={defaultLoc} Size={defaultSize}");
            return;
        }

        StartupLog.Write($"[窗口状态] 有保存几何：Location={_savedBounds.Location} Size={_savedBounds.Size} State={_savedState}");
        StartPosition = FormStartPosition.Manual;
        Location = _savedBounds.Location;
        Size = _savedBounds.Size;

        if (_savedState == FormWindowState.Maximized)
        {
            StartupLog.Write("[窗口状态] 保存状态为Maximized，正在设置WindowState=Maximized...");
            WindowState = FormWindowState.Maximized;
            StartupLog.Write($"[窗口状态] WindowState已设置为Maximized");
        }
        else
        {
            StartupLog.Write($"[窗口状态] 保存状态为{_savedState}，保持正常态");
        }

        // 推迟到布局完成后再读实际落点，确保取到最终位置/所在屏
        BeginInvoke((System.Action)(() =>
        {
            var cur = Screen.FromPoint(Location);
            int idx = System.Array.IndexOf(Screen.AllScreens, cur);
            StartupLog.Write($"应用后实际：Location={Location} Size={Size} State={WindowState} 所在屏[{idx}] Primary={cur.Primary}");
            DpiDiag.Log("frmMain.几何应用后", this);
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
        // ── 窗口状态与几何保存 ──
        // 关闭时状态 → 保存策略：
        //   Maximized → 保存 RestoreBounds(正常态矩形) + State=Maximized，下次启动直接最大化
        //   Normal   → 保存当前 Bounds + State=Normal
        //   Minimized → 保存桌面 2/3 宽高 + State=Normal，下次启动正常态
        StartupLog.Write($"[窗口状态] 关闭时 WindowState={WindowState} Bounds={Bounds} RestoreBounds={RestoreBounds}");
        if (WindowState == FormWindowState.Minimized)
        {
            StartupLog.Write("[窗口状态] 最小化关闭 → 使用桌面2/3宽高");
            var wa2 = Screen.PrimaryScreen!.WorkingArea;
            var sz2 = new Size((int)(wa2.Width * 0.667), (int)(wa2.Height * 0.667));
            var loc2 = new Point(wa2.Left + (wa2.Width - sz2.Width) / 2, wa2.Top + (wa2.Height - sz2.Height) / 2);
            s.WindowSize = sz2;
            s.WindowLocation = loc2;
            s.WindowState = (int)FormWindowState.Normal;
            StartupLog.Write($"[窗口状态] 写入 Location={loc2} Size={sz2} State=Normal");
        }
        else if (WindowState == FormWindowState.Maximized)
        {
            StartupLog.Write("[窗口状态] 最大化关闭 → 保存RestoreBounds + State=Maximized");
            var bounds = RestoreBounds;
            s.WindowLocation = bounds.Location;
            s.WindowSize = bounds.Size;
            s.WindowState = (int)FormWindowState.Maximized;
            StartupLog.Write($"[窗口状态] 写入 Location={bounds.Location} Size={bounds.Size} State=Maximized");
        }
        else
        {
            StartupLog.Write("[窗口状态] 正常关闭 → 保存当前Bounds + State=Normal");
            s.WindowLocation = Bounds.Location;
            s.WindowSize = Bounds.Size;
            s.WindowState = (int)FormWindowState.Normal;
            StartupLog.Write($"[窗口状态] 写入 Location={Bounds.Location} Size={Bounds.Size} State=Normal");
        }
        s.Save();
        StartupLog.Write("[窗口状态] settings.json 已保存");
    }

    // ── ADB 初始化 ──

    async void InitAdb()
    {
        try
        {
            _adbManager = new AdbManager(_logger);
            _adbManager.DevicesChanged += (_, devices) => SafeInvoke(() => OnDevicesChanged(devices));
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
        // 采集/停止按钮还需结合采集状态：未开始采集时「停止」应为灰
        UpdateAdbCaptureUI();
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

        // 采集与打开文件互斥（另一方向）：开始采集先取消在途的建索引/过滤，
        // StartIndex 里未完成的 BuildAsync 会被 gen 守卫丢弃，不会回头覆盖 _doc
        StopWorker();

        _adbTempPath = System.IO.Path.Combine(
            AppContext.BaseDirectory, "Temp",
            $"logcat_live_{DateTime.Now:yyyyMMdd_HHmmss}.log");

        // 重置旧文档状态：采集时 AdbAutoReload 必须走首次加载，
        // 否则会对上一个打开的文件做增量 Reload，实时内容永远不更新
        await _docBusy.WaitAsync();
        try { _doc?.Close(); _doc = null; }
        finally { _docBusy.Release(); }
        _marked.Clear();
        _adbIndexing = false;
        _listView.SetDocument(null);

        _streamThread = new LogcatStream(_adbManager, serial, _adbTempPath, _logger);
        _streamThread.LinesReceived += lines =>
        {
            Interlocked.Add(ref _adbLiveCount, lines.Count);
        };
        _streamThread.ErrorOccurred += msg => SafeInvoke(() =>
        {
            _lblMsg.Text = msg;
        });
        _streamThread.Stopped += () => SafeInvoke(() =>
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

    /// <summary>停止采集并等待采集任务退出。可 await；关窗时用 Wait(超时) 同步等待。</summary>
    async Task AdbStopCaptureAsync()
    {
        var stream = _streamThread;
        _streamThread = null;
        if (stream != null)
        {
            try { await stream.StopAsync(); }
            catch { /* 退出时的清理异常不阻断关窗 */ }
        }
        _adbCapturing = false;
        _adbReloadTimer.Stop();
        _adbStatTimer.Stop();
        UpdateAdbCaptureUI();
    }

    void UpdateAdbCaptureUI()
    {
        bool hasDevice = _comboDevice.Items.Count > 0;
        foreach (ToolStripItem item in _toolStrip.Items)
        {
            if (item.Tag is "adb" && item is ToolStripButton btn)
            {
                var btnText = btn.Text ?? ""; // WinForms 的 Text getter 注解为可返回 null
                if (btnText.Contains("开始采集")) btn.Enabled = hasDevice && !_adbCapturing;
                if (btnText.Contains("停止") && !btnText.Contains("开始")) btn.Enabled = hasDevice && _adbCapturing;
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
                    // 首次加载（构建新文档对象，不触碰共享 _doc，无需锁）
                    if (_workerGen == _adbGen) return;
                    var cts0 = new CancellationTokenSource();
                    _ctsWorker = cts0;
                    int gen = ++_workerGen;
                    _adbGen = gen;
                    _adbIndexing = true;

                    var doc = await LogDocument.BuildAsync(_adbTempPath, _join,
                        new Progress<(double, string)>(), cts0.Token);
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
                    var doc = _doc;
                    // 防御：doc 必须还是本次采集的临时文件（采集期间不允许打开其他文件）
                    if (doc == null || doc.Path != _adbTempPath) return;
                    var cts = new CancellationTokenSource();
                    _ctsWorker = cts; // 放入 _ctsWorker，Escape 可取消在途 append
                    int gen = ++_workerGen;
                    _adbGen = gen;
                    string kind;
                    await _docBusy.WaitAsync(cts.Token);
                    try
                    {
                        kind = await Task.Run(() =>
                            doc.Reload(join: _join,
                                progress: new Progress<(double, string)>(),
                                ct: cts.Token), cts.Token);
                    }
                    finally { _docBusy.Release(); }
                    if (_workerGen != gen) return;
                    if (kind != "unchanged")
                    {
                        if (kind == "appended" && !doc.LastAppendCarried)
                        {
                            // 纯尾部追加：增量过滤（旧行结果不变，只判新行）
                            await ApplyIncremental(doc);
                        }
                        else
                        {
                            // rebuilt（文件重写）或末行被续行拉长（message 变了，匹配结果可能翻转）：
                            // 回退全量。同一文档实例无需 SetDocument，静默刷新避免闪屏
                            var snap = _listView.CaptureView();
                            await ApplyFilter(snap, quiet: true);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { /* Escape/停采集取消，属正常流程 */ }
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
            _filterDialog = new FilterDialog(_panel, () => _ = ApplyFilter());

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

    /// <summary>后台线程安全投递到 UI 线程：BeginInvoke 非阻塞，句柄未建/正在销毁时静默丢弃。</summary>
    void SafeInvoke(Action action)
    {
        try
        {
            if (IsHandleCreated && !IsDisposed && !Disposing)
                BeginInvoke(action);
        }
        catch { /* 窗口正在关闭 */ }
    }

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
