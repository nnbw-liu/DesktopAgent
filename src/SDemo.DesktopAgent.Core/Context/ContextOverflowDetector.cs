using System.ClientModel;
using System.Text.RegularExpressions;

namespace SDemo.DesktopAgent.Context;

/// <summary>识别结果：是否溢出、能从报错里学到什么窗口。</summary>
public sealed record ContextOverflowInfo(int? LearnedWindow, string Detail);

/// <summary>
/// 400 溢出识别（§3.8）：不同服务商的文案不一样，只认「明确是上下文长度」的几种；
/// 能从报错里读到窗口值就顺手学下来，下次直接按真实窗口建预算。
/// </summary>
public static class ContextOverflowDetector
{
    private static readonly string[] Phrases =
    [
        "context_length_exceeded",
        "maximum context length",
        "context window",
        "too many tokens",
        "reduce the length of the messages",
        "prompt is too long",
        "input is too long",
        "exceeds the maximum number of tokens"
    ];

    private static readonly Regex WindowPattern = new(
        @"(?:maximum context length is|maximum context length of|context window of|context length of|max context length is)\s*(?<window>\d{3,7})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>判断异常是不是上下文溢出；不是返回 false。</summary>
    public static bool TryDetect(Exception exception, out ContextOverflowInfo info)
    {
        info = new ContextOverflowInfo(null, string.Empty);

        for (var current = exception; current is not null; current = current.InnerException)
        {
            var text = Describe(current);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var matchesPhrase = Phrases.Any(phrase =>
                text.Contains(phrase, StringComparison.OrdinalIgnoreCase));

            if (!matchesPhrase)
            {
                continue;
            }

            // 有状态码时必须是 400；没有状态码（部分兼容服务直接抛普通异常）时凭文案判断。
            if (TryGetStatus(current) is int status && status != 400)
            {
                continue;
            }

            var windowMatch = WindowPattern.Match(text);
            int? window = windowMatch.Success && int.TryParse(windowMatch.Groups["window"].Value, out var value)
                ? value
                : null;

            info = new ContextOverflowInfo(window, FirstLine(text));
            return true;
        }

        return false;
    }

    /// <summary>异常的可读正文：优先取原始响应体（OpenAI SDK 的报错详情在 body 里）。</summary>
    private static string Describe(Exception exception)
    {
        var builder = new System.Text.StringBuilder(exception.Message);

        if (exception is ClientResultException result)
        {
            try
            {
                var body = result.GetRawResponse()?.Content?.ToString();
                if (!string.IsNullOrWhiteSpace(body))
                {
                    builder.Append('\n').Append(body);
                }
            }
            catch (Exception)
            {
                // 读不到 body 就只用 Message。
            }
        }

        return builder.ToString();
    }

    private static int? TryGetStatus(Exception exception) =>
        exception is ClientResultException result ? result.Status : null;

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? text;

        return line.Length <= 300 ? line : string.Concat(line.AsSpan(0, 300), "…");
    }
}
