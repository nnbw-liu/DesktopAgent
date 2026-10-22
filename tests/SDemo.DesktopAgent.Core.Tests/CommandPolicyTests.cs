using System.IO;
using SDemo.DesktopAgent.Execution;
using SDemo.DesktopAgent.Models;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>P4 白名单策略：默认拒绝、参数无 shell、路径不越界、git 只读。</summary>
public sealed class CommandPolicyTests
{
    [Fact]
    public void 默认关闭直跑时拒绝()
    {
        using var temp = new TempDirectory();
        var settings = new ExecutionSettings();

        var result = CommandPolicy.Evaluate("dotnet", "build", ".", temp.Root, settings);

        Assert.False(result.Allowed);
        Assert.Contains("直跑未开启", result.Reason);
    }

    [Fact]
    public void 白名单命令允许并生成前缀()
    {
        using var temp = new TempDirectory();
        var settings = Enabled();

        var result = CommandPolicy.Evaluate("dotnet", "build Demo.sln -v minimal", ".", temp.Root, settings);

        Assert.True(result.Allowed, result.Reason);
        Assert.Equal(ExecutionMode.AllowlistDirect, result.Mode);
        Assert.Equal(["build", "Demo.sln", "-v", "minimal"], result.Arguments);
        Assert.Equal("dotnet build demo.sln", result.Prefix);
    }

    [Theory]
    [InlineData("build & del x")]
    [InlineData("build | more")]
    [InlineData("build > out.txt")]
    [InlineData("build\nbuild")]
    [InlineData("build %PATH%")]
    [InlineData("build `calc`")]
    public void shell元字符拒绝(string args)
    {
        using var temp = new TempDirectory();

        var result = CommandPolicy.Evaluate("dotnet", args, ".", temp.Root, Enabled());

        Assert.False(result.Allowed);
        Assert.Contains("shell", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("src/../../outside")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"\\server\share")]
    [InlineData("/tmp/x")]
    public void 路径越界拒绝(string args)
    {
        using var temp = new TempDirectory();

        var result = CommandPolicy.Evaluate("dotnet", "build " + args, ".", temp.Root, Enabled());

        Assert.False(result.Allowed);
    }

    [Theory]
    [InlineData("cmd")]
    [InlineData("C:\\Windows\\cmd")]
    [InlineData("dotnet.exe")]
    [InlineData("powershell")]
    public void 非白名单或带路径拒绝(string command)
    {
        using var temp = new TempDirectory();

        var result = CommandPolicy.Evaluate(command, "/c calc", ".", temp.Root, Enabled());

        Assert.False(result.Allowed);
    }

    [Theory]
    [InlineData("reset", "--hard")]
    [InlineData("clean", "-fd")]
    public void git写子命令拒绝(string subcommand, string args)
    {
        using var temp = new TempDirectory();

        var result = CommandPolicy.Evaluate("git", $"{subcommand} {args}", ".", temp.Root, Enabled());

        Assert.False(result.Allowed);
        Assert.Contains("git 只允许只读", result.Reason);
    }

    [Fact]
    public void git只读子命令允许()
    {
        using var temp = new TempDirectory();

        var result = CommandPolicy.Evaluate("git", "status --short", ".", temp.Root, Enabled());

        Assert.True(result.Allowed, result.Reason);
    }

    [Fact]
    public void 工作目录不存在或越界拒绝()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Root, "src"));

        Assert.True(CommandPolicy.Evaluate("dotnet", "build", "src", temp.Root, Enabled()).Allowed);
        Assert.False(CommandPolicy.Evaluate("dotnet", "build", "missing", temp.Root, Enabled()).Allowed);
        Assert.False(CommandPolicy.Evaluate("dotnet", "build", "..", temp.Root, Enabled()).Allowed);
    }

    [Fact]
    public void 引号内空格保留且引号去掉()
    {
        var args = CommandPolicy.SplitArguments("build \"My App.sln\" -v minimal");

        Assert.Equal(["build", "My App.sln", "-v", "minimal"], args);
    }

    [Fact]
    public void 未闭合引号拒绝()
    {
        using var temp = new TempDirectory();

        var result = CommandPolicy.Evaluate("dotnet", "build \"Demo.sln", ".", temp.Root, Enabled());

        Assert.False(result.Allowed);
        Assert.Contains("引号", result.Reason);
    }

    [Fact]
    public void 超长参数拒绝()
    {
        using var temp = new TempDirectory();

        var result = CommandPolicy.Evaluate(
            "dotnet",
            new string('a', CommandPolicy.MaxArgumentsChars + 1),
            ".",
            temp.Root,
            Enabled());

        Assert.False(result.Allowed);
    }

    [Fact]
    public void 高风险命令识别()
    {
        Assert.True(CommandPolicy.IsHighRisk("git", ["reset", "--hard"]));
        Assert.True(CommandPolicy.IsHighRisk("dotnet", ["clean"]));
        Assert.False(CommandPolicy.IsHighRisk("dotnet", ["build"]));
    }

    private static ExecutionSettings Enabled() => new()
    {
        Mode = ExecutionMode.AllowlistDirect,
        DirectRunEnabled = true
    };
}
