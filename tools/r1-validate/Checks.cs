using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Services;
using ChatMessageModel = SDemo.DesktopAgent.Models.ChatMessage;

namespace SDemo.R1Validate;

internal sealed record ToolScaleResult(
    int Registered,
    int Exposed,
    int MeasuredSchemaTokens,
    int AllToolsSchemaTokens,
    int RouterEstimate,
    int BudgetLimit,
    bool BudgetPass,
    bool ReportMatches,
    bool PlanBuilt,
    int PlanEstimated,
    int PlanToolSegment,
    LiveRequestResult? Live);

internal sealed record SkillPage(int Chars, bool HasMore, int NextOffset);

internal sealed record SkillScaleResult(
    int Skills,
    int Indexed,
    int IndexTokens,
    int IndexLimit,
    bool IndexPass,
    string? Top1,
    bool TargetInTop5,
    SkillPage? FirstPage,
    SkillPage? SecondPage,
    bool PaginationPass);

internal sealed record ArchiveScaleResult(
    int Archived,
    int Hits,
    string Query,
    string? FirstHitId,
    string? FirstHitTitle,
    bool FirstHitIsTarget,
    string? CitationId,
    bool CitationMatches,
    int FirstPageChars,
    int NextOffset,
    bool ReadPaged,
    int SearchChars,
    int SearchTokens);

/// <summary>
/// R1.1 三项确定性验收：30 工具 schema / 20 技能索引与分页 / 20 条归档检索。
/// 所有数字都来自真实对象（CapabilityRouter、SkillIndexBuilder、FTS5 索引），不手填。
/// </summary>
internal static class Checks
{
    private const string TargetSkill = "sandbox-acl-report";
    private const string ArchiveQuery = "上次关于沙箱 ACL 的结论";
    private const string TargetArchiveId = "arch-00";

