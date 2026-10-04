namespace logcat.Models;

/// <summary>
/// 一份完整的过滤条件。所有条件之间为 AND，单条件内部多值为 OR。
/// </summary>
public class FilterSpec
{
    public int[] Levels { get; set; } = Array.Empty<int>();
    public string[] Tags { get; set; } = Array.Empty<string>();
    public string TagOp { get; set; } = "or";
    public bool TagCase { get; set; }
    public bool TagExclude { get; set; }

    public string[] Msg { get; set; } = Array.Empty<string>();
    public string MsgOp { get; set; } = "and";
    public bool MsgCase { get; set; }
    public bool MsgExclude { get; set; }

    public int[] Pids { get; set; } = Array.Empty<int>();
    public int[] Tids { get; set; } = Array.Empty<int>();
    public bool PidExclude { get; set; }
    public bool TidExclude { get; set; }

    public int[] Minutes { get; set; } = Array.Empty<int>();
    public bool MarkedOnly { get; set; }

    public bool IsEmpty()
    {
        var allLevels = Services.LogParser.ALL_LEVELS;
        if (Levels.Length > 0 && !Levels.OrderBy(x => x).SequenceEqual(allLevels.OrderBy(x => x)))
            return false;
        if (Tags.Length > 0 || Msg.Length > 0 || Pids.Length > 0 || Tids.Length > 0)
            return false;
        if (Minutes.Length > 0) return false;
        return !MarkedOnly;
    }

    public string Describe()
    {
        var parts = new List<string>();
        var allLevels = Services.LogParser.ALL_LEVELS;
        if (Levels.Length > 0 && !Levels.OrderBy(x => x).SequenceEqual(allLevels.OrderBy(x => x)))
        {
            var names = string.Concat(Levels.Where(l => l >= 0 && l < Services.LogParser.LEVEL_NAME.Length)
                .OrderBy(l => l).Select(l => Services.LogParser.LEVEL_NAME[l]));
            parts.Add($"级别 {names}");
        }
        if (Tags.Length > 0)
            parts.Add($"tag{(TagExclude ? "!" : "")} {TagOp.ToUpper()}({string.Join(", ", Tags)})");
        if (Msg.Length > 0)
            parts.Add($"msg{(MsgExclude ? "!" : "")} {MsgOp.ToUpper()}({string.Join(", ", Msg)})");
        if (Pids.Length > 0)
            parts.Add($"pid{(PidExclude ? "!" : "")}({string.Join(", ", Pids)})");
        if (Tids.Length > 0)
            parts.Add($"tid{(TidExclude ? "!" : "")}({string.Join(", ", Tids)})");
        if (Minutes.Length > 0)
            parts.Add($"分钟({string.Join(", ", Minutes)})");
        if (MarkedOnly)
            parts.Add("仅标记行");
        return parts.Count > 0 ? string.Join("；", parts) : "（无过滤）";
    }
}
