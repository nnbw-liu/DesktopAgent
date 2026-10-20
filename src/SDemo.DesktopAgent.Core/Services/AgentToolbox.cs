using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace SDemo.DesktopAgent.Services;

/// <summary>输入框底部的工具开关。</summary>
public enum AgentSkill
{
    /// <summary>联网搜索。</summary>
    Search,

    /// <summary>工作区文件读写（写文件仍需确认）。</summary>
    Files
}

/// <summary>
/// 提供给 Agent 的工具集合。工具默认全部关闭，由输入框里的 chip 按需启用。
/// 文件工具只能访问工作区目录，避免误改系统文件；写文件默认要用户确认。
/// </summary>
public sealed class AgentToolbox
{
    private const int MaxToolResultChars = 8000;

    private readonly ISearchService _search;
    private readonly ISettingsService _settings;
    private readonly FileWriteApprovalService _approvals;
    private readonly WorkspaceContext _workspace;

    public AgentToolbox(
        ISearchService search,
        ISettingsService settings,
        FileWriteApprovalService approvals,
        WorkspaceContext workspace)
    {
        _search = search;
        _settings = settings;
        _approvals = approvals;
        _workspace = workspace;
    }

    /// <summary>文件工具的根目录 = 当前聊天室所属工作区。</summary>
    public string WorkspaceRoot => _workspace.RootPath;

    public IList<AITool> Create(IReadOnlySet<AgentSkill> enabled, AgentRunMetrics? metrics = null)
    {
        var tools = new List<AITool>();

        if (enabled.Contains(AgentSkill.Files))
        {
            tools.Add(Track(AIFunctionFactory.Create(
                ListFiles,
                name: "list_files",
                description: $"列出工作区（{WorkspaceRoot}）内的文件，path 为相对路径，默认当前目录。"), metrics));

            tools.Add(Track(AIFunctionFactory.Create(
                ReadFile,
                name: "read_file",
                description: "读取工作区内的文本文件内容，path 为相对路径。"), metrics));

            tools.Add(Track(AIFunctionFactory.Create(
                WriteFile,
                name: "write_file",
                description: "把文本写入工作区内的文件（会覆盖同名文件），path 为相对路径，content 为完整内容。"), metrics));

            tools.Add(Track(AIFunctionFactory.Create(
                WorkspaceInfo,
                name: "workspace_info",
                description: "获取工作区根目录路径，用于向用户说明文件保存位置。"), metrics));
        }

        if (enabled.Contains(AgentSkill.Search))
        {
            tools.Add(Track(AIFunctionFactory.Create(
                WebSearch,
                name: "web_search",
                description: "联网搜索，返回若干条标题、链接和摘要。"), metrics));
        }

        return tools;
    }

    /// <summary>包一层计数，记录每个工具实际被调用了多少次（不管框架是否把 tool_calls 透出来）。</summary>
    private static AITool Track(AITool tool, AgentRunMetrics? metrics) =>
        metrics is null || tool is not AIFunction function
            ? tool
            : new TrackingAIFunction(function, metrics);

    private sealed class TrackingAIFunction(AIFunction inner, AgentRunMetrics metrics) : AIFunction
    {
        public override string Name => inner.Name;

        public override string Description => inner.Description;

        public override JsonElement JsonSchema => inner.JsonSchema;

        public override IReadOnlyDictionary<string, object?> AdditionalProperties => inner.AdditionalProperties;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            metrics.RecordToolCall(inner.Name);
            return inner.InvokeAsync(arguments, cancellationToken);
        }
    }

    private string WorkspaceInfo() => $"工作区根目录：{WorkspaceRoot}";

    private string ListFiles(string path = ".")
    {
        var directory = ResolveWorkspacePath(path);
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

    private string ReadFile(string path)
    {
        var file = ResolveWorkspacePath(path);
        if (!File.Exists(file))
        {
            return $"文件不存在：{path}";
        }

        var info = new FileInfo(file);
        if (info.Length > 64 * 1024)
        {
            return $"文件过大（{info.Length} 字节），只支持读取 64KB 以内的文本文件。";
        }

        return Trim(File.ReadAllText(file));
    }

    private async Task<string> WriteFile(string path, string content, CancellationToken cancellationToken)
    {
        var file = ResolveWorkspacePath(path);

        // 写文件有副作用：默认先请用户确认（可在「设置 → 工具设置」里关掉）。
        if (_settings.Current.ConfirmFileWrite)
        {
            var approved = await _approvals.RequestAsync(new FileWriteRequest(path, content), cancellationToken);
            if (!approved)
            {
                return "用户拒绝了这次文件写入，未做任何修改。";
            }
        }

        var directory = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(file, content, Encoding.UTF8, cancellationToken);
        return $"已写入 {path}（{content.Length} 字符）。";
    }

    private Task<string> WebSearch(string query, CancellationToken cancellationToken) =>
        _search.SearchAsync(query, cancellationToken);

    /// <summary>把相对路径限制在工作区内，防止 ../ 越界。</summary>
    private string ResolveWorkspacePath(string relative)
    {
        var root = Path.GetFullPath(WorkspaceRoot);
        var candidate = string.IsNullOrWhiteSpace(relative) ? "." : relative;
        var full = Path.GetFullPath(Path.Combine(root, candidate));

        if (!string.Equals(full, root, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"路径超出工作区范围：{relative}");
        }

        return full;
    }

    private static string Trim(string text, int max = MaxToolResultChars) =>
        text.Length <= max ? text : text[..max] + "\n…（已截断）";
}
