using System.Text.Json;
using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Sandbox.Protocol;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>沙箱契约：协议常量与 JSON 形状必须两端一致（枚举写成字符串，不能写成数字）。</summary>
public sealed class SandboxContractsTests
{
    [Fact]
    public void 协议常量与手册一致()
    {
        Assert.Equal(1, SandboxProtocol.Version);
        Assert.Equal("job.json", SandboxProtocol.JobFileName);
        Assert.Equal("result.json", SandboxProtocol.ResultFileName);
        Assert.Equal("pdf.json", SandboxProtocol.PdfResultFileName);
    }

    [Fact]
    public void 作业文档把模式序列化成字符串()
    {
        var document = new SandboxJobDocument
        {
            JobId = "20261021-153000-ab12cd34",
            Mode = SandboxJobMode.PowerShell,
            ScriptFile = "input/script.ps1"
        };

        var json = JsonSerializer.Serialize(document, SandboxJson.Options);

        Assert.Contains("\"mode\": \"PowerShell\"", json);
        Assert.DoesNotContain("\"mode\": 0", json);
        Assert.Contains("\"protocolVersion\": 1", json);
        Assert.Contains("\"workingDirectory\": \"work\"", json);
    }

    [Fact]
    public void 作业文档可以往返序列化()
    {
        var document = new SandboxJobDocument
        {
            JobId = "job-0001",
            Mode = SandboxJobMode.PdfExtract,
            InputFile = "input/document.pdf",
            Pages = "1-5",
            MaxChars = 4_000
        };

        var json = JsonSerializer.Serialize(document, SandboxJson.Options);
        var round = JsonSerializer.Deserialize<SandboxJobDocument>(json, SandboxJson.Options);

        Assert.NotNull(round);
        Assert.Equal(document.JobId, round.JobId);
        Assert.Equal(SandboxJobMode.PdfExtract, round.Mode);
        Assert.Equal("input/document.pdf", round.InputFile);
        Assert.Equal("1-5", round.Pages);
        Assert.Equal(4_000, round.MaxChars);
    }

    [Fact]
    public void 结果文档的默认值符合协议()
    {
        var document = new SandboxResultDocument { JobId = "job-0001" };

        Assert.Equal(1, document.ProtocolVersion);
        Assert.Equal(0, document.ExitCode);
        Assert.False(document.TimedOut);
        Assert.False(document.Truncated);
        Assert.Empty(document.Artifacts);
    }
}
