using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Editing;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>
/// P2c：超长工具输出落盘。
/// <para>
/// 测的是三件事：上下文里只剩头尾预览 + 回读指引；全文真的能用 <c>read_file</c> 按行读回中间片段；
/// 落盘是尽力而为（写不进去就退回纯截断，工具结果本身永远是成功的）。
/// </para>
/// </summary>
public sealed class ToolResultSpillTests
{
    [Fact]
    public async Task 超长工具结果落盘且上下文只留头尾预览()
    {
        using var temp = new TempDirectory();
        var inner = new RecordingChatClient();
        var telemetry = new ContextRunTelemetry();
        var client = new ContextBudgetChatClient(
            inner, Options(new WorkspaceToolResultSpillStore(temp.Root)), telemetry);

        var text = BuildOutput(20_000);
        await client.GetResponseAsync(Messages("call_1", "run_command", text));

        var sent = Sent(inner);
        Assert.Contains("[输出已截断] 全文", sent, StringComparison.Ordinal);
        Assert.Contains(".agent/spill/call_1.log", sent, StringComparison.Ordinal);
        Assert.Contains("read_file(", sent, StringComparison.Ordinal);
        Assert.True(sent.Length < 2000, $"上下文里的结果应只剩预览，实际 {sent.Length} 字符");

        var file = Path.Combine(temp.Root, ".agent", "spill", "call_1.log");
        Assert.True(File.Exists(file), "落盘文件应当存在");
        Assert.Equal(text, File.ReadAllText(file));
        Assert.Equal(1, telemetry.SpilledToolResults);
        Assert.Equal(0, telemetry.SpillFailures);
    }

