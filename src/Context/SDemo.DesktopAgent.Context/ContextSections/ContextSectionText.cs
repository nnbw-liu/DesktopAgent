namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 段内截断与时间读数的公共工具（P2a）。
/// 截断策略统一为「保留头部结构、尾部丢弃 + 一行标记」：每一段都有自己的上限，
/// 因此不需要再用「先砍树、再砍改动、最后砍动作」这种全局顺序来保证最重要的事实还在。
/// </summary>
public static class ContextSectionText
{
    /// <summary>通用的段内截断标记。</summary>
    public const string TruncatedNote = "…（本段已截断）";

    /// <summary>
    /// 把一段正文压到 <paramref name="maxTokens"/> 以内：按行保留头部，尾部换成标记。
    /// 已经放得下时原样返回（多一个字符都不改），这样「没有截断」与「截断过」的文本可区分。
    /// </summary>
    public static (string Text, bool Truncated) Cap(
        string text,
        int maxTokens,
        ITokenCounter counter,
        string note = TruncatedNote)
    {
        var normalized = text.TrimEnd();

        if (normalized.Length == 0)
        {
            return (string.Empty, false);
        }

        if (maxTokens <= 0)
        {
            return (string.Empty, true);
        }

        if (counter.CountMessage("user", normalized) <= maxTokens)
        {
            return (normalized, false);
        }

        var suffix = "\n" + note;
        var kept = new List<string>();

        foreach (var line in normalized.Split('\n'))
        {
            kept.Add(line);

            if (counter.CountMessage("user", string.Join('\n', kept) + suffix) > maxTokens)
            {
                kept.RemoveAt(kept.Count - 1);
                break;
            }
        }

        var body = kept.Count == 0 ? note : string.Join('\n', kept).TrimEnd() + suffix;

        return (body, true);
    }

    /// <summary>
    /// 时间读数（`time` 段）：带偏移与 IANA 时区的绝对时间。
    /// 只有 `HH:mm` 的读数无法判断「这是三分钟前还是昨天」，所以日期、偏移、时区三样都要在。
    /// </summary>
    public static string FormatStamp(DateTimeOffset now, string timeZoneId) =>
        $"{now:yyyy-MM-dd'T'HH:mm:sszzz}[{timeZoneId}]";

    /// <summary>
    /// 把 Windows 时区 id 换成 IANA id（DSH 的读数就带 IANA）；
    /// 换不出来时保留原 id，由调用方决定是否提示「时区不确定」。
    /// </summary>
    public static string ResolveTimeZoneId(TimeZoneInfo zone)
    {
        var id = zone.Id;

        if (string.IsNullOrWhiteSpace(id) || zone.HasIanaId)
        {
            return string.IsNullOrWhiteSpace(id) ? "UTC" : id;
        }

        try
        {
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana)
                && !string.IsNullOrWhiteSpace(iana))
            {
                return iana;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or PlatformNotSupportedException)
        {
            // 非 Windows / 裁剪过的 ICU：留原 id，不因为一个显示细节让整轮请求失败。
        }

        return id;
    }

    /// <summary>经过时长：负值钳到 0（时钟倒退时不要显示「-3 分钟」），null 表示没有基线。</summary>
    public static TimeSpan? Elapsed(DateTimeOffset now, DateTimeOffset? at)
    {
        if (at is null)
        {
            return null;
        }

        var delta = now - at.Value;
        return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
    }

    /// <summary>「12 分钟」这种经过时长（不带「前」，因为它是从过去量到现在的跨度）。</summary>
    public static string DescribeSpan(TimeSpan span)
    {
        if (span.TotalSeconds < 60)
        {
            return "刚刚";
        }

        if (span.TotalMinutes < 60)
        {
            return $"{(int)span.TotalMinutes} 分钟";
        }

        if (span.TotalHours < 24)
        {
            var hours = (int)span.TotalHours;
            var minutes = span.Minutes;
            return minutes == 0 ? $"{hours} 小时" : $"{hours} 小时 {minutes} 分钟";
        }

        return $"{(int)span.TotalDays} 天";
    }

    /// <summary>「3 分钟前」这种相对年龄（改动 / 动作行用它，模型才知道信息有多旧）。</summary>
    public static string DescribeAge(TimeSpan span) =>
        span.TotalSeconds < 60 ? "刚刚" : DescribeSpan(span) + "前";
}
