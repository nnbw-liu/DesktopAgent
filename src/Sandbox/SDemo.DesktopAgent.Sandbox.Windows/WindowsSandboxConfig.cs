using System.IO;
using System.Text;
using System.Xml.Linq;

namespace SDemo.DesktopAgent.Platform.Windows.Sandbox;

/// <summary>一个映射目录：宿主机绝对路径 → 沙箱内绝对路径。</summary>
public sealed record WindowsSandboxMappedFolder(string HostFolder, string SandboxFolder, bool ReadOnly);

/// <summary>
/// Windows Sandbox 的 <c>.wsb</c> 配置生成（S4）。
/// 安全不变量：<c>Networking</c> 固定 Disable、<c>vGPU</c> 固定 Disable、剪贴板固定关闭，
/// 不提供任何“打开网络 / 剪贴板”的参数入口。XML 由 <see cref="XElement"/> 转义，不手工拼字符串。
/// </summary>
public static class WindowsSandboxConfig
{
    /// <summary>沙箱内固定的映射点，与 run.ps1 模板里的路径一致。</summary>
    public const string InputSandboxPath = @"C:\job\input";

    public const string WorkSandboxPath = @"C:\job\work";

    public const string OutputSandboxPath = @"C:\job\output";

    public const string RunScriptSandboxPath = @"C:\job\input\run.ps1";

    /// <summary>.wsb 配置文件名，生成在 input 目录下，运行结束由宿主删除。</summary>
    public const string ConfigurationFileName = "job.wsb";

    /// <summary>MemoryInMB 的下限由 Windows Sandbox 规定，低于它会启动失败。</summary>
    public const int MinimumMemoryMB = 2048;

    public const int DefaultMemoryMB = 4096;

    /// <summary>宿主机启动沙箱的 LogonCommand：只调用 input 下由宿主生成的 run.ps1。</summary>
    public const string LogonCommand =
        @"powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\job\input\run.ps1";

    public static int ClampMemory(int memoryMB) =>
        memoryMB <= 0 ? DefaultMemoryMB : Math.Max(MinimumMemoryMB, Math.Min(memoryMB, 16384));

    /// <summary>生成 .wsb XML 文本（含 XML 声明，UTF-8）。</summary>
    public static string Create(
        IReadOnlyList<WindowsSandboxMappedFolder> folders,
        int memoryMB,
        string logonCommand = LogonCommand)
    {
        ArgumentNullException.ThrowIfNull(folders);

        if (folders.Count == 0)
        {
            throw new ArgumentException("至少需要一个映射目录。", nameof(folders));
        }

        if (string.IsNullOrWhiteSpace(logonCommand))
        {
            throw new ArgumentException("LogonCommand 不能为空。", nameof(logonCommand));
        }

        foreach (var folder in folders)
        {
            Validate(folder);
        }

        var configuration = new XElement(
            "Configuration",
            new XElement("Networking", "Disable"),
            new XElement("vGPU", "Disable"),
            new XElement("ClipboardRedirection", "Disable"),
            new XElement("MemoryInMB", ClampMemory(memoryMB)),
            new XElement(
                "MappedFolders",
                folders.Select(folder => new XElement(
                    "MappedFolder",
                    new XElement("HostFolder", folder.HostFolder),
                    new XElement("SandboxFolder", folder.SandboxFolder),
                    new XElement("ReadOnly", folder.ReadOnly ? "true" : "false")))),
            new XElement("LogonCommand", new XElement("Command", logonCommand)));

        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" + configuration;
    }

    /// <summary>生成并写入 .wsb（UTF-8 无 BOM）。</summary>
    public static void Write(
        string path,
        IReadOnlyList<WindowsSandboxMappedFolder> folders,
        int memoryMB,
        string logonCommand = LogonCommand)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, Create(folders, memoryMB, logonCommand), new UTF8Encoding(false));
    }

    private static void Validate(WindowsSandboxMappedFolder folder)
    {
        if (string.IsNullOrWhiteSpace(folder.HostFolder) || !Path.IsPathRooted(folder.HostFolder))
        {
            throw new ArgumentException($"HostFolder 必须是绝对路径：{folder.HostFolder}");
        }

        if (!Directory.Exists(folder.HostFolder))
        {
            throw new DirectoryNotFoundException($"映射目录不存在：{folder.HostFolder}");
        }

        if (string.IsNullOrWhiteSpace(folder.SandboxFolder) || !Path.IsPathRooted(folder.SandboxFolder))
        {
            throw new ArgumentException($"SandboxFolder 必须是绝对路径：{folder.SandboxFolder}");
        }
    }
}
