using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using logcat.Models;
using logcat.Services;

namespace logcat.Forms;

/// <summary>
/// 命令窗口：把用过的命令按分类收藏起来，随时在选中设备上执行。
/// 两条执行通道——设备 shell（adb shell，可选 su 提权）与本机 adb（install / reboot 这类子命令），
/// 输出逐行回显，logcat 这类不结束的命令可以中途停止。
/// </summary>
public class CommandDialog : Form, ILocalizedUi
{
    /// <summary>占位符说明文案（左侧工具栏底部）。</summary>
    const string HINT_TEXT = "Enter 执行；{pkg}、{path} 等占位符执行前提示填值";

    /// <summary>文本框里最多保留的输出字符数，超出后丢弃最早的行（滚动窗口）。</summary>
    const int KeepOutputChars = 400_000;

    /// <summary>
    /// 「（全部）」选项文本。必须是<em>属性</em>而不是 <c>static readonly</c> 字段：
    /// 字段初始化在类型首次加载时求值一次（那一刻的语言），之后 <c>Loc.SetLang</c>
    /// 再切语言它也不变 —— 表现为英文界面里下拉框第一项仍是中文「（全部）」。
    /// </summary>
    static string AllCategories => Loc.T("（全部）");

    readonly AdbManager _manager;
    readonly Func<string?> _serialProvider;
    readonly Func<bool> _rootProvider;
    readonly CommandStore _store = CommandStore.Default;

    // 左侧：分类与收藏（在 BuildLeftPane 里创建，故不能是 readonly）
    ComboBox _cboCat = null!;
    TextBox _txtFilter = null!;
    RadioButton _rbFav = null!;
    RadioButton _rbHistory = null!;
    ListView _lv = null!;

    // 右侧：执行与输出
    ComboBox _txtCmd = null!;
    ComboBox _cboKind = null!;
    CheckBox _ckRoot = null!;
    CheckBox _ckFollow = null!;
    NumericUpDown _numTimeout = null!;
    TextBox _txtOut = null!;
    Button _btnRun = null!;
    Button _btnStop = null!;
    Label _lblStat = null!;
    Label _lblDevice = null!;

    CancellationTokenSource? _cts;
    bool _running;
    bool _userStop;
    bool _ready;
    long _runLines;
    readonly ConcurrentQueue<string> _pending = new();
    readonly System.Windows.Forms.Timer _outTimer = new() { Interval = 120 };

    /// <summary>当前通道。</summary>
    CommandKind Kind => CommandKinds.Of(_cboKind.SelectedItem as string);

    /// <summary>当前分类筛选，null 表示全部。下拉框里存的是显示文本（可能是译文），
    /// 取出时必须还原成存储用的分类名。</summary>
    string? Category => _cboCat.SelectedIndex <= 0
        ? null
        : CommandStore.CategoryFromDisplay(_cboCat.Text.Trim());

    /// <param name="serialProvider">取主界面当前选中的设备序列号；对话框是非模态的，执行时才取值。</param>
    /// <param name="rootProvider">取该设备是否已检测到 root，仅用于提示。</param>
    public CommandDialog(AdbManager manager, Func<string?> serialProvider, Func<bool> rootProvider)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        _manager = manager;
        _serialProvider = serialProvider;
        _rootProvider = rootProvider;

        Text = Loc.T("命令窗口");
        Loc.Bind(this, "命令窗口");   // 登记资源键，切语言时 ApplyTo 才能重设标题
        Size = new Size(1180, 760);
        MinimumSize = new Size(940, 560);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;

        BuildUi();
        // 建控件期间给 combo 塞项会提前触发 SelectedIndexChanged，装完成才允许刷新列表
        _ready = true;
        RefreshCategories();
        RefreshList();
        RefreshHistoryCombo();
        UpdateDeviceLabel();

