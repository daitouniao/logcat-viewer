namespace logcat.Services;

/// <summary>
/// 高 DPI 布局兜底：TableLayoutPanel 的绝对行高在「嵌入 TabPage / 跨屏 DPI 变化」的缩放链路里
/// 不保证被重算，行内控件（随字体变大）会被裁掉一截。这里按行内控件的实测底边把装不下的
/// 绝对行撑大（只放大不缩小）；若缩放链路正常、行高本就足够，则不做任何改动。
/// </summary>
static class DpiFix
{
    /// <summary>对 root 子树做两类兜底：TLP 绝对行高、Dock=Top/Bottom 的固定高按钮栏。</summary>
    public static void Apply(Control root)
    {
        ExpandAbsoluteRows(root);
        ExpandDockedBars(root);
    }

    public static void ExpandAbsoluteRows(Control root)
    {
        try
        {
            if (root is TableLayoutPanel tlp) Fix(tlp);
            foreach (Control c in root.Controls) ExpandAbsoluteRows(c);
        }
        catch { /* 兜底逻辑决不能影响主流程 */ }
    }

    static void Fix(TableLayoutPanel tlp)
    {
        for (int r = 0; r < tlp.RowCount && r < tlp.RowStyles.Count; r++)
        {
            if (tlp.RowStyles[r].SizeType != SizeType.Absolute) continue;
            int need = 0;
            foreach (Control c in tlp.Controls)
                if (tlp.GetCellPosition(c).Row == r)
                    need = Math.Max(need, Measure(c) + c.Margin.Vertical);
            if (need > tlp.RowStyles[r].Height)
            {
                DpiDiag.Write($"DpiFix: {tlp.Parent?.GetType().Name ?? "?"} 绝对行{r} {tlp.RowStyles[r].Height}→{need}");
                tlp.RowStyles[r].Height = need;
            }
        }
    }

    /// <summary>行内控件的实测内容高度：有子控件取最大底边+容器底内边距，否则取测量的文本高度。</summary>
    static int Measure(Control c)
    {
        int max = 0;
        foreach (Control child in c.Controls)
            max = Math.Max(max, child.Bottom + child.Margin.Bottom);
        if (max > 0) return max + c.Padding.Bottom;
        try { return Math.Max(c.Height, c.GetPreferredSize(Size.Empty).Height); }
        catch { return c.Height; }
    }

    /// <summary>
    /// 停靠在顶部/底部的固定高容器（如 Height=40 的按钮栏、Height=30 的复选框）随字体变大后
    /// 内容可能超出；按实测撑大。ToolStrip/StatusStrip 自带高度管理，明确排除。
    /// </summary>
    static void ExpandDockedBars(Control root)
    {
        try
        {
            if (root is not ToolStrip && root.Dock is DockStyle.Top or DockStyle.Bottom
                && (root.HasChildren || root is CheckBox or Label))
            {
                int need = Measure(root) + root.Margin.Vertical;
                if (need > root.Height)
                {
                    DpiDiag.Write($"DpiFix: {root.Parent?.GetType().Name ?? "?"}.{root.GetType().Name} 停靠高 {root.Height}→{need}");
                    root.Height = need;
                }
            }
            foreach (Control c in root.Controls) ExpandDockedBars(c);
        }
        catch { /* 兜底逻辑决不能影响主流程 */ }
    }
}
