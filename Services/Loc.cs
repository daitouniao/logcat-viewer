using System.Drawing;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace logcat.Services;

/// <summary>界面语言。</summary>
public enum UiLang
{
    /// <summary>简体中文。</summary>
    ZhCn = 0,

    /// <summary>English。</summary>
    En = 1,
}

/// <summary>
/// 界面字符串本地化。中英双语，运行时可切换。
///
/// ── 设计要点 ──
/// 1. **中文原文即资源键**：调用写作 <c>L("重载")</c>。中文态直接返回 "重载"（零查表），
///    英文态查 <see cref="En"/> 表返回 "Reload"。好处：调用点自带语义、可全文 grep、
///    漏翻译时自动回退中文原文（不会显示空串或裸占位符）。
/// 2. **绑控件而非重建窗体**：<see cref="Bind{T}"/> 把「控件 → 资源键」记在
///    <see cref="ConditionalWeakTable"/> 里（不用 <c>Tag</c>——主窗体已用它做
///    "adb" / "adb-free" 标记）。切换语言时 <see cref="ApplyTo(Control)"/> 遍历控件树重赋文本，
///    已打开的文件、过滤条件、标记、滚动位置全部保留。
/// 3. **运行时算出的文本走 <see cref="RegisterRefresh"/>**：状态栏等没有稳定控件可绑，
///    由各窗口注册刷新回调。
/// 4. **中英宽度差异**：见 <see cref="W"/>、<see cref="Sz"/>、<see cref="FitWidth"/>。
///    总原则是「能用 AutoSize 就不用固定宽度」；实在要固定坐标的（FixedDialog 里的
///    确定/取消、绝对定位的按钮）才按语言分别给宽度。
/// </summary>
public interface ILocalizedUi
{
    /// <summary>语言已切换（绑定文本/字体/宽度已由 <see cref="Loc.ApplyToAllForms"/> 刷过一遍）。</summary>
    void OnLanguageChanged();
}

public static class Loc
{
    static UiLang _lang;
    static Font? _font;

    /// <summary>控件上绑定的资源键（弱引用，控件回收即释放，不会泄漏）。</summary>
    sealed class BoundText
    {
        public string? Text;
        public string? Tip;
        public string? Hint;
        public string? Col;
    }

    static readonly ConditionalWeakTable<object, BoundText> _bound = new();

    /// <summary>字体自管理的控件：语言切换时不覆盖其字体（日志列表按字号自绘，不能动）。</summary>
    static readonly ConditionalWeakTable<object, object> _fontPinned = new();

    static readonly List<Action> _refresh = new();

    /// <summary>程序级 ToolTip 实例。WinForms 的 tooltip 挂在组件上，
    /// 语言切换后需重新 SetToolTip 才能刷新文案，故必须复用同一个实例。</summary>
    static readonly ToolTip _tip = new() { AutoPopDelay = 4000, InitialDelay = 500 };

    static Loc() => _lang = DetectLang();

    // ── 语言 ──

    /// <summary>当前界面语言。</summary>
    public static UiLang Current => _lang;

    /// <summary>切换语言。相同语言时不做任何事（避免无谓的全树刷新）。</summary>
    /// <param name="lang">目标语言。</param>
    /// <param name="save">是否写入 settings.json 持久化。</param>
    public static void SetLang(UiLang lang, bool save = true)
    {
        if (_lang == lang) return;
        _lang = lang;
        if (save)
        {
            try
            {
                AppSettings.Default.Language = LangCode(lang);
                AppSettings.Default.Save();
            }
            catch (Exception ex) { StartupLog.Write($"[Loc] 语言设置保存失败: {ex.Message}"); }
        }

        var font = UiFont;
        ApplyToAllForms(_font, font);
        _font = font;

        foreach (var a in _refresh.ToArray())
        {
            try { a(); } catch (Exception ex) { StartupLog.Write($"[Loc] 刷新回调异常: {ex.Message}"); }
        }
    }

