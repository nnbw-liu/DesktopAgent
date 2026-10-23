using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Editing;
using SDemo.DesktopAgent.Execution;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Platform.Windows.Execution;
using SDemo.DesktopAgent.Platform.Windows.Tools;
using SDemo.DesktopAgent.Services;
using ChatMessage = SDemo.DesktopAgent.Models.ChatMessage;
using ChatMessageRole = SDemo.DesktopAgent.Models.ChatMessageRole;
using ChatSession = SDemo.DesktopAgent.Models.ChatSession;

namespace SDemo.AgentEval;

/// <summary>
/// P7 外部仓库评测运行器：隔离 worktree、逐任务重置、产物归档、验收执行。
/// 用法：
///   dotnet run --project tools/AgentEval -c Release -- \
///     --repo C:\bench\commandline --commit b0b0ec9 \
///     --config eval/configs/compact-8k.json --tasks eval/tasks --out eval/results/round1/compact-8k
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true
    };

    public static async Task<int> Main(string[] args)
    {
        var options = CliOptions.Parse(args);
        var evalRoot = options.EvalRoot ?? Directory.GetCurrentDirectory();
        var config = LoadConfig(options.ConfigPath);
        var tasks = LoadTasks(options.TasksPath)
            .Where(task => options.Only.Count == 0 || options.Only.Contains(task.Id))
            .ToArray();
        if (tasks.Length == 0)
        {
            throw new InvalidOperationException("没有匹配的任务。");
        }

        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SDemo", "DesktopAgent", "settings.json");
        var sourceSettings = LoadSettings(settingsPath);
        var source = sourceSettings.Providers.FirstOrDefault(item => item.Id == config.ProviderId)
            ?? throw new InvalidOperationException($"找不到 provider：{config.ProviderId}");

        var provider = new ModelProvider
        {
            Id = source.Id,
            DisplayName = source.DisplayName,
            BaseUrl = source.BaseUrl,
            ApiKey = source.ApiKey,
            Models =
            [
                new ModelEntry
                {
                    Id = config.ModelId,
                    Profile = new ModelProfile(
                        config.ContextWindow,
                        config.MaxOutputTokens,
                        SupportsTools: true,
                        SupportsParallelToolCalls: false,
                        SupportsReasoning: false,
                        Preset: config.ContextWindow >= 32768 ? "32K" : "8K")
                }
            ]
        };

        await EnsureModelLoadedAsync(source.BaseUrl, config.ModelId);

        var work = options.WorkPath ?? Path.Combine(
            Path.GetTempPath(), "agent-eval", $"{config.Name}-{DateTime.Now:yyyyMMdd-HHmmss}");
        var worktree = await PrepareWorktreeAsync(options.RepoPath, options.Commit, work);
        var outRoot = Path.GetFullPath(options.OutputPath);
        Directory.CreateDirectory(outRoot);

        var settings = new StubSettings();
        settings.Current.Execution = new ExecutionSettings
        {
            Mode = ExecutionMode.AllowlistDirect,
            DirectRunEnabled = true,
            BuildTimeoutSeconds = 600
        };
        settings.Current.Tools.SetPackEnabled("command", true);
        settings.Current.Agent.Mode = config.Mode;
        settings.Current.Agent.CompactMaxToolCalls = config.MaxToolCalls;
        settings.Current.Agent.FullMaxToolCalls = config.MaxToolCalls;
        settings.Current.Workspaces.Add(new Workspace
        {
            Id = "eval",
            Name = $"P7 {config.Name}",
            Path = worktree
        });

        var audit = new MemoryAudit();
        var approvals = new AutoApprovals();
        var paths = new StubPaths(Path.Combine(Path.GetTempPath(), "agent-eval-data", config.Name));
        Directory.CreateDirectory(paths.DataRoot);
        var workspace = new WorkspaceContext(paths) { RootPath = worktree };
        var checkpoints = new CheckpointStore(Path.Combine(paths.DataRoot, "checkpoints"), audit);
        var reads = new FileReadRegistry();
        var packs = new IAgentToolPack[]
        {
            new FileToolPack(),
            new EditToolPack(checkpoints),
            new TodoToolPack(),
            new CommandToolPack(new DirectProcessRunner(), new SessionGrantStore())
        };
        var registry = new AgentToolRegistry(packs);
        var chat = new AgentChatService(registry, workspace, settings, approvals,
            new UnavailableSandboxJobService(), audit, paths, reads, new SDemo.DesktopAgent.Agents.MafAgentRuntime());
        var builder = new ContextBuilder(new HeuristicTokenCounter(), settings);

        var results = new List<TaskOutcome>();
        foreach (var task in tasks)
        {
            Console.WriteLine($"\n=== {task.Id} {task.Title} ===");
            var taskDir = Path.Combine(outRoot, task.Id);
            Directory.CreateDirectory(taskDir);

            await GitAsync(worktree, "reset", "--hard", options.Commit);
            await GitAsync(worktree, "clean", "-fd");

            var session = ChatSession.CreateEmpty();
            var messages = new List<ChatMessage> { new(ChatMessageRole.User, task.Prompt) };
            var output = new StringBuilder();
            var trace = new List<TraceRow>();
            var routings = new List<RoutingRow>();
            var totalSteps = 0;
            var totalSeconds = 0d;
            string? error = null;
            var turns = 0;
            var maxTurns = config.Repair ? 3 : 1;

            for (var turn = 1; turn <= maxTurns; turn++)
            {
                turns = turn;
                var metrics = new AgentRunMetrics();
                var plan = builder.Build(new ContextBuildRequest(provider, config.ModelId, messages));
                if (config.DisableThinking)
                {
                    plan = plan with { Instructions = "/no_think\n" + plan.Instructions };
                }

                var runPlan = new AgentRunPlan(
                    provider, config.ModelId, plan,
                    new HashSet<string>(StringComparer.Ordinal) { "files", "edits", "todo", "command" },
                    SessionId: $"p7-{task.Id}-{turn}",
                    Metrics: metrics,
                    Session: session);

                var turnOutput = new StringBuilder();
                var telemetry = new ContextRunTelemetry();
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    await foreach (var delta in chat.RunAsync(runPlan, telemetry))
                    {
                        turnOutput.Append(delta.Text);
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                stopwatch.Stop();
                totalSeconds += stopwatch.Elapsed.TotalSeconds;
                output.Append(turnOutput);
                totalSteps += metrics.ToolTrace.Count;
                trace.AddRange(metrics.ToolTrace.Select(row => new TraceRow(
                    row.ToolId, row.Status, row.ArgsDigest ?? "", Clip(row.Summary, 500))));

                var routing = telemetry.Report?.Routing;
                routings.Add(new RoutingRow(
                    turn,
                    routing?.Source ?? "none",
                    routing?.ExposedTools.ToArray() ?? [],
                    metrics.ToolCalls.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                    metrics.UnusedExposedTools.ToArray()));

                if (error is not null)
                {
                    break;
                }

                if (!config.Repair || turn == maxTurns)
                {
                    break;
                }

                // P7 实测：模型会在没有改任何文件时直接宣布“已完成”。没有 diff 时不能
                // 直接进入验收（既有测试本来就绿），必须先回灌一次「没有检测到修改」。
                var changed = !string.IsNullOrWhiteSpace(await GitAsync(worktree, "status", "--porcelain"));
                if (!changed)
                {
                    messages.Add(new ChatMessage(ChatMessageRole.Assistant, turnOutput.ToString()));
                    messages.Add(new ChatMessage(ChatMessageRole.User,
                        "[宿主提示] 本轮没有检测到任何文件修改，任务尚未完成。"
                        + "请先用 search_files / glob_files 定位文件，read_file 后再用 apply_patch 或 replace_text 写入最小改动；"
                        + "不要只输出计划或声称已经完成。"));
                    Console.WriteLine("  ↻ 未检测到文件修改，要求继续");
                    continue;
                }

                // 第一轮失败后只做一次宿主回灌：不伪造 tool 消息，只给验收失败原因。
                // 修复判定必须带隐藏测试，否则「模型没改文件、既有测试本来就绿」会被误判为通过。
                InjectHiddenTests(worktree, task, evalRoot);
                var early = await RunAcceptanceAsync(worktree, task, taskDir, writeLog: false);
                var earlyCheck = CheckFiles(worktree, task);
                CleanHiddenTests(worktree);
                if (early.Success && earlyCheck is null)
                {
                    break;
                }

                var issue = earlyCheck ?? Clip(early.Output, 1500);
                messages.Add(new ChatMessage(ChatMessageRole.Assistant, turnOutput.ToString()));
                messages.Add(new ChatMessage(ChatMessageRole.User,
                    $"[宿主提示] 验收未通过：{issue}\n请先 read_file 查看当前内容，只修复失败原因；不要重写无关部分。"));
                Console.WriteLine("  ↻ 验收未通过，宿主回灌 1 次");
            }

            // 先捕获模型改动，再注入隐藏测试；隐藏测试不应出现在 changes.diff 里。
            await GitAsync(worktree, "add", "-A");
            var diff = await GitAsync(worktree, "diff", "--cached", "--binary");
            await GitAsync(worktree, "reset");

            InjectHiddenTests(worktree, task, evalRoot);
            var acceptance = await RunAcceptanceAsync(worktree, task, taskDir, writeLog: true);
            var checkFailure = CheckFiles(worktree, task);
            var success = error is null && acceptance.Success && checkFailure is null;

            File.WriteAllText(Path.Combine(taskDir, "changes.diff"), diff);
            File.WriteAllText(Path.Combine(taskDir, "acceptance.log"), acceptance.Log);
            var failureClass = success ? null : Classify(task, trace, diff, acceptance, error);
            var outcome = new TaskOutcome(
                task.Id, task.Type, task.Title, success,
                totalSteps, turns, Math.Round(totalSeconds, 1),
                error, acceptance.ExitCode, Clip(acceptance.Output, 3000),
                checkFailure, failureClass, trace, routings);
            results.Add(outcome);

            var transcript = new
            {
                task.Id,
                task.Type,
                task.Title,
                Prompt = task.Prompt,
                Model = config.ModelId,
                Mode = config.Mode.ToString(),
                ContextWindow = config.ContextWindow,
                Output = output.ToString(),
                Turns = turns,
                Steps = totalSteps,
                Seconds = Math.Round(totalSeconds, 1),
                Error = error,
                AcceptanceExit = acceptance.ExitCode,
                AcceptanceOutput = acceptance.Output,
                CheckFailure = checkFailure,
                FailureClass = failureClass,
                Routing = routings,
                Trace = trace
            };
            File.WriteAllText(Path.Combine(taskDir, "transcript.json"),
                JsonSerializer.Serialize(transcript, JsonOptions));
            File.WriteAllText(Path.Combine(taskDir, "verdict.json"),
                JsonSerializer.Serialize(outcome, JsonOptions));

            Console.WriteLine($"  success={success} steps={totalSteps} turns={turns} sec={totalSeconds:F1} class={failureClass ?? "-"}");
            if (!success)
            {
                Console.WriteLine($"  reason: {Clip(error ?? checkFailure ?? acceptance.Output, 500)}");
            }
        }

        var summary = new
        {
            Config = config.Name,
            Model = config.ModelId,
            Mode = config.Mode.ToString(),
            ContextWindow = config.ContextWindow,
            Commit = options.Commit,
            Repo = options.RepoPath,
            StartedAt = DateTimeOffset.Now,
            Passed = results.Count(item => item.Success),
            Total = results.Count,
            ExposedToolRounds = results.Sum(item => item.Routing.Sum(round => round.Exposed.Length)),
            CalledToolRounds = results.Sum(item => item.Routing.Sum(round => round.Called.Length)),
            Results = results
        };
        File.WriteAllText(Path.Combine(outRoot, "summary.json"),
            JsonSerializer.Serialize(summary, JsonOptions));
        Console.WriteLine($"\nP7 {config.Name}: {summary.Passed}/{summary.Total} passed");
        Console.WriteLine($"artifacts: {outRoot}");

        if (!options.KeepWork)
        {
            await GitAsync(options.RepoPath, "worktree", "remove", "--force", worktree);
        }

        return summary.Passed == summary.Total ? 0 : 2;
    }

    private static async Task EnsureModelLoadedAsync(string baseUrl, string modelId)
    {
        // 只有本地 LM Studio 需要预先加载；云端 /models 可能需要鉴权，且没有“加载”概念。
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint)
            || (!string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(endpoint.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var uri = baseUrl.TrimEnd('/') + "/models";
        try
        {
            var payload = await http.GetFromJsonAsync<ModelList>(uri);
            var ids = payload?.Data?.Select(item => item.Id).ToArray() ?? [];
            if (!ids.Contains(modelId, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"模型 {modelId} 未加载；当前 endpoint 可用：{string.Join(", ", ids)}。请先在 LM Studio 加载后重跑。");
            }
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"无法连接模型端点 {baseUrl}：{ex.Message}");
        }
    }

    private static async Task<string> PrepareWorktreeAsync(string repo, string commit, string work)
    {
        repo = Path.GetFullPath(repo);
        work = Path.GetFullPath(work);
        if (!Directory.Exists(Path.Combine(repo, ".git")))
        {
            throw new InvalidOperationException($"不是 git 仓库：{repo}");
        }

        var resolved = (await GitAsync(repo, "rev-parse", commit + "^{commit}")).Trim();
        if (Directory.Exists(work))
        {
            await GitAsync(repo, "worktree", "remove", "--force", work);
            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(work)!);
        var add = await GitAsync(repo, "worktree", "add", "--detach", work, resolved);
        var gitMarker = Path.Combine(work, ".git");
        if (!Directory.Exists(gitMarker) && !File.Exists(gitMarker))
        {
            throw new InvalidOperationException($"worktree 创建失败：{add}");
        }

        return work;
    }

    private static async Task<(bool Success, int ExitCode, string Output, string Log)> RunAcceptanceAsync(
        string root, EvalTask task, string taskDir, bool writeLog)
    {
        var log = new StringBuilder();
        foreach (var command in task.Acceptance)
        {
            var result = await RunShellAsync(root, command, TimeSpan.FromMinutes(5));
            log.AppendLine($"> {command}");
            log.AppendLine(result.Output);
            log.AppendLine($"[exit {result.ExitCode}]");
            if (result.ExitCode != 0)
            {
                return (false, result.ExitCode, result.Output, log.ToString());
            }
        }

        return (true, 0, log.ToString(), log.ToString());
    }

    private static string? CheckFiles(string root, EvalTask task)
    {
        foreach (var check in task.Checks)
        {
            var separator = check.IndexOf('|');
            if (separator < 0)
            {
                continue;
            }

            var relative = check[..separator];
            var pattern = check[(separator + 1)..];
            var path = Path.Combine(root, relative);
            if (!File.Exists(path))
            {
                return $"缺少文件：{relative}";
            }

            var text = File.ReadAllText(path);
            if (!Regex.IsMatch(text, pattern, RegexOptions.Multiline))
            {
                return $"文件 {relative} 未包含验收模式：{pattern}";
            }
        }

        return null;
    }

    private static void InjectHiddenTests(string root, EvalTask task, string evalRoot)
    {
        if (task.HiddenTests.Count == 0)
        {
            return;
        }

        var target = Path.Combine(root, "tests", "CommandLine.Tests", "Unit", "AgentEval");
        Directory.CreateDirectory(target);
        foreach (var hidden in task.HiddenTests)
        {
            var source = Path.IsPathRooted(hidden) ? hidden : Path.Combine(evalRoot, hidden);
            if (!File.Exists(source))
            {
                throw new FileNotFoundException($"隐藏测试不存在：{source}");
            }

            File.Copy(source, Path.Combine(target, $"{task.Id}_{Path.GetFileName(source)}"), overwrite: true);
        }
    }

    private static void CleanHiddenTests(string root)
    {
        var target = Path.Combine(root, "tests", "CommandLine.Tests", "Unit", "AgentEval");
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunShellAsync(
        string workdir, string command, TimeSpan timeout)
    {
        var info = new ProcessStartInfo("cmd.exe", "/c " + command)
        {
            WorkingDirectory = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment["DOTNET_NOLOGO"] = "1";

        using var process = new Process { StartInfo = info };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            output.AppendLine($"[timeout after {timeout.TotalSeconds:F0}s]");
            return (124, output.ToString());
        }

        return (process.ExitCode, output.ToString());
    }

    private static async Task<string> GitAsync(string workdir, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0 && !error.Contains("warning:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} 失败：{error}{output}");
        }

        return output + error;
    }

    private static string? Classify(EvalTask task, List<TraceRow> trace, string diff,
        (bool Success, int ExitCode, string Output, string Log) acceptance, string? error)
    {
        if (error is not null && error.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return "F4 构建环境";
        }

        var repeated = trace
            .GroupBy(row => row.ToolId + "|" + row.ArgsDigest)
            .Any(group => group.Count() >= 3);
        if (repeated)
        {
            return "F6 循环";
        }

        var patchErrors = trace.Count(row =>
            row.ToolId is "apply_patch" or "replace_text" or "write_file"
            && !string.Equals(row.Status, "ok", StringComparison.OrdinalIgnoreCase));
        if (!acceptance.Success && patchErrors > 0 && string.IsNullOrWhiteSpace(diff))
        {
            return "F3 补丁失败";
        }

        if (string.IsNullOrWhiteSpace(diff))
        {
            return "F5 过早结束";
        }

        if (!acceptance.Success && acceptance.Output.Contains("error CS", StringComparison.Ordinal))
        {
            return "F2 工具误用";
        }

        if (!acceptance.Success && acceptance.Output.Contains("NU1", StringComparison.Ordinal))
        {
            return "F4 构建环境";
        }

        return acceptance.Success ? null : "F2 工具误用";
    }

    private static EvalTask[] LoadTasks(string path)
    {
        var directory = Path.GetFullPath(path);
        return Directory.GetFiles(directory, "*.md")
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .Select(file => EvalTask.Parse(file))
            .ToArray();
    }

    private static EvalConfig LoadConfig(string path) =>
        JsonSerializer.Deserialize<EvalConfig>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidOperationException($"配置解析失败：{path}");

    private static AppSettings LoadSettings(string path)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException("设置文件解析失败");
        var entropy = Encoding.UTF8.GetBytes("SDemo.DesktopAgent.ApiKey.v1");
        foreach (var provider in settings.Providers)
        {
            if (string.IsNullOrWhiteSpace(provider.ApiKeyProtected))
            {
                continue;
            }

            try
            {
                var bytes = ProtectedData.Unprotect(
                    Convert.FromBase64String(provider.ApiKeyProtected),
                    entropy,
                    DataProtectionScope.CurrentUser);
                provider.ApiKey = Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                Console.Error.WriteLine($"provider {provider.Id} 的 Key 无法解密。");
            }
        }

        return settings;
    }

    private static string Clip(string? text, int max) =>
        string.IsNullOrEmpty(text) ? string.Empty :
        text.Length <= max ? text : text[..max] + "…";

    private sealed record TraceRow(string ToolId, string Status, string ArgsDigest, string Summary);

    private sealed record RoutingRow(
        int Turn, string Source, string[] Exposed, string[] Called, string[] UnusedExposed);

    private sealed record TaskOutcome(
        string Id, string Type, string Title, bool Success, int Steps, int Turns, double Seconds,
        string? Error, int AcceptanceExit, string AcceptanceOutput, string? CheckFailure,
        string? FailureClass, IReadOnlyList<TraceRow> Trace, IReadOnlyList<RoutingRow> Routing);

    private sealed record EvalConfig(
        string Name, string ProviderId, string ModelId, AgentModePreference Mode,
        int ContextWindow, int MaxOutputTokens, int MaxToolCalls,
        bool DisableThinking, bool Repair);

    private sealed record EvalTask(
        string Id, string Type, string Title, string Prompt, int MaxSteps,
        IReadOnlyList<string> AllowedTools, IReadOnlyList<string> Acceptance,
        IReadOnlyList<string> HiddenTests, IReadOnlyList<string> Checks)
    {
        public static EvalTask Parse(string path)
        {
            var text = File.ReadAllText(path);
            var match = Regex.Match(text, @"\A---\r?\n(?<meta>.*?)\r?\n---\r?\n(?<body>.*)\z",
                RegexOptions.Singleline);
            if (!match.Success)
            {
                throw new InvalidOperationException($"任务文件缺少 front matter：{path}");
            }

            var meta = match.Groups["meta"].Value;
            var body = match.Groups["body"].Value.Trim();
            string? id = null, type = null, title = null, prompt = null;
            var acceptance = new List<string>();
            var hidden = new List<string>();
            var checks = new List<string>();
            var allowed = new List<string>();
            var inAcceptance = false;
            var inHidden = false;
            var inChecks = false;
            var inAllowed = false;

            foreach (var raw in meta.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.StartsWith("  - ", StringComparison.Ordinal))
                {
                    var item = line[4..].Trim();
                    if (inAcceptance) acceptance.Add(item);
                    else if (inHidden) hidden.Add(item);
                    else if (inChecks) checks.Add(item);
                    else if (inAllowed) allowed.Add(item);
                    continue;
                }

                if (!line.Contains(':'))
                {
                    continue;
                }

                var colon = line.IndexOf(':');
                var key = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();
                inAcceptance = key == "acceptance";
                inHidden = key == "hiddenTests";
                inChecks = key == "checks";
                inAllowed = key == "allowedTools";
                switch (key)
                {
                    case "id": id = value; break;
                    case "type": type = value; break;
                    case "title": title = value; break;
                    case "prompt": prompt = value; break;
                }
            }

            if (id is null || type is null)
            {
                throw new InvalidOperationException($"任务缺少 id/type：{path}");
            }

            var heading = body.Split('\n').FirstOrDefault(line => line.StartsWith("# "));
            title ??= heading?[2..].Trim() ?? id;
            // 任务正文去掉一级标题，标题已单列。
            if (heading is not null)
            {
                body = body[body.IndexOf('\n')..].Trim();
            }

            if (acceptance.Count == 0)
            {
                throw new InvalidOperationException($"任务 {id} 没有 acceptance。");
            }

            return new EvalTask(id, type, title, body, 15,
                allowed, acceptance, hidden, checks);
        }
    }

    private sealed class CliOptions
    {
        public string RepoPath { get; private set; } = "";
        public string Commit { get; private set; } = "";
        public string ConfigPath { get; private set; } = "";
        public string TasksPath { get; private set; } = "";
        public string OutputPath { get; private set; } = "";
        public string? WorkPath { get; private set; }
        public string? EvalRoot { get; private set; }
        public bool KeepWork { get; private set; }
        public HashSet<string> Only { get; } = new(StringComparer.OrdinalIgnoreCase);

        public static CliOptions Parse(string[] args)
        {
            var options = new CliOptions();
            for (var i = 0; i < args.Length; i++)
            {
                var key = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"缺少 {key} 的值");
                switch (key)
                {
                    case "--repo": options.RepoPath = Next(); break;
                    case "--commit": options.Commit = Next(); break;
                    case "--config": options.ConfigPath = Next(); break;
                    case "--tasks": options.TasksPath = Next(); break;
                    case "--out": options.OutputPath = Next(); break;
                    case "--work": options.WorkPath = Next(); break;
                    case "--eval-root": options.EvalRoot = Next(); break;
                    case "--only": options.Only.UnionWith(Next().Split(',')); break;
                    case "--keep-work": options.KeepWork = true; break;
                    default: throw new ArgumentException($"未知参数：{key}");
                }
            }

            if (string.IsNullOrWhiteSpace(options.RepoPath) || string.IsNullOrWhiteSpace(options.Commit)
                || string.IsNullOrWhiteSpace(options.ConfigPath) || string.IsNullOrWhiteSpace(options.TasksPath)
                || string.IsNullOrWhiteSpace(options.OutputPath))
            {
                throw new ArgumentException("必须提供 --repo --commit --config --tasks --out");
            }

            return options;
        }
    }

    private sealed class ModelList
    {
        public List<ModelItem>? Data { get; set; }
    }

    private sealed class ModelItem
    {
        public string Id { get; set; } = "";
    }

    private sealed class StubSettings : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public event EventHandler? Changed { add { } remove { } }
        public void Save() { }
    }

    private sealed class MemoryAudit : IToolAuditLog
    {
        public List<ToolAuditEntry> Entries { get; } = [];
        public void Write(ToolAuditEntry entry) => Entries.Add(entry);
    }

    private sealed class AutoApprovals : IToolApprovalService
    {
        public ToolApprovalRequest? Current => null;
        public event Action? Changed { add { } remove { } }

        public Task<ToolApprovalDecision> RequestAsync(
            ToolApprovalRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(request.Kind == ToolApprovalKind.ProcessExecution
                ? ToolApprovalDecision.ApproveForSession
                : ToolApprovalDecision.ApproveOnce);

        public void Resolve(ToolApprovalDecision decision) { }
        public void CancelAll() { }
    }

    private sealed class StubPaths(string dataRoot) : IAppPaths
    {
        public string DataRoot { get; } = dataRoot;
        public string SettingsFile => Path.Combine(DataRoot, "settings.json");
        public string ChatsRoot => Path.Combine(DataRoot, "chats");
        public string UploadsRoot => Path.Combine(DataRoot, "uploads");
        public string SkillsRoot => Path.Combine(DataRoot, "skills");
        public string PluginsRoot => Path.Combine(DataRoot, "plugins");
        public string DefaultWorkspaceRoot => Path.Combine(DataRoot, "workspace");
        public string SandboxRoot => Path.Combine(DataRoot, "sandbox");
    }
}
