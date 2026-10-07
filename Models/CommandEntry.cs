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
    // 显示名走本地化：Kind 本身以枚举名持久化（JsonStringEnumConverter），显示名不入盘，
    // 故切语言不会影响已存的 commands.json。
    public static string ShellText => Services.Loc.T("设备 shell");
    public static string AdbText => Services.Loc.T("本机 adb");

    public static string Text(this CommandKind kind) => kind == CommandKind.Adb ? AdbText : ShellText;

    /// <summary>由显示名反查通道。两种语言的显示名都要认：
    /// 切换语言时若下拉框已用旧语言填充过，<c>Of</c> 仍须能正确解析。</summary>
    public static CommandKind Of(string? text) =>
        text == Services.Loc.T("本机 adb") || text == "本机 adb" ? CommandKind.Adb : CommandKind.Shell;

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

    /// <summary>
    /// 内置命令的备注原文（非本地化）。仅内置模板非空：界面显示走
    /// <see cref="RemarkText"/> 按当前语言查表，切语言立即生效，
    /// 而 <see cref="Remark"/> 保持中文原文不动（它同时是 commands.json 里的持久化值，
    /// 也用于筛选匹配——改了会让已存的用户数据与内置项脱节）。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? RemarkZh { get; set; }

    /// <summary>备注显示文本：内置项按语言翻译，自建项原样返回。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string RemarkText => RemarkZh != null ? Services.Loc.T(RemarkZh) : (Remark ?? "");

    /// <summary>
    /// 内置分类的原文（非本地化）。界面显示走 <see cref="CategoryText"/>。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? CategoryZh { get; set; }

    /// <summary>分类显示文本：内置分类按语言翻译，自建分类原样返回。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string CategoryText => CategoryZh != null ? Services.Loc.T(CategoryZh) : Category;

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