        _outTimer.Tick += (_, _) => FlushPending();
        Activated += (_, _) => UpdateDeviceLabel();
        FormClosed += (_, _) => { _outTimer.Stop(); _cts?.Cancel(); _cts?.Dispose(); };
    }

    // ── 界面 ──

    void BuildUi()
    {
        var root = NewGrid(1, 2);
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        Controls.Add(root);

        _lblStat = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
        };
        root.Controls.Add(_lblStat, 0, 1);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 6,
        };
        root.Controls.Add(split, 0, 0);
        // 构造期控件还是默认尺寸，先设最小尺寸会让 SplitterDistance 越界抛异常，必须等布局完成
        Load += (_, _) =>
        {
            try
            {
                split.Panel1MinSize = 320;
                split.Panel2MinSize = 420;
                split.SplitterDistance = Math.Clamp(560, split.Panel1MinSize,
                    Math.Max(split.Panel1MinSize, split.Width - split.Panel2MinSize));
            }
            catch { /* 布局异常不应阻断窗口打开 */ }
        };

        BuildLeftPane(split.Panel1);
        BuildRightPane(split.Panel2);
    }

    static TableLayoutPanel NewGrid(int cols, int rows) => new()
    {
        Dock = DockStyle.Fill,
        ColumnCount = cols,
        RowCount = rows,
        Margin = Padding.Empty,
        Padding = new Padding(4),
    };

    static FlowLayoutPanel NewFlow() => new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = false,
        Margin = Padding.Empty,
        Padding = new Padding(0),
    };

    /// <summary>
    /// 窄窗口下允许换行的工具栏，必须配 AutoSize 行使用：换行后行高会跟着变高，
    /// 最右侧控件不会被裁掉（WrapContents=false 时超出部分直接不可见）。
    /// </summary>
    static FlowLayoutPanel NewWrapFlow() => new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = true,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Margin = Padding.Empty,
        Padding = new Padding(0),
    };

    /// <summary>命令窗口按钮。<paramref name="zh"/> 是中文原文（资源键），不是已译文本——
    /// 走 <c>Loc.Bind</c> 绑定，切语言时才会重设文本；只用 <c>Text = Loc.T(…)</c> 赋值的话
    /// 控件与资源键之间没有记录，<c>Loc.ApplyTo</c> 遍历时查不到，切语言完全不刷新。</summary>
    static Button NewButton(string zh, int width) => Loc.Bind(new Button
    {
        // AutoSize + MinimumSize：宽度随文字自适应，高 DPI 缩放下不裁字；width 保留为最小宽度
        AutoSize = true,
        MinimumSize = new Size(width, 25),
        Margin = new Padding(2, 2, 0, 0),
    }, zh);

    void BuildLeftPane(Control host)
    {
        var grid = NewGrid(1, 4);
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        host.Controls.Add(grid);

        // 分类行
        var rowCat = NewFlow();
        rowCat.Controls.Add(Loc.Bind(new Label { AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, "分类"));
        _cboCat = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 120,
            Margin = new Padding(4, 3, 0, 0),
        };
        _cboCat.SelectedIndexChanged += (_, _) => RefreshList();
        rowCat.Controls.Add(_cboCat);

        var btnCatAdd = NewButton("新建", 46);
        btnCatAdd.Click += (_, _) => OnAddCategory();
        rowCat.Controls.Add(btnCatAdd);

        var btnCatRename = NewButton("重命名", 60);
        btnCatRename.Click += (_, _) => OnRenameCategory();
        rowCat.Controls.Add(btnCatRename);

        var btnCatDel = NewButton("删除", 46);
        btnCatDel.Click += (_, _) => OnDeleteCategory();
        rowCat.Controls.Add(btnCatDel);
        grid.Controls.Add(rowCat, 0, 0);

        // 列表来源与筛选
        var rowMode = NewFlow();
        _rbFav = Loc.Bind(new RadioButton { AutoSize = true, Checked = true, Margin = new Padding(0, 5, 0, 0) }, "收藏");
        _rbHistory = Loc.Bind(new RadioButton { AutoSize = true, Margin = new Padding(8, 5, 0, 0) }, "最近使用");
        foreach (var rb in new[] { _rbFav, _rbHistory })
            rb.CheckedChanged += (_, _) => { if (rb.Checked) RefreshList(); };
        rowMode.Controls.Add(_rbFav);
        rowMode.Controls.Add(_rbHistory);
        rowMode.Controls.Add(Loc.Bind(new Label { AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, "  筛选"));
        // PlaceholderText 走 BindHint（绑定表的Hint 槽），不能走 Bind
        _txtFilter = Loc.BindHint(new TextBox { Width = 130, Margin = new Padding(4, 3, 0, 0) }, "命令或备注");
        _txtFilter.TextChanged += (_, _) => RefreshList();
        rowMode.Controls.Add(_txtFilter);
        grid.Controls.Add(rowMode, 0, 1);

        _lv = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            HideSelection = false,
            MultiSelect = false,
            Font = new Font("Consolas", 9.5F),
        };
        _lv.DoubleClick += (_, _) => FillFromSelection();
        _lv.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { FillFromSelection(); e.Handled = true; }
        };
        _lv.MouseClick += (_, e) => { if (e.Button == MouseButtons.Right) ShowListMenu(e); };
        grid.Controls.Add(_lv, 0, 2);

        // 收藏操作
        var rowOps = NewFlow();
        var btnFav = NewButton("★ 收藏当前…", 100);
        btnFav.Click += (_, _) => OnAddFavorite();
        rowOps.Controls.Add(btnFav);

        var btnEdit = NewButton("编辑", 50);
        btnEdit.Click += (_, _) => OnEditFavorite();
        rowOps.Controls.Add(btnEdit);

        var btnDel = NewButton("移除", 50);
        btnDel.Click += (_, _) => OnRemoveSelected();
        rowOps.Controls.Add(btnDel);

        var btnClear = NewButton("清空列表", 74);
        btnClear.Click += (_, _) => OnClearList();
        rowOps.Controls.Add(btnClear);
        grid.Controls.Add(rowOps, 0, 3);
    }

    void BuildRightPane(Control host)
    {
        var grid = NewGrid(1, 6);
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        // 选项行：控件多，窄窗口下宁可换行也不能裁掉最右侧的「跟随输出」，
        // 所以行改 AutoSize，并让 rowOpt 开 AutoSize + WrapContents（必须配套，否则行高测不准）
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        // 占位符说明独占一整行（AutoSize）：不能与右侧按钮同行。英文说明约 445px，
        // 而按钮列固定 248px，同行时 Label 放不下就换行，AutoSize 优先于 Dock，
        // 撑高的 Label 直接溢出 34px 行高盖住按钮（TLP 不会裁剪子控件）。
        // 独占一行后即便换行，行高也自己跟着长，按钮不再被压。
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        // 输出操作按钮行：按钮实测 27 高（含上边距 29），留 34 上下各留一点余量
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        host.Controls.Add(grid);

        // 命令输入行
        var rowCmd = NewGrid(3, 1);
        rowCmd.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        rowCmd.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rowCmd.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74));
        rowCmd.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74));
        _txtCmd = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDown,
            Font = new Font("Consolas", 10F),
            Margin = new Padding(2, 4, 0, 0),
        };
        _txtCmd.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { Run(); e.Handled = true; e.SuppressKeyPress = true; }
        };
        rowCmd.Controls.Add(_txtCmd, 0, 0);

        _btnRun = NewButton("执行", 68);
        _btnRun.Click += (_, _) => Run();
        rowCmd.Controls.Add(_btnRun, 1, 0);

        _btnStop = NewButton("停止", 68);
        _btnStop.Enabled = false;
        _btnStop.Click += (_, _) => Stop();
        rowCmd.Controls.Add(_btnStop, 2, 0);
        grid.Controls.Add(rowCmd, 0, 0);

        // 选项行（窄窗口下换行，不裁掉最右侧的「跟随输出」）
        var rowOpt = NewWrapFlow();
        _cboKind = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 100,
            Margin = new Padding(0, 3, 0, 0),
        };
        _cboKind.Items.AddRange(new object[] { CommandKinds.ShellText, CommandKinds.AdbText });
        _cboKind.SelectedIndex = 0;
        _cboKind.SelectedIndexChanged += (_, _) => UpdateRootEnabled();
        rowOpt.Controls.Add(_cboKind);
        _ckRoot = Loc.Bind(new CheckBox { AutoSize = true, Margin = new Padding(10, 5, 0, 0) }, "root（su -c）");
        _ckRoot.CheckedChanged += (_, _) => OnRootToggled();
        rowOpt.Controls.Add(_ckRoot);

        rowOpt.Controls.Add(Loc.Bind(new Label { AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, "  超时"));
        _numTimeout = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 3600,
            Value = 30,
            Width = 56,
            Margin = new Padding(2, 3, 0, 0),
        };
        rowOpt.Controls.Add(_numTimeout);
        rowOpt.Controls.Add(Loc.Bind(new Label { AutoSize = true, Padding = new Padding(2, 5, 0, 0) }, "秒（0 不限）"));

        _ckFollow = Loc.Bind(new CheckBox { AutoSize = true, Checked = true, Margin = new Padding(10, 5, 0, 0) }, "跟随输出");
        rowOpt.Controls.Add(_ckFollow);
        grid.Controls.Add(rowOpt, 0, 1);

        // 占位符说明：独占一行。AutoSize 让 WinForms 按整行宽度自动折行，
        // 行是 AutoSize 所以折几行都不会压到别的东西上。
        var hint = Loc.Bind(new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            Margin = new Padding(2, 2, 0, 0),
        }, HINT_TEXT);
        grid.Controls.Add(hint, 0, 2);

        // 输出操作按钮：单独一行，靠右排。原来把按钮和说明塞在同一行，
        // 英文说明换行后会盖住它们（AutoSize 的 Label 不受 TLP 行高约束）。
        var outBtns = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        var btnClearOut = NewButton("清空输出", 74);
        btnClearOut.Click += (_, _) => { _txtOut.Clear(); _lblStat.Text = Loc.T("输出已清空"); };
        var btnCopyOut = NewButton("复制输出", 74);
        btnCopyOut.Click += (_, _) => CopyOutput();
        var btnSaveOut = NewButton("另存输出…", 80);
        btnSaveOut.Click += (_, _) => SaveOutput();
        // RightToLeft 下后加的靠右，按逆序加入才能保持从左到右的阅读顺序
        outBtns.Controls.Add(btnSaveOut);
        outBtns.Controls.Add(btnCopyOut);
        outBtns.Controls.Add(btnClearOut);
        grid.Controls.Add(outBtns, 0, 3);

        _txtOut = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            Font = new Font("Consolas", 10F),
            MaxLength = KeepOutputChars * 3,
            BorderStyle = BorderStyle.FixedSingle,
        };
        grid.Controls.Add(_txtOut, 0, 4);

        _lblDevice = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
        };
        grid.Controls.Add(_lblDevice, 0, 5);
    }

    // ── 列表 ──

    void RefreshCategories()
    {
        // 保留当前选中项：比较的是「还原后的分类名」，因为切语言后 Text 会从中文变成英文
        var keep = _cboCat.SelectedIndex <= 0 ? null : _cboCat.Text.Trim();
        _cboCat.Items.Clear();
        _cboCat.Items.Add(AllCategories);
        foreach (var c in _store.Categories) _cboCat.Items.Add(CommandStore.CategoryDisplay(c));
        if (keep != null)
        {
            // 显示文本与还原名都要试：新加的分类名和译文不重合
            int i = _cboCat.Items.IndexOf(keep);
            if (i < 0) i = _cboCat.Items.IndexOf(CommandStore.CategoryDisplay(CommandStore.CategoryFromDisplay(keep)));
            _cboCat.SelectedIndex = i > 0 ? i : 0;
        }
        else _cboCat.SelectedIndex = 0;
    }

    void RefreshHistoryCombo()
    {
        if (!_ready) return;
        var keep = _txtCmd.Text;
        _txtCmd.Items.Clear();
        foreach (var h in _store.History.Take(50)) _txtCmd.Items.Add(h.Command);
        _txtCmd.Text = keep;
    }

    /// <summary>
    /// 重填通道下拉框。项文本走 <c>Loc.T</c>，只在<em>构造那一刻</em>求值一次，
    /// ComboBox 的 <c>Items</c> 里存的就是成品字符串——<c>Loc.ApplyTo</c> 遍历控件树
    /// 只认 <c>Loc.Bind</c> 登记过的 <c>Text</c>，下拉项不在其中，
    /// 不重填就会永远停在打开时的语言（英文界面里「通道」下拉显示中文「设备 shell」）。
    /// 选中项按 <see cref="CommandKind"/> 还原，不按文本比对。
    /// </summary>
    void RefreshKindCombo()
    {
        var keep = Kind;
        _cboKind.Items.Clear();
        _cboKind.Items.AddRange(new object[] { CommandKinds.ShellText, CommandKinds.AdbText });
        _cboKind.SelectedIndex = keep == CommandKind.Adb ? 1 : 0;
    }

    void RefreshList()
    {
        if (!_ready) return;
        var keepSelected = (_lv.SelectedItems.Count > 0 ? _lv.SelectedItems[0].Text : null) ?? _txtCmd.Text;
        string filter = _txtFilter.Text.Trim();

        _lv.BeginUpdate();
        _lv.Items.Clear();
        RebuildColumns();

        foreach (var row in EnumerateRows(filter))
        {
            var item = new ListViewItem(row.Cells[0]) { Tag = row.Tag };
            for (int i = 1; i < row.Cells.Length; i++) item.SubItems.Add(row.Cells[i]);
            _lv.Items.Add(item);
        }
        _lv.EndUpdate();

        foreach (ListViewItem item in _lv.Items)
        {
            if (item.Text == keepSelected)
            {
                item.Selected = true;
                item.EnsureVisible();
                break;
            }
        }
        _lblStat.Text = Loc.F("{0} 条", _lv.Items.Count);
    }

    /// 列头走 <c>Loc.BindCol</c> 绑定资源键：<c>Columns.Add(Loc.T(…))</c> 是一次性赋值，
    /// 列头又不在控件树里（<c>Loc.ApplyTo</c> 原本扫不到），切语言时会停在启动时的语言。
    void RebuildColumns()
    {
        _lv.Columns.Clear();
        bool withCat = ShowCategoryColumn;
        // 列宽合计别超过默认面板宽度，否则一打开就带横向滚动条
        _lv.Columns.Add(Loc.BindCol(new ColumnHeader { Text = Loc.T("命令"), Width = withCat ? 250 : 280 }, "命令"));
        if (_rbHistory.Checked)
        {
            _lv.Columns.Add(Loc.BindCol(new ColumnHeader { Text = Loc.T("通道"), Width = 90 }, "通道"));
            _lv.Columns.Add(Loc.BindCol(new ColumnHeader { Text = Loc.T("次数"), Width = 46, TextAlign = HorizontalAlignment.Right }, "次数"));
            _lv.Columns.Add(Loc.BindCol(new ColumnHeader { Text = Loc.T("最近使用"), Width = 110 }, "最近使用"));
        }
        else
        {
            if (withCat)
            {
                _lv.Columns.Add(Loc.BindCol(new ColumnHeader { Text = Loc.T("分类"), Width = 76 }, "分类"));
                _lv.Columns.Add(Loc.BindCol(new ColumnHeader { Text = Loc.T("通道"), Width = 84 }, "通道"));
            }
            else
            {
                // 选了具体分类时分类列没有信息量，把宽度让给命令与备注
                _lv.Columns.Add(Loc.BindCol(new ColumnHeader { Text = Loc.T("通道"), Width = 90 }, "通道"));
            }
            _lv.Columns.Add(Loc.BindCol(new ColumnHeader { Text = Loc.T("备注"), Width = withCat ? 106 : 130 }, "备注"));
        }
    }

    bool ShowCategoryColumn => !_rbHistory.Checked && Category == null;

    IEnumerable<(object Tag, string[] Cells)> EnumerateRows(string filter)
    {
        if (_rbHistory.Checked)
        {
            foreach (var h in _store.History)
            {
                if (filter.Length > 0 && !h.Command.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                yield return (h, new[]
                {
                    h.Command, h.Kind.Text(), h.UseCount.ToString(), h.LastUsed.ToString("MM-dd HH:mm"),
                });
            }
            yield break;
        }

        foreach (var f in _store.FavoritesIn(Category))
        {
            if (filter.Length > 0 &&
                !f.Command.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !(f.Remark ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            var cells = new List<string> { f.Command };
            if (ShowCategoryColumn) cells.Add(f.CategoryText);
            cells.Add(f.Kind.Text() + (f.Root ? " · root" : ""));
            cells.Add(f.RemarkText);
            yield return (f, cells.ToArray());
        }
    }

    CommandEntry? SelectedFavorite() => _lv.SelectedItems.Count > 0 ? _lv.SelectedItems[0].Tag as CommandEntry : null;
    CommandRun? SelectedRun() => _lv.SelectedItems.Count > 0 ? _lv.SelectedItems[0].Tag as CommandRun : null;

    void ShowListMenu(MouseEventArgs e)
    {
        var hit = _lv.GetItemAt(e.X, e.Y);
        if (hit != null) { hit.Selected = true; }

        var menu = new ContextMenuStrip();
        var fav = SelectedFavorite();
        var run = SelectedRun();
        var cmdText = hit?.Text ?? "";

        menu.Items.Add(Loc.T("填入命令框"), null, (_, _) => FillFromSelection());
        if (cmdText.Length > 0)
            menu.Items.Add(Loc.T("直接执行"), null, (_, _) => { FillFromSelection(); Run(); });
        menu.Items.Add(Loc.T("复制命令"), null, (_, _) => { if (cmdText.Length > 0) ClipboardHelper.SetText(cmdText); });
        menu.Items.Add(new ToolStripSeparator());

        if (fav != null)
        {
            menu.Items.Add(Loc.T("编辑收藏…"), null, (_, _) => OnEditFavorite());
            menu.Items.Add(Loc.T("从收藏移除"), null, (_, _) => OnRemoveSelected());
        }
        else if (run != null)
        {
            menu.Items.Add(Loc.T("收藏这条…"), null, (_, _) => OnAddFavorite(run.Command));
            menu.Items.Add(Loc.T("从历史删除"), null, (_, _) => OnRemoveSelected());
        }

        if (_lv.Items.Count > 0) menu.Items.Add(Loc.T("清空当前列表"), null, (_, _) => OnClearList());
        menu.Show(_lv, e.Location);
    }

    /// <summary>把选中条目（收藏或历史）连同通道、root 设置回填到命令框。</summary>
    void FillFromSelection()
    {
        if (SelectedFavorite() is { } fav)
        {
            _txtCmd.Text = fav.Command;
            _cboKind.SelectedItem = fav.Kind.Text();
            _ckRoot.Checked = fav.Root && fav.Kind == CommandKind.Shell;
        }
        else if (SelectedRun() is { } run)
        {
            _txtCmd.Text = run.Command;
            _cboKind.SelectedItem = run.Kind.Text();
            _ckRoot.Checked = run.Root && run.Kind == CommandKind.Shell;
        }
        else return;

        UpdateRootEnabled();
        _lblStat.Text = Loc.T("已填入命令框，按 Enter 执行");
        _txtCmd.Focus();
    }

    // ── 分类与收藏操作 ──

    void OnAddCategory()
    {
        var name = SimpleInputBox.Show(this, Loc.T("新建分类"), Loc.T("分类名称："), "");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!_store.AddCategory(name))
        {
            _lblStat.Text = Loc.T("分类已存在或名称为空");
            return;
        }
        _store.Save();
        RefreshCategories();
        _cboCat.Text = name.Trim();
    }

    void OnRenameCategory()
    {
        var old = Category;
        if (old == null) { _lblStat.Text = Loc.T("请先在分类下拉框选中要改名的分类"); return; }
        var name = SimpleInputBox.Show(this, "重命名分类", Loc.F("把「{0}」改名为：", old), old);
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!_store.RenameCategory(old, name))
        {
            _lblStat.Text = Loc.T("改名失败：新名称为空或已存在");
            return;
        }
        _store.Save();
        RefreshCategories();
        _cboCat.Text = name.Trim();
    }

    void OnDeleteCategory()
    {
        var name = Category;
        if (name == null) { _lblStat.Text = Loc.T("请先在分类下拉框选中要删除的分类"); return; }
        int count = _store.CountIn(name);
        if (!Confirm(Loc.F("删除分类「{0}」？\n\n该分类下的 {1} 条收藏会一并删除。", name, count), "删除分类")) return;
        _store.RemoveCategory(name);
        _store.Save();
        RefreshCategories();
        _cboCat.SelectedIndex = 0;
    }

    void OnAddFavorite(string? commandOverride = null)
    {
        var cmd = commandOverride ?? _txtCmd.Text.Trim();
        if (cmd.Length == 0)
        {
            _lblStat.Text = Loc.T("命令为空，无法收藏");
            return;
        }
        var draft = new CommandEntry
        {
            Category = Category ?? _store.Categories.FirstOrDefault() ?? CommandStore.FallbackCategory,
            Command = cmd,
            Kind = Kind,
            Root = _ckRoot.Checked,
        };
        if (_store.FindFavorite(cmd, draft.Kind) is { } dup)
        {
            _lblStat.Text = Loc.F("该命令已在「{0}」收藏中", dup.Category);
            return;
        }
        using var dlg = new CommandEditDialog(draft);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        if (_store.AddFavorite(dlg.Category, dlg.Command, dlg.Remark, dlg.Kind, dlg.Root) == null)
        {
            _lblStat.Text = Loc.T("收藏失败：命令重复或为空");
            return;
        }
        _store.Save();
        _rbFav.Checked = true;
        RefreshCategories();
        RefreshList();
        _lblStat.Text = Loc.F("已收藏到「{0}」", dlg.Category);
    }

    void OnEditFavorite()
    {
        var fav = SelectedFavorite();
        if (fav == null) { _lblStat.Text = Loc.T("请先在列表中选中一条收藏"); return; }
        using var dlg = new CommandEditDialog(fav);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        if (!_store.UpdateFavorite(fav, dlg.Category, dlg.Command, dlg.Remark, dlg.Kind, dlg.Root))
        {
            _lblStat.Text = Loc.T("保存失败：与另一条收藏的命令重复");
            return;
        }
        _store.Save();
        RefreshCategories();
        RefreshList();
    }

    void OnRemoveSelected()
    {
        if (SelectedFavorite() is { } fav)
        {
            if (!Confirm(Loc.F("从收藏移除？\n{0}", fav.Command), "移除收藏")) return;
            _store.RemoveFavorite(fav);
        }
        else if (SelectedRun() is { } run)
        {
            _store.RemoveRun(run);
        }
        else return;
        _store.Save();
        RefreshList();
        RefreshHistoryCombo();
    }

    void OnClearList()
    {
        bool history = _rbHistory.Checked;
        // 整句交给 Loc.F 查表：译文表里存的是「「{0}」的收藏」这一整条，
        // 若在这里用插值串先拼出中文再塞进 Loc.F，英文态就永远匹配不上（回退显示中文）。
        // 分类名要先转显示文本：Category 存的是中文原文，直接当 {0} 传进去，
        // 英文界面会出现 the favorites in "应用与包" 这种夹中文的句子。
        string what = history
            ? Loc.T("最近使用历史")
            : Loc.F("「{0}」的收藏", Category == null
                ? Loc.T("全部分类")
                : CommandStore.CategoryDisplay(Category));
        if (!Confirm(Loc.F("清空{0}？此操作不可恢复。", what), "清空")) return;
        if (history) _store.ClearHistory(); else _store.ClearFavorites();
        _store.Save();
        RefreshList();
        RefreshHistoryCombo();
    }

    bool Confirm(string text, string title) =>
        MessageBox.Show(this, text, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    // ── 执行 ──

    void UpdateRootEnabled()
    {
        if (!_ready)
        {
            // 构造期 _cboKind 设默认选中会先到这里，那时 _ckRoot 还没创建
            return;
        }
        if (Kind != CommandKind.Shell)
        {
            _ckRoot.Enabled = false;
            _ckRoot.Checked = false;
            return;
        }
        _ckRoot.Enabled = true;
    }

    void OnRootToggled()
    {
        if (_ckRoot.Checked && Kind == CommandKind.Shell && !_rootProvider())
            _lblStat.Text = Loc.T("提示：当前设备未检测到 root，su -c 可能失败并在手机上弹授权");
    }

    void UpdateDeviceLabel()
    {
        var serial = _serialProvider();
        if (string.IsNullOrEmpty(serial))
        {
            _lblDevice.Text = Loc.T("设备：未选择（本机 adb 通道仍可用）");
            return;
        }
        _lblDevice.Text = Loc.F("设备：{0}{1}", serial, _rootProvider() ? "  [root]" : "");
    }

    async void Run()
    {
        if (_running) return;
        var raw = _txtCmd.Text.Trim();
        if (raw.Length == 0)
        {
            _lblStat.Text = Loc.T("请输入命令");
            return;
        }
        var kind = Kind;
        var root = _ckRoot.Checked && kind == CommandKind.Shell;
        var serial = _serialProvider();
        if (kind == CommandKind.Shell && string.IsNullOrEmpty(serial))
        {
            _lblStat.Text = Loc.T("未选择设备：请先在主界面选设备，或把通道切到「本机 adb」");
            return;
        }

        var expanded = ExpandPlaceholders(raw);
        if (expanded == null) return;      // 取消填参数
        _txtCmd.Text = expanded;

        AppendLine($"> {CommandKinds.Describe(kind, expanded, root)}");
        SetStatus(Loc.T("执行中…"));
        _running = true;
        _userStop = false;
        _runLines = 0;
        _btnRun.Enabled = false;
        _btnStop.Enabled = true;
        UpdateDeviceLabel();
        _outTimer.Start();

        var sw = Stopwatch.StartNew();
        var cts = new CancellationTokenSource();
        _cts = cts;
        int timeout = (int)_numTimeout.Value;
        if (timeout > 0) cts.CancelAfter(TimeSpan.FromSeconds(timeout));
        var progress = new QueueProgress(_pending);
        int exitCode = 0;
        string? failure = null;

        try
        {
            if (kind == CommandKind.Shell)
            {
                string cmd = root ? $"su -c {AdbManager.ShellQuote(expanded)}" : expanded;
                await _manager.ShellLinesAsync(serial!, cmd, progress, cts.Token);
            }
            else
            {
                exitCode = await AdbManager.RunAdbAsync(AdbManager.AdbArgs(expanded, serial), progress, cts.Token);
            }
            // 枚举被取消时 adb 库会直接结束迭代而不抛异常，这里补上，否则会被当成正常执行完成
            if (cts.IsCancellationRequested)
                failure = _userStop ? "已手动停止" : Loc.F("超过 {0} 秒未结束，已中断", timeout);
        }
        catch (OperationCanceledException)
        {
            failure = _userStop ? "已手动停止" : Loc.F("超过 {0} 秒未结束，已中断", timeout);
        }
        catch (Exception ex)
        {
            failure = FriendlyError(ex);
        }
        finally
        {
            _outTimer.Stop();
            FlushPending();
            _running = false;
            _btnRun.Enabled = true;
            _btnStop.Enabled = false;
            _cts?.Dispose();
            _cts = null;
        }

        FlushPending();
        sw.Stop();
        _store.RecordRun(expanded, kind, root);
        _store.Save();
        RefreshHistoryCombo();
        if (!_rbHistory.Checked) RefreshList();

        if (failure != null)
        {
            AppendLine($"!! {failure}");
            SetStatus(Loc.F("失败 · {0:F1}s · {1} 行", sw.Elapsed.TotalSeconds, _runLines));
        }
        else
        {
            if (kind == CommandKind.Adb) AppendLine(Loc.F("— 退出码 {0}", exitCode));
            SetStatus(Loc.F("完成 · {0:F1}s · {1} 行", sw.Elapsed.TotalSeconds, _runLines) +
                      (kind == CommandKind.Adb && exitCode != 0 ? Loc.F(" · 退出码 {0}", exitCode) : ""));
        }
        _txtCmd.Focus();
    }

    /// <summary>
    /// adb 对「没拿到输出的失败命令」一律报 shell command has become unresponsive，
    /// 这句对用户没有信息量，换成可行动的提示。
    /// </summary>
    static string FriendlyError(Exception ex)
    {
        var msg = ex.Message ?? "";
        if (msg.Contains("unresponsive", StringComparison.OrdinalIgnoreCase))
            msg = Loc.T("命令没有返回输出：可能是命令不存在、权限不足，或输出里含报错文本被 adb 判定为失联（可试试勾选 root）");
        var inner = ex.InnerException?.Message;
        if (!string.IsNullOrEmpty(inner) && !msg.Contains(inner, StringComparison.Ordinal))
            msg += $"（{inner}）";
        return msg;
    }

    void Stop()
    {
        if (!_running) return;
        _userStop = true;
        try { _cts?.Cancel(); } catch { }
        SetStatus(Loc.T("停止中…"));
    }

    // ── 输出 ──

    /// <summary>只入队，由 UI 定时器成批刷进文本框；逐行 Post 到界面线程会被高频输出拖垮。</summary>
    sealed class QueueProgress(ConcurrentQueue<string> queue) : IProgress<string>
    {
        public void Report(string value) => queue.Enqueue(value ?? "");
    }

    void FlushPending()
    {
        if (_pending.IsEmpty) return;
        var sb = new StringBuilder();
        int lines = 0;
        while (_pending.TryDequeue(out var line))
        {
            sb.Append(line).Append(Environment.NewLine);
            lines++;
            if (sb.Length > 200_000) break;
        }
        if (sb.Length == 0) return;
        _runLines += lines;
        AppendText(sb.ToString());
    }

    void AppendLine(string text)
    {
        _pending.Enqueue(text);
        if (!_running) FlushPending();
    }

    void AppendText(string text)
    {
        // 超出上限时丢掉最早的行，保证最新输出始终可见
        if (_txtOut.TextLength + text.Length > KeepOutputChars)
        {
            int cut = KeepOutputChars / 2;
            var head = _txtOut.Text;
            if (head.Length > cut)
            {
                int nl = head.IndexOf('\n', cut);
                if (nl > 0) head = head[(nl + 1)..];
                _txtOut.Text = Loc.T("…（最早的输出已丢弃）…\n") + head;
            }
        }
        _txtOut.AppendText(text);
        if (_ckFollow.Checked)
        {
            _txtOut.SelectionStart = _txtOut.TextLength;
            _txtOut.ScrollToCaret();
        }
    }

    void SetStatus(string text) => _lblStat.Text = text;

    void CopyOutput()
    {
        if (_txtOut.TextLength == 0) { SetStatus(Loc.T("没有输出可复制")); return; }
        ClipboardHelper.SetText(_txtOut.Text);
        SetStatus(Loc.T("输出已复制到剪贴板"));
    }

    void SaveOutput()
    {
        if (_txtOut.TextLength == 0) { SetStatus(Loc.T("没有输出可保存")); return; }
        using var dlg = new SaveFileDialog
        {
            Title = Loc.T("保存命令输出"),
            FileName = $"command_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            Filter = Loc.T("文本文件 (*.txt)|*.txt|日志文件 (*.log)|*.log|所有文件 (*.*)|*.*"),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dlg.FileName, _txtOut.Text);
            SetStatus(Loc.F("已保存 → {0}", dlg.FileName));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("保存失败"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ── 占位符 ──

    static readonly Regex PlaceholderRegex = new(@"\{([A-Za-z][\w\-]*)\}", RegexOptions.Compiled);

    /// <summary>
    /// 把 {name} 换成用户输入的值；取消时返回 null。
    /// 填过的值记进命令存储，下次作为默认值给出（包名还会借用 run-as 收藏）。
    /// </summary>
    string? ExpandPlaceholders(string command)
    {
        var names = PlaceholderRegex.Matches(command).Select(m => m.Groups[1].Value).Distinct().ToArray();
        if (names.Length == 0) return command;

        var map = new Dictionary<string, string>();
        foreach (var name in names)
        {
            var def = _store.PlaceholderValue(name);
            if (def.Length == 0 && name.Equals("pkg", StringComparison.OrdinalIgnoreCase))
                def = FavoritesStore.Default.RunAsPackages.FirstOrDefault() ?? "";
            var value = SimpleInputBox.Show(this, "填写命令参数", Loc.F("为 {{{0}}} 输入值：", name), def);
            if (value == null) return null;
            value = value.Trim();
            if (value.Length == 0)
            {
                MessageBox.Show(this, Loc.F("参数 {{{0}}} 不能为空，否则命令会拼错。", name),
                    Loc.T("填写命令参数"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
            map[name] = value;
            _store.RememberPlaceholder(name, value);
        }
        return PlaceholderRegex.Replace(command, m => map[m.Groups[1].Value]);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _outTimer.Stop();
        FlushPending();
        try { _cts?.Cancel(); } catch { }
        base.OnFormClosing(e);
    }
    /// <summary>
    /// 切语言后重算带参数的动态文本（设备标签、状态行）。
    /// 静态文案已由 <c>Loc.Bind</c> 登记，这里只管带变量的部分。
    /// </summary>
    void ILocalizedUi.OnLanguageChanged()
    {
        UpdateDeviceLabel();     // 已有方法：按当前设备/root 重算标签
        RefreshCategories();     // 下拉框里含译文项（"（全部）"/分类名），必须重填
        RefreshKindCombo();      // 同上：通道名的显示文本是译文，构造时求值一次就固化在 Items 里
        RefreshList();           // 列表单元格里也是译文（通道名 / 备注 / 内置分类），要整表重建
        PerformLayout();
        // 按钮启用态不重算：它只在具体事件（开始/结束执行）里改，
        // 切语言不会改变这些状态，_btnRun.Enabled 等保持原值即为正确。
    }
}
