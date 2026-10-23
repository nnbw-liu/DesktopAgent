using System.IO;
using SDemo.DesktopAgent.Context;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>token 校准（§3.3）：EMA、偏差停用、窗口学习与重置。</summary>
public sealed class TokenCalibrationStoreTests : IDisposable
{
    private readonly string _root;

    public TokenCalibrationStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sdemo-calibration-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    private TokenCalibrationStore CreateStore() => new(Path.Combine(_root, "calibration.json"));

    [Fact]
    public void 首次样本按EMA计入因子()
    {
        var store = CreateStore();
        var result = store.RecordUsage("p", "m", estimatedInputTokens: 1000, actualInputTokens: 1400);

        Assert.False(result.Skipped);
        Assert.Equal(1.4, result.RawRatio, 3);
        // 初始 1.0，样本权重 0.3：1.0 * 0.7 + 1.4 * 0.3 = 1.12。
        Assert.Equal(1.12, store.GetFactor("p", "m"), 3);
    }

    [Fact]
    public void 连续三次偏差过大会停用校准()
    {
        var store = CreateStore();

        for (var index = 0; index < 3; index++)
        {
            store.RecordUsage("p", "m", 1000, 2000);
        }

        var entry = store.Snapshot()["p/m"];
        Assert.True(entry.Disabled);
        // 停用后调用方拿到中性因子，不再放大误差。
        Assert.Equal(1.0, store.GetFactor("p", "m"));
    }

    [Fact]
    public void 偏差回到阈值内会清空连续异常计数()
    {
        var store = CreateStore();
        store.RecordUsage("p", "m", 1000, 1500);
        store.RecordUsage("p", "m", 1000, 1020);

        Assert.Equal(0, store.Snapshot()["p/m"].MismatchStreak);
        Assert.False(store.Snapshot()["p/m"].Disabled);
    }

    [Fact]
    public void 没有usage时跳过校准()
    {
        var store = CreateStore();
        var result = store.RecordUsage("p", "m", 1000, 0);

        Assert.True(result.Skipped);
        Assert.Equal(1.0, store.GetFactor("p", "m"));
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public void 因子被夹在0点7到1点6之间()
    {
        var store = CreateStore();
        store.RecordUsage("p", "m", 100, 100_000);

        Assert.InRange(store.GetFactor("p", "m"), 0.7, 1.6);
    }

    [Fact]
    public void 写入文件后新实例能读回_重置后清空()
    {
        var path = Path.Combine(_root, "calibration.json");
        var first = new TokenCalibrationStore(path);
        first.RecordUsage("p", "m", 1000, 1200);
        first.LearnWindow("p", "m", 65_536);

        var second = new TokenCalibrationStore(path);
        Assert.Equal(first.GetFactor("p", "m"), second.GetFactor("p", "m"), 4);
        Assert.Equal(65_536, second.GetWindowOverride("p", "m"));

        second.ResetAll();
        Assert.Equal(1.0, second.GetFactor("p", "m"));
        Assert.Null(second.GetWindowOverride("p", "m"));
        Assert.Empty(new TokenCalibrationStore(path).Snapshot());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
