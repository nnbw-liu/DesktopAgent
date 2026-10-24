using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>R1.4：用量按 provider / model 汇总；未配价只显示 token，旧消息单独计数。</summary>
public sealed class UsageCostServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task 按提供商与模型汇总并计算金额()
    {
        var settings = new StubSettingsService();
        settings.Current.Providers.Add(new ModelProvider
        {
            Id = "cloud",
            Models =
            [
                new ModelEntry
                {
                    Id = "flash",
                    InputPricePerMillion = 2m,
                    OutputPricePerMillion = 8m
                }
            ]
        });
        settings.Current.Providers.Add(new ModelProvider
        {
            Id = "local",
            Models = [new ModelEntry { Id = "qwen" }]
        });

        var store = new ChatStore(_temp.Root);
        var session = new ChatSession { Id = "s1", Title = "用量" };

        session.Messages.Add(Assistant(1200, 400, "cloud", "flash"));
        session.Messages.Add(Assistant(800, 100, "cloud", "flash"));
        session.Messages.Add(Assistant(5000, 900, "local", "qwen"));
        session.Messages.Add(Assistant(1000, 500, providerId: null, modelId: null));
        // 用户消息不是一次生成，不能进成本。
        session.Messages.Add(new ChatMessage(ChatMessageRole.User, "问题") { InputTokens = 999 });

        await store.SaveSessionAsync(session);

        var summary = await new UsageCostService(store, settings).SummarizeAsync([]);

        Assert.Equal(2, summary.Lines.Count);
        Assert.Equal(1, summary.CallsWithoutProvider);
        Assert.Equal(1, summary.SessionsScanned);

        var cloud = summary.Lines.Single(line => line.ProviderId == "cloud");
        Assert.Equal(2, cloud.Calls);
        Assert.Equal(2000, cloud.InputTokens);
        Assert.Equal(500, cloud.OutputTokens);
        Assert.True(cloud.Priced);
        // 2000 / 1e6 * 2 + 500 / 1e6 * 8 = 0.004 + 0.004 = 0.008
        Assert.Equal(0.008m, cloud.Cost);

        var local = summary.Lines.Single(line => line.ProviderId == "local");
        Assert.False(local.Priced);
        Assert.Null(local.Cost);

        // 有模型没配价格时，总额不能拿 0 冒充。
        Assert.Null(summary.TotalCost);
        Assert.Equal(7000, summary.TotalInputTokens);
        Assert.Equal(1400, summary.TotalOutputTokens);

        // 合计调用次数包含没有 provider 归属的旧消息（4 次生成）。
        Assert.Equal(4, summary.TotalCalls);
    }

    [Fact]
    public async Task 所有模型都配价时给出总额()
    {
        var settings = new StubSettingsService();
        settings.Current.Providers.Add(new ModelProvider
        {
            Id = "cloud",
            Models =
            [
                new ModelEntry
                {
                    Id = "flash",
                    InputPricePerMillion = 1m,
                    OutputPricePerMillion = 2m
                }
            ]
        });

        var store = new ChatStore(_temp.Root);
        var session = new ChatSession { Id = "s2", Title = "总价" };
        session.Messages.Add(Assistant(1_000_000, 500_000, "cloud", "flash"));
        await store.SaveSessionAsync(session);

        var summary = await new UsageCostService(store, settings).SummarizeAsync([]);

        Assert.Equal(2m, summary.TotalCost);
    }

    private static ChatMessage Assistant(
        int input,
        int output,
        string? providerId,
        string? modelId) =>
        new(ChatMessageRole.Assistant, "回复")
        {
            InputTokens = input,
            OutputTokens = output,
            ProviderId = providerId,
            ModelId = modelId
        };
}
