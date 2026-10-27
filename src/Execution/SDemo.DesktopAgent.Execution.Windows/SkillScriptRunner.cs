using System.Diagnostics;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.Platform.Windows.Execution;

/// <summary>
/// 技能脚本的 Windows 执行器：解析 Node / 浏览器，固定 argv 走 <see cref="IDirectProcessRunner"/>。
/// 只对 Archify 技能注入 <c>ARCHIFY_CHROME</c> 与 <c>ARCHIFY_CHROME_NO_SANDBOX</c>；
/// 其它技能拿到的是普通直跑环境（不继承宿主 API Key）。
/// </summary>
public sealed class SkillScriptRunner : ISkillScriptRunner
{
    private const string NodeExecutable = "node";

    private readonly IDirectProcessRunner _runner;

    public SkillScriptRunner(IDirectProcessRunner runner)
    {
        _runner = runner;
        Host = DetectHost();
    }

    public SkillScriptHostInfo Host { get; }

    public async Task<SkillScriptRunResult> RunAsync(
        SkillScriptRequest request,
        CancellationToken cancellationToken = default)
    {
        var host = Host;

        if (string.IsNullOrWhiteSpace(host.NodePath))
        {
            return SkillScriptRunResult.Failed(
                host,
                "没有找到 Node.js。请安装 Node 20+，或把 node.exe 放进 PATH 后重启应用。");
        }

        var arguments = new List<string>(request.Arguments.Count + 1)
        {
            request.ScriptPath
        };
        arguments.AddRange(request.Arguments);

        var environment = BuildEnvironment(request, host);

        var result = await _runner.RunAsync(
                host.NodePath,
                arguments,
                request.WorkingDirectory,
                request.Timeout,
                request.MaxOutputChars,
                cancellationToken,
                environment)
            .ConfigureAwait(false);

        return new SkillScriptRunResult(
            host.NodeAvailable,
            host.NodePath,
            host.NodeVersion,
            result.ExitCode,
            result.StdOut,
            result.StdErr,
            result.TimedOut,
            result.Canceled,
            result.DurationMs,
            result.StdOutTruncated,
            result.StdErrTruncated);
    }

    /// <summary>
    /// 宿主注入的环境变量。Archify 目标：更新检查关闭、Edge 路径显式给出、
    /// 以及 P0 已确认的 <c>--no-sandbox</c> 开关；其它技能不碰这些变量。
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildEnvironment(
        SkillScriptRequest request,
        SkillScriptHostInfo host)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // 任何技能脚本都不该在渲染链路里偷偷联网检查更新。
            ["ARCHIFY_UPDATE_CHECK_DISABLED"] = "1"
        };

        if (!LooksLikeArchify(request))
        {
            return environment;
        }

        // P0 发现：Edge 的渲染进程在直跑器的 Job Object 里起不来，需要关掉浏览器沙箱。
        // 这个开关只作用于 Archify 子进程，不改变 DesktopAgent 自身的沙箱策略。
        environment["ARCHIFY_CHROME_NO_SANDBOX"] = "1";

        if (!string.IsNullOrWhiteSpace(host.ChromePath))
        {
            // 直跑器的环境白名单不含 PROGRAMFILES，Archify 自己找不到浏览器。
            environment["ARCHIFY_CHROME"] = host.ChromePath;
        }

        return environment;
    }

    private static bool LooksLikeArchify(SkillScriptRequest request) =>
        request.SkillName.Contains("archify", StringComparison.OrdinalIgnoreCase)
        || request.SkillSource.Contains("archify", StringComparison.OrdinalIgnoreCase);

    private static SkillScriptHostInfo DetectHost()
    {
        var node = FindNode();

        if (node is null)
        {
            return new SkillScriptHostInfo(
                false,
                null,
                null,
                FindBrowser(),
                "未找到 Node.js；请安装 Node 20+，或把 node.exe 放进 PATH 后重启应用。");
        }

        var version = ReadVersion(node);
        var browser = FindBrowser();

        return new SkillScriptHostInfo(
            true,
            node,
            version,
            browser,
            browser is null
                ? $"Node {version ?? "已找到"}；未找到 Edge / Chrome，Archify 的 browser-check 可能失败。"
                : $"Node {version ?? "已找到"}；浏览器已找到。");
    }

    /// <summary>先走 PATH，再查 Node 安装器 / nvm 常见目录。</summary>
    private static string? FindNode()
    {
        var resolved = DirectProcessRunner.ResolveExecutable(NodeExecutable);

        if (resolved is not null)
        {
            return resolved;
        }

        var candidates = new List<string>();

        foreach (var root in new[]
                 {
                     Environment.GetEnvironmentVariable("ProgramFiles"),
                     Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                     Environment.GetEnvironmentVariable("LocalApplicationData")
                 })
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                candidates.Add(Path.Combine(root, "nodejs", "node.exe"));
                candidates.Add(Path.Combine(root, "Programs", "nodejs", "node.exe"));
            }
        }

        var nvm = Environment.GetEnvironmentVariable("NVM_HOME");

        if (!string.IsNullOrWhiteSpace(nvm) && Directory.Exists(nvm))
        {
            candidates.AddRange(Directory.EnumerateDirectories(nvm, "v*")
                .OrderByDescending(directory => directory, StringComparer.OrdinalIgnoreCase)
                .Select(directory => Path.Combine(directory, "node.exe")));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>浏览器候选：Edge 优先（系统自带），其次 Chrome。</summary>
    private static string? FindBrowser()
    {
        var candidates = new List<string>();

        foreach (var root in new[]
                 {
                     Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                     Environment.GetEnvironmentVariable("ProgramFiles"),
                     Environment.GetEnvironmentVariable("ProgramW6432"),
                     Environment.GetEnvironmentVariable("LocalApplicationData")
                 })
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            candidates.Add(Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"));
            candidates.Add(Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? ReadVersion(string path)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(path).FileVersion;
        }
        catch
        {
            return null;
        }
    }
}
