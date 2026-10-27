using SDemo.DesktopAgent.Execution;
using SDemo.DesktopAgent.Models;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>P2 技能脚本策略：直跑 / Node 白名单、argv 长度与路径越界、cwd 与超时。</summary>
public sealed class SkillScriptPolicyTests : IDisposable
{
    private readonly TempDirectory _workspace = new();

    private ExecutionSettings Direct(ExecutionSettings? settings = null)
    {
        settings ??= new ExecutionSettings();
        settings.DirectRunEnabled = true;
        settings.Mode = ExecutionMode.AllowlistDirect;
        return settings;
    }

    [Fact]
    public void 直跑未开启时拒绝()
    {
        var result = SkillScriptPolicy.Evaluate(
            ["finalize"], null, 0, _workspace.Root, new ExecutionSettings());

        Assert.False(result.Allowed);
        Assert.Contains("直跑", result.Reason);
    }

    [Fact]
    public void 白名单没有_node_时拒绝()
    {
        var settings = Direct();
        settings.Allowlist.Remove("node");

        var result = SkillScriptPolicy.Evaluate(["doctor"], null, 0, _workspace.Root, settings);

        Assert.False(result.Allowed);
        Assert.Contains("node", result.Reason);
    }

    [Fact]
    public void 参数里的绝对路径_UNC_与越界都被拒绝()
    {
        var settings = Direct();

        foreach (var argument in new[]
                 {
                     @"C:\Windows\win.ini",
                     @"\\server\share\x.json",
                     "/etc/passwd",
                     "~/secret",
                     "./a/../../secret.json",
                     "a\0b",
                     "a\nb"
                 })
        {
            var result = SkillScriptPolicy.Evaluate([argument], null, 0, _workspace.Root, settings);
            Assert.False(result.Allowed);
        }
    }

    [Fact]
    public void 参数过长或过多时拒绝()
    {
        var settings = Direct();

        Assert.False(SkillScriptPolicy.Evaluate(
            [new string('a', SkillScriptPolicy.MaxArgumentChars + 1)], null, 0, _workspace.Root, settings).Allowed);

        Assert.False(SkillScriptPolicy.Evaluate(
            Enumerable.Repeat("x", SkillScriptPolicy.MaxArguments + 1).ToArray(), null, 0, _workspace.Root, settings).Allowed);
    }

    [Fact]
    public void 工作目录必须在工作区内且存在()
    {
        var settings = Direct();
        Directory.CreateDirectory(Path.Combine(_workspace.Root, "diagrams"));

        var ok = SkillScriptPolicy.Evaluate(
            ["finalize"], "diagrams", 30, _workspace.Root, settings);

        Assert.True(ok.Allowed);
        Assert.Equal("diagrams", ok.WorkingDirectory);
        Assert.Equal(30, ok.TimeoutSeconds);

        Assert.False(SkillScriptPolicy.Evaluate(
            [], "../outside", 0, _workspace.Root, settings).Allowed);

        Assert.False(SkillScriptPolicy.Evaluate(
            [], "missing", 0, _workspace.Root, settings).Allowed);
    }

    [Fact]
    public void 超时按默认值回退并受构建超时上限约束()
    {
        var settings = Direct();
        settings.DefaultTimeoutSeconds = 120;
        settings.BuildTimeoutSeconds = 300;

        Assert.Equal(120, SkillScriptPolicy.Evaluate([], null, 0, _workspace.Root, settings).TimeoutSeconds);
        Assert.Equal(300, SkillScriptPolicy.Evaluate([], null, 9999, _workspace.Root, settings).TimeoutSeconds);
        Assert.Equal(120, SkillScriptPolicy.Evaluate([], null, -5, _workspace.Root, settings).TimeoutSeconds);
    }

    public void Dispose() => _workspace.Dispose();
}
