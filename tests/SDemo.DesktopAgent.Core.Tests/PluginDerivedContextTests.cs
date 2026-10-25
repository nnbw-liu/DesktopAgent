using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>
/// 钉住一条约定：派生卡片（Derived）默认只用于渲染，<b>不进入发给模型的上下文</b>；
/// 只有用户显式点过「加入上下文」（<see cref="DerivedArtifact.IncludeInContext"/>）的才追加。
/// 以后有人「顺手」把全部 Derived 拼进 history 时，这个文件会红。
/// </summary>
public sealed class PluginDerivedContextTests
{
    [Fact]
    public void 派生卡片默认不进入请求上下文()
    {
        var message = new ChatMessage(ChatMessageRole.Assistant, "原始回答")
        {
            Derived = [Artifact("这段译文不应该出现在请求里")]
        };

        var mapped = AgentMessageMapper.ToRequestMessage(message);

        Assert.Equal("原始回答", mapped.Text);
        Assert.DoesNotContain("这段译文不应该出现在请求里", mapped.Text);
    }

    [Fact]
    public void 加入上下文的派生卡片进入请求上下文()
    {
        var message = new ChatMessage(ChatMessageRole.Assistant, "原始回答")
        {
            Derived = [Artifact("这段译文应该出现在请求里", includeInContext: true)]
        };

        var mapped = AgentMessageMapper.ToRequestMessage(message);

        Assert.Contains("原始回答", mapped.Text);
        Assert.Contains("[插件结果 · translate]", mapped.Text);
        Assert.Contains("这段译文应该出现在请求里", mapped.Text);
    }

    [Fact]
    public void 只有未加入上下文的派生卡片的消息整条不进上下文()
    {
        var message = new ChatMessage(ChatMessageRole.Assistant, string.Empty)
        {
            Derived = [Artifact("只有译文")]
        };

        Assert.False(AgentMessageMapper.ShouldInclude(message));
    }

    [Fact]
    public void 只有加入上下文的派生卡片的消息也会进入上下文()
    {
        var message = new ChatMessage(ChatMessageRole.Assistant, string.Empty)
        {
            Derived = [Artifact("只有译文", includeInContext: true)]
        };

        Assert.True(AgentMessageMapper.ShouldInclude(message));
        Assert.Contains("只有译文", AgentMessageMapper.ToRequestMessage(message).Text);
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

    [Fact]
    public void 失败或运行中的卡片即使标记加入上下文也不进入()
    {
        var message = new ChatMessage(ChatMessageRole.Assistant, "原始回答")
        {
            Derived = [Artifact("半截结果", includeInContext: true, state: DerivedState.Failed)]
        };

        var text = AgentMessageMapper.ToRequestMessage(message).Text;

        Assert.Equal("原始回答", text);
        Assert.DoesNotContain("半截结果", text);
    }

    private static DerivedArtifact Artifact(
        string text,
        bool includeInContext = false,
        DerivedState state = DerivedState.Done) => new(
        "a1",
        "translate",
        "bubble",
        "translate",
        new Dictionary<string, string>(StringComparer.Ordinal),
        null,
        text,
        state,
        null,
        "mock-model",
        1,
        1,
        1,
        DateTime.Now,
        IncludeInContext: includeInContext);
}
