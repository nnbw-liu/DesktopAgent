using System.Diagnostics;
using System.Text;
using SDemo.DesktopAgent.Platform.Windows.Sandbox;
using SDemo.DesktopAgent.Sandbox;

namespace SDemo.DesktopAgent.Platform.Windows.Execution;

/// <summary>直跑结果；输出已经按上限截断。</summary>
public sealed record DirectRunResult(
    int ExitCode,
    string StdOut,
    string StdErr,
    bool TimedOut,
    bool Canceled,
    long DurationMs,
    bool StdOutTruncated,
    bool StdErrTruncated);

/// <summary>直跑执行器接口：工具层只依赖它，测试可以替换成假实现。</summary>
public interface IDirectProcessRunner
{
    Task<DirectRunResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        int maxOutputChars,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 宿主直跑：无 shell、无窗口、环境变量白名单、Job Object 绑定整棵进程树，
/// 超时 / 取消时终止整棵树。只用于白名单命令（策略由 <see cref="Execution.CommandPolicy"/> 负责）。
/// </summary>
public sealed class DirectProcessRunner : IDirectProcessRunner
{
    private const int MaxProcesses = 16;
    private const int MemoryLimitMB = 2048;

    public async Task<DirectRunResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        int maxOutputChars,
        CancellationToken cancellationToken = default)
    {
        var resolved = ResolveExecutable(executable)
            ?? throw new FileNotFoundException($"找不到可执行文件：{executable}");

        var startInfo = new ProcessStartInfo
        {
            FileName = resolved,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        ApplyEnvironment(startInfo);

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        using var job = new JobObject(new SandboxLimits
        {
            TimeoutSeconds = Math.Max(1, (int)timeout.TotalSeconds),
            MaxProcesses = MaxProcesses,
            MemoryLimitMB = MemoryLimitMB,
            JobMemoryLimitMB = MemoryLimitMB
        });

        if (!process.Start())
        {
            throw new InvalidOperationException($"无法启动进程：{resolved}");
        }

        var timedOut = false;
        var canceled = false;

        try
        {
            job.Assign(process.Handle);
        }
        catch
        {
            // 极短命令可能在 Assign 前就退出：此时没有子进程需要约束，继续读结果即可。
            if (!process.HasExited)
            {
                throw;
            }
        }

        using var cancellation = cancellationToken.Register(() =>
        {
            canceled = true;
            TryKill(job, process);
        });

        var stdoutTask = ReadCappedAsync(process.StandardOutput, maxOutputChars);
        var stderrTask = ReadCappedAsync(process.StandardError, maxOutputChars);

        try
        {
            var exitTask = process.WaitForExitAsync(CancellationToken.None);
            var completed = await Task.WhenAny(exitTask, Task.Delay(timeout, CancellationToken.None))
                .ConfigureAwait(false);

            if (completed != exitTask)
            {
                timedOut = true;
                TryKill(job, process);
                await exitTask.ConfigureAwait(false);
            }
            else
            {
                await exitTask.ConfigureAwait(false);
            }
        }
        finally
        {
            // 取消时 cancellation callback 已终止进程；这里保证读取端能结束。
            if (!process.HasExited)
            {
                TryKill(job, process);
            }
        }

        var (stdout, stdoutTruncated) = await stdoutTask.ConfigureAwait(false);
        var (stderr, stderrTruncated) = await stderrTask.ConfigureAwait(false);
        stopwatch.Stop();

        cancellationToken.ThrowIfCancellationRequested();

        var exitCode = process.HasExited ? process.ExitCode : -1;

        return new DirectRunResult(
            exitCode,
            stdout,
            stderr,
            timedOut,
            canceled,
            stopwatch.ElapsedMilliseconds,
            stdoutTruncated,
            stderrTruncated);
    }

    /// <summary>按 PATH + PATHEXT 只解析 .exe；.cmd/.bat 需要 shell，直跑不支持。</summary>
    public static string? ResolveExecutable(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        if (Path.IsPathRooted(command) && File.Exists(command))
        {
            return command;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = new[] { string.Empty, ".exe" };

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory.Trim(), command + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static void ApplyEnvironment(ProcessStartInfo startInfo)
    {
        var inherited = startInfo.Environment;
        var allowed = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in inherited.Keys)
        {
            if (IsAllowed(key))
            {
                allowed[key] = inherited[key];
            }
        }

        inherited.Clear();

        foreach (var (key, value) in allowed)
        {
            inherited[key] = value;
        }
    }

    private static bool IsAllowed(string key) =>
        key.Equals("PATH", StringComparison.OrdinalIgnoreCase)
        || key.Equals("PATHEXT", StringComparison.OrdinalIgnoreCase)
        || key.Equals("TEMP", StringComparison.OrdinalIgnoreCase)
        || key.Equals("TMP", StringComparison.OrdinalIgnoreCase)
        || key.Equals("USERPROFILE", StringComparison.OrdinalIgnoreCase)
        || key.Equals("HOME", StringComparison.OrdinalIgnoreCase)
        || key.Equals("NUGET_PACKAGES", StringComparison.OrdinalIgnoreCase)
        || key.Equals("SystemRoot", StringComparison.OrdinalIgnoreCase)
        || key.Equals("SystemDrive", StringComparison.OrdinalIgnoreCase)
        || key.Equals("ComSpec", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase);

    private static async Task<(string Text, bool Truncated)> ReadCappedAsync(
        StreamReader reader,
        int cap)
    {
        var maximum = Math.Max(256, cap);
        var builder = new StringBuilder();
        var buffer = new char[4096];
        var truncated = false;

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var remaining = maximum - builder.Length;
            if (remaining <= 0)
            {
                truncated = true;
                continue;
            }

            if (read <= remaining)
            {
                builder.Append(buffer, 0, read);
            }
            else
            {
                builder.Append(buffer, 0, remaining);
                truncated = true;
            }
        }

        return (builder.ToString(), truncated);
    }

    private static void TryKill(JobObject job, Process process)
    {
        try
        {
            job.Terminate();
        }
        catch
        {
            // Job 已关闭 / 进程已退出。
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 进程树已经结束。
        }
    }
}
