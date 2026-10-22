using System.Text.Json;
using SDemo.DesktopAgent.Platform.Windows.Sandbox;
using SDemo.DesktopAgent.Sandbox.Protocol;
using Xunit;

namespace SDemo.DesktopAgent.Sandbox.Tests;

/// <summary>
/// Windows Sandbox 运行器：用假启动器模拟虚拟机，覆盖结果读取、超时、取消、单实例排队与清理。
/// 真正的虚拟机启动只在 Pro / Enterprise 手工验收里跑（CI 与 Home 不跑）。
/// </summary>
[Trait("Category", "Sandbox")]
[Trait("Category", "WindowsOnly")]
public sealed class WindowsSandboxRunnerTests : IDisposable
{
    private const string JobId = "job-00000001";

    private static readonly WindowsSandboxHostInfo HostInfo = new(
        "Professional",
        Path.Combine(Environment.SystemDirectory, "WindowsSandbox.exe"),
        ExecutableExists: true);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sdemo-wsb-runner", Guid.NewGuid().ToString("N"));

    private readonly SandboxDirectory _directory;

    public WindowsSandboxRunnerTests()
    {
        _directory = new SandboxDirectory(_root);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响测试结论。
        }
    }

    [Fact]
    public async Task 正常执行读回输出与结果()
    {
        var (request, layout) = CreateJob();
        FakeInstance? instance = null;
        string? configuration = null;

        var launcher = new FakeLauncher(() =>
        {
            // .wsb 必须在启动时已经写好，且映射的是 job 目录。
            configuration = File.ReadAllText(Path.Combine(layout.InputDirectory, "job.wsb"));
            File.WriteAllText(Path.Combine(layout.OutputDirectory, "started.txt"), "started");
            File.WriteAllText(Path.Combine(layout.OutputDirectory, "stdout.txt"), "hello");
            File.WriteAllText(Path.Combine(layout.OutputDirectory, "stderr.txt"), string.Empty);
            WriteResult(layout, JobId, exitCode: 0);
            instance = new FakeInstance();
            return instance;
        });

        var runner = new WindowsSandboxRunner(launcher, () => HostInfo, _directory);
        var result = await runner.RunAsync(request);

        Assert.Equal(SandboxFailureCode.None, result.Failure);
        Assert.Equal(SandboxKind.WindowsSandbox, result.Kind);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello", result.StdOut);
        Assert.False(result.TimedOut);

        Assert.NotNull(instance);
        Assert.True(instance!.Terminated);
        Assert.True(File.Exists(Path.Combine(layout.InputDirectory, "run.ps1")));
        Assert.True(File.Exists(Path.Combine(layout.InputDirectory, "run-script.ps1")));
        Assert.False(File.Exists(Path.Combine(layout.InputDirectory, "job.wsb")));

        Assert.NotNull(configuration);
        Assert.Contains("<Networking>Disable</Networking>", configuration);
        Assert.Equal(
            layout.OutputDirectory,
            System.Xml.Linq.XDocument.Parse(configuration!).Root!
                .Element("MappedFolders")!.Elements("MappedFolder").Last()
                .Element("HostFolder")?.Value);
    }

    [Fact]
    public async Task 超时后终止虚拟机()
    {
        var (request, layout) = CreateJob(timeoutSeconds: 1, graceSeconds: 0);
        FakeInstance? instance = null;

        var launcher = new FakeLauncher(() =>
        {
            // 虚拟机启动了、脚本卡住不写结果：这是真正的超时。
            File.WriteAllText(Path.Combine(layout.OutputDirectory, "started.txt"), "started");
            instance = new FakeInstance();
            return instance;
        });

        var runner = new WindowsSandboxRunner(launcher, () => HostInfo, _directory);
        var result = await runner.RunAsync(request);

        Assert.True(result.TimedOut);
        Assert.Equal(SandboxFailureCode.Timeout, result.Failure);
        Assert.True(instance!.Terminated);
        Assert.Contains("已关闭沙箱", result.Message);
    }

    [Fact]
    public async Task 取消后终止虚拟机()
    {
        var (request, _) = CreateJob(timeoutSeconds: 30);
        FakeInstance? instance = null;

        var launcher = new FakeLauncher(() =>
        {
            instance = new FakeInstance();
            return instance;
        });

        var runner = new WindowsSandboxRunner(launcher, () => HostInfo, _directory);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var result = await runner.RunAsync(request, cancellation.Token);

        Assert.True(result.Canceled);
        Assert.Equal(SandboxFailureCode.Canceled, result.Failure);
        Assert.True(instance!.Terminated);
    }

    [Fact]
    public async Task 缺少job描述时拒绝且不启动虚拟机()
    {
        var (request, _) = CreateJob();
        var launcher = new FakeLauncher(() => new FakeInstance());

        var runner = new WindowsSandboxRunner(launcher, () => HostInfo, _directory);
        var result = await runner.RunAsync(request with { WindowsSandbox = null });

        Assert.Equal(SandboxFailureCode.ProtocolError, result.Failure);
        Assert.Equal(0, launcher.LaunchCount);
    }

    [Fact]
    public async Task Home版不可用时不启动虚拟机()
    {
        var (request, _) = CreateJob();
        var launcher = new FakeLauncher(() => new FakeInstance());

        var runner = new WindowsSandboxRunner(
            launcher,
            () => new WindowsSandboxHostInfo("Core", HostInfo.ExecutablePath, ExecutableExists: true),
            _directory);

        var result = await runner.RunAsync(request);

        Assert.Equal(SandboxFailureCode.NotAvailable, result.Failure);
        Assert.Contains("Home", result.Message);
        Assert.Equal(0, launcher.LaunchCount);
    }

    [Fact]
    public async Task 结果jobId不匹配时报协议错误()
    {
        var (request, layout) = CreateJob();

        var launcher = new FakeLauncher(() =>
        {
            File.WriteAllText(Path.Combine(layout.OutputDirectory, "started.txt"), "started");
            WriteResult(layout, "job-99999999", exitCode: 0);
            return new FakeInstance();
        });

        var runner = new WindowsSandboxRunner(launcher, () => HostInfo, _directory);
        var result = await runner.RunAsync(request);

        Assert.Equal(SandboxFailureCode.ProtocolError, result.Failure);
        Assert.Contains("jobId", result.Message);
    }

    [Fact]
    public async Task 映射目录必须是job布局()
    {
        var (request, layout) = CreateJob();
        var launcher = new FakeLauncher(() => new FakeInstance());
        var runner = new WindowsSandboxRunner(launcher, () => HostInfo, _directory);

        var other = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(other);

        var result = await runner.RunAsync(request with
        {
            WindowsSandbox = request.WindowsSandbox! with { WorkDirectory = other }
        });

        Assert.Equal(SandboxFailureCode.PathEscape, result.Failure);
        Assert.Equal(0, launcher.LaunchCount);
    }

    [Fact]
    public async Task 沙箱没启动起来时给出可读原因()
    {
        var (request, _) = CreateJob(timeoutSeconds: 1, graceSeconds: 0);
        var launcher = new FakeLauncher(() => new FakeInstance());

        var runner = new WindowsSandboxRunner(launcher, () => HostInfo, _directory);
        var result = await runner.RunAsync(request);

        Assert.Equal(SandboxFailureCode.LaunchFailed, result.Failure);
        Assert.Contains("没有启动完成", result.Message);
    }

    [Fact]
    public async Task 沙箱启动了但没有结果时给出超时原因()
    {
        var (request, layout) = CreateJob(timeoutSeconds: 1, graceSeconds: 0);

        var launcher = new FakeLauncher(() =>
        {
            File.WriteAllText(Path.Combine(layout.OutputDirectory, "started.txt"), "started");
            return new FakeInstance();
        });

        var runner = new WindowsSandboxRunner(launcher, () => HostInfo, _directory);
        var result = await runner.RunAsync(request);

        Assert.Equal(SandboxFailureCode.Timeout, result.Failure);
        Assert.True(result.TimedOut);
        Assert.Contains("result.json", result.Message);
    }

    [Fact]
    public async Task 单实例排队并发请求不会同时开两个虚拟机()
    {
        var launcher = new ConcurrentLauncher(_directory, TimeSpan.FromMilliseconds(600));
        var runner = new WindowsSandboxRunner(launcher, () => HostInfo, _directory);

        var first = _directory.CreateJob("job-00000001");
        var second = _directory.CreateJob("job-00000002");
        File.WriteAllText(Path.Combine(first.InputDirectory, "script.ps1"), "Write-Output 'one'");
        File.WriteAllText(Path.Combine(second.InputDirectory, "script.ps1"), "Write-Output 'two'");

        var results = await Task.WhenAll(
            runner.RunAsync(CreateRequest(first, "job-00000001", timeoutSeconds: 10)),
            runner.RunAsync(CreateRequest(second, "job-00000002", timeoutSeconds: 10)));

        Assert.All(results, result => Assert.Equal(SandboxFailureCode.None, result.Failure));
        Assert.Equal(1, launcher.MaxConcurrent);
        Assert.Equal(2, launcher.LaunchCount);
    }

    [Fact]
    public async Task 沙箱根目录之外的job被拒绝()
    {
        var (request, layout) = CreateJob();
        var launcher = new FakeLauncher(() => new FakeInstance());
        var runner = new WindowsSandboxRunner(launcher, () => HostInfo, _directory);

        var outside = Path.Combine(Path.GetTempPath(), "sdemo-wsb-outside", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(outside, "input"));
        Directory.CreateDirectory(Path.Combine(outside, "work"));
        Directory.CreateDirectory(Path.Combine(outside, "output"));

        try
        {
            var result = await runner.RunAsync(request with
            {
                WindowsSandbox = request.WindowsSandbox! with
                {
                    JobRoot = outside,
                    InputDirectory = Path.Combine(outside, "input"),
                    WorkDirectory = Path.Combine(outside, "work"),
                    OutputDirectory = Path.Combine(outside, "output")
                }
            });

            Assert.Equal(SandboxFailureCode.PathEscape, result.Failure);
            Assert.Contains("沙箱根目录", result.Message);
            Assert.Equal(0, launcher.LaunchCount);

            // 反向确认：原布局没有被破坏。
            Assert.True(File.Exists(Path.Combine(layout.InputDirectory, "script.ps1")));
        }
        finally
        {
            try
            {
                Directory.Delete(outside, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 清理失败不影响测试结论。
            }
        }
    }

    private (SandboxRequest Request, SandboxJobLayout Layout) CreateJob(
        int timeoutSeconds = 10,
        int graceSeconds = 60,
        string jobId = JobId)
    {
        var layout = _directory.CreateJob(jobId);
        File.WriteAllText(Path.Combine(layout.InputDirectory, "script.ps1"), "Write-Output 'hi'");

        return (CreateRequest(layout, jobId, timeoutSeconds, graceSeconds), layout);
    }

    private static SandboxRequest CreateRequest(
        SandboxJobLayout layout,
        string jobId,
        int timeoutSeconds = 10,
        int graceSeconds = 60) =>
        new()
        {
            JobId = jobId,
            Kind = SandboxKind.WindowsSandbox,
            Executable = HostInfo.ExecutablePath,
            WorkingDirectory = layout.WorkDirectory,
            OutputDirectory = layout.OutputDirectory,
            Limits = new SandboxLimits
            {
                TimeoutSeconds = timeoutSeconds,
                MaxOutputBytes = 64 * 1024
            },
            WindowsSandbox = new WindowsSandboxJobSpec
            {
                JobRoot = layout.Root,
                InputDirectory = layout.InputDirectory,
                WorkDirectory = layout.WorkDirectory,
                OutputDirectory = layout.OutputDirectory,
                StartupGraceSeconds = graceSeconds
            }
        };

    private static void WriteResult(SandboxJobLayout layout, string jobId, int exitCode)
    {
        var document = new SandboxResultDocument
        {
            JobId = jobId,
            ExitCode = exitCode,
            DurationMs = 12
        };

        File.WriteAllText(
            Path.Combine(layout.OutputDirectory, SandboxProtocol.ResultFileName),
            JsonSerializer.Serialize(document, SandboxJson.Options));
    }

    private sealed class FakeLauncher(Func<IWindowsSandboxInstance> factory) : IWindowsSandboxLauncher
    {
        public int LaunchCount { get; private set; }

        public IWindowsSandboxInstance Launch(string configurationPath)
        {
            LaunchCount++;
            return factory();
        }
    }

    private sealed class FakeInstance : IWindowsSandboxInstance
    {
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

        public int? ProcessId => 4242;

        public bool Terminated { get; private set; }

        public void Terminate() => Terminated = true;

        public void Dispose()
        {
        }
    }

    /// <summary>每个请求延迟一段时间才写结果，并记录同时在跑的虚拟机数量。</summary>
    private sealed class ConcurrentLauncher : IWindowsSandboxLauncher
    {
        private readonly SandboxDirectory _directory;
        private readonly TimeSpan _delay;
        private int _active;

        public ConcurrentLauncher(SandboxDirectory directory, TimeSpan delay)
        {
            _directory = directory;
            _delay = delay;
        }

        public int LaunchCount { get; private set; }

        public int MaxConcurrent { get; private set; }

        public IWindowsSandboxInstance Launch(string configurationPath)
        {
            LaunchCount++;

            var current = Interlocked.Increment(ref _active);
            MaxConcurrent = Math.Max(MaxConcurrent, current);

            // configurationPath = <job 根>\input\job.wsb，jobId 是上两级目录名。
            var jobDirectory = Path.GetDirectoryName(Path.GetDirectoryName(configurationPath))!;
            var jobId = new DirectoryInfo(jobDirectory).Name;
            var layout = _directory.GetLayout(jobId);

            _ = Task.Run(async () =>
            {
                await Task.Delay(_delay).ConfigureAwait(false);
                File.WriteAllText(Path.Combine(layout.OutputDirectory, "started.txt"), "started");
                File.WriteAllText(Path.Combine(layout.OutputDirectory, "stdout.txt"), jobId);
                WriteResult(layout, jobId, exitCode: 0);
            });

            return new FakeInstance(() => Interlocked.Decrement(ref _active));
        }

        private sealed class FakeInstance(Action onTerminate) : IWindowsSandboxInstance
        {
            private bool _terminated;

            public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

            public int? ProcessId => 4243;

            public void Terminate()
            {
                if (_terminated)
                {
                    return;
                }

                _terminated = true;
                onTerminate();
            }

            public void Dispose()
            {
            }
        }
    }
}
