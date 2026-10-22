using System.Text.Json;
using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Sandbox.Protocol;

namespace SDemo.DesktopAgent.SandboxHost;

/// <summary>
/// 沙箱内 console helper：<c>run --job &lt;path&gt;</c>。
/// 只依赖契约，不引用 Core；所有路径都从 job.json 所在目录推导，不信任文档里的绝对路径。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            // 宿主读取重定向的 stdout / stderr 时按 UTF-8 解码，避免中文错误信息乱码。
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            // 无控制台时忽略。
        }

        if (args.Length != 3 || args[0] != "run" || args[1] != "--job")
        {
            Console.Error.WriteLine("Usage: SandboxHost run --job <path>");
            return 2;
        }

        try
        {
            return RunJob(args[2]);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SandboxHost 失败：{ex.Message}");
            return 3;
        }
    }

    private static int RunJob(string jobPath)
    {
        var fullJobPath = Path.GetFullPath(jobPath);
        var inputDirectory = Path.GetDirectoryName(fullJobPath)
                             ?? throw new SandboxProtocolException("无法推导 input 目录。");
        var jobRoot = Path.GetDirectoryName(inputDirectory)
                      ?? throw new SandboxProtocolException("无法推导 job 根目录。");

        if (!SandboxPathGuard.IsUnder(Path.Combine(jobRoot, "input"), fullJobPath))
        {
            Console.Error.WriteLine("job.json 必须位于 job 根目录的 input 子目录内。");
            return 3;
        }

        var job = JsonSerializer.Deserialize<SandboxJobDocument>(File.ReadAllText(fullJobPath), SandboxJson.Options)
                  ?? throw new SandboxProtocolException("job.json 为空或格式不正确。");

        if (job.ProtocolVersion != SandboxProtocol.Version)
        {
            Console.Error.WriteLine($"协议版本不匹配：{job.ProtocolVersion}（期望 {SandboxProtocol.Version}）。");
            return 3;
        }

        var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(jobRoot));
        if (!string.Equals(directoryName, job.JobId, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"jobId 与目录名不一致：{job.JobId} / {directoryName}。");
            return 3;
        }

        SandboxResultDocument result;
        try
        {
            Directory.CreateDirectory(Path.Combine(jobRoot, "output"));
            Validate(job);
            result = ExecuteAsync(job, jobRoot).GetAwaiter().GetResult();
        }
        catch (SandboxProtocolException ex)
        {
            result = new SandboxResultDocument
            {
                JobId = job.JobId,
                ExitCode = -1,
                Error = ex.Message
            };
        }

        var outputDirectory = Path.Combine(jobRoot, "output");
        Directory.CreateDirectory(outputDirectory);
        ResultWriter.WriteAtomic(Path.Combine(outputDirectory, SandboxProtocol.ResultFileName), result);
        return 0;
    }

    private static void Validate(SandboxJobDocument job)
    {
        if (job.TimeoutSeconds is < 1 or > 120)
        {
            throw new SandboxProtocolException("timeoutSeconds 必须在 1–120 之间。");
        }

        if (job.MaxOutputBytes is < 1 or > 8 * 1024 * 1024)
        {
            throw new SandboxProtocolException("maxOutputBytes 必须在 1–8MB 之间。");
        }

        if (job.Mode == SandboxJobMode.PowerShell && string.IsNullOrWhiteSpace(job.ScriptFile))
        {
            throw new SandboxProtocolException("PowerShell 模式缺少 scriptFile。");
        }

        if (job.Mode is SandboxJobMode.PdfExtract or SandboxJobMode.PdfInfo
            && string.IsNullOrWhiteSpace(job.InputFile))
        {
            throw new SandboxProtocolException("PDF 模式缺少 inputFile。");
        }
    }

    private static async Task<SandboxResultDocument> ExecuteAsync(SandboxJobDocument job, string jobRoot) =>
        job.Mode switch
        {
            SandboxJobMode.PowerShell => await PowerShellRunner.RunAsync(job, jobRoot).ConfigureAwait(false),
            SandboxJobMode.PdfExtract or SandboxJobMode.PdfInfo => PdfCommand.Run(job, jobRoot),
            _ => new SandboxResultDocument
            {
                JobId = job.JobId,
                ExitCode = -1,
                Error = $"未知 mode：{job.Mode}"
            }
        };
}
