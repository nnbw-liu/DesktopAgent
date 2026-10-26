using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SDemo.DesktopAgent.Platform.Windows.Execution;
using SDemo.DesktopAgent.Platform.Windows.Services;

namespace SDemo.ArchifyP0;

/// <summary>
/// P0 探针：验证 DesktopAgent 的生产直跑链（<see cref="DirectProcessRunner"/>）能否承载
/// Archify 3.2.0：node 解析、固定 argv、工作区 cwd、超时、输出上限，以及 Edge 里的
/// 真实浏览器校验（browser-check）。
/// <para>
/// 用法（在仓库根）：
/// <c>dotnet run --project tools/p0-archify -c Release -- --no-open</c>
/// </para>
/// <para>
/// 结论写到 workspace 的 <c>.archify-p0/report.json</c> 与 <c>report.md</c>；
/// HTML 产物默认用系统浏览器打开（<c>--no-open</c> 可关）。
/// </para>
/// </summary>
internal static class Program
{
    private const string SkillVersionDirectory = "3.2.0";
    private const string ProbeReportDirectory = ".archify-p0";

    public static async Task<int> Main(string[] args)
    {
        var options = Options.Parse(args);
        var repoRoot = FindRepoRoot();
        var source = options.Source ?? Path.Combine(repoRoot, ".tmp", "archify-repo", "archify");

        var installRoot = options.InstallRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SDemo", "DesktopAgent", "archify", SkillVersionDirectory);

        var workspace = options.Workspace ?? Path.Combine(repoRoot, ".tmp", "p0-workspace");

        Console.WriteLine("== Archify P0 探针 ==");
        Console.WriteLine($"源技能目录 : {source}");
        Console.WriteLine($"安装目录   : {installRoot}");
        Console.WriteLine($"工作区     : {workspace}");

        if (!Directory.Exists(source) || !File.Exists(Path.Combine(source, "bin", "archify.mjs")))
        {
            Console.Error.WriteLine($"找不到 Archify 技能目录（需要 bin/archify.mjs）：{source}");
            Console.Error.WriteLine("先把上游仓库 clone 到 .tmp/archify-repo，或用 --source 指定。");
            return 2;
        }

        Directory.CreateDirectory(workspace);

        var runner = new DirectProcessRunner();
        var launcher = new ShellFileLauncher();

        // 1. Node 是否可用（生产直跑器 + 环境白名单）。
        var node = await runner.RunAsync(
            "node",
            ["--version"],
            workspace,
            TimeSpan.FromSeconds(10),
            4_096).ConfigureAwait(false);

        var nodeVersion = node.StdOut.Trim();
        Console.WriteLine($"node        : exit={node.ExitCode} version={nodeVersion}");
        Console.WriteLine($"node stderr : {Trim(node.StdErr)}");

        // 2. Edge 是否存在（只做宿主侧探测；真正的证据以 browser-check 收据为准）。
        var edge = FindEdge();
        Console.WriteLine($"edge        : {edge.Path ?? "(未找到)"} {edge.Version}");

        // 3. 按设计中的安装布局落一份运行时（剔除 examples/*.html）。
        var runtime = StageRuntime(source, installRoot, options.KeepRuntime);

        // 4. 在工作区里按 `.archify/<type>-<slug>-<时间戳>/` 约定跑一次 finalize。
        //    显式注入 ARCHIFY_CHROME，修复直跑器白名单清掉 PROGRAMFILES 后找不到 Edge 的问题；
        //    同时关闭更新检查，保证探针不联网。
        var run = await RenderAsync(runner, runtime, workspace, edge.Path, options.TimeoutSeconds)
            .ConfigureAwait(false);

        // 5. 校验收据与产物。
        var artifactFull = run.ArtifactPath is null
            ? null
            : ResolveUnder(workspace, run.ArtifactPath);

        var artifactExists = artifactFull is not null && File.Exists(artifactFull);
        var artifactSha = artifactExists ? Sha256File(artifactFull!) : null;
        var shaMatches = artifactSha is not null
                         && string.Equals(artifactSha, run.ArtifactSha256, StringComparison.OrdinalIgnoreCase);

        var opened = false;

        if (artifactExists && !options.NoOpen)
        {
            opened = await launcher.OpenFileAsync(artifactFull!).ConfigureAwait(false);
            Console.WriteLine($"打开产物    : {opened}");
        }

        // 6. 判定。browser-check 是 P0 的核心：Edge 真跑起来才算通过。
        var gates = run.Gates;
        var pass = run.ExitCode == 0
                   && run.Ok
                   && run.TimedOut == false
                   && artifactExists
                   && shaMatches
                   && GateIs(gates, "validate")
                   && GateIs(gates, "deliver")
                   && GateIs(gates, "check")
                   && GateIs(gates, "browser-check");

        var report = new
        {
            probe = "archify-p0",
            timestamp = DateTimeOffset.Now,
            machine = new
            {
                os = Environment.OSVersion.VersionString,
                architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                dataRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SDemo", "DesktopAgent")
            },
            node = new { version = nodeVersion, exitCode = node.ExitCode, stderr = Trim(node.StdErr) },
            edge = new { path = edge.Path, version = edge.Version },
            runtime = new
            {
                source,
                installRoot,
                version = runtime.Version,
                releaseSource = runtime.ReleaseSource,
                files = runtime.Files,
                bytes = runtime.Bytes,
                archifyScriptSha256 = runtime.ScriptSha256
            },
            run = new
            {
                command = "node bin/archify.mjs finalize architecture <candidate> <output> --quality showcase --json",
                arguments = run.Arguments,
                workingDirectory = workspace,
                exitCode = run.ExitCode,
                timedOut = run.TimedOut,
                durationMs = run.DurationMs,
                stdoutTruncated = run.StdOutTruncated,
                stderr = Trim(run.StdErr),
                ok = run.Ok,
                status = run.Status,
                gates,
                chrome = run.Chrome,
                update = run.Update,
                diagnostics = run.Diagnostics,
                visualReview = run.VisualReview
            },
            artifact = new
            {
                path = run.ArtifactPath,
                exists = artifactExists,
                bytes = artifactExists ? new FileInfo(artifactFull!).Length : 0,
                sha256 = artifactSha,
                receiptSha256 = run.ArtifactSha256,
                shaMatches
            },
            opened,
            pass
        };

        var reportDir = Path.Combine(workspace, ProbeReportDirectory);
        Directory.CreateDirectory(reportDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var jsonPath = Path.Combine(reportDir, $"report-{stamp}.json");
        var mdPath = Path.Combine(reportDir, $"report-{stamp}.md");

        await File.WriteAllTextAsync(
            jsonPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);

        await File.WriteAllTextAsync(mdPath, BuildMarkdown(report, gates, run, artifactExists, shaMatches))
            .ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine($"闸门        : {FormatGates(gates)}");
        Console.WriteLine($"chrome      : {run.Chrome ?? "(收据里没有 browser-check 证据)"}");
        Console.WriteLine($"产物        : {run.ArtifactPath} ({run.Bytes} bytes, sha256 {(shaMatches ? "匹配" : "不匹配")})");
        Console.WriteLine($"报告        : {jsonPath}");
        Console.WriteLine($"              {mdPath}");
        Console.WriteLine($"结论        : {(pass ? "PASS" : "FAIL")}");

        if (!pass)
        {
            Console.WriteLine();
            Console.WriteLine("stderr：");
            Console.WriteLine(Trim(run.StdErr));
            Console.WriteLine("diagnostics：");
            Console.WriteLine(run.Diagnostics);
        }

        return pass ? 0 : 1;
    }

    // ------------------------------------------------------------------ 运行时

    private static RuntimeInfo StageRuntime(string source, string installRoot, bool keep)
    {
        var expectedBase = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SDemo", "DesktopAgent", "archify"));

        var full = Path.GetFullPath(installRoot);

        if (!full.StartsWith(expectedBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"拒绝在非预期目录下覆盖运行时：{full}");
        }

        if (!keep || !File.Exists(Path.Combine(full, "bin", "archify.mjs")))
        {
            if (Directory.Exists(full))
            {
                Directory.Delete(full, recursive: true);
            }

            CopyDirectory(source, full, (relative) =>
            {
                // 只裁掉示例 HTML（12.2 MB）；示例 JSON 与 locales 的作者参考保留。
                var normalized = relative.Replace('\\', '/');
                return !(normalized.StartsWith("examples/", StringComparison.OrdinalIgnoreCase)
                         && normalized.EndsWith(".html", StringComparison.OrdinalIgnoreCase));
            });
        }

        var release = ReadRelease(source);
        var files = Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories).ToArray();
        var bytes = files.Sum(file => new FileInfo(file).Length);
        var script = Path.Combine(full, "bin", "archify.mjs");

        return new RuntimeInfo(
            full,
            release.Version,
            release.Source,
            files.Length,
            bytes,
            Sha256File(script));
    }

