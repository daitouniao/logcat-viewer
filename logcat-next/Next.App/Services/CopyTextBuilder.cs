using System.Text;
using Next.Interop;

namespace Next.App.Services;

/// <summary>
/// 复制内容拼装（design D4）：每行取原始文本（Raw 的 UTF-8 解码，与旧查看器「复制原始文本」同口径），
/// 按行序以 \n 连接，末尾不加换行。
/// </summary>
internal static class CopyTextBuilder
{
    internal static string Build(IEnumerable<LogcorePageRow> rows)
    {
        var builder = new StringBuilder();
        var first = true;
        foreach (var row in rows)
        {
            if (!first)
            {
                builder.Append('\n');
            }
            builder.Append(RowText(row));
            first = false;
        }
        return builder.ToString();
    }

    internal static string RowText(LogcorePageRow row) => Encoding.UTF8.GetString(row.Raw);
}
