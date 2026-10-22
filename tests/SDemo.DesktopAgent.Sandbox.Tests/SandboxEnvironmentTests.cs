using System.Runtime.InteropServices;
using SDemo.DesktopAgent.Platform.Windows.Sandbox;
using Xunit;

namespace SDemo.DesktopAgent.Sandbox.Tests;

[Trait("Category", "Sandbox")]
[Trait("Category", "WindowsOnly")]
public sealed class SandboxEnvironmentTests
{
    private static string NewHome() =>
        Path.Combine(Path.GetTempPath(), "sdemo-home", Guid.NewGuid().ToString("N"));

    [Fact]
    public void 沙箱内的应用数据目录指向Home()
    {
        var home = NewHome();
        var variables = SandboxEnvironment.Build(home);

        // AppContainer 的 CreateProcess 缺 LOCALAPPDATA 会返回 203；这里必须始终存在。
        Assert.Equal(Path.Combine(home, "AppData", "Local"), variables["LOCALAPPDATA"]);
        Assert.Equal(Path.Combine(home, "AppData", "Roaming"), variables["APPDATA"]);
        Assert.Equal(home, variables["USERPROFILE"]);
        Assert.Equal(Path.Combine(home, "temp"), variables["TEMP"]);
        Assert.Equal(Path.Combine(home, "temp"), variables["TMP"]);
        Assert.Equal(home, variables["HOMEDRIVE"] + variables["HOMEPATH"]);
    }

    [Fact]
    public void 系统变量保留而用户私有变量不传入()
    {
        var home = NewHome();
        Environment.SetEnvironmentVariable("SDEMO_TEST_API_KEY", "should-not-leak");
        try
        {
            var variables = SandboxEnvironment.Build(home);

            Assert.False(string.IsNullOrWhiteSpace(variables["SystemRoot"]));
            Assert.False(string.IsNullOrWhiteSpace(variables["ComSpec"]));
            Assert.Contains(Path.Combine(variables["SystemRoot"], "System32"), variables["PATH"]);
            Assert.DoesNotContain("SDEMO_TEST_API_KEY", variables.Keys);
            Assert.DoesNotContain("USERNAME", variables.Keys);
            Assert.DoesNotContain("USERDOMAIN", variables.Keys);
            Assert.Equal(
                Path.Combine(variables["SystemRoot"], "System32", "WindowsPowerShell", "v1.0", "Modules"),
                variables["PSModulePath"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SDEMO_TEST_API_KEY", null);
        }
    }

    [Fact]
    public void 环境块以双NUL结尾且按名排序()
    {
        var home = NewHome();
        var variables = SandboxEnvironment.Build(home);
        var block = SandboxEnvironment.CreateBlock(variables);
        try
        {
            var length = variables.Sum(pair => pair.Key.Length + 1 + pair.Value.Length + 1) + 1;
            var text = Marshal.PtrToStringUni(block, length)!;

            Assert.EndsWith("\0\0", text);
            Assert.StartsWith("APPDATA=", text);

            var names = text.TrimEnd('\0')
                .Split('\0')
                .Select(line => line[..line.IndexOf('=')])
                .ToArray();
            Assert.Equal(names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase), names);
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }
}
