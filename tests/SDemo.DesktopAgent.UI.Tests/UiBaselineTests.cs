using System.IO;
using System.Text;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using SDemo.DesktopAgent.Components.Chat;
using SDemo.DesktopAgent.Components.Settings;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Execution;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.UI.Tests;

/// <summary>
/// R1.2 截图基线：把真实组件渲染成独立 HTML（同一份 app.css），
/// 再由 <c>tools/ui-baseline/capture.ps1</c> 用无头 Edge 截图，放到
/// <c>docs/reports/ui-baseline/</c>。
/// <para>
/// 说明：这是「组件渲染基线」，不是完整 WebView2 外壳截图；外壳内的真实交互仍由
/// <c>docs/reports/r1-ui-checklist.md</c> 的人工清单覆盖。
/// </para>
/// </summary>
public sealed class UiBaselineTests : TestContext
{
    private readonly TempDirectory _temp = new();

    [Fact]
    public void 生成截图基线HTML()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SDEMO_UI_BASELINE"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var output = Path.Combine(FindRepositoryRoot(), "docs", "reports", "ui-baseline");
        Directory.CreateDirectory(output);

        // 服务必须在第一次 Render 之前一次性注册好（bUnit 渲染后会冻结容器）。
        var settings = new InMemorySettings();
        ChatRoomFixture.SeedProvider(settings.Current);
        ChatRoomFixture.Register(this, settings, new QuietRunner(), _temp.Root);
        Services.AddSingleton(new TokenCalibrationStore(
            Path.Combine(_temp.Root, "context-calibration.json")));

