using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>
/// 钉住一条约定：派生卡片（Derived）只用于渲染，<b>永远不进入发给模型的上下文</b>。
/// 以后有人「顺手」把 Derived 拼进 history 时，这个文件会红。
/// </summary>
public sealed class PluginDerivedContextTests
{
    [Fact]
    public void 派生卡片不进入请求上下文()
    {
        var message = new ChatMessage(ChatMessageRole.Assistant, "原始回答")
        {
            Derived =
            [
                new DerivedArtifact(
                    "a1",
                    "translate",
                    "bubble",
                    "translate",
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["target"] = "ja" },
                    null,
                    "这段译文不应该出现在请求里",
                    DerivedState.Done,
                    null,
                    "mock-model",
                    1200,
                    30,
                    8,
                    DateTime.Now)
            ]
        };

        var mapped = AgentMessageMapper.ToRequestMessage(message);

        Assert.Equal("原始回答", mapped.Text);
        Assert.DoesNotContain("这段译文不应该出现在请求里", mapped.Text);
    }

    [Fact]
    public void 只有派生卡片的消息整条不进上下文()
    {
        var message = new ChatMessage(ChatMessageRole.Assistant, string.Empty)
        {
            Derived =
            [
                new DerivedArtifact(
                    "a1",
                    "translate",
                    "bubble",
                    "translate",
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    null,
                    "只有译文",
                    DerivedState.Done,
                    null,
                    "mock-model",
                    1,
                    1,
                    1,
                    DateTime.Now)
            ]
        };

        Assert.False(AgentMessageMapper.ShouldInclude(message));
    }

    [Fact]
    public void 插件生成的消息也只按正文进入上下文()
    {
        var message = new ChatMessage(ChatMessageRole.Assistant, "翻译后的正文")
        {
            Plugin = new PluginMessageInfo(
                "translate",
                "composer",
                "translate",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["target"] = "ja" },
                null)
        };

        var mapped = AgentMessageMapper.ToRequestMessage(message);

        Assert.Equal("翻译后的正文", mapped.Text);
    }
}