    private static (string Version, string Source) ReadRelease(string source)
    {
        var releasePath = Path.Combine(source, "skill-release.json");

        if (File.Exists(releasePath))
        {
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(releasePath));

                return (
                    node?["version"]?.GetValue<string>() ?? SkillVersionDirectory,
                    node?["source"]?["repository"]?.GetValue<string>() ?? "unknown");
            }
            catch
            {
                // 收据文件偶尔不存在 / 结构变化时不影响探针。
            }
        }

        return (SkillVersionDirectory, "unknown");
    }

    // ------------------------------------------------------------------ 渲染

    private static async Task<RunInfo> RenderAsync(
        DirectProcessRunner runner,
        RuntimeInfo runtime,
        string workspace,
        string? edgePath,
        int timeoutSeconds)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var relativeDirectory = $".archify/architecture-web-app-{stamp}";
        var relativeCandidate = $"{relativeDirectory}/candidate.json";
        var relativeOutput = $"{relativeDirectory}/web-app.html";

        var directory = Path.Combine(workspace, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);

        // 用上游示例当候选；把 meta.output 指到本次相对输出，符合 SKILL.md 的约定。
        var examplePath = Path.Combine(runtime.Root, "examples", "web-app.architecture.json");
        var candidateText = await File.ReadAllTextAsync(examplePath).ConfigureAwait(false);
        var candidate = JsonNode.Parse(candidateText)
                        ?? throw new InvalidOperationException("示例候选不是合法 JSON。");

        candidate["meta"] ??= new JsonObject();
        candidate["meta"]!["output"] = relativeOutput;

        var candidatePath = Path.Combine(workspace, relativeCandidate.Replace('/', Path.DirectorySeparatorChar));
        await File.WriteAllTextAsync(candidatePath, candidate.ToJsonString(new JsonSerializerOptions { WriteIndented = true }))
            .ConfigureAwait(false);

        var arguments = new[]
        {
            "finalize",
            "architecture",
            relativeCandidate,
            relativeOutput,
            "--quality",
            "showcase",
            "--json"
        };

        var commandArguments = new List<string> { Path.Combine(runtime.Root, "bin", "archify.mjs") };
        commandArguments.AddRange(arguments);

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // 探针不联网：更新检查会去 GitHub 看新版本，这里明确关掉。
            ["ARCHIFY_UPDATE_CHECK_DISABLED"] = "1",

            // P0 发现：Edge 在直跑器的 Job Object 里起不来渲染进程（CDP Page.enable 超时），
            // 加 --no-sandbox 才能跑通。是否接受这个开关留给 P1 决策，见 docs/reports/p0-archify.md。
            ["ARCHIFY_CHROME_NO_SANDBOX"] = "1"
        };

        if (!string.IsNullOrWhiteSpace(edgePath))
        {
            // 直跑器的环境白名单不含 PROGRAMFILES，Archify 自己找不到 Edge；由宿主解析后显式给出。
            environment["ARCHIFY_CHROME"] = edgePath;
        }

        var result = await runner.RunAsync(
            "node",
            commandArguments,
            workspace,
            TimeSpan.FromSeconds(timeoutSeconds),
            400_000,
            environment: environment).ConfigureAwait(false);

        var receipt = TryParseJson(result.StdOut);
        var gates = receipt?["gates"]?.AsObject().ToDictionary(
            pair => pair.Key,
            pair => pair.Value?.GetValue<string>() ?? "unknown") ?? [];

        var artifactPath = receipt?["artifact"]?["path"]?.GetValue<string>();
        var artifactSha = receipt?["artifact"]?["sha256"]?.GetValue<string>();
        var bytes = receipt?["artifact"]?["bytes"]?.GetValue<long>() ?? 0;
        var chrome = ReadChromeEvidence(workspace, artifactPath, receipt);

        return new RunInfo(
            result.ExitCode,
            result.TimedOut,
            result.DurationMs,
            result.StdOutTruncated,
            result.StdErr,
            commandArguments,
            receipt?["ok"]?.GetValue<bool>() ?? false,
            receipt?["status"]?.GetValue<string>() ?? "unknown",
            gates,
            chrome,
            receipt?["update"]?.ToJsonString(),
            receipt?["diagnostics"]?.ToJsonString() ?? "[]",
            receipt?["visualReview"]?.GetValue<string>() ?? "unknown",
            artifactPath,
            artifactSha,
            bytes);
    }

    /// <summary>browser-check 的 chrome 证据在侧车收据里；优先读侧车，其次读 finalize 的 evidence 路径。</summary>
    private static string? ReadChromeEvidence(string workspace, string? artifactPath, JsonNode? receipt)
    {
        try
        {
            var sidecar = receipt?["evidence"]?["browserCheckReceipt"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(sidecar))
            {
                if (artifactPath is null)
                {
                    return null;
                }

                sidecar = Path.ChangeExtension(ResolveUnder(workspace, artifactPath), ".browser-check.json");
            }

            if (string.IsNullOrWhiteSpace(sidecar) || !File.Exists(sidecar))
            {
                return null;
            }

            var node = JsonNode.Parse(File.ReadAllText(sidecar));
            var chrome = node?["chrome"];

            return chrome is null
                ? null
                : $"status={chrome["status"]?.GetValue<string>()} executable={chrome["executable"]?.GetValue<string>()}";
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ 输出

    private static string BuildMarkdown(
        dynamic report,
        IReadOnlyDictionary<string, string> gates,
        RunInfo run,
        bool artifactExists,
        bool shaMatches)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Archify P0 探针报告");
        builder.AppendLine();
        builder.AppendLine($"- 时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} (Asia/Shanghai)");
        builder.AppendLine($"- node：{report.node.version}（直跑器解析）");
        builder.AppendLine($"- Edge：{(string.IsNullOrWhiteSpace(report.edge.path) ? "未找到" : report.edge.path + " " + report.edge.version)}");
        builder.AppendLine($"- 运行时：{report.runtime.version}，{report.runtime.files} 个文件 / {report.runtime.bytes} 字节，archify.mjs sha256 `{report.runtime.archifyScriptSha256}`");
        builder.AppendLine($"- 命令：`{string.Join(' ', run.Arguments.Select(arg => arg.Contains(' ') ? $"\"{arg}\"" : arg))}`");
        builder.AppendLine($"- 工作目录：`{report.run.workingDirectory}`");
        builder.AppendLine($"- 退出码：{run.ExitCode}，耗时 {run.DurationMs} ms，超时 {run.TimedOut}，stdout 截断 {run.StdOutTruncated}");
        builder.AppendLine();
        builder.AppendLine("## 闸门");
        builder.AppendLine();
        builder.AppendLine("| 闸门 | 结果 |");
        builder.AppendLine("| --- | --- |");

        foreach (var (gate, value) in gates)
        {
            builder.AppendLine($"| {gate} | {value} |");
        }

        builder.AppendLine();
        builder.AppendLine("## 证据");
        builder.AppendLine();
        builder.AppendLine($"- chrome：{run.Chrome ?? "（收据里没有）"}");
        builder.AppendLine($"- 产物：`{run.ArtifactPath}`（{run.Bytes} 字节，sha256 {(shaMatches ? "匹配" : "不匹配")}，存在 {artifactExists}）");
        builder.AppendLine($"- visualReview：{run.VisualReview}");
        builder.AppendLine($"- update：`{run.Update ?? "（无）"}`");
        builder.AppendLine();
        builder.AppendLine("## diagnostics");
        builder.AppendLine();
        builder.AppendLine("```json");
        builder.AppendLine(run.Diagnostics);
        builder.AppendLine("```");
        builder.AppendLine();
        builder.AppendLine($"## 结论：{(report.pass ? "PASS" : "FAIL")}");
        builder.AppendLine();
        builder.AppendLine("> 本报告由 `tools/p0-archify` 生成；HTML 产物在同一工作区 `.archify/` 下。");

        return builder.ToString();
    }

    private static string FormatGates(IReadOnlyDictionary<string, string> gates) =>
        gates.Count == 0 ? "(无)" : string.Join(", ", gates.Select(pair => $"{pair.Key}={pair.Value}"));

    private static bool GateIs(IReadOnlyDictionary<string, string> gates, string name) =>
        gates.TryGetValue(name, out var value)
        && string.Equals(value, "pass", StringComparison.OrdinalIgnoreCase);

    private static string Trim(string text) =>
        text.Length <= 4_000 ? text : text[..4_000] + "…";

    // ------------------------------------------------------------------ 工具

    private static JsonNode? TryParseJson(string stdout)
    {
        var text = stdout.Trim();

        if (text.Length == 0)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            // 有些命令会先打一行进度再加收据；取第一个 `{` 到最后一个 `}`。
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');

            if (start < 0 || end <= start)
            {
                return null;
            }

            try
            {
                return JsonNode.Parse(text[start..(end + 1)]);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    private static string ResolveUnder(string root, string relative) =>
        Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string FindRepoRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(Path.GetFullPath(start));

            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "SDemo.DesktopAgent.sln")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new InvalidOperationException("找不到仓库根（SDemo.DesktopAgent.sln）。");
    }

    private static (string? Path, string Version) FindEdge()
    {
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
                candidates.Add(Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"));
            }
        }

        var found = candidates.FirstOrDefault(File.Exists);

        if (found is null)
        {
            return (null, string.Empty);
        }

        try
        {
            return (found, FileVersionInfo.GetVersionInfo(found).FileVersion ?? string.Empty);
        }
        catch
        {
            return (found, string.Empty);
        }
    }

    private static void CopyDirectory(string source, string destination, Func<string, bool> include)
    {
        var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, file);

            if (!include(relative))
            {
                continue;
            }

            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    // ------------------------------------------------------------------ 记录

    private sealed record RuntimeInfo(
        string Root,
        string Version,
        string ReleaseSource,
        int Files,
        long Bytes,
        string ScriptSha256);

    private sealed record RunInfo(
        int ExitCode,
        bool TimedOut,
        long DurationMs,
        bool StdOutTruncated,
        string StdErr,
        IReadOnlyList<string> Arguments,
        bool Ok,
        string Status,
        IReadOnlyDictionary<string, string> Gates,
        string? Chrome,
        string? Update,
        string Diagnostics,
        string VisualReview,
        string? ArtifactPath,
        string? ArtifactSha256,
        long Bytes);

    // ------------------------------------------------------------------ 参数

    private sealed class Options
    {
        public string? Source { get; private init; }
        public string? InstallRoot { get; private init; }
        public string? Workspace { get; private init; }
        public int TimeoutSeconds { get; private init; } = 120;
        public bool NoOpen { get; private init; }
        public bool KeepRuntime { get; private init; }

        public static Options Parse(string[] args)
        {
            string? source = null;
            string? installRoot = null;
            string? workspace = null;
            var timeout = 120;
            var noOpen = false;
            var keep = false;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--source" when i + 1 < args.Length:
                        source = args[++i];
                        break;
                    case "--install-root" when i + 1 < args.Length:
                        installRoot = args[++i];
                        break;
                    case "--workspace" when i + 1 < args.Length:
                        workspace = args[++i];
                        break;
                    case "--timeout-seconds" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsed):
                        timeout = parsed;
                        i++;
                        break;
                    case "--no-open":
                        noOpen = true;
                        break;
                    case "--keep-runtime":
                        keep = true;
                        break;
                    default:
                        Console.Error.WriteLine($"忽略未知参数：{args[i]}");
                        break;
                }
            }

            return new Options
            {
                Source = source,
                InstallRoot = installRoot,
                Workspace = workspace,
                TimeoutSeconds = timeout,
                NoOpen = noOpen,
                KeepRuntime = keep
            };
        }
    }
}
