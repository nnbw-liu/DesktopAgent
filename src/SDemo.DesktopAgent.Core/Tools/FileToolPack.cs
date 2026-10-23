using System.ComponentModel;
using System.IO;
using System.Text;
using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Editing;
using SDemo.DesktopAgent.Sandbox.Protocol;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 只读文件工具包：按行读取、目录列表 / 目录树、正则搜索、glob。
/// 所有路径都过 <see cref="WorkspacePath"/>（拒绝绝对路径 / UNC / <c>..</c> / 重解析点），
/// 不写盘；写入在 <see cref="EditToolPack"/>。
/// </summary>
public sealed class FileToolPack : IAgentToolPack
{
    public const int DefaultReadLimit = 400;
    public const int MaxReadLimit = 2000;
    public const int MaxResultChars = 30_000;
    public const long MaxReadFileBytes = 8L * 1024 * 1024;

    private readonly WorkspaceSearch _search = new();

    public AgentToolPackDescriptor Descriptor { get; } = new(
        Id: "files",
        Name: "文件（只读）",
        Description: "按行读取、搜索和枚举当前工作区；引用工作区外的路径会被拒绝。",
        Glyph: "▤",
        Risk: AgentToolRisk.Low,
        Order: 20,
        DefaultEnabled: true,
        WindowsOnly: false,
        Functions:
        [
            new AgentToolFunctionDescriptor("list_files", "列出目录"),
            new AgentToolFunctionDescriptor("list_tree", "目录树"),
            new AgentToolFunctionDescriptor("read_file", "按行读取"),
            new AgentToolFunctionDescriptor("search_files", "正则搜索"),
            new AgentToolFunctionDescriptor("glob_files", "路径匹配"),
            new AgentToolFunctionDescriptor("workspace_info", "工作区信息")
        ],
        BuiltIn: true);

    public IReadOnlyList<AITool> Create(ToolContext context)
    {
        var functions = new FileFunctions(context, _search);

        return
        [
            AIFunctionFactory.Create(
                functions.ListFiles,
                name: "list_files",
                description: $"列出工作区（{context.WorkspaceRoot}）某个目录的直接子项，path 为相对路径。"),

            AIFunctionFactory.Create(
                functions.ListTree,
                name: "list_tree",
                description: "列出相对路径的目录树，depth 默认 2、最多 200 项。"),

            AIFunctionFactory.Create(
                functions.ReadFile,
                name: "read_file",
                description: "按行读取工作区内的文本文件；offset 从 1 开始，limit 默认 400（最多 2000）。先读再改。"),

            AIFunctionFactory.Create(
                functions.SearchFiles,
                name: "search_files",
                description: "按正则搜索工作区文本，返回 路径:行号: 内容；glob 可限定文件类型。"),

            AIFunctionFactory.Create(
                functions.GlobFiles,
                name: "glob_files",
                description: "按 glob（** / * / ?）返回匹配的文件路径，按路径排序。"),

            AIFunctionFactory.Create(
                functions.WorkspaceInfo,
                name: "workspace_info",
                description: "获取工作区根目录路径，用于说明文件位置。")
        ];
    }

    private sealed class FileFunctions(ToolContext context, WorkspaceSearch search)
    {
        public string WorkspaceInfo()
        {
            var builder = new StringBuilder();
            builder.Append("工作区根目录：").Append(context.WorkspaceRoot);

            var workspace = FindWorkspace();
            if (workspace is not null)
            {
                if (!string.IsNullOrWhiteSpace(workspace.BuildCommand))
                {
                    builder.Append("\n构建命令：").Append(workspace.BuildCommand);
                }

                if (!string.IsNullOrWhiteSpace(workspace.TestCommand))
                {
                    builder.Append("\n测试命令：").Append(workspace.TestCommand);
                }

                if (!string.IsNullOrWhiteSpace(workspace.BuildCommand)
                    || !string.IsNullOrWhiteSpace(workspace.TestCommand))
                {
                    builder.Append("\n（来自工作区设置，可直接用于 run_command）");
                }
            }

            return builder.ToString();
        }

