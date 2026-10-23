using SDemo.DesktopAgent.Context;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>400 溢出识别（§3.8）：只认明确文案，能从报错里学到窗口。</summary>
public sealed class ContextOverflowDetectorTests
{
    [Fact]
    public void 识别常见溢出文案()
    {
        var detected = ContextOverflowDetector.TryDetect(
            new InvalidOperationException("This model's maximum context length is 4096 tokens"),
            out var info);

        Assert.True(detected);
        Assert.Equal(4096, info.LearnedWindow);
        Assert.Contains("maximum context length", info.Detail);
    }

    [Fact]
    public void 服务商文案没有窗口时也能识别()
    {
        var detected = ContextOverflowDetector.TryDetect(
            new InvalidOperationException("prompt is too long: 200000 tokens > 128000 maximum"),
            out var info);

        Assert.True(detected);
        Assert.Null(info.LearnedWindow);
    }

    [Fact]
    public void 嵌套异常里的溢出也能识别()
    {
        var detected = ContextOverflowDetector.TryDetect(
            new InvalidOperationException(
                "请求失败",
                new InvalidOperationException("context_length_exceeded: maximum context length of 8192")),
            out var info);

        Assert.True(detected);
        Assert.Equal(8192, info.LearnedWindow);
    }

    [Fact]
    public void 普通错误不会被误判成溢出()
    {
        Assert.False(ContextOverflowDetector.TryDetect(new InvalidOperationException("401 Unauthorized"), out _));
        Assert.False(ContextOverflowDetector.TryDetect(new HttpRequestException("连接被拒绝"), out _));
        Assert.False(ContextOverflowDetector.TryDetect(new InvalidOperationException("rate limit exceeded"), out _));
    }
}
