using SDemo.DesktopAgent.Editing;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>读前写注册表：TTL、按会话隔离、clear。</summary>
public sealed class ReadRegistryTests
{
    [Fact]
    public void 记录后能按会话找到()
    {
        var registry = new FileReadRegistry();

        registry.RecordRead("s1", "src/a.cs", "hash");

        var record = registry.Find("s1", "src/a.cs");
        Assert.NotNull(record);
        Assert.Equal("hash", record!.Hash);
    }

    [Fact]
    public void 路径分隔符和大小写不敏感()
    {
        var registry = new FileReadRegistry();
        registry.RecordRead("s1", "src\\A.cs", "hash");

        Assert.NotNull(registry.Find("s1", "src/a.cs"));
        Assert.NotNull(registry.Find("s1", "SRC/A.CS"));
    }

    [Fact]
    public void 超过TTL后记录失效()
    {
        var time = new ManualTimeProvider();
        var registry = new FileReadRegistry(time, TimeSpan.FromHours(2));
        registry.RecordRead("s1", "a.cs", "hash");

        time.Now = time.Now.AddHours(3);

        Assert.Null(registry.Find("s1", "a.cs"));
    }

    [Fact]
    public void 会话之间隔离()
    {
        var registry = new FileReadRegistry();
        registry.RecordRead("s1", "a.cs", "hash");

        Assert.Null(registry.Find("s2", "a.cs"));
    }

    [Fact]
    public void Clear清空指定会话()
    {
        var registry = new FileReadRegistry();
        registry.RecordRead("s1", "a.cs", "hash");
        registry.RecordRead("s2", "a.cs", "hash");

        registry.Clear("s1");

        Assert.Null(registry.Find("s1", "a.cs"));
        Assert.NotNull(registry.Find("s2", "a.cs"));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-01-01T00:00:00+08:00");

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
