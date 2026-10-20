using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Plugins;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>双语对照解析：配对、数量不等、流式尾标签、无标签降级（§10.2）。</summary>
public sealed class SegmentedOutputParserTests
{
    [Fact]
    public void 完整配对解析成段落()
    {
        var result = SegmentedOutputParser.Parse("<s>A</s><t>甲</t><s>B</s><t>乙</t>");

        Assert.True(result.HasPairs);
        Assert.False(result.Pending);
        Assert.Equal(2, result.Segments.Count);
        Assert.Equal(new OutputSegment("A", "甲"), result.Segments[0]);
        Assert.Equal(new OutputSegment("B", "乙"), result.Segments[1]);
        Assert.Equal("甲\n\n乙", result.Text);
    }

    [Fact]
    public void 多出来的译文并入最后一段()
    {
        var result = SegmentedOutputParser.Parse("<s>A</s><t>甲</t><t>乙</t>");

        var segment = Assert.Single(result.Segments);
        Assert.Equal("A", segment.Source);
        Assert.Equal("甲\n\n乙", segment.Target);
    }

    [Fact]
    public void 只有译文也算成功段落()
    {
        var result = SegmentedOutputParser.Parse("<t>只有译文</t>");

        var segment = Assert.Single(result.Segments);
        Assert.Equal(string.Empty, segment.Source);
        Assert.Equal("只有译文", segment.Target);
        Assert.Equal("只有译文", result.Text);
    }

    [Fact]
    public void 数量不等时丢掉落单的原文()
    {
        var result = SegmentedOutputParser.Parse("<s>A</s><t>甲</t><s>落单</s>");

        Assert.Single(result.Segments);
        Assert.Equal("甲", result.Text);
        Assert.False(result.Pending);
    }

    [Fact]
    public void 流式中未闭合的尾部先丢弃并标记pending()
    {
        var result = SegmentedOutputParser.Parse("<s>A</s><t>甲</t><s>B</s><t>乙");

        var segment = Assert.Single(result.Segments);
        Assert.Equal("甲", segment.Target);
        Assert.True(result.Pending);
    }

    [Fact]
    public void 流式中还没配对时不显示半截标签()
    {
        var partial = SegmentedOutputParser.Parse("<s>还没译完");

        Assert.Empty(partial.Segments);
        Assert.False(partial.HasPairs);
        Assert.True(partial.Pending);
        Assert.Equal(string.Empty, partial.Text);
    }

    [Fact]
    public void 收尾时未闭合标签被丢弃_没有配对则降级为纯译文()
    {
        var result = SegmentedOutputParser.Parse("<s>只有原文没有译文", final: true);

        Assert.False(result.HasPairs);
        Assert.False(result.Pending);
        Assert.Equal("只有原文没有译文", result.Text);
    }

    [Fact]
    public void 标签外文本被忽略()
    {
        var result = SegmentedOutputParser.Parse("这里是前言<s>A</s><t>甲</t>这里是后记");

        Assert.Single(result.Segments);
        Assert.Equal("甲", result.Text);
    }

    [Fact]
    public void 没有标签时整段降级为纯译文()
    {
        var result = SegmentedOutputParser.Parse("  模型直接返回了普通文本  ");

        Assert.False(result.HasPairs);
        Assert.Empty(result.Segments);
        Assert.Equal("模型直接返回了普通文本", result.Text);
    }

    [Fact]
    public void 多行段落与CRLF归一()
    {
        var result = SegmentedOutputParser.Parse("<s>第一行\r\n第二行</s><t>甲\r\n乙</t>");

        var segment = Assert.Single(result.Segments);
        Assert.Equal("第一行\n第二行", segment.Source);
        Assert.Equal("甲\n乙", segment.Target);
    }

    [Fact]
    public void 空输入返回空结果()
    {
        var result = SegmentedOutputParser.Parse(null);

        Assert.Empty(result.Segments);
        Assert.Equal(string.Empty, result.Text);
        Assert.False(result.HasPairs);
        Assert.False(result.Pending);
    }

    [Fact]
    public void 累积器可以增量解析()
    {
        var accumulator = new SegmentedOutputAccumulator();

        accumulator.Append("<s>He");
        Assert.Empty(accumulator.Parse().Segments);

        accumulator.Append("llo</s><t>你");
        Assert.Empty(accumulator.Parse().Segments);
        Assert.True(accumulator.Parse().Pending);

        accumulator.Append("好</t>");
        var final = accumulator.Parse(final: true);

        Assert.Equal(new OutputSegment("Hello", "你好"), Assert.Single(final.Segments));
        Assert.False(final.Pending);
    }

    [Fact]
    public void 累积器超长后截断()
    {
        var accumulator = new SegmentedOutputAccumulator(maxChars: 10);
        accumulator.Append("0123456789ABC");

        Assert.True(accumulator.Truncated);
        Assert.Equal("0123456789", accumulator.RawText);
    }
}