        private SDemo.DesktopAgent.Models.Workspace? FindWorkspace()
        {
            try
            {
                var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.WorkspaceRoot));
                return context.Settings.Current.Workspaces.FirstOrDefault(workspace =>
                {
                    try
                    {
                        return string.Equals(
                            Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace.Path)),
                            root,
                            StringComparison.OrdinalIgnoreCase);
                    }
                    catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
                    {
                        return false;
                    }
                });
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

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

        public string ListTree(
            [Description("相对工作区的目录路径，默认当前目录。")] string path = ".",
            [Description("展开深度，默认 2。")] int depth = 2,
            [Description("最多显示多少项，默认 200。")] int maxEntries = 200)
        {
            ResolveReadPath(path);
            return search.ListTree(context.WorkspaceRoot, path, depth, maxEntries);
        }

        public string ReadFile(
            [Description("相对工作区的文件路径。")] string path,
            [Description("起始行号，从 1 开始；默认 1。")] int offset = 1,
            [Description("本次最多读取多少行，默认 400，最多 2000。")] int limit = DefaultReadLimit)
        {
            var file = ResolveReadPath(path);
            if (!File.Exists(file))
            {
                return $"文件不存在：{path}";
            }

            var info = new FileInfo(file);
            if (info.Length > MaxReadFileBytes)
            {
                return $"文件过大（{info.Length} 字节），读取上限为 {MaxReadFileBytes / 1024 / 1024}MB。";
            }

            if (WorkspacePath.LooksBinary(file))
            {
                return "看起来是二进制文件，不做文本读取。";
            }

            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"读取失败：{ex.Message}";
            }

            var all = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var lineCount = all.Length > 0 && all[^1].Length == 0 ? all.Length - 1 : all.Length;
            if (lineCount == 0)
            {
                RecordRead(file);
                return $"# {WorkspacePath.NormalizeRelative(path)} · 空文件";
            }

            var start = Math.Clamp(offset, 1, lineCount);
            var count = Math.Clamp(limit, 1, MaxReadLimit);
            var end = Math.Min(lineCount, start + count - 1);

            var builder = new StringBuilder();
            builder.Append("# ")
                .Append(WorkspacePath.NormalizeRelative(path))
                .Append(" · 共 ")
                .Append(lineCount)
                .Append(" 行 · 本次 ")
                .Append(start)
                .Append('–')
                .Append(end);

            if (end < lineCount)
            {
                builder.Append(" · 接续用 offset=").Append(end + 1);
            }

            var chars = 0;
            var last = end;

            for (var index = start; index <= end; index++)
            {
                var line = all[index - 1];
                if (line.Length > 500)
                {
                    line = string.Concat(line.AsSpan(0, 500), "…");
                }

                var rendered = $"{index.ToString().PadLeft(4)}| {line}";
                if (chars + rendered.Length + 1 > MaxResultChars)
                {
                    last = index - 1;
                    builder.Append("\n…（本次结果超过 ")
                        .Append(MaxResultChars)
                        .Append(" 字符，已截断；接续用 offset=")
                        .Append(index)
                        .Append('）');
                    break;
                }

                builder.Append('\n').Append(rendered);
                chars += rendered.Length + 1;
            }

            if (last < start)
            {
                last = start;
            }

            if (last < lineCount && builder.ToString().Contains("接续用 offset=", StringComparison.Ordinal) == false)
            {
                builder.Append("\n…（接续用 offset=").Append(last + 1).Append('）');
            }

            RecordRead(file);
            return builder.ToString();
        }

        public string SearchFiles(
            [Description("正则表达式。")] string pattern,
            [Description("文件 glob，默认 **/*。")] string glob = "**/*",
            [Description("最多返回多少条，默认 80。")] int maxResults = WorkspaceSearch.MaxResultLines,
            [Description("命中行前后各带几行上下文，默认 0。")] int contextLines = 0,
            [Description("是否区分大小写，默认 false。")] bool caseSensitive = false)
        {
            var result = search.Search(context.WorkspaceRoot, pattern, glob, maxResults, contextLines, caseSensitive);

            foreach (var relative in result.MatchedPaths)
            {
                var full = WorkspacePath.Resolve(context.WorkspaceRoot, relative, allowRoot: false);
                var hash = FileReadRegistry.HashFile(full);
                if (hash is not null)
                {
                    context.Reads?.RecordRead(context.SessionId, relative, hash);
                }
            }

            return result.Text;
        }

        public string GlobFiles(
            [Description("glob，例如 **/*.cs。")] string pattern,
            [Description("最多返回多少条，默认 200。")] int maxResults = 200)
        {
            var paths = search.Glob(context.WorkspaceRoot, pattern, maxResults);
            return paths.Count == 0
                ? "未找到匹配的路径；建议放宽 glob。"
                : string.Join('\n', paths);
        }

        private void RecordRead(string fullPath)
        {
            if (context.Reads is null || !SandboxPathGuard.IsUnder(context.WorkspaceRoot, fullPath))
            {
                return;
            }

            var relative = WorkspacePath.Relative(context.WorkspaceRoot, fullPath);
            var hash = FileReadRegistry.HashFile(fullPath);
            if (hash is not null)
            {
                context.Reads.RecordRead(context.SessionId, relative, hash);
            }
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
                    var candidate = WorkspacePath.Resolve(root, relative, allowRoot: true);
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
    }
}