    [Fact]
    public async Task 落盘后能按指引读回中间片段()
    {
        using var temp = new TempDirectory();
        var inner = new RecordingChatClient();
        var client = new ContextBudgetChatClient(
            inner, Options(new WorkspaceToolResultSpillStore(temp.Root)), new ContextRunTelemetry());

        const string marker = "MIDDLE-MARKER-9527";
        var lines = Enumerable.Range(1, 20_000)
            .Select(index => index == 3000 ? marker : $"line {index}")
            .ToArray();

        await client.GetResponseAsync(Messages("call_2", "run_command", string.Join('\n', lines)));

        var sent = Sent(inner);
        var hint = Regex.Match(sent, "read_file\\(\"(?<path>[^\"]+)\", offset: (?<offset>\\d+)\\)");
        Assert.True(hint.Success, $"预览里应当带 read_file 回读指引：{sent}");

        var path = hint.Groups["path"].Value;
        var offset = int.Parse(hint.Groups["offset"].Value);

        // 模型会照 read_file 的「接续用 offset=…」一路翻；这里用同一台机器模拟，最多翻 20 次。
        var (pack, context) = Tool(temp.Root);
        var read = ToolFunction(pack, context, "read_file");
        string? content = null;

        for (var page = 0; page < 20; page++)
        {
            content = (await read.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["path"] = path,
                ["offset"] = offset,
                ["limit"] = 2000
            })))?.ToString();

            if (content is null || content.Contains(marker, StringComparison.Ordinal))
            {
                break;
            }

            var next = Regex.Match(content, "接续用 offset=(?<offset>\\d+)");
            if (!next.Success)
            {
                break;
            }

            offset = int.Parse(next.Groups["offset"].Value);
        }

        Assert.NotNull(content);
        Assert.Contains(marker, content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 读文件的结果不落盘_仍按纯截断处理()
    {
        using var temp = new TempDirectory();
        var inner = new RecordingChatClient();
        var telemetry = new ContextRunTelemetry();
        var client = new ContextBudgetChatClient(
            inner, Options(new WorkspaceToolResultSpillStore(temp.Root)), telemetry);

        await client.GetResponseAsync(Messages("call_3", "read_file", BuildOutput(5_000)));

        var sent = Sent(inner);
        Assert.Contains("工具结果过长，已省略", sent, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(temp.Root, ".agent", "spill")), "读文件的结果不该落盘");
        Assert.Equal(0, telemetry.SpilledToolResults);
        Assert.Equal(0, telemetry.SpillFailures);
    }

    [Fact]
    public async Task 写盘失败时退回纯截断并记账()
    {
        using var temp = new TempDirectory();
        var notADirectory = Path.Combine(temp.Root, "blocked");
        await File.WriteAllTextAsync(notADirectory, "x");

        var inner = new RecordingChatClient();
        var telemetry = new ContextRunTelemetry();
        var client = new ContextBudgetChatClient(
            inner, Options(new WorkspaceToolResultSpillStore(notADirectory)), telemetry);

        await client.GetResponseAsync(Messages("call_4", "run_command", BuildOutput(5_000)));

        var sent = Sent(inner);
        Assert.Contains("工具结果过长，已省略", sent, StringComparison.Ordinal);
        Assert.Contains("全文未能落盘", sent, StringComparison.Ordinal);
        Assert.Equal(0, telemetry.SpilledToolResults);
        Assert.Equal(1, telemetry.SpillFailures);
    }

    [Fact]
    public async Task 同一次生成里重复请求不会重复落盘()
    {
        using var temp = new TempDirectory();
        var inner = new RecordingChatClient();
        var telemetry = new ContextRunTelemetry();
        var client = new ContextBudgetChatClient(
            inner, Options(new WorkspaceToolResultSpillStore(temp.Root)), telemetry);

        var messages = Messages("call_5", "run_command", BuildOutput(5_000));

        await client.GetResponseAsync(messages);
        var directory = Path.Combine(temp.Root, ".agent", "spill");
        var file = Path.Combine(directory, "call_5.log");
        var written = File.GetLastWriteTimeUtc(file);

        await client.GetResponseAsync(messages);

        Assert.Single(Directory.GetFiles(directory, "*.log"));
        Assert.Equal(written, File.GetLastWriteTimeUtc(file));
        Assert.Equal(Sent(inner, 0), Sent(inner, 1));
        // 报告里数的是「条」而不是「次」。
        Assert.Equal(1, telemetry.SpilledToolResults);
    }

    [Fact]
    public void 文件名清洗与缺省哈希()
    {
        Assert.Equal("call_abc.log", WorkspaceToolResultSpillStore.FileName("call/abc:", "x"));
        Assert.Matches(@"^[0-9a-f]{16}\.log$", WorkspaceToolResultSpillStore.FileName(string.Empty, "正文"));
        Assert.Matches(@"^[0-9a-f]{16}\.log$", WorkspaceToolResultSpillStore.FileName("_.", "正文"));
        Assert.Equal("a-1_b.2.log", WorkspaceToolResultSpillStore.FileName(" a-1_b.2 ", "x"));
    }

    [Fact]
    public void 落盘目录自带gitignore_不污染工作区状态()
    {
        using var temp = new TempDirectory();
        var store = new WorkspaceToolResultSpillStore(temp.Root);

        Assert.NotNull(store.Save("call_6", new string('x', 10)));
        Assert.Equal("*\n", File.ReadAllText(Path.Combine(temp.Root, ".agent", ".gitignore")));
    }

    [Fact]
    public void 没有工作区时不落盘()
    {
        var store = new WorkspaceToolResultSpillStore(string.Empty);

        Assert.Null(store.Save("call_7", new string('x', 100)));
    }

    private static string BuildOutput(int lines) =>
        string.Join('\n', Enumerable.Range(1, lines).Select(index => $"line {index} 0123456789abcdef"));

    private static ContextBudgetChatOptions Options(IToolResultSpillStore store) => new(
        Enabled: true,
        Budget: new ContextBudget(8192, 2048, 409, 4000, 400, 300),
        Counter: new HeuristicTokenCounter(),
        CalibrationFactor: 1.0,
        KeepRecentTurns: 2,
        ToolResultChars: 2000,
        Spill: store);

    private static List<ChatMessage> Messages(string callId, string toolName, string result) =>
    [
        new(ChatRole.System, "系统提示"),
        new(ChatRole.User, "跑一下构建"),
        new(ChatRole.Assistant, [new FunctionCallContent(callId, toolName, new Dictionary<string, object?>
        {
            ["path"] = "a.cs"
        })]),
        new(ChatRole.Tool, [new FunctionResultContent(callId, result)])
    ];

    /// <summary>发出去的那条工具结果（最后一个请求里）。</summary>
    private static string Sent(RecordingChatClient inner, int index = -1)
    {
        var call = index < 0 ? inner.Calls[^1] : inner.Calls[index];
        return call.SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .Single()
            .Result!
            .ToString()!;
    }

    private static (FileToolPack Pack, ToolContext Context) Tool(string root)
    {
        var pack = new FileToolPack();
        var context = new ToolContext(
            "session",
            root,
            [],
            new StubSettingsService(),
            new ToolApprovalService(),
            new StubSandboxJobService(),
            new NullAuditLog(),
            new FileReadRegistry());

        return (pack, context);
    }

    private static AIFunction ToolFunction(FileToolPack pack, ToolContext context, string name) =>
        Assert.IsAssignableFrom<AIFunction>(pack.Create(context).Single(function => function.Name == name));

    private sealed class RecordingChatClient : IChatClient
    {
        public List<List<ChatMessage>> Calls { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(messages.ToList());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls.Add(messages.ToList());
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
