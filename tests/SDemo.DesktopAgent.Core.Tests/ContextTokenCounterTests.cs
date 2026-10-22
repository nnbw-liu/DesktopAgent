using SDemo.DesktopAgent.Context;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>启发式计量（§3.3）：只验证公式与边界，不追求与真实分词器逐 token 相同。</summary>
public sealed class ContextTokenCounterTests
{
    private readonly HeuristicTokenCounter _counter = new();

    [Fact]
    public void 空文本为0_空消息只算固定开销()
    {
        Assert.Equal(0, _counter.Count(null));
        Assert.Equal(0, _counter.Count(string.Empty));
        Assert.Equal(HeuristicTokenCounter.MessageOverhead, _counter.CountMessage("user", string.Empty));
        Assert.Equal(14, _counter.CountMessage("user", string.Empty, extraOverhead: 10));
    }

    [Fact]
    public void 英文按3点8字符每token()
    {
        // 11 个字符 / 3.8 = 2.89 → 3。
        Assert.Equal(3, _counter.Count("hello world"));
    }

    [Fact]
    public void 中文按0点7字每token()
    {
        // 10 个汉字 * 0.7 = 7。
        Assert.Equal(7, _counter.Count(new string('中', 10)));
    }

    [Fact]
    public void 中英混合按码点加权()
    {
        // 3 / 3.8 = 0.79，2 * 0.7 = 1.4，合计 2.19 → 3。
        Assert.Equal(3, _counter.Count("abc中文"));
    }

    [Fact]
    public void 代理对按一个字符计数()
    {
        // emoji 占两个 char 但只算一个非 CJK 字符：1 / 3.8 → 1。
        Assert.Equal(1, _counter.Count("😀"));
    }

    [Fact]
    public void 全角标点按CJK权重()
    {
        // 「，」属于 U+FF00–FFEF，按 0.7 计；结果一定小于同等数量的英文逗号。
        Assert.True(_counter.Count("，，，，") > _counter.Count(",,,,"));
    }
}
