using System.Text;

namespace SDemo.DesktopAgent.Platform.Windows.Sandbox;

/// <summary>
/// Windows 命令行参数的引号规则：反斜杠只在引号前 / 参数结尾时翻倍。
/// 与 <c>CommandLineToArgvW</c> 的解析规则对称，避免脚本内容被拆成多个参数。
/// </summary>
public static class CommandLineBuilder
{
    public static string Build(string executable, IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return Quote(executable);
        }

        var builder = new StringBuilder(Quote(executable));
        foreach (var argument in arguments)
        {
            builder.Append(' ').Append(Quote(argument));
        }

        return builder.ToString();
    }

    public static string Quote(string argument)
    {
        if (argument.Length == 0)
        {
            return "\"\"";
        }

        if (!NeedsQuoting(argument))
        {
            return argument;
        }

        var builder = new StringBuilder(argument.Length + 8);
        builder.Append('"');
        var backslashes = 0;

        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes);
            backslashes = 0;
            builder.Append(character);
        }

        // 结尾的反斜杠会转义收尾引号，必须翻倍。
        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    private static bool NeedsQuoting(string argument) =>
        argument.Any(character => character is ' ' or '\t' or '"' or '\n' or '\v' or '\r');
}