    public static async Task<ToolScaleResult> RunToolScaleAsync(
        string root,
        bool live,
        string providerId,
        string? modelId,
        CancellationToken cancellationToken)
    {
        var counter = new HeuristicTokenCounter();
        var pack = new GeneratedToolPack();
        var registry = new AgentToolRegistry([pack]);
        var packIds = new HashSet<string>(StringComparer.Ordinal) { GeneratedToolPack.PackId };
        var names = registry.ToolNames(packIds);

        var settings = new StubSettings();
        settings.Current.Context = new ContextSettings();

        var profile = ModelProfilePresets.FromPreset("8K")!;
        var provider = new ModelProvider
        {
            Id = "r1",
            DisplayName = "R1",
            BaseUrl = "https://example.com/v1",
            Models = [new ModelEntry { Id = "r1-8k", Profile = profile }]
        };

        var agent = new AgentSettings();
        var mode = AgentMode.Full;
        var maxTools = agent.ResolveMaxExposedTools(profile.ContextWindow, mode);
        var input = new CapabilityRoutingInput(
            "帮我修改这个项目里的 bug",
            [],
            names,
            mode,
            maxTools,
            EscapeHatch: true,
            ExposeAll: false);

        var decision = CapabilityRouter.Route(input);
        var context = new ToolContext(
            "r1-tool",
            root,
            [],
            settings,
            new AutoApprovals(),
            new UnavailableSandboxJobService(),
            new NullAudit());

        var tools = registry.CreateExposed(
            context,
            packIds,
            decision.ExposedTools.ToHashSet(StringComparer.Ordinal),
            metrics: null,
            decision.EscapeHatch);

        var measured = MeasureSchema(tools, counter);

        // 对照：不走路由、把 30 个工具全量交给模型会占多少（用来说明路由的必要性）。
        var allTools = registry.Create(context, packIds);
        var allTokens = MeasureSchema(allTools, counter);

        var budget = ContextBudget.Create(profile, settings.Current.Context);

        // 与产品同一条路径：ContextBuilder 组装 8K 请求，再把宿主实测值回填报告。
        var builder = new ContextBuilder(counter, settings);
        var messages = new List<ChatMessageModel>
        {
            new(ChatMessageRole.User, "帮我修改这个项目里的 bug")
        };

        var plan = builder.Build(new ContextBuildRequest(
            provider,
            "r1-8k",
            messages,
            InstructionsOverride: "R1 validation：只对资源做只读检查。",
            Routing: decision));

        var report = plan.Report.WithRouting(measured, decision with { EstimatedSchemaTokens = measured });
        var reportMatches = report.ToolSchemaTokens == measured
                            && report.Estimated == report.Segments.Total;

        LiveRequestResult? liveResult = null;

        if (live)
        {
            var settingsOnDisk = LiveToolRequest.LoadSettings();
            var targetProvider = settingsOnDisk.Providers.FirstOrDefault(item =>
                string.Equals(item.Id, providerId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"settings.json 里没有 provider：{providerId}");

            var targetModel = modelId;
            if (string.IsNullOrWhiteSpace(targetModel))
            {
                targetModel = targetProvider.Models.FirstOrDefault()?.Id
                    ?? throw new InvalidOperationException($"provider {providerId} 没有配置模型。");
            }

            liveResult = await LiveToolRequest.SendAsync(
                settingsOnDisk,
                providerId,
                targetModel,
                tools,
                cancellationToken);
        }

        return new ToolScaleResult(
            Registered: names.Count,
            Exposed: tools.Count,
            MeasuredSchemaTokens: measured,
            AllToolsSchemaTokens: allTokens,
            RouterEstimate: decision.EstimatedSchemaTokens,
            BudgetLimit: budget.ToolSchemaLimit,
            BudgetPass: measured <= budget.ToolSchemaLimit,
            ReportMatches: reportMatches,
            PlanBuilt: plan.Messages.Count > 0 && plan.Report.Estimated > 0,
            PlanEstimated: report.Estimated,
            PlanToolSegment: report.Segments.ToolSchema,
            Live: liveResult);
    }

    public static async Task<SkillScaleResult> RunSkillScaleAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var counter = new HeuristicTokenCounter();
        var skillsRoot = Path.Combine(root, "skills");
        Directory.CreateDirectory(skillsRoot);

        WriteSkills(skillsRoot);

        var catalog = new SkillCatalog(skillsRoot);
        var index = catalog.Index();

        var selected = SkillIndexBuilder.Select(index, ArchiveQuery, maxEntries: 5);
        var rendered = SkillIndexBuilder.Render(selected);
        var tokens = counter.Count(rendered);
        var limit = ContextBudget.SkillIndexTokenLimit;

        var settings = new StubSettings();
        settings.Current.Context = new ContextSettings { MidTurnToolResultChars = 2000 };
        var context = new ToolContext(
            "r1-skill",
            root,
            [],
            settings,
            new AutoApprovals(),
            new UnavailableSandboxJobService(),
            new NullAudit(),
            Skills: catalog);

        var tools = new SkillToolPack().Create(context);
        var loadTool = tools.OfType<AIFunction>().First(tool => tool.Name == "load_skill");

        var first = await InvokeAsync(loadTool, new Dictionary<string, object?>
        {
            ["name"] = TargetSkill,
            ["offset"] = 0
        }, cancellationToken);

        var firstPage = ParsePage(first);
        SkillPage? secondPage = null;
        var pagination = false;

        if (firstPage.HasMore)
        {
            var second = await InvokeAsync(loadTool, new Dictionary<string, object?>
            {
                ["name"] = TargetSkill,
                ["offset"] = firstPage.NextOffset
            }, cancellationToken);

            secondPage = ParsePage(second);
            pagination = secondPage.Chars > 0
                         && second.Contains($"第 {firstPage.NextOffset + 1}–", StringComparison.Ordinal);
        }

        return new SkillScaleResult(
            Skills: index.Count,
            Indexed: selected.Count,
            IndexTokens: tokens,
            IndexLimit: limit,
            IndexPass: tokens <= limit,
            Top1: selected.FirstOrDefault()?.Name,
            TargetInTop5: selected.Any(entry => string.Equals(entry.Name, TargetSkill, StringComparison.Ordinal)),
            FirstPage: firstPage,
            SecondPage: secondPage,
            PaginationPass: pagination);
    }

