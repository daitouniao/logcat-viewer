namespace Next.App.Selection;

/// <summary>单击手势类型（由修饰键决定，主窗负责换算）。</summary>
internal enum SelectionClickKind
{
    /// <summary>普通单击：重设选择并置锚点。</summary>
    Plain,

    /// <summary>Ctrl 单击：切换该行并置锚点。</summary>
    Toggle,

    /// <summary>Shift 单击：从锚点扩到目标行，不移动锚点。</summary>
    Extend,
}

/// <summary>
/// 选择手势控制器（design D3）：锚点只由普通 / Ctrl 单击更新；Shift 始终以锚点重算区间，
/// 连续 Shift 不累积。无行（留白 / 空文件 / 滚动条）时不动任何状态。
/// </summary>
internal sealed class SelectionGesture
{
    readonly RowSelection _selection;

    public SelectionGesture(RowSelection selection) => _selection = selection;

    public long? Anchor { get; private set; }

    /// <summary>应用一次点击；返回是否改动了选择（无行时 false）。</summary>
    public bool Click(long? row, SelectionClickKind kind)
    {
        if (row is not { } target)
        {
            return false;
        }
        switch (kind)
        {
            case SelectionClickKind.Toggle:
                _selection.Toggle(target);
                Anchor = target;
                break;
            case SelectionClickKind.Extend when Anchor is { } anchor:
                _selection.ReplaceWithRange(Math.Min(anchor, target), Math.Max(anchor, target));
                break;
            default:
                // 普通单击；Shift 无锚点时同样按普通单击处理
                _selection.ReplaceWith(target);
                Anchor = target;
                break;
        }
        return true;
    }

    /// <summary>重新装载 / 结果集变化：清空选择与锚点。</summary>
    public void Reset()
    {
        Anchor = null;
        _selection.Clear();
    }
}
