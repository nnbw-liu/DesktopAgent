using System.ComponentModel;
using System.IO;
using System.Text;
using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Sandbox.Protocol;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 工作区文件读写工具包。文件工具不是进程执行，不进沙箱，靠“路径 jail + 写入审批”控制：
/// 显式拒绝绝对路径 / UNC / 设备路径 / <c>..</c> / 重解析点（符号链接、junction），
/// 写文件时先临时文件再原子替换，并且每次都走通用审批。
/// </summary>
public sealed class FileToolPack : IAgentToolPack
{
    private const int MaxReadBytes = 64 * 1024;
    private const int MaxResultChars = 8000;

    public AgentToolPackDescriptor Descriptor { get; } = new(
        Id: "files",
        Name: "文件",
        Description: "读写当前工作区的文本文件；引用工作区外的路径会被拒绝，写入需要确认。",
        Glyph: "▤",
        Risk: AgentToolRisk.Medium,
        Order: 20,
        DefaultEnabled: true,
        WindowsOnly: false,
        Functions:
        [
            new AgentToolFunctionDescriptor("list_files", "列出文件"),
            new AgentToolFunctionDescriptor("read_file", "读取文件"),
            new AgentToolFunctionDescriptor(
                "write_file",
                "写入文件",
                AgentToolRisk.Medium,
                RequiresApproval: true),
            new AgentToolFunctionDescriptor("workspace_info", "工作区信息")
        ]);

    public IReadOnlyList<AITool> Create(ToolContext context)
    {
        var functions = new FileFunctions(context);

        return
        [
            AIFunctionFactory.Create(
                functions.ListFiles,
                name: "list_files",
                description: $"列出工作区（{context.WorkspaceRoot}）内的文件，path 为相对路径，默认当前目录。"),

            AIFunctionFactory.Create(
                functions.ReadFile,
                name: "read_file",
                description: "读取工作区内的文本文件内容，path 为相对路径。"),

            AIFunctionFactory.Create(
                functions.WriteFileAsync,
                name: "write_file",
                description: "把文本写入工作区内的文件（会覆盖同名文件），path 为相对路径，content 为完整内容。"),

            AIFunctionFactory.Create(
                functions.WorkspaceInfo,
                name: "workspace_info",
                description: "获取工作区根目录路径，用于向用户说明文件保存位置。")
        ];
    }

    private sealed class FileFunctions(ToolContext context)
    {
        public string WorkspaceInfo() => $"工作区根目录：{context.WorkspaceRoot}";

        public string ListFiles(
            [Description("相对工作区的目录路径，默认当前目录。")] string path = ".")
        {
            var directory = ResolveReadPath(path);
            if (!Directory.Exists(directory))
            {
                return $"目录不存在：{path}";
            }

            var entries = new List<string>();

            foreach (var item in Directory.EnumerateFileSystemEntries(directory).OrderBy(name => name))
            {
                var name = Path.GetFileName(item);

                if (Directory.Exists(item))
                {
                    entries.Add($"[目录] {name}");
                    continue;
                }

                var size = new FileInfo(item).Length;
                entries.Add($"[文件] {name} ({size} 字节)");
            }

            return entries.Count == 0 ? "（空目录）" : string.Join('\n', entries);
        }

        public string ReadFile(
            [Description("相对工作区的文件路径。")] string path)
        {
            var file = ResolveReadPath(path);
            if (!File.Exists(file))
            {
                return $"文件不存在：{path}";
            }

            var info = new FileInfo(file);
            if (info.Length > MaxReadBytes)
            {
                return $"文件过大（{info.Length} 字节），只支持读取 64KB 以内的文本文件。";
            }

            return Trim(File.ReadAllText(file));
        }

