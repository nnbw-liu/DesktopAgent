using SDemo.DesktopAgent.Platform.Windows.Sandbox;
using Xunit;

namespace SDemo.DesktopAgent.Sandbox.Tests;

/// <summary>Windows Sandbox 可用性检测：Home 明确不可用、功能未启用可解释、Verified 只来自自检。</summary>
[Trait("Category", "Sandbox")]
[Trait("Category", "WindowsOnly")]
public sealed class WindowsSandboxAvailabilityTests
{
    private const string ExecutablePath = @"C:\Windows\System32\WindowsSandbox.exe";

    [Theory]
    [InlineData("Core")]
    [InlineData("CoreN")]
    [InlineData("CoreSingleLanguage")]
    [InlineData("CoreCountrySpecific")]
    public void Home版明确不可用(string edition)
    {
        var availability = WindowsSandboxAvailability.Detect(
            new WindowsSandboxHostInfo(edition, ExecutablePath, ExecutableExists: true),
            verified: true);

        Assert.Equal(SandboxKind.WindowsSandbox, availability.Kind);
        Assert.False(availability.Available);
        Assert.False(availability.Verified);
        Assert.Contains("Home", availability.Reason);
    }

    [Theory]
    [InlineData("Professional")]
    [InlineData("Enterprise")]
    [InlineData("EnterpriseS")]
    [InlineData("Education")]
    [InlineData(null)]
    public void 功能已启用时可用但默认未验证(string? edition)
    {
        var availability = WindowsSandboxAvailability.Detect(
            new WindowsSandboxHostInfo(edition, ExecutablePath, ExecutableExists: true),
            verified: false);

        Assert.Equal(SandboxKind.WindowsSandbox, availability.Kind);
        Assert.True(availability.Available);
        Assert.False(availability.Verified);
        Assert.Contains("自检", availability.Reason);
    }

    [Fact]
    public void 自检通过后Verified为真()
    {
        var availability = WindowsSandboxAvailability.Detect(
            new WindowsSandboxHostInfo("Professional", ExecutablePath, ExecutableExists: true),
            verified: true);

        Assert.True(availability.Available);
        Assert.True(availability.Verified);
        Assert.Null(availability.Reason);
    }

    [Theory]
    [InlineData("Professional")]
    [InlineData("Enterprise")]
    public void 功能未启用时给出启用提示(string edition)
    {
        var availability = WindowsSandboxAvailability.Detect(
            new WindowsSandboxHostInfo(edition, ExecutablePath, ExecutableExists: false),
            verified: false);

        Assert.False(availability.Available);
        Assert.Contains("未启用", availability.Reason);
        Assert.Contains(WindowsSandboxAvailability.FeatureName, availability.Detail);
        Assert.Contains(ExecutablePath, availability.Detail);
    }

    [Fact]
    public void 本机检测结果自洽()
    {
        var availability = WindowsSandboxAvailability.Detect(
            WindowsSandboxAvailability.ReadHostInfo(),
            verified: false);

        Assert.Equal(SandboxKind.WindowsSandbox, availability.Kind);
        Assert.False(availability.Verified);

        if (availability.Available)
        {
            if (availability.Verified)
            {
                Assert.Null(availability.Reason);
            }
            else
            {
                // 可用但未自检：Reason 会提示先跑自检，UI 依赖这条文案。
                Assert.Contains("自检", availability.Reason);
            }
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(availability.Reason));
        }
    }
}
