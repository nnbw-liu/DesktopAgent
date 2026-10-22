using System.IO;
using SDemo.DesktopAgent.Platform.Windows.Sandbox;
using Xunit;

namespace SDemo.DesktopAgent.Sandbox.Tests;

/// <summary>
/// S5 升级路径：host 的 payload 新增 PdfPig 依赖后，旧安装必须被识别为“不是最新”并覆盖复制。
/// 只判断可执行文件是否存在会把 S4 的旧 host 留在原地，PDF 作业会因为缺 DLL 失败。
/// </summary>
public sealed class SandboxHostInstallerTests
{
    [Fact]
    public void 目标缺少可执行文件时视为未安装()
    {
        using var source = new TempDirectory();
        using var target = new TempDirectory();
        File.WriteAllText(Path.Combine(source.Root, SandboxHostInstaller.ExecutableName), "host");

        Assert.False(SandboxHostInstaller.IsPayloadCurrent(source.Root, target.Root));
    }

    [Fact]
    public void 文件完全一致时复用现有安装()
    {
        using var source = new TempDirectory();
        using var target = new TempDirectory();
        WritePayload(source.Root, target.Root);

        Assert.True(SandboxHostInstaller.IsPayloadCurrent(source.Root, target.Root));
    }

    [Fact]
    public void 发布目录多出DLL时要求重新复制()
    {
        using var source = new TempDirectory();
        using var target = new TempDirectory();
        WritePayload(source.Root, target.Root);

        var pdfPig = Path.Combine(source.Root, "UglyToad.PdfPig.dll");
        File.WriteAllBytes(pdfPig, [0x01, 0x02, 0x03]);

        Assert.False(SandboxHostInstaller.IsPayloadCurrent(source.Root, target.Root));
    }

    [Fact]
    public void 目标文件被替换后要求重新复制()
    {
        using var source = new TempDirectory();
        using var target = new TempDirectory();
        WritePayload(source.Root, target.Root);

        var dll = Path.Combine(target.Root, "runtime.dll");
        File.SetLastWriteTimeUtc(dll, DateTime.UtcNow.AddMinutes(-5));

        Assert.False(SandboxHostInstaller.IsPayloadCurrent(source.Root, target.Root));
    }

    private static void WritePayload(string source, string target)
    {
        var host = Path.Combine(source, SandboxHostInstaller.ExecutableName);
        File.WriteAllText(host, "host");
        var dll = Path.Combine(source, "runtime.dll");
        File.WriteAllText(dll, "runtime");

        Directory.CreateDirectory(target);
        File.Copy(host, Path.Combine(target, SandboxHostInstaller.ExecutableName));
        File.Copy(dll, Path.Combine(target, "runtime.dll"));
    }
}
