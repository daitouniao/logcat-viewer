namespace Next.App.Selection;

/// <summary>
/// 指针命中纯逻辑（design D2）：局部坐标换算为行序，落在滚动条 / 留白 / 空文件时返回 null。
/// </summary>
internal static class SelectionHitTest
{
    /// <summary>
    /// 视口内指针位置 → 行序。
    /// </summary>
    /// <param name="offsetY">纵向滚动偏移。</param>
    /// <param name="y">指针相对滚动容器的局部 Y。</param>
    /// <param name="rowHeight">逻辑行高（与绘制同一常量）。</param>
    /// <param name="totalRows">当前结果集行数。</param>
    /// <param name="x">指针局部 X（用于排除滚动条区域）。</param>
    /// <param name="viewportWidth">内容视口宽度（不含滚动条）。</param>
    internal static long? RowAt(
        double offsetY,
        double y,
        double rowHeight,
        long totalRows,
        double x = 0,
        double viewportWidth = double.PositiveInfinity)
    {
        if (rowHeight <= 0 || totalRows <= 0)
        {
            return null;
        }
        if (x >= viewportWidth)
        {
            return null;   // 滚动条 / 滑块占位区域
        }
        if (y < 0)
        {
            return null;
        }
        var row = (long)Math.Floor((offsetY + y) / rowHeight);
        return row >= 0 && row < totalRows ? row : null;
    }

    /// <summary>双击打开详情：必须有命中行且无 Ctrl / Shift 修饰键。</summary>
    internal static bool ShouldOpenDetail(long? row, bool ctrl, bool shift) =>
        row is not null && !ctrl && !shift;
}
