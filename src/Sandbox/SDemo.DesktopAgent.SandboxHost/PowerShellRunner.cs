using System.Diagnostics;
using System.Text;
using SDemo.DesktopAgent.Sandbox.Protocol;

namespace SDemo.DesktopAgent.SandboxHost;

/// <summary>
/// 在沙箱内启动 Windows PowerShell 5.1 执行用户脚本。
/// stdout / stderr 直接按字节读取并限流，避免管道死锁；超时按整棵进程树终止。
/// </summary>
internal static class PowerShellRunner
{
    private static readonly IReadOnlySet<string> AllowedEnvironment = new HashSet<string>(
        [
            "SystemRoot", "windir", "SystemDrive", "ComSpec", "PATH", "PATHEXT",
            "TEMP", "TMP", "USERPROFILE", "HOMEDRIVE", "HOMEPATH",
            "LOCALAPPDATA", "APPDATA", "PSModulePath"
        ],
        StringComparer.OrdinalIgnoreCase);

    public static async Task<SandboxResultDocument> RunAsync(SandboxJobDocument job, string jobRoot)
    {
        var stopwatch = Stopwatch.StartNew();
        var scriptPath = SandboxPathGuard.ResolveUnder(jobRoot, job.ScriptFile, "input");
        if (!File.Exists(scriptPath))
        {
            throw new SandboxProtocolException($"脚本不存在：{job.ScriptFile}");
        }

        var workDirectory = SandboxPathGuard.ResolveUnder(jobRoot, job.WorkingDirectory, "work");
        if (!Directory.Exists(workDirectory))
        {
            throw new SandboxProtocolException($"工作目录不存在：{job.WorkingDirectory}");
        }

        // 包装脚本放在 output（沙箱内唯一可写目录），避免宿主需要写 input。
        var outputDirectory = Path.Combine(jobRoot, "output");
        Directory.CreateDirectory(outputDirectory);
        var wrapperPath = Path.Combine(outputDirectory, $".sandbox-run-{Guid.NewGuid():N}.ps1");

        // UTF-8 BOM：Windows PowerShell 5.1 只有看到 BOM 才会按 UTF-8 读取 .ps1。
        // AppContainer 内 PowerShell 5.1 的 Set-Location 会因 NativeFileExists 的访问检查失败，
        // 但 PSDrive 可以正常建立并切换；用专用盘符承载 work，相对路径仍然落在工作目录里。
        var wrapper = "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)\n"
                      + "$OutputEncoding = [Console]::OutputEncoding\n"
                      + $"New-PSDrive -Name SDEMO -PSProvider FileSystem -Root '{workDirectory.Replace("'", "''")}' -Scope Global | Out-Null\n"
                      + "Set-Location SDEMO:\n"
                      + $"& '{scriptPath.Replace("'", "''")}'\n"
                      + "exit $LASTEXITCODE\n";
        await File.WriteAllTextAsync(wrapperPath, wrapper, new UTF8Encoding(true)).ConfigureAwait(false);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                WorkingDirectory = workDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (var argument in new[]
                     {
                         "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                         "-File", wrapperPath
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            foreach (var pair in job.Environment)
            {
                if (!AllowedEnvironment.Contains(pair.Key))
                {
                    throw new SandboxProtocolException($"environment 不允许设置 {pair.Key}。");
                }

                startInfo.Environment[pair.Key] = pair.Value;
            }

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException("无法启动 powershell.exe。");
            }

            var stdoutTask = ReadLimitedAsync(process.StandardOutput.BaseStream, job.MaxOutputBytes);
            var stderrTask = ReadLimitedAsync(process.StandardError.BaseStream, job.MaxOutputBytes);

            var timedOut = false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(job.TimeoutSeconds));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                timedOut = true;
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // 进程刚好自行退出。
                }

                await process.WaitForExitAsync().ConfigureAwait(false);
            }

            var (stdout, stdoutTruncated) = await stdoutTask.ConfigureAwait(false);
            var (stderr, stderrTruncated) = await stderrTask.ConfigureAwait(false);
            stopwatch.Stop();

            return new SandboxResultDocument
            {
                JobId = job.JobId,
                ExitCode = process.ExitCode,
                TimedOut = timedOut,
                Truncated = stdoutTruncated || stderrTruncated,
                StdOut = stdout,
                StdErr = stderr,
                DurationMs = stopwatch.ElapsedMilliseconds
            };
        }
        finally
        {
            TryDelete(wrapperPath);
        }
    }

    private static async Task<(string Text, bool Truncated)> ReadLimitedAsync(Stream stream, int maxBytes)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8 * 1024];
        var truncated = false;

        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(chunk).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length < maxBytes)
                {
                    var take = (int)Math.Min(read, maxBytes - buffer.Length);
                    buffer.Write(chunk, 0, take);
                    if (take < read)
                    {
                        truncated = true;
                    }
                }
                else
                {
                    truncated = true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // 进程被终止时流会被关闭；保留已读内容。
        }

        var text = Encoding.UTF8.GetString(buffer.ToArray());
        return (text, truncated);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响结果。
        }
    }
}