    /// <summary>把语言写成 settings.json 里的字符串值。</summary>
    public static string LangCode(UiLang lang) => lang == UiLang.En ? "en" : "zh-CN";

    /// <summary>
    /// 临时切到指定语言，返回一个「恢复原语言」的委托。**仅供单元测试使用**：
    /// 走 save=false，免得测试把语言写进用户真实的 settings.json。
    /// </summary>
    public static Action SetLangGuard(UiLang lang)
    {
        var origin = _lang;
        SetLang(lang, save: false);
        return () => SetLang(origin, save: false);
    }

    /// <summary>解析语言代码。非中文一律英文（含系统既非中文也非英文的情形）。</summary>
    public static UiLang ParseLang(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return UiLang.En;
        var n = code!.Replace("_", "-");
        return n.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? UiLang.ZhCn : UiLang.En;
    }

    /// <summary>推断初始语言：优先已保存的设置，否则按系统区域（当前用户区域 → 非特定区域）。</summary>
    public static UiLang DetectLang()
    {
        try
        {
            var saved = AppSettings.Default.Language;
            if (!string.IsNullOrWhiteSpace(saved)) return ParseLang(saved);
        }
        catch { /* 设置损坏时退回系统区域 */ }

        var ci = CultureInfo.CurrentUICulture;
        if (!string.IsNullOrEmpty(ci.Name)) return ParseLang(ci.Name);
        return ParseLang(CultureInfo.InstalledUICulture.Name);
    }

    // ── 字体 ──

    static readonly Font FontZh = new("Microsoft YaHei UI", 9F);
    static readonly Font FontEn = new("Segoe UI", 9F);

    /// <summary>当前语言对应的界面字体。
    ///
    /// 中文必须用「微软雅黑 UI」：默认的 Segoe UI 没有中文字形，高 DPI 下 GDI 回退字体
    /// 「测量宽度 ≠ 绘制宽度」，中文会被裁成残缺字形（125% 缩放下「新建」只画出「新」）。
    /// 英文态用原生 Segoe UI，观感更地道。</summary>
    public static Font UiFont => _lang == UiLang.ZhCn ? FontZh : FontEn;

    /// <summary>登记程序启动时设置的默认字体，供语言切换时识别哪些控件需要换字体。</summary>
    public static void SetDefaultFont(Font f) => _font = f;

    /// <summary>标记控件字体为「自管理」，语言切换时不覆盖（日志列表按用户字号自绘）。</summary>
    public static void PinFont(object o)
    {
        if (o == null) return;
        _fontPinned.Remove(o);
        _fontPinned.Add(o, o);
    }

    // ── 取文本 ──

    /// <summary>取界面文本。中文态零开销；英文态查表，缺译文回退中文原文。</summary>
    public static string T(string zh)
    {
        if (zh.Length == 0 || _lang == UiLang.ZhCn) return zh;
        return En.TryGetValue(zh, out var v) && v.Length > 0 ? v : zh;
    }

    /// <summary>取带参数的界面文本。占位符用 <c>{0}</c> 风格，译文须保持相同的占位符个数
    /// 与类型（由 <c>LocTableTests</c> 断言强制）。格式串异常时返回未格式化的译文，
    /// 宁可多显示几个花括号，也不要在弹窗里抛异常。</summary>
    public static string F(string zh, params object?[] args)
    {
        var t = T(zh);
        if (args.Length == 0) return t;
        try { return string.Format(CultureInfo.CurrentCulture, t, args); }
        catch (FormatException) { return t; }
    }

    /// <summary>英文译文表（合并自 <see cref="LocTable"/> 的各分区）。键 = 中文原文。</summary>
    static Dictionary<string, string>? _en;

    /// <summary>英文译文表，只读。</summary>
    public static IReadOnlyDictionary<string, string> En => _en ??= BuildEn();

