namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 启发式 token 估算（§3.3）：
/// <code>tokens = ceil(cjk * 0.7 + (len - cjk) / 3.8) + 4 + extraOverhead</code>
/// 中文按 ~1.43 字/token、英文按 ~3.8 字符/token；不做词典分词，只求稳定可校准。
/// </summary>
public sealed class HeuristicTokenCounter : ITokenCounter
{
    /// <summary>每条消息的固定开销（角色、分隔符等）。</summary>
    public const int MessageOverhead = 4;

    private const double CjkWeight = 0.7;
    private const double AsciiCharsPerToken = 3.8;

    public int Count(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var cjk = 0;
        var other = 0;

        // 按码点计数：emoji 的代理对只算一个字符，避免被算成两个。
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];

            if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                other++;
                index++;
                continue;
            }

            if (IsCjk(character))
            {
                cjk++;
            }
            else
            {
                other++;
            }
        }

        var estimate = cjk * CjkWeight + other / AsciiCharsPerToken;
        return (int)Math.Ceiling(estimate);
    }

    public int CountMessage(string role, string? text, int extraOverhead = 0) =>
        Count(text) + MessageOverhead + Math.Max(0, extraOverhead);

    /// <summary>汉字、假名、全角标点、CJK 兼容区都按「一个字 ≈ 0.7 token」处理。</summary>
    internal static bool IsCjk(char character) =>
        character is >= '\u3000' and <= '\u303F'      // CJK 标点
        || character is >= '\u3040' and <= '\u30FF'   // 日文假名
        || character is >= '\u3400' and <= '\u4DBF'   // CJK 扩展 A
        || character is >= '\u4E00' and <= '\u9FFF'   // CJK 基本区
        || character is >= '\uF900' and <= '\uFAFF'   // 兼容表意文字
        || character is >= '\uFF00' and <= '\uFFEF';  // 全角字符 / 标点
}
