using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>P6 双模判定：窗口、能力位、模型名标记、手动覆盖优先级。</summary>
public sealed class AgentModeTests
{
    [Fact]
    public void 八K窗口判为精简()
    {
        Assert.Equal(AgentMode.Compact, AgentModeResolver.Resolve(
            new ModelProfile(8192, 2048), "any-model"));
    }

    [Fact]
    public void 十六K边界仍为精简()
    {
        Assert.Equal(AgentMode.Compact, AgentModeResolver.Resolve(
            new ModelProfile(16_384, 2048), "any-model"));
    }

    [Fact]
    public void 三十二K判为完整()
    {
        Assert.Equal(AgentMode.Full, AgentModeResolver.Resolve(
            new ModelProfile(32_768, 4096), "any-model"));
    }

    [Fact]
    public void 不支持工具判为精简()
    {
        Assert.Equal(AgentMode.Compact, AgentModeResolver.Resolve(
            new ModelProfile(32_768, 4096, SupportsTools: false), "any-model"));
    }

    [Fact]
    public void 小模型名称标记判为精简()
    {
        Assert.Equal(AgentMode.Compact, AgentModeResolver.Resolve(
            new ModelProfile(131_072, 8192), "qwen3-4b-thinking"));
    }

    [Fact]
    public void 手动完整覆盖自动规则()
    {
        Assert.Equal(AgentMode.Full, AgentModeResolver.Resolve(
            new ModelProfile(8192, 2048), "qwen3-4b",
            AgentModePreference.Full));
    }

    [Fact]
    public void 手动精简覆盖大窗口()
    {
        Assert.Equal(AgentMode.Compact, AgentModeResolver.Resolve(
            new ModelProfile(131_072, 8192), "big-model",
            AgentModePreference.Compact));
    }
}
