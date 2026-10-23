using System.IO;
using SDemo.DesktopAgent.Execution;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>工作区命令探测：sln / csproj / package.json / 空目录。</summary>
public sealed class WorkspaceCommandDetectorTests
{
    [Fact]
    public void 优先解决方案()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Root, "Demo.sln"), string.Empty);
        File.WriteAllText(Path.Combine(temp.Root, "Other.csproj"), "<Project />");

        var suggestion = WorkspaceCommandDetector.Detect(temp.Root);

        Assert.Contains("dotnet build \"Demo.sln\"", suggestion.BuildCommand);
        Assert.Contains("dotnet test \"Demo.sln\"", suggestion.TestCommand);
    }

    [Fact]
    public void 单项目回退csproj()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Root, "Demo.csproj"), "<Project />");

        var suggestion = WorkspaceCommandDetector.Detect(temp.Root);

        Assert.Contains("dotnet build \"Demo.csproj\"", suggestion.BuildCommand);
    }

    [Fact]
    public void pnpm锁文件选择pnpm()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Root, "package.json"), "{}");
        File.WriteAllText(Path.Combine(temp.Root, "pnpm-lock.yaml"), string.Empty);

        var suggestion = WorkspaceCommandDetector.Detect(temp.Root);

        Assert.Equal("pnpm run build", suggestion.BuildCommand);
        Assert.Equal("pnpm test", suggestion.TestCommand);
    }

    [Fact]
    public void 空目录不猜命令()
    {
        using var temp = new TempDirectory();

        var suggestion = WorkspaceCommandDetector.Detect(temp.Root);

        Assert.Null(suggestion.BuildCommand);
        Assert.Null(suggestion.TestCommand);
    }
}
