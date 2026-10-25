namespace SDemo.DesktopAgent.Models;

/// <summary>一条界面命令（`/compact` 这类）：只是目录项，真正执行由宿主完成。</summary>
public sealed record ComposerCommandInfo(string Name, string Description, string? InputHint = null);

/// <summary>
/// `/` 命令的语法与目录。
/// <para>
/// 命令只走界面：不进模型、不花 token；未知命令必须被拒绝，绝不退化成用户提示词。
/// </para>
/// </summary>
public static class ComposerCommands
{
    /// <summary>首版命令面。名字小写，参数由命令自己解析。</summary>
    public static readonly IReadOnlyList<ComposerCommandInfo> All =
    [
        new("compact", "压缩当前会话的历史"),
        new("model", "切换本轮使用的模型"),
        new("index", "重建工作区知识库索引"),
        new("help", "列出可用命令")
    ];

    /// <summary>是否是已知命令（大小写不敏感）。</summary>
    public static bool IsKnown(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && All.Any(command => string.Equals(command.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>按名字找命令；找不到返回 null。</summary>
    public static ComposerCommandInfo? Find(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : All.FirstOrDefault(command => string.Equals(command.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 把整行解析成命令：首字节 `/` + 小写名（字母/数字/`_`/`-`）+ 末尾或空白。
    /// <paramref name="rawInput"/> 是名字之后的全部内容（已去首尾空白）。
    /// </summary>
    public static bool TryParseLine(string? text, out string name, out string rawInput)
    {
        name = string.Empty;
        rawInput = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var line = text.Trim();

        if (line.Length < 2 || line[0] != ComposerTriggers.Command)
        {
            return false;
        }

        var end = 1;

        while (end < line.Length && IsNameChar(line[end]))
        {
            end++;
        }

        if (end == 1)
        {
            return false;
        }

        // 名字之后只能是行末或空白（`/compcat` 合法，`/compactx1` 也合法但名字是 compactx1）。
        if (end < line.Length && !char.IsWhiteSpace(line[end]))
        {
            return false;
        }

        name = line[1..end];
        rawInput = line[end..].Trim();

        return true;
    }

    /// <summary>命令名允许的字符：小写字母、数字、下划线、连字符。</summary>
    public static bool IsNameChar(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-';
}
