using SDemo.DesktopAgent.Plugins;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>清单解析层：注释 / 尾逗号 / 大小写 / 语法错误行号 / 未知字段。</summary>
public sealed class PluginManifestTests
{
    [Fact]
    public void 语法错误的行号从1开始()
    {
        var parse = PluginManifestJson.Parse("{\n  \"id\": x\n}");

        Assert.False(parse.Success);
        var issue = Assert.Single(parse.Issues);
        Assert.True(issue.IsError);
        Assert.Equal("plugin.json", issue.File);
        Assert.Equal(2, issue.Line);
        Assert.Contains("无法解析", issue.Message);
    }

    [Fact]
    public void 允许注释与尾逗号()
    {
        var parse = PluginManifestJson.Parse("""
            {
              // 手写配置允许注释
              "id": "x",
              "name": "X",
              "capabilities": [ { "id": "main", "name": "X", "skill": "a.md", }, ],
            }
            """);

        Assert.True(parse.Success);
        Assert.Equal("x", parse.Manifest!.Id);
        Assert.Single(parse.Manifest.Capabilities!);
    }

    [Fact]
    public void 字段名大小写不敏感()
    {
        var parse = PluginManifestJson.Parse("""
            {
              "ID": "x",
              "NAME": "X",
              "Capabilities": [ { "Id": "main", "Name": "X", "Skill": "a.md" } ],
              "MOUNTS": [ { "ID": "composer", "Surface": "composer", "Capability": "main" } ]
            }
            """);

        Assert.True(parse.Success);
        Assert.Equal("x", parse.Manifest!.Id);
        Assert.Equal("X", parse.Manifest.Name);
        Assert.Single(parse.Manifest.Mounts!);
    }

    [Fact]
    public void 未知字段按JSON路径报告()
    {
        var issues = PluginManifestJson.FindUnknownMembers("""
            {
              "id": "x",
              "foo": 1,
              "mounts": [ { "id": "a", "surface": "composer", "capabiltiy": "main" } ]
            }
            """);

        Assert.Equal(2, issues.Count);
        Assert.Contains(issues, issue => issue.Path == "foo");
        Assert.Contains(issues, issue => issue.Path == "mounts[0].capabiltiy");
        Assert.All(issues, issue => Assert.Equal(PluginIssueSeverity.Warning, issue.Severity));
    }

    [Fact]
    public void 未知字段检查遇到坏JSON不抛异常()
    {
        var issues = PluginManifestJson.FindUnknownMembers("{ \"id\": x }");

        Assert.Empty(issues);
    }
}
