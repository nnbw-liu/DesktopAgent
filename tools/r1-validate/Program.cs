using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SDemo.R1Validate;

/// <summary>
/// R1.1 规模化验收运行器：30 工具 schema / 20 技能索引与分页 / 20 条归档检索。
/// 输出 <c>eval/results/r1/summary.json</c>；报告里的数字一律从这里来。
/// <code>
/// dotnet run --project tools/r1-validate -c Release -- --out eval/results/r1
/// dotnet run --project tools/r1-validate -c Release -- --out eval/results/r1 --live --provider local
/// </code>
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> Main(string[] args)
    {
        var options = CliOptions.Parse(args);
        using var temp = new TempRoot();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        Console.WriteLine("R1.1 规模化验收");
        Console.WriteLine($"临时工作目录：{temp.Path}");

        var tools = await Checks.RunToolScaleAsync(
            temp.Path,
            options.Live,
            options.ProviderId,
            options.ModelId,
            cts.Token);

        var skills = await Checks.RunSkillScaleAsync(temp.Path, cts.Token);
        var archives = await Checks.RunArchiveScaleAsync(temp.Path, cts.Token);

        var livePass = tools.Live is null || (tools.Live.Sent && tools.Live.Status is >= 200 and < 300);
        var pass = tools.BudgetPass
                   && tools.ReportMatches
                   && tools.PlanBuilt
                   && skills.IndexPass
                   && skills.TargetInTop5
                   && skills.PaginationPass
                   && archives.FirstHitIsTarget
                   && archives.CitationMatches
                   && archives.ReadPaged
                   && livePass;

        Directory.CreateDirectory(options.OutputDirectory);

        var result = new
        {
            generatedAt = DateTimeOffset.Now,
            pass,
            tools,
            skills,
            archives
        };

        var jsonPath = Path.Combine(options.OutputDirectory, "summary.json");
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(result, JsonOptions), cts.Token);

        var markdownPath = Path.Combine(options.OutputDirectory, "summary.md");
        await File.WriteAllTextAsync(markdownPath, RenderMarkdown(tools, skills, archives, pass), cts.Token);

        Print(tools, skills, archives, pass);
        Console.WriteLine($"\n结果：{jsonPath}");
        Console.WriteLine($"摘要：{markdownPath}");

        return pass ? 0 : 1;
    }

    private static void Print(
        ToolScaleResult tools,
        SkillScaleResult skills,
        ArchiveScaleResult archives,
        bool pass)
    {
        Console.WriteLine($"\n[工具] 注册 {tools.Registered} / 暴露 {tools.Exposed}；"
                          + $"schema 实测 {tools.MeasuredSchemaTokens} token（上限 {tools.BudgetLimit}）"
                          + $" → {(tools.BudgetPass ? "通过" : "超限")}；报告一致={tools.ReportMatches}");
        Console.WriteLine($"       对照：全量暴露 {tools.AllToolsSchemaTokens} token；"
                          + $"请求组装：估算 {tools.PlanEstimated} token，工具段 {tools.PlanToolSegment}");

        if (tools.Live is { } live)
        {
            Console.WriteLine($"       真机请求：sent={live.Sent} status={live.Status} "
                              + $"tools={live.ToolCount} {live.ElapsedMs}ms {live.Error}");
        }

        Console.WriteLine($"\n[技能] {skills.Skills} 条技能，索引 top{skills.Indexed} = {skills.IndexTokens} token"
                          + $"（上限 {skills.IndexLimit}）→ {(skills.IndexPass ? "通过" : "超限")}；"
                          + $"top1={skills.Top1} 目标命中={skills.TargetInTop5}");
        Console.WriteLine($"       正文分页：首页 {skills.FirstPage?.Chars} 字符 / 续读 {(skills.PaginationPass ? "通过" : "失败")}"
                          + $"（第二页 {skills.SecondPage?.Chars} 字符）");

        Console.WriteLine($"\n[归档] {archives.Archived} 条归档；命中 {archives.Hits}；"
                          + $"首条={archives.FirstHitTitle}（{archives.FirstHitId}）"
                          + $" → {(archives.FirstHitIsTarget ? "目标在首位" : "未命中目标")}");
        Console.WriteLine($"       引用 chip 数据源：{archives.CitationId}；正文分页 offset={archives.NextOffset}"
                          + $" → {(archives.ReadPaged ? "通过" : "失败")}");

        Console.WriteLine($"\nR1.1 总体：{(pass ? "通过" : "未通过")}");
    }

    private static string RenderMarkdown(
        ToolScaleResult tools,
        SkillScaleResult skills,
        ArchiveScaleResult archives,
        bool pass)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# R1.1 规模化验收（运行器输出）");
        builder.AppendLine();
        builder.AppendLine($"> 生成时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}　结论：**{(pass ? "通过" : "未通过")}**");
        builder.AppendLine();
        builder.AppendLine("## 30 工具 / 8K");
        builder.AppendLine();
        builder.AppendLine("| 指标 | 值 | 验收 |");
        builder.AppendLine("| --- | --- | --- |");
        builder.AppendLine($"| 注册工具数 | {tools.Registered} | — |");
        builder.AppendLine($"| 本轮暴露 | {tools.Exposed} | — |");
        builder.AppendLine($"| schema 实测 token | {tools.MeasuredSchemaTokens} | ≤ {tools.BudgetLimit}：{(tools.BudgetPass ? "✅" : "❌")} |");
        builder.AppendLine($"| 全量暴露对照 | {tools.AllToolsSchemaTokens} token | 仅作对比，产品不这样发 |");
        builder.AppendLine($"| 分解面板一致 | {tools.ReportMatches} | {(tools.ReportMatches ? "✅" : "❌")} |");
        builder.AppendLine($"| 请求组装 | {tools.PlanEstimated} token | {(tools.PlanBuilt ? "✅" : "❌")} |");
        builder.AppendLine($"| 真机请求 | {(tools.Live is null ? "未运行（--live 未开启）" : $"{tools.Live.Status} / {tools.Live.ElapsedMs}ms")} | — |");
        builder.AppendLine();
        builder.AppendLine("## 20 技能");
        builder.AppendLine();
        builder.AppendLine("| 指标 | 值 | 验收 |");
        builder.AppendLine("| --- | --- | --- |");
        builder.AppendLine($"| 技能数 / 索引条数 | {skills.Skills} / {skills.Indexed} | — |");
        builder.AppendLine($"| 索引 token | {skills.IndexTokens} | ≤ {skills.IndexLimit}：{(skills.IndexPass ? "✅" : "❌")} |");
        builder.AppendLine($"| top1 | {skills.Top1} | 目标在 top5：{(skills.TargetInTop5 ? "✅" : "❌")} |");
        builder.AppendLine($"| 正文分页 | 首页 {skills.FirstPage?.Chars} 字符 | {(skills.PaginationPass ? "✅" : "❌")} |");
        builder.AppendLine();
        builder.AppendLine("## 20 条归档");
        builder.AppendLine();
        builder.AppendLine("| 指标 | 值 | 验收 |");
        builder.AppendLine("| --- | --- | --- |");
        builder.AppendLine($"| 归档会话数 | {archives.Archived} | — |");
        builder.AppendLine($"| 查询 | {archives.Query} | — |");
        builder.AppendLine($"| 首条命中 | {archives.FirstHitTitle}（{archives.FirstHitId}） | 目标首位：{(archives.FirstHitIsTarget ? "✅" : "❌")} |");
        builder.AppendLine($"| 引用 chip 数据源 | {archives.CitationId} | {(archives.CitationMatches ? "✅" : "❌")} |");
        builder.AppendLine($"| 检索结果长度 | {archives.SearchChars} 字符 / {archives.SearchTokens} token | — |");
        builder.AppendLine($"| 正文分页 | 首页 {archives.FirstPageChars} 字符，next={archives.NextOffset} | {(archives.ReadPaged ? "✅" : "❌")} |");
        builder.AppendLine();
        builder.AppendLine("> 说明：以上数字全部来自运行器（CapabilityRouter / SkillIndexBuilder / FTS5 索引实测），不接受手填。");
        return builder.ToString();
    }

    private sealed record CliOptions(
        string OutputDirectory,
        bool Live,
        string ProviderId,
        string? ModelId)
    {
        public static CliOptions Parse(string[] args)
        {
            var output = "eval/results/r1";
            var live = false;
            var providerId = "local";
            string? modelId = null;

            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--out" when index + 1 < args.Length:
                        output = args[++index];
                        break;
                    case "--live":
                        live = true;
                        break;
                    case "--provider" when index + 1 < args.Length:
                        providerId = args[++index];
                        break;
                    case "--model" when index + 1 < args.Length:
                        modelId = args[++index];
                        break;
                }
            }

            return new CliOptions(output, live, providerId, modelId);
        }
    }
}
