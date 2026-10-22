using System.IO;
using System.Text.Json;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>审计日志：默认不落脚本正文，只记 hash / 长度 / 审批结果。</summary>
public sealed class ToolAuditLogTests
{
    [Fact]
    public void 默认不写脚本正文但保留hash与审批结果()
    {
        using var temp = new TempDirectory();
        var settings = new StubSettingsService();
        var log = new JsonlToolAuditLog(new StubAppPaths(temp.Root), settings);

        log.Write(Entry(scriptPreview: "Write-Output top-secret"));

        var line = Assert.Single(ReadLines(log));
        Assert.DoesNotContain("top-secret", line);
        Assert.Contains("sha256:abc", line);
        Assert.Contains("\"approval\":\"ApproveOnce\"", line.Replace(" ", string.Empty));
        Assert.Contains("\"exitCode\":0", line.Replace(" ", string.Empty));
        Assert.Contains("\"changeCount\":2", line.Replace(" ", string.Empty));
    }

    [Fact]
    public void 打开保留脚本后写入截断的preview()
    {
        using var temp = new TempDirectory();
        var settings = new StubSettingsService();
        settings.Current.Sandbox.RetainScriptsInAuditLog = true;
        var log = new JsonlToolAuditLog(new StubAppPaths(temp.Root), settings);
        var script = new string('a', 5000);

        log.Write(Entry(scriptPreview: script));

        var line = Assert.Single(ReadLines(log));
        var preview = JsonDocument.Parse(line).RootElement.GetProperty("scriptPreview").GetString();

        Assert.NotNull(preview);
        Assert.Equal(4097, preview!.Length);
        Assert.EndsWith("…", preview);
    }

    [Fact]
    public void 写入失败不影响调用方()
    {
        // 数据根目录指向一个非法路径：实现必须吞掉 IO 异常。
        var paths = new StubAppPaths(Path.Combine("Z:", "sdemo-missing-drive"));
        var log = new JsonlToolAuditLog(paths, new StubSettingsService());

        log.Write(Entry(scriptPreview: null));
    }

    [Fact]
    public void 变更明细只写类型与路径hash()
    {
        using var temp = new TempDirectory();
        var log = new JsonlToolAuditLog(new StubAppPaths(temp.Root), new StubSettingsService());

        log.Write(Entry(scriptPreview: null) with
        {
            Changes = [new ToolAuditChange("Deleted", "sha256:pathhash")]
        });

        var line = Assert.Single(ReadLines(log));
        Assert.Contains("\"kind\":\"Deleted\"", line.Replace(" ", string.Empty));
        Assert.Contains("\"pathSha256\":\"sha256:pathhash\"", line.Replace(" ", string.Empty));
        Assert.DoesNotContain("secret.txt", line);
    }

    [Fact]
    public void 超过保留天数的日志在首次写入时清理()
    {
        using var temp = new TempDirectory();
        var settings = new StubSettingsService();
        settings.Current.Sandbox.AuditRetentionDays = 7;
        var log = new JsonlToolAuditLog(new StubAppPaths(temp.Root), settings);
        Directory.CreateDirectory(log.DirectoryPath);

        var expired = Path.Combine(log.DirectoryPath, "tools-20200101.jsonl");
        File.WriteAllText(expired, "{}\n");
        File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-30));
        var recent = Path.Combine(log.DirectoryPath, "tools-20200102.jsonl");
        File.WriteAllText(recent, "{}\n");

        log.Write(Entry(scriptPreview: null));

        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(recent));
        Assert.Contains("run_powershell", File.ReadAllText(log.FilePathFor(DateTime.Now)));
    }

    [Fact]
    public void 单文件超过10MB时轮转()
    {
        using var temp = new TempDirectory();
        var log = new JsonlToolAuditLog(new StubAppPaths(temp.Root), new StubSettingsService());
        Directory.CreateDirectory(log.DirectoryPath);

        var today = log.FilePathFor(DateTime.Now);
        File.WriteAllText(today, new string('x', (int)JsonlToolAuditLog.MaxFileBytes + 1));

        log.Write(Entry(scriptPreview: null));

        var rotated = Path.Combine(log.DirectoryPath, $"tools-{DateTime.Now:yyyyMMdd}-1.jsonl");
        Assert.True(File.Exists(rotated));
        Assert.True(new FileInfo(rotated).Length > JsonlToolAuditLog.MaxFileBytes);
        Assert.Single(File.ReadAllLines(today), line => !string.IsNullOrWhiteSpace(line));
    }

    private static ToolAuditEntry Entry(string? scriptPreview) => new()
    {
        SessionId = "session",
        ToolId = "run_powershell",
        JobId = "job",
        Sandbox = SandboxKind.AppContainer,
        ScriptSha256 = "sha256:abc",
        ScriptPreview = scriptPreview,
        ExitCode = 0,
        DurationMs = 12,
        StdoutBytes = 3,
        StderrBytes = 0,
        ChangeCount = 2,
        Approval = "ApproveOnce"
    };

    private static string[] ReadLines(JsonlToolAuditLog log) =>
        Directory.GetFiles(log.DirectoryPath, "tools-*.jsonl")
            .SelectMany(File.ReadAllLines)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
}

/// <summary>工具包开关模型：缺省按描述默认值，显式关闭优先生效。</summary>
public sealed class ToolSettingsTests
{
    [Fact]
    public void 缺省使用工具包默认值()
    {
        var settings = new ToolSettings();

        Assert.False(settings.IsPackEnabled("powershell", defaultEnabled: false));
        Assert.True(settings.IsPackEnabled("files", defaultEnabled: true));
    }

    [Fact]
    public void 显式开关覆盖默认值且忽略大小写()
    {
        var settings = new ToolSettings();
        settings.SetPackEnabled("PowerShell", true);

        Assert.True(settings.IsPackEnabled("powershell", defaultEnabled: false));
        Assert.Equal("powershell", Assert.Single(settings.EnabledPacks.Keys));
    }
}
