using System.IO;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>
/// PDF 工具的可读范围：只有当前会话的附件目录。会话 id 来自数据库 / UI，
/// 仍要当成不可信输入——带分隔符或 <c>..</c> 时不能把可读根扩大到 uploads 之外。
/// </summary>
public sealed class AgentAttachmentRootsTests
{
    [Fact]
    public void 会话附件目录存在时授予只读根()
    {
        using var temp = new TempDirectory();
        var paths = new StubAppPaths(temp.Root);
        var directory = Path.Combine(paths.UploadsRoot, "session-1");
        Directory.CreateDirectory(directory);

        var roots = AgentAttachmentRoots.Build(paths, "session-1");

        Assert.Equal(new[] { Path.GetFullPath(directory) }, roots);
    }

    [Fact]
    public void 目录不存在时不授予()
    {
        using var temp = new TempDirectory();
        var paths = new StubAppPaths(temp.Root);

        Assert.Empty(AgentAttachmentRoots.Build(paths, "session-1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("../other")]
    [InlineData(@"..\other")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    public void 非法会话id不授予任何根(string sessionId)
    {
        using var temp = new TempDirectory();
        var paths = new StubAppPaths(temp.Root);
        Directory.CreateDirectory(paths.UploadsRoot);

        Assert.Empty(AgentAttachmentRoots.Build(paths, sessionId));
    }
}
