using System.Text;

namespace logcat.Services;

/// <summary>
/// 状态栏取证探针：把状态栏与各分区项的布局快照写成一行 <c>[StatBar]</c> 诊断记录，
/// 用于定位「右下角统计区偶发不显示、拖动滚动条即恢复」的成因。
///
/// <para>
/// 纪律：只读快照，不修改任何控件状态；整段 try/catch，异常绝不影响主流程；
/// 只在打开文件的收尾时序与滚动恢复瞬间调用，进度回调 / 逐行路径一律不打点。
/// </para>
/// </summary>
internal static class StatBarProbe
{
    /// <summary>诊断流前缀（与既有 <c>[DPI]</c> 平级，落在同一条启动日志）。</summary>
    internal const string Prefix = "[StatBar]";

    /// <summary>落盘出口；测试可替换以捕获输出，生产即写启动日志。</summary>
    internal static Action<string> Sink = line => StartupLog.Write(line);

    /// <summary>组一行快照：tag + 状态条（宽高 / 布局风格 / 能否溢出）+ 逐项字段。</summary>
    internal static string Describe(string tag, StatusStrip? strip,
        params (string Name, ToolStripItem? Item)[] items)
    {
        try
        {
            var sb = new StringBuilder(256);
            sb.Append(Prefix).Append(' ').Append(tag).Append(": ");
            if (strip == null)
            {
                sb.Append("strip=<null>");
                return sb.ToString();
            }

            sb.Append("strip=").Append(strip.Width).Append('x').Append(strip.Height);
            sb.Append(" style=").Append(strip.LayoutStyle);
            sb.Append(" overflow=").Append(strip.CanOverflow);

            foreach (var (name, item) in items)
            {
                sb.Append(" | ").Append(name);
                if (item == null)
                {
                    sb.Append("=<null>");
                    continue;
                }

                sb.Append("(w=").Append(item.Width)
                  .Append(",len=").Append(item.Text?.Length ?? 0)
                  .Append(",avail=").Append(item.Available)
                  .Append(",place=").Append(item.Placement)
                  .Append(",vis=").Append(item.Visible)
                  .Append(')');
            }

            return sb.ToString();
        }
        catch
        {
            return string.Empty;   // 量测失败就当没这一帧，绝不把异常抛回 UI
        }
    }

    /// <summary>写一帧快照到诊断日志（组串失败或空串则不写）。</summary>
    internal static void Log(string tag, StatusStrip? strip,
        params (string Name, ToolStripItem? Item)[] items)
    {
        try
        {
            var line = Describe(tag, strip, items);
            if (line.Length > 0) Sink(line);
        }
        catch
        {
            /* 诊断不得影响主流程 */
        }
    }
}
