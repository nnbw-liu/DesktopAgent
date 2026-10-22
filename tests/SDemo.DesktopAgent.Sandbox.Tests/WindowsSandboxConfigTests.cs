using System.Xml.Linq;
using SDemo.DesktopAgent.Platform.Windows.Sandbox;
using Xunit;

namespace SDemo.DesktopAgent.Sandbox.Tests;

/// <summary>.wsb 生成：网络 / vGPU / 剪贴板固定关闭、XML 转义、映射目录必须存在、内存下限。</summary>
[Trait("Category", "Sandbox")]
[Trait("Category", "WindowsOnly")]
public sealed class WindowsSandboxConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sdemo-wsb-config", Guid.NewGuid().ToString("N"));

    public WindowsSandboxConfigTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响测试结论。
        }
    }

    [Fact]
    public void 关闭网络剪贴板与vGPU()
    {
        var input = CreateDirectory("input");
        var work = CreateDirectory("work");
        var output = CreateDirectory("output");

        var xml = WindowsSandboxConfig.Create(Folders(input, work, output), memoryMB: 4096);
        var document = XDocument.Parse(xml);
        var configuration = document.Root!;

        Assert.Equal("Disable", configuration.Element("Networking")?.Value);
        Assert.Equal("Disable", configuration.Element("vGPU")?.Value);
        Assert.Equal("Disable", configuration.Element("ClipboardRedirection")?.Value);
        Assert.Equal("4096", configuration.Element("MemoryInMB")?.Value);
        Assert.NotNull(configuration.Element("LogonCommand")?.Element("Command"));
    }

    [Fact]
    public void 目录映射与只读标志正确()
    {
        var input = CreateDirectory("input");
        var work = CreateDirectory("work");
        var output = CreateDirectory("output");

        var xml = WindowsSandboxConfig.Create(Folders(input, work, output), memoryMB: 4096);
        var document = XDocument.Parse(xml);
        var mappings = document.Root!.Element("MappedFolders")!.Elements("MappedFolder").ToArray();

        Assert.Equal(3, mappings.Length);
        Assert.Equal(WindowsSandboxConfig.InputSandboxPath, mappings[0].Element("SandboxFolder")?.Value);
        Assert.Equal("true", mappings[0].Element("ReadOnly")?.Value);
        Assert.Equal(WindowsSandboxConfig.WorkSandboxPath, mappings[1].Element("SandboxFolder")?.Value);
        Assert.Equal("false", mappings[1].Element("ReadOnly")?.Value);
        Assert.Equal(WindowsSandboxConfig.OutputSandboxPath, mappings[2].Element("SandboxFolder")?.Value);
        Assert.Equal("false", mappings[2].Element("ReadOnly")?.Value);
    }

    [Fact]
    public void 元素顺序符合文档()
    {
        var input = CreateDirectory("input");
        var work = CreateDirectory("work");
        var output = CreateDirectory("output");

        var xml = WindowsSandboxConfig.Create(Folders(input, work, output), memoryMB: 4096);

        var networking = xml.IndexOf("<Networking>", StringComparison.Ordinal);
        var vgpu = xml.IndexOf("<vGPU>", StringComparison.Ordinal);
        var clipboard = xml.IndexOf("<ClipboardRedirection>", StringComparison.Ordinal);
        var memory = xml.IndexOf("<MemoryInMB>", StringComparison.Ordinal);
        var folders = xml.IndexOf("<MappedFolders>", StringComparison.Ordinal);
        var logon = xml.IndexOf("<LogonCommand>", StringComparison.Ordinal);

        Assert.True(networking >= 0 && networking < vgpu);
        Assert.True(vgpu < clipboard);
        Assert.True(clipboard < memory);
        Assert.True(memory < folders);
        Assert.True(folders < logon);
    }

    [Fact]
    public void 路径与命令中的特殊字符被转义()
    {
        var input = CreateDirectory("in & put");
        var work = CreateDirectory("work");
        var output = CreateDirectory("output");

        var xml = WindowsSandboxConfig.Create(
            Folders(input, work, output),
            memoryMB: 4096,
            logonCommand: @"powershell.exe -File ""C:\job\input\run<1>.ps1""");

        Assert.DoesNotContain("in & put", xml);
        Assert.Contains("in &amp; put", xml);
        Assert.Contains("run&lt;1&gt;.ps1", xml);
        Assert.Equal(input, XDocument.Parse(xml).Root!
            .Element("MappedFolders")!.Elements("MappedFolder").First()
            .Element("HostFolder")?.Value);
    }

    [Fact]
    public void 内存低于系统下限时抬到2048()
    {
        var input = CreateDirectory("input");
        var work = CreateDirectory("work");
        var output = CreateDirectory("output");

        var xml = WindowsSandboxConfig.Create(Folders(input, work, output), memoryMB: 1024);

        Assert.Contains("<MemoryInMB>2048</MemoryInMB>", xml);
    }

    [Fact]
    public void 未设置内存时用默认值()
    {
        var input = CreateDirectory("input");
        var work = CreateDirectory("work");
        var output = CreateDirectory("output");

        var xml = WindowsSandboxConfig.Create(Folders(input, work, output), memoryMB: 0);

        Assert.Contains($"<MemoryInMB>{WindowsSandboxConfig.DefaultMemoryMB}</MemoryInMB>", xml);
    }

    [Fact]
    public void 映射目录不存在时拒绝生成()
    {
        var work = CreateDirectory("work");
        var output = CreateDirectory("output");
        var missing = Path.Combine(_root, "missing");

        var exception = Assert.Throws<DirectoryNotFoundException>(() =>
            WindowsSandboxConfig.Create(
                [
                    new WindowsSandboxMappedFolder(missing, WindowsSandboxConfig.InputSandboxPath, true),
                    new WindowsSandboxMappedFolder(work, WindowsSandboxConfig.WorkSandboxPath, false),
                    new WindowsSandboxMappedFolder(output, WindowsSandboxConfig.OutputSandboxPath, false)
                ],
                4096));

        Assert.Contains(missing, exception.Message);
    }

    [Fact]
    public void 相对路径映射被拒绝()
    {
        var work = CreateDirectory("work");
        var output = CreateDirectory("output");

        Assert.Throws<ArgumentException>(() =>
            WindowsSandboxConfig.Create(
                [
                    new WindowsSandboxMappedFolder("relative/input", WindowsSandboxConfig.InputSandboxPath, true),
                    new WindowsSandboxMappedFolder(work, WindowsSandboxConfig.WorkSandboxPath, false),
                    new WindowsSandboxMappedFolder(output, WindowsSandboxConfig.OutputSandboxPath, false)
                ],
                4096));
    }

    [Fact]
    public void 写入的文件是不带BOM的UTF8()
    {
        var input = CreateDirectory("input");
        var work = CreateDirectory("work");
        var output = CreateDirectory("output");
        var path = Path.Combine(_root, "job.wsb");

        WindowsSandboxConfig.Write(path, Folders(input, work, output), 4096);

        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", File.ReadAllText(path));
    }

    private string CreateDirectory(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static IReadOnlyList<WindowsSandboxMappedFolder> Folders(
        string input,
        string work,
        string output) =>
    [
        new WindowsSandboxMappedFolder(input, WindowsSandboxConfig.InputSandboxPath, ReadOnly: true),
        new WindowsSandboxMappedFolder(work, WindowsSandboxConfig.WorkSandboxPath, ReadOnly: false),
        new WindowsSandboxMappedFolder(output, WindowsSandboxConfig.OutputSandboxPath, ReadOnly: false)
    ];
}
