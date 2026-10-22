using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SDemo.DesktopAgent.Sandbox.Protocol;
using Xunit;

namespace SDemo.DesktopAgent.Sandbox.Tests;

/// <summary>SandboxHost 直接执行（不经过 AppContainer），验证文件协议与输出限制。</summary>
public sealed class SandboxHostTests
{
    private static readonly string HostPath = Path.Combine(
        AppContext.BaseDirectory,
        "sandbox-host",
        "SDemo.DesktopAgent.SandboxHost.exe");

    [Fact]
    public void SandboxHost已随测试输出复制()
    {
        Assert.True(File.Exists(HostPath), $"找不到 {HostPath}");
    }

    [Fact]
    public async Task 执行PowerShell并原子写出result()
    {
        using var job = new HostJob("Write-Output \"hello\"\n[Console]::Error.WriteLine(\"oops\")\nexit 5");

        var run = await RunAsync(job);

        Assert.Equal(0, run.ExitCode);
        var result = ReadResult(job);
        Assert.Equal(5, result.ExitCode);
        Assert.Contains("hello", result.StdOut);
        Assert.Contains("oops", result.StdErr);
        Assert.False(result.TimedOut);
        Assert.False(File.Exists(Path.Combine(job.Root, "output", "result.json.tmp")));
        Assert.Equal(2, Directory.GetFiles(job.Input).Length);
    }

    [Fact]
    public async Task 中文脚本按UTF8执行()
    {
        using var job = new HostJob("Write-Output \"中文输出\"");
        var run = await RunAsync(job);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("中文输出", ReadResult(job).StdOut);
    }

    [Fact]
    public async Task 超时写TimedOut并终止进程()
    {
        using var job = new HostJob("Start-Sleep -Seconds 5", timeoutSeconds: 1);
        var run = await RunAsync(job);

        Assert.Equal(0, run.ExitCode);
        var result = ReadResult(job);
        Assert.True(result.TimedOut);
        Assert.True(result.DurationMs >= 900);
        Assert.False(File.Exists(Path.Combine(job.Root, "output", "result.json.tmp")));
    }

    [Fact]
    public async Task 超限输出被截断()
    {
        using var job = new HostJob(
            "1..3000 | ForEach-Object { Write-Output ('line ' + $_ + ' yyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy') }",
            maxOutputBytes: 2048);
        var run = await RunAsync(job);

        Assert.Equal(0, run.ExitCode);
        var result = ReadResult(job);
        Assert.True(result.Truncated);
        Assert.True(result.StdOut.Length <= 2048);
    }

    [Fact]
    public async Task 协议版本不匹配时拒绝执行()
    {
        using var job = new HostJob("Write-Output 'x'");
        job.WriteJobJson(protocolVersion: 2);

        var run = await RunAsync(job);

        Assert.Equal(3, run.ExitCode);
        Assert.Contains("协议版本", run.StdErr);
        Assert.False(File.Exists(Path.Combine(job.Root, "output", SandboxProtocol.ResultFileName)));
    }

    [Fact]
    public async Task jobId与目录名不一致时拒绝执行()
    {
        using var job = new HostJob("Write-Output 'x'");
        job.WriteJobJson(jobId: "other-job");

        var run = await RunAsync(job);

        Assert.Equal(3, run.ExitCode);
        Assert.Contains("jobId", run.StdErr);
    }

    [Fact]
    public async Task 路径逃逸写回错误result()
    {
        using var job = new HostJob("Write-Output 'x'", scriptFile: "../outside.ps1");
        var run = await RunAsync(job);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("拒绝", ReadResult(job).Error);
    }

    [Fact]
    public async Task PDF模式缺少inputFile时按协议错误拒绝()
    {
        using var job = new HostJob("Write-Output 'x'");
        job.WriteJobJson(mode: SandboxJobMode.PdfInfo, scriptFile: null);
        var run = await RunAsync(job);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("inputFile", ReadResult(job).Error);
        Assert.False(File.Exists(Path.Combine(job.Output, SandboxProtocol.PdfResultFileName)));
    }

    [Fact]
    public async Task 未知mode按协议错误拒绝()
    {
        using var job = new HostJob("Write-Output 'x'");
        job.WriteRawJobJson("""{"protocolVersion":1,"jobId":"__JOB_ID__","mode":"Nope"}""".Replace("__JOB_ID__", job.Id));
        var run = await RunAsync(job);

        Assert.Equal(3, run.ExitCode);
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(HostJob job)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = HostPath,
            WorkingDirectory = job.Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
            StandardErrorEncoding = new System.Text.UTF8Encoding(false),
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--job");
        startInfo.ArgumentList.Add(job.JobJsonPath);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 SandboxHost。");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static SandboxResultDocument ReadResult(HostJob job)
    {
        var json = File.ReadAllText(Path.Combine(job.Root, "output", SandboxProtocol.ResultFileName));
        return JsonSerializer.Deserialize<SandboxResultDocument>(json, SandboxJson.Options)
               ?? throw new InvalidOperationException("result.json 为空。");
    }

    private sealed class HostJob : IDisposable
    {
        public HostJob(
            string script,
            int timeoutSeconds = 10,
            int maxOutputBytes = 1024 * 1024,
            SandboxJobMode mode = SandboxJobMode.PowerShell,
            string? scriptFile = "input/script.ps1")
        {
            Id = $"20260101-000000-{Guid.NewGuid():N}"[..22];
            Root = Path.Combine(Path.GetTempPath(), "sdemo-host-tests", Id);
            Input = Path.Combine(Root, "input");
            Work = Path.Combine(Root, "work");
            Output = Path.Combine(Root, "output");
            Directory.CreateDirectory(Input);
            Directory.CreateDirectory(Work);
            Directory.CreateDirectory(Output);

            if (scriptFile is not null)
            {
                var path = Path.Combine(Root, scriptFile.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, script, new System.Text.UTF8Encoding(true));
            }

            WriteJobJson(mode: mode, scriptFile: scriptFile, timeoutSeconds: timeoutSeconds, maxOutputBytes: maxOutputBytes);
        }

        public string Id { get; }

        public string Root { get; }

        public string Input { get; }

        public string Work { get; }

        public string Output { get; }

        public string JobJsonPath => Path.Combine(Input, SandboxProtocol.JobFileName);

        public void WriteJobJson(
            int protocolVersion = 1,
            string? jobId = null,
            SandboxJobMode mode = SandboxJobMode.PowerShell,
            string? scriptFile = "input/script.ps1",
            int timeoutSeconds = 10,
            int maxOutputBytes = 1024 * 1024)
        {
            var document = new SandboxJobDocument
            {
                ProtocolVersion = protocolVersion,
                JobId = jobId ?? Id,
                Mode = mode,
                ScriptFile = scriptFile ?? string.Empty,
                WorkingDirectory = "work",
                TimeoutSeconds = timeoutSeconds,
                MaxOutputBytes = maxOutputBytes
            };
            File.WriteAllText(JobJsonPath, JsonSerializer.Serialize(document, SandboxJson.Options));
        }

        public void WriteRawJobJson(string json) => File.WriteAllText(JobJsonPath, json);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 清理失败不影响测试结论。
            }
        }
    }
}
