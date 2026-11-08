using System;
using System.Drawing;
using System.Windows.Forms;
using logcat.Services;

namespace logcat.AndTools;

/// <summary>
/// 工具箱主窗体几何持久化（design D7 / N11）：与窗体解耦成静态方法，存取逻辑可脱离窗体单测。
/// 校验与保存口径与日志查看器 frmMain 一致（落在屏内、宽度不低于主屏工作区一半、
/// 最小化不持久化、最大化存 RestoreBounds），落 andtools.json 三键，与日志查看器互不干扰。
/// </summary>
public static class AndToolsWindowGeometry
{
    /// <summary>把保存的三键应用回窗体（OnLoad 时机调用：句柄就绪，最大化可靠落回原屏）。</summary>
    public static void Restore(Form form, AndToolsSettings s)
    {
        var areas = Screen.AllScreens.Select(sc => sc.WorkingArea).ToArray();
        var primary = Screen.PrimaryScreen!.WorkingArea;
        if (!TryRead(s.WindowLocation, s.WindowSize, s.WindowState, areas, primary,
                out var bounds, out var state))
            return;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = bounds.Location;
        form.Size = bounds.Size;
        if (state == FormWindowState.Maximized)
            form.WindowState = FormWindowState.Maximized;
    }

    /// <summary>关闭时把当前几何写回设置对象（调用方自行 Save）。</summary>
    public static void Save(Form form, AndToolsSettings s)
    {
        var (loc, size, state) = Capture(form.Bounds, form.RestoreBounds, form.WindowState,
            Screen.PrimaryScreen!.WorkingArea);
        s.WindowLocation = loc;
        s.WindowSize = size;
        s.WindowState = state;
    }

    /// <summary>
    /// 关闭时状态 → 保存三键（frmMain 同口径）：
    /// Maximized → RestoreBounds（正常态矩形）+ Maximized；Normal → 当前 Bounds + Normal；
    /// Minimized → 主屏工作区 2/3 居中 + Normal（最小化不持久化）。
    /// </summary>
    internal static (Point Location, Size Size, int State) Capture(
        Rectangle bounds, Rectangle restoreBounds, FormWindowState state, Rectangle primaryWorkArea)
    {
        if (state == FormWindowState.Minimized)
        {
            var sz = new Size((int)(primaryWorkArea.Width * 0.667), (int)(primaryWorkArea.Height * 0.667));
            var loc = new Point(primaryWorkArea.Left + (primaryWorkArea.Width - sz.Width) / 2,
                                primaryWorkArea.Top + (primaryWorkArea.Height - sz.Height) / 2);
            return (loc, sz, (int)FormWindowState.Normal);
        }
        if (state == FormWindowState.Maximized)
            return (restoreBounds.Location, restoreBounds.Size, (int)FormWindowState.Maximized);
        return (bounds.Location, bounds.Size, (int)FormWindowState.Normal);
    }

    /// <summary>
    /// 三键校验（frmMain 同口径）：状态值 0..2；尺寸 &gt; 100x100 且与任一屏工作区相交
    /// （防上次在副屏、这次该屏未连接）；宽度不低于主屏工作区一半（防异常残留值）。
    /// </summary>
    internal static bool TryRead(
        Point location, Size size, int stateValue,
        Rectangle[] workingAreas, Rectangle primaryWorkArea,
        out Rectangle bounds, out FormWindowState state)
    {
        bounds = default;
        state = FormWindowState.Normal;
        if (stateValue is < 0 or > 2) return false;
        var candidate = new Rectangle(location, size);
        state = (FormWindowState)stateValue;
        bool onScreen = candidate.Width > 100 && candidate.Height > 100 &&
                        Array.Exists(workingAreas, a => a.IntersectsWith(candidate));
        bool reasonable = candidate.Width >= primaryWorkArea.Width / 2;
        if (!onScreen || !reasonable) return false;
        bounds = candidate;
        return true;
    }
}
