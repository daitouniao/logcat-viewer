using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace logcat.Services;

/// <summary>
/// 起始目录的<b>候选链构造</b>与<b>首个可用候选选取</b>（纯逻辑，不依赖 WinForms）。
///
/// <para>
/// 下沉为独立组件的理由：候选顺序、跳过失败项、全失败这三条语义各自可独立单测，
/// 且测试不需 STA 线程、不需构造窗体，跑得更稳更快。
/// </para>
/// </summary>
public static class StartDirResolver
{
    /// <summary>设备可写存储根目录：普通（无 root）设备也能访问的存储目录。</summary>
    public const string DeviceStorageRoot = "/sdcard";

    /// <summary>本机驱动器列表在面板里以空路径表示（「此电脑」）。</summary>
    public const string LocalDriveList = "";

    /// <summary>
    /// 设备侧候选链：本设备记忆目录 → 设备可写存储根目录。
    /// 没有记忆记录时以末位候选起头；记忆目录与末位候选相同时不产生重复项。
    /// </summary>
    public static IReadOnlyList<string> BuildDeviceCandidates(string? rememberedPath)
    {
        var list = new List<string>(2);
        if (!string.IsNullOrEmpty(rememberedPath)) list.Add(rememberedPath);
        if (!list.Contains(DeviceStorageRoot)) list.Add(DeviceStorageRoot);
        return list;
    }

    /// <summary>
    /// 本机侧候选链：本机记忆目录 → 本机驱动器列表。
    /// 没有记忆记录时以末位候选起头；记忆目录与末位候选相同时不产生重复项。
    /// </summary>
    public static IReadOnlyList<string> BuildLocalCandidates(string? rememberedPath)
    {
        var list = new List<string>(2);
        if (!string.IsNullOrEmpty(rememberedPath)) list.Add(rememberedPath);
        if (!list.Contains(LocalDriveList)) list.Add(LocalDriveList);
        return list;
    }

    /// <summary>
    /// 依次尝试候选目录，返回<b>首个打开成功</b>的目录；全部失败返回 null。
    /// </summary>
    /// <param name="candidates">候选链，顺序即尝试顺序。</param>
    /// <param name="tryOpen">
    /// 尝试打开某路径并返回成败的委托。委托抛出 <see cref="OperationCanceledException"/>
    /// 表示「已取消」（用户已导航或窗体已关闭）——此时整条解析就此中止，
    /// 不得当成候选失败去试下一个候选。
    /// </param>
    public static async Task<string?> PickFirstAvailableAsync(
        IReadOnlyList<string> candidates,
        Func<string, Task<bool>> tryOpen)
    {
        foreach (var candidate in candidates)
        {
            if (await tryOpen(candidate)) return candidate;
        }
        return null;
    }
}
