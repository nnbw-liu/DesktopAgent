using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Sandbox;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>工作区 / 聊天室设置的三级合并：聊天室 &gt; 工作区 &gt; 通用。</summary>
public sealed class ScopedSettingsTests
{
    [Fact]
    public void 聊天室覆盖工作区并继续继承通用值()
    {
        var global = new AppSettings
        {
            ConfirmFileWrite = true,
            LastProviderId = "global",
            LastModelId = "g-model",
            Context = new ContextSettings
            {
                BudgetRatio = 0.85,
                KeepRecentTurns = 8,
                AutoCompact = true,
                MaxPinnedMessages = 20
            },
            Agent = new AgentSettings { Mode = AgentModePreference.Auto },
            Checkpoints = new SDemo.DesktopAgent.Execution.CheckpointSettings { Enabled = true }
        };

        var workspace = new Workspace
        {
            Id = "w1",
            Name = "工作区",
            Path = "C:\\work",
            ScopedSettings = new ScopedSettings
            {
                ProviderId = "ws-provider",
                ModelId = "ws-model",
                BudgetRatio = 0.7,
                KeepRecentTurns = 5,
                ConfirmFileWrite = false,
                AgentMode = AgentModePreference.Compact
            }
        };

        var session = new ChatSession
        {
            Id = "c1",
            Title = "聊天室",
            WorkspaceId = workspace.Id,
            ScopedSettings = new ScopedSettings
            {
                ProviderId = "chat-provider",
                ModelId = "chat-model",
                BudgetRatio = 0.6,
                AutoCompact = false
            }
        };

        var effective = EffectiveSettings.Resolve(global, workspace, session);

        // 聊天室值优先。
        Assert.Equal(0.6, effective.Context.BudgetRatio);
        Assert.False(effective.Context.AutoCompact);
        Assert.Equal("chat-provider", effective.ProviderId);
        Assert.Equal("chat-model", effective.ModelId);

        // 聊天室没设的取工作区。
        Assert.Equal(5, effective.Context.KeepRecentTurns);
        Assert.False(effective.ConfirmFileWrite);
        Assert.Equal(AgentModePreference.Compact, effective.Agent.Mode);

        // 工作区也没设的取通用。
        Assert.Equal(20, effective.Context.MaxPinnedMessages);
        Assert.True(effective.Checkpoints.Enabled);
    }

    [Fact]
    public void 只设置一半的模型对时整对忽略()
    {
        var global = new AppSettings
        {
            LastProviderId = "global",
            LastModelId = "global-model"
        };

        var workspace = new Workspace
        {
            Id = "w1",
            Name = "工作区",
            Path = "C:\\work",
            ScopedSettings = new ScopedSettings
            {
                ProviderId = "ws-provider"
                // 没有 ModelId
            }
        };

        var chat = new ChatSession
        {
            Id = "c1",
            Title = "聊天室",
            WorkspaceId = workspace.Id,
            ScopedSettings = new ScopedSettings
            {
                ModelId = "chat-model"
                // 没有 ProviderId
            }
        };

        var effective = EffectiveSettings.Resolve(global, workspace, chat);

        Assert.Equal("global", effective.ProviderId);
        Assert.Equal("global-model", effective.ModelId);
    }

    [Fact]
    public void 没有作用域覆盖时完全回退通用()
    {
        var global = new AppSettings
        {
            Context = new ContextSettings { BudgetRatio = 0.8, ShowContextMeter = false },
            Agent = new AgentSettings { WorkspaceMemoryEnabled = false }
        };

        var effective = EffectiveSettings.Resolve(global, null, null);

        Assert.Equal(0.8, effective.Context.BudgetRatio);
        Assert.False(effective.Context.ShowContextMeter);
        Assert.False(effective.Agent.WorkspaceMemoryEnabled);
    }

    [Fact]
    public void 聊天室可以关掉沙箱并打开完全权限而其余沙箱字段仍继承()
    {
        var global = new AppSettings
        {
            Sandbox = new SandboxSettings { Enabled = true, TimeoutSeconds = 45, Requirement = SandboxRequirement.Required }
        };

        var workspace = new Workspace
        {
            Id = "w1",
            Name = "工作区",
            Path = "C:\\work",
            ScopedSettings = new ScopedSettings { SandboxEnabled = false }
        };

        var chat = new ChatSession
        {
            Id = "c1",
            Title = "聊天室",
            WorkspaceId = workspace.Id,
            ScopedSettings = new ScopedSettings { SkipApprovals = true }
        };

        var effective = EffectiveSettings.Resolve(global, workspace, chat);

        // 沙箱来自工作区，完全权限来自聊天室：两级各自生效，互不覆盖。
        Assert.False(effective.Sandbox.Enabled);
        Assert.True(effective.SkipApprovals);

        // 没在作用域里写的沙箱字段继续继承通用值。
        Assert.Equal(45, effective.Sandbox.TimeoutSeconds);
        Assert.Equal(SandboxRequirement.Required, effective.Sandbox.Requirement);

        // 合并不改通用对象本身：其它聊天室仍然有沙箱、仍然逐次确认。
        Assert.True(global.Sandbox.Enabled);
        Assert.False(EffectiveSettings.Resolve(global, null, null).SkipApprovals);
        Assert.True(EffectiveSettings.Resolve(global, null, null).Sandbox.Enabled);
    }

    [Fact]
    public void 聊天室可以重新打开工作区关掉的沙箱()
    {
        var global = new AppSettings { Sandbox = new SandboxSettings { Enabled = false } };

        var workspace = new Workspace
        {
            Id = "w1",
            Name = "工作区",
            Path = "C:\\work",
            ScopedSettings = new ScopedSettings { SandboxEnabled = false }
        };

        var chat = new ChatSession
        {
            Id = "c1",
            Title = "聊天室",
            WorkspaceId = workspace.Id,
            ScopedSettings = new ScopedSettings { SandboxEnabled = true }
        };

        Assert.True(EffectiveSettings.Resolve(global, workspace, chat).Sandbox.Enabled);
    }
}
