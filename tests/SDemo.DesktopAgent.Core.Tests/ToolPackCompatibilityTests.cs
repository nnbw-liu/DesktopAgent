using System.IO;
using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Editing;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>P3 工具面包拆分：描述、默认值、旧设置兼容、搜索命中登记。</summary>
public sealed class ToolPackCompatibilityTests
{
    [Fact]
    public void 只读包包含读取搜索枚举六个工具()
    {
        var descriptor = new FileToolPack().Descriptor;

        Assert.Equal("files", descriptor.Id);
        Assert.Equal(
            ["list_files", "list_tree", "read_file", "search_files", "glob_files", "workspace_info"],
            descriptor.Functions.Select(function => function.Id));
        Assert.All(descriptor.Functions, function => Assert.False(function.RequiresApproval));
        Assert.True(descriptor.BuiltIn);
    }

    [Fact]
    public void 编辑包包含补丁与整文件写入且都需要审批()
    {
        var descriptor = new EditToolPack().Descriptor;

        Assert.Equal("edits", descriptor.Id);
        Assert.Equal(
            ["replace_text", "apply_patch", "write_file"],
            descriptor.Functions.Select(function => function.Id));
        Assert.All(descriptor.Functions, function => Assert.True(function.RequiresApproval));
        Assert.Equal(AgentToolRisk.Medium, descriptor.Risk);
    }

    [Fact]
    public void 旧设置只记录files时edits按默认开启()
    {
        var settings = new ToolSettings();
        settings.EnabledPacks["files"] = true;

        Assert.True(settings.IsPackEnabled("files", defaultEnabled: true));
        Assert.True(settings.IsPackEnabled("edits", defaultEnabled: true));
    }

    [Fact]
    public async Task 工作区信息带构建与测试命令()
    {
        using var temp = new TempDirectory();
        var settings = new StubSettingsService();
        settings.Current.Workspaces.Add(new Workspace
        {
            Id = "w1",
            Name = "Demo",
            Path = temp.Root,
            BuildCommand = "dotnet build Demo.sln -v minimal",
            TestCommand = "dotnet test Demo.sln --no-build"
        });
        var context = new ToolContext(
            "session",
            temp.Root,
            [],
            settings,
            new ToolApprovalService(),
            new StubSandboxJobService(),
            new NullAuditLog());

        var tool = Assert.IsAssignableFrom<AIFunction>(
            new FileToolPack().Create(context).Single(function => function.Name == "workspace_info"));
        var result = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>()));

        Assert.Contains("构建命令：dotnet build Demo.sln -v minimal", result?.ToString());
        Assert.Contains("测试命令：dotnet test Demo.sln --no-build", result?.ToString());
    }

    [Fact]
    public async Task 搜索命中会登记读前写记录()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Root, "a.cs");
        File.WriteAllText(file, "class A {}\n");
        var reads = new FileReadRegistry();
        var context = new ToolContext(
            "session",
            temp.Root,
            [],
            new StubSettingsService(),
            new ToolApprovalService(),
            new StubSandboxJobService(),
            new NullAuditLog(),
            reads);

        var tool = Assert.IsAssignableFrom<AIFunction>(
            new FileToolPack().Create(context).Single(function => function.Name == "search_files"));
        await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["pattern"] = "class",
            ["glob"] = "**/*.cs"
        }));

        var record = reads.Find("session", "a.cs");
        Assert.NotNull(record);
        Assert.Equal(FileReadRegistry.HashFile(file), record!.Hash);
    }
}
