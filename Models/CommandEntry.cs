namespace logcat.Models;

/// <summary>命令的执行通道。</summary>
public enum CommandKind
{
    /// <summary>设备端 shell 命令，走 adb 的 shell 通道执行。</summary>
    Shell,

    /// <summary>本机 adb 子命令（devices / install / reboot 这类不经过 shell 的命令）。</summary>
    Adb,
}

/// <summary>命令通道显示名。</summary>
public static class CommandKinds
{
    public const string ShellText = "设备 shell";
    public const string AdbText = "本机 adb";

    public static string Text(this CommandKind kind) => kind == CommandKind.Adb ? AdbText : ShellText;

    public static CommandKind Of(string? text) => text == AdbText ? CommandKind.Adb : CommandKind.Shell;

    /// <summary>输出区里标注本次执行的完整调用形式。</summary>
    public static string Describe(CommandKind kind, string command, bool root) =>
        kind == CommandKind.Adb
            ? $"adb {command}"
            : root ? $"adb shell su -c {command}" : $"adb shell {command}";
}

/// <summary>
/// 收藏的命令。按分类归组，记录执行通道与是否需要 root 提权。
/// </summary>
public class CommandEntry
{
    /// <summary>所属分类名。</summary>
    public string Category { get; set; } = "";

    /// <summary>命令正文，可含 <c>{name}</c> 占位符，执行前提示填值。</summary>
    public string Command { get; set; } = "";

    /// <summary>备注（用途说明，可空）。</summary>
    public string? Remark { get; set; }

    /// <summary>执行通道：设备 shell 或本机 adb。</summary>
    public CommandKind Kind { get; set; }

    /// <summary>设备 shell 命令是否用 su -c 包裹执行。</summary>
    public bool Root { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.Now;

    public override string ToString() => Command;
}

/// <summary>
/// 用过的命令（历史，最近使用排在最前）。
/// </summary>
public class CommandRun
{
    public string Command { get; set; } = "";

    public CommandKind Kind { get; set; }

    public bool Root { get; set; }

    public DateTime LastUsed { get; set; } = DateTime.Now;

    public int UseCount { get; set; } = 1;

    public override string ToString() => Command;
}