    public static async Task<ArchiveScaleResult> RunArchiveScaleAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var store = new ChatStore(Path.Combine(root, "chats"));
        const string workspaceId = "r1-workspace";

        for (var index = 0; index < 20; index++)
        {
            var session = BuildArchiveSession(index);
            session.WorkspaceId = workspaceId;
            session.IsArchived = true;
            await store.SaveSessionAsync(session, cancellationToken);
        }

        var memory = new WorkspaceMemoryIndex(store);
        memory.Rebuild(workspaceId);

        var metrics = new AgentRunMetrics();
        var settings = new StubSettings();
        settings.Current.Context = new ContextSettings { MidTurnToolResultChars = 2000 };

        var context = new ToolContext(
            "r1-archive",
            root,
            [],
            settings,
            new AutoApprovals(),
            new UnavailableSandboxJobService(),
            new NullAudit(),
            Metrics: metrics,
            WorkspaceId: workspaceId,
            Memory: memory);

        var pack = new MemoryToolPack();
        var tools = pack.Create(context).OfType<AIFunction>().ToArray();
        var searchTool = tools.First(tool => tool.Name == MemoryToolPack.SearchFunctionId);
        var readTool = tools.First(tool => tool.Name == MemoryToolPack.ReadFunctionId);

        var searchText = await InvokeAsync(searchTool, new Dictionary<string, object?>
        {
            ["query"] = ArchiveQuery,
            ["topK"] = 5
        }, cancellationToken);

        var firstHit = metrics.Citations.FirstOrDefault();
        var searchTokens = new HeuristicTokenCounter().Count(searchText);

        // 引用 chip 的数据源就是 Metrics.Citations；第一条必须指向目标会话。
        var citationMatches = firstHit is not null
                              && string.Equals(firstHit.SessionId, TargetArchiveId, StringComparison.Ordinal);

        var readText = await InvokeAsync(readTool, new Dictionary<string, object?>
        {
            ["sessionId"] = TargetArchiveId,
            ["query"] = "ACL",
            ["offset"] = 0
        }, cancellationToken);

        var nextOffset = ParseNextOffset(readText);
        var readPaged = nextOffset > 0;

