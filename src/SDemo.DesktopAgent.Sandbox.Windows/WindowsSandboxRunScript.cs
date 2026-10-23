using System.IO;
using System.Text;

namespace SDemo.DesktopAgent.Platform.Windows.Sandbox;

/// <summary>
/// 生成 Windows Sandbox 内的两个脚本（S4）：
/// <list type="bullet">
///   <item><c>run.ps1</c>：LogonCommand 入口，启动子 powershell、把 stdout/stderr 写进映射的 output 目录，
///   最后原子写出 <c>result.json</c>。</item>
///   <item><c>run-script.ps1</c>：子 powershell 的包装脚本，先设置 UTF-8 输出编码再调用用户脚本，
///   避免中文输出按控制台代码页被写成乱码。</item>
/// </list>
/// 两个文件都由宿主写入只读的 input 映射目录，内容不拼接任何模型提供的路径。
/// </summary>
public static class WindowsSandboxRunScript
{
    public const string RunScriptFileName = "run.ps1";

    public const string ScriptWrapperFileName = "run-script.ps1";

    /// <summary>生成 run.ps1。jobId 必须是 [A-Za-z0-9-]，工作目录是相对 work 的安全相对路径。</summary>
    public static string CreateRunner(string jobId, string workingDirectory = ".")
    {
        if (!SandboxDirectory.IsValidJobId(jobId))
        {
            throw new ArgumentException("jobId 必须是 8–64 个字母 / 数字 / 连字符。", nameof(jobId));
        }

        var workDirectory = ResolveWorkDirectory(workingDirectory);

        return RunnerTemplate
            .Replace("{{JOB_ID}}", jobId)
            .Replace("{{WORK_DIR}}", workDirectory);
    }

    /// <summary>生成 run-script.ps1（调用用户脚本的包装脚本）。</summary>
    public static string CreateWrapper(string scriptPath = @"C:\job\input\script.ps1") =>
        WrapperTemplate.Replace("{{SCRIPT}}", scriptPath.Replace("'", "''"));

    /// <summary>把两个脚本写入 input 目录；run.ps1 用 UTF-8 无 BOM，run-script.ps1 用 BOM（PS 5.1 依赖 BOM 识别 UTF-8）。</summary>
    public static void Write(string inputDirectory, string jobId, string workingDirectory = ".")
    {
        Directory.CreateDirectory(inputDirectory);

        File.WriteAllText(
            Path.Combine(inputDirectory, RunScriptFileName),
            CreateRunner(jobId, workingDirectory),
            new UTF8Encoding(false));

        File.WriteAllText(
            Path.Combine(inputDirectory, ScriptWrapperFileName),
            CreateWrapper(),
            new UTF8Encoding(true));
    }

    /// <summary>把 job.json 里的 <c>work/...</c> 转成沙箱内绝对路径。</summary>
    public static string ResolveWorkDirectory(string workingDirectory)
    {
        var value = string.IsNullOrWhiteSpace(workingDirectory) || workingDirectory == "work"
            ? "."
            : workingDirectory;

        if (value == ".")
        {
            return @"C:\job\work";
        }

        const string prefix = "work/";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[prefix.Length..];
        }

        if (value.Contains("..", StringComparison.Ordinal)
            || value.Contains('\'')
            || value.Contains('\n')
            || value.Contains('\r')
            || value.Contains('\0'))
        {
            throw new ArgumentException($"工作目录不是安全相对路径：{workingDirectory}", nameof(workingDirectory));
        }

        return @"C:\job\work\" + value.Replace('/', '\\').TrimEnd('\\');
    }

    private const string RunnerTemplate = """
        $ErrorActionPreference = 'Continue'
        $jobId = '{{JOB_ID}}'
        $input = 'C:\job\input'
        $work = '{{WORK_DIR}}'
        $output = 'C:\job\output'
        $stdout = Join-Path $output 'stdout.txt'
        $stderr = Join-Path $output 'stderr.txt'
        $result = Join-Path $output 'result.json'
        $started = Join-Path $output 'started.txt'
        $wrapper = Join-Path $input 'run-script.ps1'
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $code = 1

        try {
            [System.IO.File]::WriteAllText($started, [DateTime]::UtcNow.ToString('o'), (New-Object System.Text.UTF8Encoding($false)))
        } catch { }

        try {
            if (-not (Test-Path -LiteralPath $work)) { throw "working directory does not exist: $work" }
            if (-not (Test-Path -LiteralPath $wrapper)) { throw "runner wrapper does not exist: $wrapper" }
            $arguments = @('-NoLogo','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$wrapper)
            $process = Start-Process -FilePath 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' `
                -ArgumentList $arguments -WorkingDirectory $work -NoNewWindow -Wait -PassThru `
                -RedirectStandardOutput $stdout -RedirectStandardError $stderr
            $code = $process.ExitCode
            if ($null -eq $code) { $code = 1 }
        } catch {
            $code = 1
            try {
                [System.IO.File]::AppendAllText($stderr, "run.ps1: $($_.Exception.Message)`r`n", (New-Object System.Text.UTF8Encoding($false)))
            } catch { }
        }

        $sw.Stop()
        $payload = [ordered]@{
            protocolVersion = 1
            jobId = $jobId
            exitCode = [int]$code
            timedOut = $false
            canceled = $false
            truncated = $false
            stdout = ''
            stderr = ''
            durationMs = [int]$sw.Elapsed.TotalMilliseconds
            artifacts = @()
            error = $null
        }

        $json = $payload | ConvertTo-Json -Depth 4 -Compress
        $tmp = $result + '.tmp'
        try {
            [System.IO.File]::WriteAllText($tmp, $json, (New-Object System.Text.UTF8Encoding($false)))
            if (Test-Path -LiteralPath $result) { Remove-Item -LiteralPath $result -Force }
            [System.IO.File]::Move($tmp, $result)
        } catch {
            try {
                Set-Content -LiteralPath $result -Value $json -Encoding utf8
            } catch { }
        }
        """;

    private const string WrapperTemplate = """
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        $OutputEncoding = [Console]::OutputEncoding
        $ErrorActionPreference = 'Continue'
        try {
            & '{{SCRIPT}}'
        } catch {
            Write-Error $_
            exit 1
        }
        exit $LASTEXITCODE
        """;
}
