using System.Diagnostics;
using System.Text.Json;
using SDemo.DesktopAgent.Platform.Windows.Sandbox;
using SDemo.DesktopAgent.Sandbox.Protocol;
using Xunit;

namespace SDemo.DesktopAgent.Sandbox.Tests;

/// <summary>
/// run.ps1 / run-script.ps1 生成：jobId 与工作目录替换、路径安全、编码符合 PS 5.1 要求，
/// 以及把 C:\job 换成本地目录后在真实 PowerShell 上跑通（验证脚本本身没有语法 / 逻辑错误）。
/// </summary>
[Trait("Category", "Sandbox")]
[Trait("Category", "WindowsOnly")]
public sealed class WindowsSandboxRunScriptTests
{
    [Fact]
    public void runner写入jobId与工作目录()
    {
        var script = WindowsSandboxRunScript.CreateRunner("20261021-120000-ab12cd34", "work/sub");

        Assert.Contains("$jobId = '20261021-120000-ab12cd34'", script);
        Assert.Contains(@"$work = 'C:\job\work\sub'", script);
        Assert.DoesNotContain("{{", script);
    }

    [Theory]
    [InlineData("work", @"C:\job\work")]
    [InlineData(".", @"C:\job\work")]
    [InlineData("", @"C:\job\work")]
    [InlineData("work/sub", @"C:\job\work\sub")]
    [InlineData("work/a/b", @"C:\job\work\a\b")]
    [InlineData("work/中文目录", @"C:\job\work\中文目录")]
    public void 工作目录被转换成沙箱内绝对路径(string jobWorkingDirectory, string expected) =>
        Assert.Equal(expected, WindowsSandboxRunScript.ResolveWorkDirectory(jobWorkingDirectory));

    [Theory]
    [InlineData("work/../outside")]
    [InlineData("../../Windows")]
    [InlineData("work/a'b")]
    [InlineData("work/a\nb")]
    public void 不安全的工作目录被拒绝(string jobWorkingDirectory) =>
        Assert.Throws<ArgumentException>(() => WindowsSandboxRunScript.ResolveWorkDirectory(jobWorkingDirectory));

    [Theory]
    [InlineData("short")]
    [InlineData("job_00000001")]
    [InlineData("job/00000001")]
    public void 非法jobId被拒绝(string jobId) =>
        Assert.Throws<ArgumentException>(() => WindowsSandboxRunScript.CreateRunner(jobId));

    [Fact]
    public void runner脚本包含结果与输出约定()
    {
        var script = WindowsSandboxRunScript.CreateRunner("20261021-120000-ab12cd34");

        // 输出路径、原子写结果、启动标记都必须是约定好的文件名，宿主按这些名字轮询。
        Assert.Contains(@"C:\job\output", script);
        Assert.Contains("result.json", script);
        Assert.Contains("started.txt", script);
        Assert.Contains("stdout.txt", script);
        Assert.Contains("stderr.txt", script);
        Assert.Contains("Move(", script);
        Assert.Contains("run-script.ps1", script);
    }

    [Fact]
    public void 包装脚本转义单引号()
    {
        var script = WindowsSandboxRunScript.CreateWrapper(@"C:\job\input\it's.ps1");

        Assert.Contains("it''s.ps1", script);
        Assert.Contains("[Console]::OutputEncoding", script);
    }

    [Fact]
    public void 写入时runner无BOM而包装脚本有BOM()
    {
        var root = Path.Combine(Path.GetTempPath(), "sdemo-wsb-script", Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "input");

        try
        {
            WindowsSandboxRunScript.Write(input, "20261021-120000-ab12cd34", "work");

            var runner = File.ReadAllBytes(Path.Combine(input, "run.ps1"));
            var wrapper = File.ReadAllBytes(Path.Combine(input, "run-script.ps1"));

            Assert.False(IsUtf8Bom(runner));
            Assert.True(IsUtf8Bom(wrapper));
            Assert.Contains(@"C:\job\input\script.ps1", File.ReadAllText(Path.Combine(input, "run-script.ps1")));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    /// <summary>
    /// 把脚本里的 C:\job 换成本地临时 job 目录后真的跑一遍：验证脚本语法、Start-Process 参数、
    /// result.json 原子写与 UTF-8 输出都成立（虚拟机映射只是把这套路径换成 C:\job）。
    /// </summary>
    [Fact]
    public async Task 生成的脚本在本机能跑通并写出结果()
    {
        const string jobId = "20261021-120000-ab12cd34";
        var root = Path.Combine(Path.GetTempPath(), "sdemo-wsb-native", Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "input");
        var work = Path.Combine(root, "work");
        var output = Path.Combine(root, "output");

        try
        {
            Directory.CreateDirectory(input);
            Directory.CreateDirectory(work);
            Directory.CreateDirectory(output);

            File.WriteAllText(
                Path.Combine(input, "script.ps1"),
                "Write-Output 'WSB_NATIVE_OK'\r\nWrite-Output '中文输出'\r\n",
                new System.Text.UTF8Encoding(true));

            // 沙箱内固定是 C:\job；本机测试换成临时目录，其余内容保持完全一致。
            var runnerScript = WindowsSandboxRunScript.CreateRunner(jobId, "work").Replace(@"C:\job", root);
            var wrapperScript = WindowsSandboxRunScript.CreateWrapper()
                .Replace(@"C:\job", root);

            var runnerPath = Path.Combine(input, "run.ps1");
            File.WriteAllText(runnerPath, runnerScript, new System.Text.UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(input, "run-script.ps1"),
                wrapperScript,
                new System.Text.UTF8Encoding(true));

            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var argument in new[]
                     {
                         "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                         "-File", runnerPath
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(timeout.Token);

            var resultPath = Path.Combine(output, "result.json");
            Assert.True(File.Exists(resultPath), $"没有写出 result.json；stderr={ReadText(Path.Combine(output, "stderr.txt"))}");

            var document = JsonSerializer.Deserialize<SandboxResultDocument>(
                await File.ReadAllTextAsync(resultPath),
                SandboxJson.Options);

            Assert.NotNull(document);
            Assert.Equal(jobId, document!.JobId);
            Assert.Equal(0, document.ExitCode);

            var stdout = await File.ReadAllTextAsync(Path.Combine(output, "stdout.txt"));
            Assert.Contains("WSB_NATIVE_OK", stdout);
            Assert.Contains("中文输出", stdout);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static bool IsUtf8Bom(byte[] content) =>
        content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF;

    private static string ReadText(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : string.Empty;

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响测试结论。
        }
    }
}
