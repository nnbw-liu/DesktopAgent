using SDemo.DesktopAgent.Platform.Windows.Sandbox;
using Xunit;

namespace SDemo.DesktopAgent.Sandbox.Tests;

[Trait("Category", "Sandbox")]
[Trait("Category", "WindowsOnly")]
public sealed class CommandLineBuilderTests
{
    [Fact]
    public void 无空格参数不加引号() =>
        Assert.Equal("cmd.exe /c echo", CommandLineBuilder.Build("cmd.exe", ["/c", "echo"]));

    [Fact]
    public void 含空格参数加引号() =>
        Assert.Equal(
            "\"C:\\Program Files\\app.exe\" \"hello world\"",
            CommandLineBuilder.Build(@"C:\Program Files\app.exe", ["hello world"]));

    [Fact]
    public void 空参数变成一对空引号() =>
        Assert.Equal("a \"\"", CommandLineBuilder.Build("a", [string.Empty]));

    [Fact]
    public void 结尾反斜杠翻倍() =>
        Assert.Equal("\"C:\\some path\\\\\"", CommandLineBuilder.Quote(@"C:\some path\"));

    [Fact]
    public void 内嵌引号被转义() =>
        Assert.Equal("\"say \\\"hi\\\"\"", CommandLineBuilder.Quote("say \"hi\""));

    [Fact]
    public void 无特殊字符原样返回() =>
        Assert.Equal(@"C:\Windows\System32\cmd.exe", CommandLineBuilder.Quote(@"C:\Windows\System32\cmd.exe"));

    [Fact]
    public void PowerShell脚本按单个参数传入()
    {
        var commandLine = CommandLineBuilder.Build(
            @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            ["-NoProfile", "-Command", "Write-Output 'SDEMO PS OK'"]);

        Assert.Contains(" -Command ", commandLine);
        Assert.EndsWith(" \"Write-Output 'SDEMO PS OK'\"", commandLine);
    }
}
