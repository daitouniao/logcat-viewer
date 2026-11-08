namespace logcat.Models;

/// <summary>
/// 刷新设备时的 adb server 自愈结果。
/// </summary>
/// <param name="Prompt">拉起失败时指向手动处理的提示串（确认 adb 已加入 PATH，或手动执行
/// <c>adb start-server</c>）；自愈成功（含 server 原本就在运行）为 <c>null</c>。</param>
/// <param name="Restarted">本次是否实际发生过拉起（server 原未运行且拉起成功）。调用方据此决定
/// 是否无条件重建设备监控——server 被杀后存量监控进入僵尸态（不抛异常也不再收事件，已实证），
/// 只能整体重建，不能以异常为重建触发器。</param>
public sealed record AdbServerSelfHealResult(string? Prompt, bool Restarted);
