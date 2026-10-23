using Bunit;
using Microsoft.Extensions.DependencyInjection;
using SDemo.DesktopAgent.Components.Chat;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Execution;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.UI.Tests;

/// <summary>验证徽标：dirty / 失败 / 已验证三种状态，以及手动触发验证。</summary>
public sealed class VerificationBadgeTests : TestContext
{
    public VerificationBadgeTests()
    {
        // 徽标测试不需要隔离的库文件：共用一个固定的临时根目录即可。
        var root = Path.Combine(Path.GetTempPath(), "sdemo-ui-badge-tests");
        Directory.CreateDirectory(root);

        var paths = new StubAppPaths(root);
        Services.AddSingleton<WorkspaceContext>(new WorkspaceContext(paths) { RootPath = root });
        Services.AddSingleton<ChatStore>(new ChatStore(paths.ChatsRoot));
    }

    [Fact]
    public void 有改动未验证时显示文件数与运行按钮()
    {
        RegisterRunner(null);
        var session = SessionWithEdit();

        var cut = RenderComponent<VerificationBadge>(parameters => parameters
            .Add(component => component.Session, session));

        Assert.Contains("未验证改动：2 个文件", cut.Markup);
        Assert.Contains("运行验证", cut.Markup);
        Assert.DoesNotContain("verification-badge--fail", cut.Markup);
    }

    [Fact]
    public void 上次验证失败时显示失败样式与首条问题()
    {
        RegisterRunner(null);
        var session = SessionWithEdit();
        session.LastVerification = new VerificationRecord(
            VerificationRecord.BuildKind,
            "dotnet build",
            Success: false,
            ExitCode: 1,
            Errors: 2,
            Warnings: 0,
            Passed: 0,
            Failed: 0,
            TopIssues: ["CS0102 重复定义"],
            At: DateTime.Now.AddMinutes(-5),
            DurationMs: 1200);

        var cut = RenderComponent<VerificationBadge>(parameters => parameters
            .Add(component => component.Session, session));

        Assert.Contains("verification-badge--fail", cut.Markup);
        Assert.Contains("验证失败：", cut.Markup);
        Assert.Contains("CS0102 重复定义", cut.Markup);
    }

    [Fact]
    public void 已成功验证后不再显示徽标()
    {
        RegisterRunner(null);
        var session = SessionWithEdit();
        session.LastVerification = new VerificationRecord(
            VerificationRecord.BuildKind,
            "dotnet build",
            Success: true,
            ExitCode: 0,
            Errors: 0,
            Warnings: 1,
            Passed: 0,
            Failed: 0,
            TopIssues: [],
            At: DateTime.Now.AddMinutes(1),
            DurationMs: 900);

        var cut = RenderComponent<VerificationBadge>(parameters => parameters
            .Add(component => component.Session, session));

        Assert.DoesNotContain("verification-badge", cut.Markup);
    }

    [Fact]
    public async Task 点击运行验证会调用验证器并提示结果()
    {
        var session = SessionWithEdit();
        var runner = RegisterRunner(new VerificationRecord(
            VerificationRecord.TestKind,
            "dotnet test",
            Success: true,
            ExitCode: 0,
            Errors: 0,
            Warnings: 0,
            Passed: 10,
            Failed: 0,
            TopIssues: [],
            At: DateTime.Now.AddMinutes(1),
            DurationMs: 800));

        var cut = RenderComponent<VerificationBadge>(parameters => parameters
            .Add(component => component.Session, session));

        await cut.Find("button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal(1, runner.Calls);
        Assert.Contains("验证通过。", cut.Markup);
    }

    [Fact]
    public void 没有可用命令时提示去设置()
    {
        RegisterRunner(null);
        var session = SessionWithEdit();

        var cut = RenderComponent<VerificationBadge>(parameters => parameters
            .Add(component => component.Session, session));

        Assert.Contains("运行验证", cut.Markup);
    }

    private FakeVerificationRunner RegisterRunner(VerificationRecord? result)
    {
        var runner = new FakeVerificationRunner(result);
        Services.AddSingleton<IVerificationRunner>(runner);
        return runner;
    }

    /// <summary>两条带 [EDIT] 的轨迹，且之后没有成功验证 → dirty。</summary>
    private static ChatSession SessionWithEdit()
    {
        var session = new ChatSession { Id = "s1", Title = "验证" };
        var reply = new ChatMessage(ChatMessageRole.Assistant, "改好了")
        {
            CreatedAt = DateTime.Now.AddMinutes(-2)
        };

        reply.ToolTrace.Add(new ToolTraceEntry(
            "apply_patch",
            ToolTraceEntry.StatusOk,
            null,
            "src/a.cs",
            "ok",
            120,
            DateTime.Now.AddMinutes(-1),
            [new EditedFile("src/a.cs", 3, 1), new EditedFile("src/b.cs", 1, 0)]));

        session.Messages.Add(new ChatMessage(ChatMessageRole.User, "改一下"));
        session.Messages.Add(reply);
        return session;
    }
}
