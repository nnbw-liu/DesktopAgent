using System.Diagnostics;
using System.IO;
using SDemo.DesktopAgent.Editing;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>路径安全：绝对路径、UNC、设备路径、目录穿越、junction 全部拒绝。</summary>
public sealed class WorkspacePathTests
{
    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("..\\outside.txt")]
    [InlineData("src/../../outside.txt")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("\\\\server\\share\\a.txt")]
    [InlineData("//server/share/a.txt")]
    [InlineData("\\\\.\\C:\\a.txt")]
    [InlineData("a\0b.txt")]
    public void 危险路径全部拒绝(string path)
    {
        using var temp = new TempDirectory();

        Assert.Throws<InvalidOperationException>(
            () => WorkspacePath.Resolve(temp.Root, path, allowRoot: false));
    }

    [Fact]
    public void 正常相对路径可以解析()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Root, "src"));

        var full = WorkspacePath.Resolve(temp.Root, "src/a.cs", allowRoot: false);

        Assert.Equal(Path.Combine(temp.Root, "src", "a.cs"), full);
    }

    [Fact]
    public void 经过junction的路径拒绝()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();
        var real = Path.Combine(temp.Root, "real");
        var link = Path.Combine(temp.Root, "link");
        Directory.CreateDirectory(real);

        if (!TryCreateJunction(link, real))
        {
            return;
        }

        var error = Assert.Throws<InvalidOperationException>(
            () => WorkspacePath.Resolve(temp.Root, "link/a.txt", allowRoot: false));

        Assert.Contains("junction", error.Message);
    }

    [Fact]
    public async Task 补丁经过junction时拒绝且不落盘()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();
        var real = Path.Combine(temp.Root, "real");
        var link = Path.Combine(temp.Root, "link");
        Directory.CreateDirectory(real);

        if (!TryCreateJunction(link, real))
        {
            return;
        }

        var context = new ToolContext(
            "session",
            temp.Root,
            [],
            new StubSettingsService(),
            new ToolApprovalService(),
            new StubSandboxJobService(),
            new NullAuditLog(),
            new FileReadRegistry());
        var tool = Assert.IsAssignableFrom<Microsoft.Extensions.AI.AIFunction>(
            new EditToolPack().Create(context).Single(function => function.Name == "apply_patch"));

        var result = await tool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(
            new Dictionary<string, object?>
            {
                ["patch"] = """
                    --- /dev/null
                    +++ b/link/new.txt
                    @@ -0,0 +1,1 @@
                    +bad
                    """
            }));

        Assert.Contains("未写入任何文件", result?.ToString());
        Assert.False(File.Exists(Path.Combine(real, "new.txt")));
    }

    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(
                "cmd.exe",
                $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10_000);
            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
