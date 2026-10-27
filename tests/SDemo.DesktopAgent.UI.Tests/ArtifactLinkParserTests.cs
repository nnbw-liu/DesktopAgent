using SDemo.DesktopAgent.Components.Chat;
using Xunit;

namespace SDemo.DesktopAgent.UI.Tests;

/// <summary>P3：助手正文里的 .archify 相对路径提取与去重。</summary>
public sealed class ArtifactLinkParserTests
{
    [Fact]
    public void 提取相对产物路径并去重()
    {
        const string text = """
            已生成：`.archify/architecture-order-20261027-103000/order.html`
            浏览器证据在 `.archify/architecture-order-20261027-103000/review-2/summary.html`；
            重复一次 `.archify/architecture-order-20261027-103000/order.html`。
            """;

        var links = ArtifactLinkParser.Extract(text);

        Assert.Equal(
            [
                ".archify/architecture-order-20261027-103000/order.html",
                ".archify/architecture-order-20261027-103000/review-2/summary.html"
            ],
            links);
    }

    [Fact]
    public void 忽略绝对路径与越界路径()
    {
        const string text = """
            C:\work\.archify\a\a.html
            .archify/../secret.html
            .archify/a/../../secret.html
            .archify/a/a.txt
            """;

        Assert.Empty(ArtifactLinkParser.Extract(text));
    }

    [Fact]
    public void 反斜杠路径也归一化()
    {
        Assert.Equal(
            [".archify/a/b.html"],
            ArtifactLinkParser.Extract(@"产物 .archify\a\b.html"));
    }
}