        return new ArchiveScaleResult(
            Archived: memory.CountArchived(workspaceId),
            Hits: metrics.Citations.Count,
            Query: ArchiveQuery,
            FirstHitId: firstHit?.SessionId,
            FirstHitTitle: firstHit?.Title,
            FirstHitIsTarget: citationMatches,
            CitationId: firstHit?.SessionId,
            CitationMatches: citationMatches,
            FirstPageChars: readText.Length,
            NextOffset: nextOffset,
            ReadPaged: readPaged,
            SearchChars: searchText.Length,
            SearchTokens: searchTokens);
    }

    private static ChatSession BuildArchiveSession(int index)
    {
        var target = index == 0;
        var session = new ChatSession
        {
            Id = target ? TargetArchiveId : $"arch-{index:00}",
            Title = target ? "沙箱 ACL 结论" : $"沙箱工作记录 {index:00}",
            UpdatedAt = new DateTime(2026, 9, 1, 10, 0, 0).AddDays(index)
        };

        if (target)
        {
            session.Messages.Add(new ChatMessageModel(ChatMessageRole.User, ArchiveQuery + "？"));
            session.Messages.Add(new ChatMessageModel(
                ChatMessageRole.Assistant,
                "结论：沙箱 ACL 只给 AppContainer SID 读执行权限，工作区默认只读；"
                + "写回必须经过 apply 面板，不直接改真实目录。"
                + string.Concat(Enumerable.Repeat(" 复现步骤与验证记录见下方清单。", 80))));
            session.Messages.Add(new ChatMessageModel(
                ChatMessageRole.Assistant,
                "沙箱 ACL 补充记录：" + string.Concat(Enumerable.Repeat(" 挂载点与写出路径在白名单里核对。", 70))));
            session.Messages.Add(new ChatMessageModel(
                ChatMessageRole.Assistant,
                "沙箱 ACL 复核记录：" + string.Concat(Enumerable.Repeat(" 权限变更前后各跑一次探针。", 70))));
            session.Summary = "沙箱 ACL：只读挂载 + apply 面板写回。";
            session.SummaryThroughOrder = 4;
        }
        else
        {
            session.Messages.Add(new ChatMessageModel(
                ChatMessageRole.User,
                $"第 {index:00} 次沙箱试跑：PowerShell 命令、作业清理与内存上限。"));
            session.Messages.Add(new ChatMessageModel(
                ChatMessageRole.Assistant,
                "记录：沙箱作业完成后清理 job 目录；没有涉及 ACL 权限模型。"));
            session.Summary = "沙箱试跑记录。";
            session.SummaryThroughOrder = 2;
        }

        return session;
    }

    private static void WriteSkills(string root)
    {
        var topics = new[]
        {
            "沙箱排障", "补丁定位", "构建失败", "测试选择", "依赖审计", "日志分析",
            "配置迁移", "权限校验", "编码修复", "并发写入", "发布检查", "性能剖析",
            "回滚演练", "密钥轮换", "索引重建", "缓存清理", "网络诊断", "文件恢复",
            "版本对比", "文档校对"
        };

        for (var index = 0; index < 20; index++)
        {
            var isTarget = index == 6;
            var name = isTarget ? TargetSkill : $"r1-skill-{index + 1:00}-{index}";
            var topic = topics[index];

            var description = isTarget
                ? "沙箱 ACL 结论与权限模型：核对只读挂载、写出路径与历史结论，覆盖 20 条归档会话的检索与复核。"
                : $"处理{topic}的完整流程：定位问题、收集证据、给出最小改动与复核步骤，覆盖常见边界与失败回退。";

            var whenToUse = isTarget
                ? "询问上次关于沙箱 ACL 的结论、或需要复核权限模型时使用。"
                : $"遇到{topic}、或需要复用既有排障步骤时使用。";

            var body = new string('说', 2400)
                       + (isTarget
                           ? "\n\n## 结论\n沙箱 ACL 只读挂载，写回走 apply 面板。\n"
                           : $"\n\n## 步骤\n1. 复现 {topic}。\n2. 收集日志。\n3. 最小修复。\n");

            var content = $"""
                ---
                description: {description}
                icon: R
                when-to-use: {whenToUse}
                tags: [{topic}, r1]
                ---
                # {name}

                {body}
                """;

            File.WriteAllText(Path.Combine(root, name + ".md"), content);
        }
    }

    private static int MeasureSchema(IReadOnlyList<AITool> tools, ITokenCounter counter)
    {
        var total = 0;

        foreach (var tool in tools)
        {
            var schema = tool is AIFunction function ? function.JsonSchema.GetRawText() : string.Empty;
            total += counter.CountMessage("tool", $"{tool.Name}\n{tool.Description}\n{schema}");
        }

        return total;
    }

    private static async Task<string> InvokeAsync(
        AIFunction function,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var result = await function.InvokeAsync(new AIFunctionArguments(arguments), cancellationToken);
        return result as string ?? result?.ToString() ?? string.Empty;
    }

    private static SkillPage ParsePage(string text)
    {
        var match = Regex.Match(text, @"第 (\d+)–(\d+) 字符 / 共 (\d+) 字符");
        var chars = match.Success
            ? int.Parse(match.Groups[2].Value) - int.Parse(match.Groups[1].Value) + 1
            : 0;

        var next = Regex.Match(text, @"offset: (\d+)");
        var hasMore = next.Success;

        return new SkillPage(chars, hasMore, hasMore ? int.Parse(next.Groups[1].Value) : 0);
    }

    private static int ParseNextOffset(string text)
    {
        var match = Regex.Match(text, @"offset: (\d+)");
        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }
}