        Write("01-empty.html", RenderEmptyState(), output);
        Write("02-chat.html", RenderChatWithRecords(), output);
        Write("03-settings.html", RenderSettings(), output);
        Write("04-compaction.html", RenderCompaction(), output);
    }

    private string RenderEmptyState()
    {
        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, new ChatSession { Id = "empty", Title = "新聊天室" })
            .Add(component => component.Workspaces, Array.Empty<Workspace>()));

        return Frame("空态 · 新聊天室", cut.Markup);
    }

    private string RenderChatWithRecords()
    {
        var session = new ChatSession { Id = "chat", Title = "修复上下文预算" };

        session.Messages.Add(new ChatMessage(ChatMessageRole.User, "帮我修复 ContextBudget 的取整问题，并补一个单测。"));
        var reply = new ChatMessage(ChatMessageRole.Assistant, "已定位到 `ContextBudget.Create`，把 `total` 的取整改成向下取整，并补了边界测试。")
        {
            CreatedAt = DateTime.Now.AddMinutes(-2),
            DurationMs = 18_400,
            InputTokens = 5200,
            OutputTokens = 640,
            ApprovalCount = 2,
            ProviderId = "test",
            ModelId = "qwen3.5-4b"
        };

        reply.ToolCalls["read_file"] = 3;
        reply.ToolCalls["apply_patch"] = 1;
        reply.ToolTrace.Add(new ToolTraceEntry(
            "apply_patch",
            ToolTraceEntry.StatusOk,
            null,
            "src/Context/ContextBudget.cs",
            "已写入 2 处",
            420,
            DateTime.Now.AddMinutes(-1),
            [new EditedFile("src/Context/ContextBudget.cs", 6, 3), new EditedFile("tests/ContextBudgetTests.cs", 18, 0)]));

        session.Messages.Add(reply);

        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session)
            .Add(component => component.Workspaces, Array.Empty<Workspace>()));

        return Frame("有记录 · 工具轨迹与验证徽标", cut.Markup);
    }

    private string RenderSettings()
    {
        var cut = RenderComponent<ContextSettingsPanel>();

        return Frame("设置 · 上下文 / 压缩 / 路由", cut.Markup);
    }

    private string RenderCompaction()
    {
        var session = new ChatSession
        {
            Id = "compaction",
            Title = "长任务压缩",
            Summary = "## 目标与约束\n- 只改 Context 模块 [m0]\n## 关键事实\n- 8K 预算 4874 token",
            SummaryThroughOrder = 24,
            SummaryTokens = 320,
            SummaryUpdatedAt = DateTime.Now
        };

        session.Messages.Add(new ChatMessage(ChatMessageRole.User, "继续，把剩下的两个测试补完。"));
        session.Messages.Add(new ChatMessage(ChatMessageRole.Assistant, "好，先补 `ContextBudgetTests` 的边界用例。"));

        var cut = RenderComponent<ChatRoom>(parameters => parameters
            .Add(component => component.Session, session)
            .Add(component => component.Workspaces, Array.Empty<Workspace>()));

        var breakdown = new ContextReport(
            ProviderId: "test",
            ModelId: "qwen3.5-4b",
            ContextWindow: 8192,
            ReservedOutput: 2048,
            Safety: 409,
            Total: 4874,
            FixedCap: 1949,
            Estimated: 4870,
            RawEstimated: 5000,
            CalibrationFactor: 1.02,
            Segments: new ContextSegments(
                System: 900,
                Skills: 333,
                Turns: 1300,
                Attachments: 0,
                ToolTrace: 240,
                Pinned: 0,
                Summary: 320,
                StatePack: 400,
                ToolSchema: 493,
                Knowledge: 40),
            OmittedMessages: 22,
            OmittedTokens: 2100,
            KeptTurns: 6,
            TotalTurns: 18,
            PinnedCount: 0,
            PinnedTruncated: false,
            AttachmentsStubbed: false,
            Trimmed: true,
            SummaryUsed: true,
            SummaryTokens: 320,
            CompactedMessages: 22,
            SummaryInputTokens: 1800,
            SummaryOutputTokens: 320);

        var popover = RenderComponent<ContextBreakdown>(parameters => parameters
            .Add(component => component.Report, breakdown));

        var html = new StringBuilder();
        html.Append(cut.Markup);
        html.Append("""

            <div class="context-popover" style="position:relative;margin:24px auto;max-width:420px;left:auto;bottom:auto;">
                <div class="context-popover-head">
                    <span class="context-popover-title">上下文占用</span>
                </div>
            """);
        html.Append(popover.Markup);
        html.Append("</div>");

        return Frame("压缩 · 分隔线与分段明细", html.ToString());
    }

    private static string Frame(string title, string body) => $$"""
        <!doctype html>
        <html lang="zh-CN" data-theme="dark">
        <head>
          <meta charset="utf-8" />
          <title>{{title}}</title>
          <link rel="stylesheet" href="../../../src/App/SDemo.DesktopAgent.UI/wwwroot/css/app.css" />
          <style>
            body { margin: 0; background: var(--bg); }
            .baseline-frame { width: 1180px; min-height: 820px; margin: 0 auto; }
            .baseline-caption { padding: 14px 20px 0; color: var(--muted); font-size: 12px; }
          </style>
        </head>
        <body>
          <div class="baseline-frame">
            <div class="baseline-caption">R1 截图基线 · {{title}}</div>
            {{body}}
          </div>
        </body>
        </html>
        """;

    private static void Write(string file, string html, string output) =>
        File.WriteAllText(Path.Combine(output, file), html);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SDemo.DesktopAgent.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("找不到仓库根目录（SDemo.DesktopAgent.sln）。");
    }

    /// <summary>截图基线不触发真实请求：预览直接失败，流式没有内容。</summary>
    private sealed class QuietRunner : IAgentSessionRunner
    {
        public ContextPlan Preview(AgentRunRequest request) =>
            throw new NotSupportedException("截图基线不预览");

        public Task<ContextPlan> PreviewAsync(
            AgentRunRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("截图基线不预览");

        public Task<CompactionOutcome> CompactAsync(
            AgentCompactRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CompactionOutcome.NotNeeded("截图基线不压缩"));

        public async IAsyncEnumerable<AgentDelta> StreamAsync(
            AgentRunRequest request,
            ContextRunTelemetry telemetry,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
        }
    }
}
