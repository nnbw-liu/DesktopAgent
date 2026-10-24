using SDemo.DesktopAgent.Sandbox;
using Xunit;

namespace SDemo.DesktopAgent.Sandbox.Tests;

/// <summary>
/// 副作用分类只是提示：系统级操作（关机 / 注册表 / 服务 / 提权）必须被标出来，
/// 并且明确「不允许在工作区重放」；联网调用单独标注。
/// </summary>
public sealed class SandboxEffectDetectorTests
{
    [Fact]
    public void 关机脚本被判为系统级且不可重放()
    {
        const string script = """
            whoami
            cmd /c "shutdown /s /t 3600"
            """;

        var effects = SandboxEffectDetector.Detect(script, []);

        Assert.True(effects.HasSystemLevel);
        Assert.False(effects.CanReplayInWorkspace);
        Assert.Contains("shutdown", effects.Signals, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void 注册表与服务操作也算系统级()
    {
        foreach (var script in new[] { "reg add HKLM\\Software\\X /v Y /d 1", "New-Service -Name x -BinaryPathName y", "bcdedit /set testsigning on" })
        {
            Assert.True(SandboxEffectDetector.Detect(script, []).HasSystemLevel, script);
        }
    }

    [Fact]
    public void 联网脚本单独标注但仍可重放()
    {
        var effects = SandboxEffectDetector.Detect("Invoke-WebRequest https://example.com -OutFile a.txt", []);

        Assert.True(effects.HasNetwork);
        Assert.False(effects.HasSystemLevel);
        Assert.True(effects.CanReplayInWorkspace);
    }

    [Fact]
    public void 纯文件脚本只标文件变更()
    {
        var changes = new[]
        {
            new SandboxChange("a.txt", SandboxChangeKind.Modified, 3, false, "@@")
        };

        var effects = SandboxEffectDetector.Detect("'hello' | Set-Content a.txt", changes);

        Assert.True(effects.HasFileChanges);
        Assert.False(effects.HasNetwork);
        Assert.False(effects.HasSystemLevel);
        Assert.True(effects.CanReplayInWorkspace);
    }

    [Fact]
    public void 空脚本没有副作用()
    {
        var effects = SandboxEffectDetector.Detect(null, []);

        Assert.Empty(effects.Effects);
        Assert.Empty(effects.Signals);
        Assert.True(effects.CanReplayInWorkspace);
    }
}