    static Dictionary<string, string> BuildEn()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in new[]
        {
            LocTable.Common(), LocTable.MainUi(), LocTable.FilterUi(), LocTable.DeviceUi(),
            LocTable.ApkUi(), LocTable.CommandUi(), LocTable.CommandUiMain(), LocTable.FileUi(),
            LocTable.RuntimeMsg(), LocTable.CommandSeed(),
        })
        {
            foreach (var kv in part) d[kv.Key] = kv.Value;
        }
        return d;
    }

    // ── 宽度：中英文宽度不同 ──

    /// <summary>按语言取固定宽度。英文单词比同义中文长，固定宽度处需分别给值。</summary>
    public static int W(int zh, int en) => _lang == UiLang.En ? en : zh;

    /// <summary>按语言取控件尺寸，供 FixedDialog / 绝对定位控件使用。</summary>
    public static Size Sz(int zhW, int zhH, int enW, int enH)
        => _lang == UiLang.En ? new Size(enW, enH) : new Size(zhW, zhH);

    /// <summary>量出文本在给定字体下的像素宽（与 WinForms AutoSize 同源的测量路径）。</summary>
    public static int TextWidth(string text, Font font)
        => text.Length == 0 ? 0 : TextRenderer.MeasureText(text, font).Width;

    /// <summary>AutoSize=false 的控件按当前文本实测宽度撑开（只放大不缩小，且不小于下限）。
    /// 用于「文案随语言变长、但控件是固定宽度」的按钮与标签。</summary>
    public static void FitWidth(Control c, int minWidth = 0, int padding = 14)
    {
        try
        {
            if (c.AutoSize) return;  // AutoSize 控件由 WinForms 自行测量，无需干预
            int need = TextWidth(c.Text ?? "", c.Font) + padding;
            int w = Math.Max(c.Width, Math.Max(need, minWidth));
            if (w != c.Width) c.Width = w;
        }
        catch { /* 量测失败保持原宽 */ }
    }

    /// <summary>
    /// 按实测文本宽度创建一个定宽按钮（绝对定位布局用），并把文本<b>绑定</b>到资源键。
    /// 绝对定位的对话框里不能用 AutoSize（父容器不参与布局），必须显式给够宽度，
    /// 否则英文态（Segoe UI 下英文单词普遍比同义中文宽）会裁字。
    ///
    /// <para>
    /// 走<see cref="Bind{TObj}"/> 而不是 <c>Loc.T</c> 一次性赋值很关键：
    /// 只有绑定了资源键，<see cref="ApplyTo(Control)"/> 才能在切语言时重设文本，
    /// <see cref="FitWidths"/> 也才能按<b>新</b>文本重新量宽。只用 Loc.T 赋值的控件
    /// 切语言后会停留在旧语言文本 + 旧宽度上（英文文案更长 → 裁字）。
    /// </para>
    /// </summary>
    public static Button SizedButton(string text, int zhMinWidth = 0, int height = 24, int padding = 16)
        => SizedButtonCore(text, text, zhMinWidth, height, padding);

    /// <summary>
    /// <see cref="SizedButton(string,int,int,int)"/> 的显式键版本：<paramref name="text"/> 只是
    /// 当前语言的显示文本，<paramref name="zhKey"/> 是中文原文（资源键）。
    /// 调用点已持有 <c>Loc.T("…")</c> 的结果时用它，避免重复查表。
    /// </summary>
    public static Button SizedButton(string text, string zhKey, int zhMinWidth = 0, int height = 24, int padding = 16)
        => SizedButtonCore(text, zhKey, zhMinWidth, height, padding);

    static Button SizedButtonCore(string text, string zhKey, int zhMinWidth, int height, int padding)
    {
        int need = TextWidth(text, UiFont) + padding;
        return Bind(new Button
        {
            Size = new Size(Math.Max(need, zhMinWidth), height),
        }, zhKey);
    }

    /// <summary>
    /// 把「确定 / 取消」这类按钮对按右边缘对齐排布，两种语言都用同一套调用。
    /// <paramref name="right"/> 是按钮右边缘的 x 坐标；英文更宽时整对向左让位，不会越界。
    /// </summary>
    public static void LayoutButtonPair(Control parent, Button ok, Button cancel, int right, int y, int gap = 6)
    {
        cancel.Location = new Point(right - cancel.Width, y);
        ok.Location = new Point(cancel.Left - gap - ok.Width, y);
        parent.Controls.Add(ok);
        parent.Controls.Add(cancel);
    }

    /// <summary>
    /// 量出多行文本在给定宽度下实际需要的高度（像素）。
    /// 用于「说明性Label 固定高度装不下两种语言」的场景：
    /// 英文句子普遍比中文长，同一宽度下要换 3~4 行而不是 2 行。
    /// </summary>
    public static int TextHeight(string text, Font font, int width)
    {
        if (string.IsNullOrEmpty(text) || width <= 0) return 0;
        try
        {
            using var bmp = new Bitmap(1, 1);
            using var g = Graphics.FromImage(bmp);
            g.PageUnit = GraphicsUnit.Pixel;
            var sz = g.MeasureString(text, font, width);
            return (int)Math.Ceiling(sz.Height) + 2;
        }
        catch { return TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue)).Height; }
    }

    /// <summary>
    /// 按当前文本与给定宽度算出Label 需要的高度并设上去。
    /// 固定高度的说明 Label 在另一种语言下会被截断（中文 2 行放得下，英文要 4 行），
    /// 切语言时必须重算——这是<code>Loc.Bind</code> 之外必须单独处理的动态尺寸。
    /// </summary>
    public static void FitHeight(Control c, int width)
    {
        try
        {
            if (c.AutoSize) return;
            int h = TextHeight(c.Text ?? "", c.Font, width);
            if (h > 0 && c.Height != h) c.Height = h;
        }
        catch { /* 量测失败保持原高 */ }
    }

    /// <summary>
    /// 遍历子树，对所有「固定宽度但文案可变」的控件重新撑宽（AutoSize 的自动跳过）。
    /// </summary>
    public static void FitWidths(Control root)
    {
        try
        {
            foreach (Control c in AllControls(root)) FitWidth(c);
        }
        catch (Exception ex) { StartupLog.Write($"[Loc] FitWidths 异常: {ex.Message}"); }
    }

    // ── 绑定 ──

    static BoundText Slot(object o)
    {
        if (_bound.TryGetValue(o, out var b)) return b;
        b = new BoundText();
        _bound.Add(o, b);
        return b;
    }

    /// <summary>把控件/菜单项的显示文本绑定到资源键，并立即应用。
    /// 适用 <see cref="Control"/>（Label / Button / CheckBox / GroupBox …）与
    /// <see cref="ToolStripItem"/>（ToolStripButton / MenuItem / StatusLabel …）。</summary>
    public static TObj Bind<TObj>(TObj o, string zh) where TObj : class
    {
        ArgumentNullException.ThrowIfNull(o);
        Slot(o).Text = zh;
        ApplyText(o, T(zh));
        return o;
    }

    /// <summary>绑定义文本（<see cref="ColumnHeader.Text"/>）。</summary>
    public static TObj BindCol<TObj>(TObj o, string zh) where TObj : class
    {
        ArgumentNullException.ThrowIfNull(o);
        Slot(o).Col = zh;
        if (o is ColumnHeader h) h.Text = T(zh);
        return o;
    }

    /// <summary>绑定提示文本。<see cref="ToolStripItem"/> 走 <c>ToolTipText</c>，
    /// 普通 <see cref="Control"/> 走程序级 <see cref="ToolTip"/> 组件（切换语言后需重设）。</summary>
    public static TObj BindTip<TObj>(TObj o, string zh) where TObj : class
    {
        ArgumentNullException.ThrowIfNull(o);
        Slot(o).Tip = zh;
        ApplyTip(o, T(zh));
        return o;
    }

    /// <summary>绑定输入框占位提示（<see cref="TextBox.PlaceholderText"/>）。</summary>
    public static TObj BindHint<TObj>(TObj o, string zh) where TObj : class
    {
        if (o is TextBox tb) { Slot(o).Hint = zh; tb.PlaceholderText = T(zh); }
        return o;
    }

    static void ApplyText(object o, string v)
    {
        switch (o)
        {
            case ToolStripItem i: i.Text = v; break;
            case Control c: c.Text = v; break;
        }
    }

    static void ApplyTip(object o, string v)
    {
        switch (o)
        {
            case ToolStripItem i: i.ToolTipText = v; break;
            case Control c: _tip.SetToolTip(c, v); break;
        }
    }

    /// <summary>注册「语言切换后需要额外刷新」的动作（动态文本、尺寸重算等）。
    /// 注意：不参与控件 GC 判定，窗口自己负责在 Dispose 时
    /// <see cref="UnregisterRefresh"/>，否则回调会一直持有窗口引用。</summary>
    public static void RegisterRefresh(Action a)
    {
        if (a != null && !_refresh.Contains(a)) _refresh.Add(a);
    }

    /// <summary>注销刷新回调（窗口销毁时调用，避免闭包把窗口钉在内存里）。</summary>
    public static void UnregisterRefresh(Action a) => _refresh.Remove(a);

    // ── 应用 ──

    /// <summary>对一棵控件树重应用所有绑定的文本 / 提示 / 占位符，并重新布局。</summary>
    public static void ApplyTo(Control? root)
    {
        if (root == null) return;
        try
        {
            Walk(root, new HashSet<object>(ReferenceEqualityComparer.Instance));
            root.PerformLayout();
        }
        catch (Exception ex) { StartupLog.Write($"[Loc] ApplyTo 异常: {ex.Message}"); }
    }

    static void Walk(object o, HashSet<object> seen)
    {
        if (o == null || !seen.Add(o)) return;

        if (_bound.TryGetValue(o, out var b))
        {
            if (b.Text != null) ApplyText(o, T(b.Text));
            if (b.Tip != null) ApplyTip(o, T(b.Tip));
            if (b.Hint != null && o is TextBox tb) tb.PlaceholderText = T(b.Hint);
            if (b.Col != null && o is ColumnHeader col) col.Text = T(b.Col);
        }

        // ToolStrip 也是 Control：其 Items 不是子控件，须单独下钻（menu / dropdown / 收藏菜单）
        if (o is ToolStrip ts)
        {
            foreach (ToolStripItem i in ts.Items) Walk(i, seen);
        }
        else if (o is ToolStripDropDownItem dd)
        {
            foreach (ToolStripItem i in dd.DropDownItems) Walk(i, seen);
        }

        if (o is Control ctl)
        {
            // ListView 的列头不是子控件（不在 ctl.Controls 里），须顺着 Columns 单独下钻，
            // 否则列表列头切语言时永远停在启动时的语言
            if (ctl is ListView lv)
            {
                foreach (ColumnHeader col in lv.Columns) Walk(col, seen);
            }

            foreach (Control ch in ctl.Controls) Walk(ch, seen);
            var cm = ctl.ContextMenuStrip;
            if (cm != null) Walk(cm, seen);
        }
    }

    /// <summary>语言切换后统一刷新：遍历所有已打开窗体重应用文本、字体与宽度，
    /// 再执行各窗口注册的刷新回调与 <see cref="ILocalizedUi"/> 钩子。</summary>
    public static void ApplyToAllForms(Font? oldFont, Font newFont)
    {
        // TopLevel=false 的嵌入页签不在 Application.OpenForms 里（设备操作窗口的各页就是），
        // 由 DeviceOpsDialog 作为宿主转发，故这里只遍历顶层窗体即可
        foreach (Form f in Application.OpenForms.Cast<Form>().ToArray())
        {
            try
            {
                ApplyTo(f);
                SwapFont(f, oldFont, newFont);
                FitWidths(f);
                f.PerformLayout();
            }
            catch (Exception ex) { StartupLog.Write($"[Loc] 刷新窗体失败 {f.GetType().Name}: {ex.Message}"); }
        }

        foreach (var a in _refresh.ToArray())
        {
            try { a(); } catch (Exception ex) { StartupLog.Write($"[Loc] 刷新回调异常: {ex.Message}"); }
        }

        foreach (Form f in Application.OpenForms.Cast<Form>().ToArray())
        {
            if (f is ILocalizedUi ui)
            {
                try { ui.OnLanguageChanged(); }
                catch (Exception ex) { StartupLog.Write($"[Loc] OnLanguageChanged 失败 {f.GetType().Name}: {ex.Message}"); }
            }
        }
    }

    /// <summary>
    /// 刷新一棵<b>不在 <see cref="Application.OpenForms"/> 里</b>的控件树，
    /// 补做 <see cref="ApplyToAllForms"/> 对每个顶层窗体做的三件事：绑定文本、字体、宽度。
    ///
    /// <para>
    /// 典型场景：非模态窗口在主窗体构造函数里就 <c>new</c> 出来了，但<b>首次 Show 之前</b>
    /// 它不在 <see cref="Application.OpenForms"/> 里 —— 「启动中文 → 切英文 → 才首次打开该窗口」
    /// 这条路径下 <see cref="ApplyToAllForms"/> 一次都扫不到它，窗口会带着启动时的旧语言弹出。
    /// 宿主窗体需在自己的 <see cref="ILocalizedUi.OnLanguageChanged"/> 里显式调一次。
    /// </para>
    ///
    /// <para>
    /// 必须在 <see cref="ApplyToAllForms"/> 内部（即 <see cref="_font"/> 尚未被新字体覆盖时）调用，
    /// <see cref="SwapFont"/> 才能比出「哪些控件还在用旧默认字体」。
    /// </para>
    /// </summary>
    public static void ApplyToDetached(Control? root)
    {
        if (root == null) return;
        try
        {
            ApplyTo(root);
            SwapFont(root, _font, UiFont);
            FitWidths(root);
            root.PerformLayout();
        }
        catch (Exception ex) { StartupLog.Write($"[Loc] ApplyToDetached 异常: {ex.Message}"); }
    }

    /// <summary>把「仍使用旧默认字体」的控件换成新字体。已自设字体的控件不动，
    /// 被 <see cref="PinFont"/> 标记的（日志列表）也不动。</summary>
    static void SwapFont(object o, Font? oldF, Font newF)
    {
        if (o is not Control c || oldF == null) return;
        if (_fontPinned.TryGetValue(o, out _)) return;
        try { if (c.Font.Equals(oldF)) c.Font = newF; } catch { /* 字体被占用时跳过 */ }
    }

    // ── 遍历辅助 ──

    /// <summary>深度优先枚举控件树（含所有层级的子控件）。</summary>
    public static IEnumerable<Control> AllControls(Control root)
    {
        var stack = new Stack<Control>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var c = stack.Pop();
            yield return c;
            foreach (Control ch in c.Controls) stack.Push(ch);
        }
    }

    /// <summary>枚举控件树里所有菜单项 / 工具栏项（含下拉菜单与右键菜单）。</summary>
    public static IEnumerable<ToolStripItem> AllToolItems(Control root)
    {
        //遍历时把 ToolStripItem 也塞进队列（它不是 Control，只能顺着 ToolStrip /
        // ToolStripDropDownItem 的 Items 下去），故这些项本身都已在 visited 里，
        // 最后按类型筛出来即可，无需再各自下钻一次。
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var q = new Queue<object>();
        q.Enqueue(root);
        while (q.Count > 0)
        {
            var o = q.Dequeue();
            if (!visited.Add(o)) continue;

            if (o is ToolStrip ts) foreach (ToolStripItem i in ts.Items) q.Enqueue(i);
            else if (o is ToolStripDropDownItem dd) foreach (ToolStripItem i in dd.DropDownItems) q.Enqueue(i);

            if (o is Control c)
            {
                foreach (Control ch in c.Controls) q.Enqueue(ch);
                if (c.ContextMenuStrip != null) q.Enqueue(c.ContextMenuStrip);
            }
        }

        foreach (var o in visited)
            if (o is ToolStripItem item) yield return item;
    }
}