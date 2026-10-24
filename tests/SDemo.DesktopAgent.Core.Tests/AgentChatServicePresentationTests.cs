using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>
/// 工具行的展示数据由宿主在记录轨迹时算好（标题 / 参数原文 / 结果预览 / 耗时），
/// 界面只负责渲染——这样新增工具不用改模板。
/// </summary>
public sealed class AgentChatServicePresentationTests
{
    [Fact]
    public void 命令类工具标题是命令加前两个参数()
    {
        var title = AgentChatService.BuildTitle("run_command", new Dictionary<string, object?>
        {
            ["command"] = "dotnet",
            ["args"] = "build Demo.sln -v minimal"
        });

        Assert.Equal("dotnet build Demo.sln", title);
    }

    [Fact]
    public void 文件类工具标题取路径()
    {
        Assert.Equal(
            "src/Program.cs",
            AgentChatService.BuildTitle("read_file", new Dictionary<string, object?> { ["path"] = "src/Program.cs" }));

        Assert.Equal(
            "**/*.cs",
            AgentChatService.BuildTitle("glob_files", new Dictionary<string, object?> { ["pattern"] = "**/*.cs" }));
    }

    [Fact]
    public void 没有可展示参数时标题为空()
    {
        Assert.Null(AgentChatService.BuildTitle("todo_write", new Dictionary<string, object?>
        {
            ["todos"] = new[] { "a" }
        }));

        Assert.Null(AgentChatService.BuildTitle("read_file", new Dictionary<string, object?>()));
    }

    [Fact]
    public void 参数原文按上限截断()
    {
        var arguments = new Dictionary<string, object?> { ["code"] = new string('x', 2000) };

        var json = AgentChatService.DescribeArguments(arguments);

        Assert.NotNull(json);
        Assert.True(json!.Length <= 513, $"实际长度 {json.Length}");
        Assert.EndsWith("…", json, StringComparison.Ordinal);
    }

    [Fact]
    public void 空参数不给原文()
    {
        Assert.Null(AgentChatService.DescribeArguments(null));
        Assert.Null(AgentChatService.DescribeArguments(new Dictionary<string, object?>()));
    }

    [Fact]
    public void 结果预览只留前两百字()
    {
        var text = new string('y', 500);

        var preview = AgentChatService.Preview(text);

        Assert.NotNull(preview);
        Assert.Equal(201, preview!.Length);
        Assert.EndsWith("…", preview, StringComparison.Ordinal);
        Assert.Null(AgentChatService.Preview("   "));
    }
}