        public async Task<string> WriteFileAsync(
            [Description("相对工作区的文件路径；父目录不存在时会自动创建。")] string path,
            [Description("要写入的完整文本内容。")] string content,
            CancellationToken cancellationToken)
        {
            var file = ResolveWorkspacePath(path, allowRoot: false);

            // 写文件有副作用：默认先请用户确认（可在「设置 → 工具设置」里关掉）。
            if (context.Settings.Current.ConfirmFileWrite)
            {
                var decision = await context.Approvals.RequestAsync(
                    new ToolApprovalRequest(
                        ToolId: "write_file",
                        Kind: ToolApprovalKind.FileWrite,
                        Title: "允许写入这个文件吗？",
                        Summary: $"路径：{NormalizeRelative(path)}",
                        Detail: content,
                        Risk: AgentToolRisk.Medium,
                        AllowSessionRemember: false,
                        SessionKey: null),
                    cancellationToken).ConfigureAwait(false);

                if (decision == ToolApprovalDecision.Deny)
                {
                    return "用户拒绝了这次文件写入，未做任何修改。";
                }
            }

            var directory = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 同目录临时文件再原子替换：中途失败不会留下半截内容。
            var temporary = $"{file}.tmp-{Guid.NewGuid():N}";
            await File.WriteAllTextAsync(temporary, content, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, file, overwrite: true);

            return $"已写入 {NormalizeRelative(path)}（{content.Length} 字符）。";
        }

        private string ResolveReadPath(string relative)
        {
            var roots = new List<string> { context.WorkspaceRoot };
            roots.AddRange(context.ReadOnlyRoots.Where(root => !string.IsNullOrWhiteSpace(root)));

            InvalidOperationException? last = null;

            foreach (var root in roots)
            {
                try
                {
                    var candidate = ResolveUnder(root, relative, allowRoot: true);
                    if (File.Exists(candidate) || Directory.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (InvalidOperationException ex)
                {
                    last = ex;
                }
            }

            return last is not null
                ? throw last
                : throw new InvalidOperationException($"路径不在允许的工作区内：{relative}");
        }

        private string ResolveWorkspacePath(string relative, bool allowRoot) =>
            ResolveUnder(context.WorkspaceRoot, relative, allowRoot);

        private static string ResolveUnder(string root, string? relative, bool allowRoot)
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var value = string.IsNullOrWhiteSpace(relative) ? "." : relative.Trim();

            if (value.Contains('\0'))
            {
                throw new InvalidOperationException("路径包含非法字符。");
            }

            if (value.StartsWith(@"\\", StringComparison.Ordinal)
                || value.StartsWith("//", StringComparison.Ordinal)
                || value.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"拒绝 UNC / 设备路径：{relative}");
            }

            if (Path.IsPathRooted(value))
            {
                throw new InvalidOperationException($"拒绝绝对路径：{relative}");
            }

            var segments = value
                .Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Where(segment => segment != ".")
                .ToArray();

            if (segments.Any(segment => segment == ".."))
            {
                throw new InvalidOperationException($"拒绝包含 .. 的路径：{relative}");
            }

            if (!allowRoot && segments.Length == 0)
            {
                throw new InvalidOperationException("必须指定具体文件路径，不能是工作区根目录。");
            }

            var full = Path.GetFullPath(
                Path.Combine(normalizedRoot, string.Join(Path.DirectorySeparatorChar, segments)));

            if (!SandboxPathGuard.IsUnder(normalizedRoot, full))
            {
                throw new InvalidOperationException($"路径超出工作区范围：{relative}");
            }

            EnsureNoReparsePoint(normalizedRoot, segments);
            return full;
        }

        /// <summary>
        /// 逐段检查重解析点：符号链接 / junction 可能把真实目标指到工作区外，
        /// 只用字符串前缀判断不够（§3.8）。
        /// </summary>
        private static void EnsureNoReparsePoint(string normalizedRoot, IReadOnlyList<string> segments)
        {
            var current = normalizedRoot;

            foreach (var segment in segments)
            {
                current = Path.Combine(current, segment);

                if (!File.Exists(current) && !Directory.Exists(current))
                {
                    // 后面不存在的目录 / 文件由调用方处理；已存在的部分都已经检查过。
                    return;
                }

                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException("路径包含符号链接或 junction，已拒绝。");
                }
            }
        }

        private static string NormalizeRelative(string path) =>
            path.Replace('\\', '/').TrimStart('/');

        private static string Trim(string text, int max = MaxResultChars) =>
            text.Length <= max ? text : string.Concat(text.AsSpan(0, max), "\n…（已截断）");
    }
}
