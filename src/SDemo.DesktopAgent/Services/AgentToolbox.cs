using System.IO;
using System.Text;
using Microsoft.Extensions.AI;

namespace SDemo.DesktopAgent.Services;

/// <summary>输入框底部的三个技能开关。</summary>
public enum AgentSkill
{
    /// <summary>本地技能库：读取 skills 目录里的 Markdown 技能。</summary>
    Skills,

    /// <summary>联网搜索。</summary>
    Search,

    /// <summary>工作区文件读写。</summary>
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

        SkillsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SDemo",
            "DesktopAgent",
            "skills");
    }

    /// <summary>文件工具的根目录 = 当前聊天室所属工作区。</summary>
    public string WorkspaceRoot => _workspace.RootPath;

    public string SkillsRoot { get; }

    public IList<AITool> Create(IReadOnlySet<AgentSkill> enabled)
    {
        var tools = new List<AITool>();

        if (enabled.Contains(AgentSkill.Files))
        {
            tools.Add(AIFunctionFactory.Create(
                ListFiles,
                name: "list_files",
                description: $"列出工作区（{WorkspaceRoot}）内的文件，path 为相对路径，默认当前目录。"));

            tools.Add(AIFunctionFactory.Create(
                ReadFile,
                name: "read_file",
                description: "读取工作区内的文本文件内容，path 为相对路径。"));

            tools.Add(AIFunctionFactory.Create(
                WriteFile,
                name: "write_file",
                description: "把文本写入工作区内的文件（会覆盖同名文件），path 为相对路径，content 为完整内容。"));

            tools.Add(AIFunctionFactory.Create(
                WorkspaceInfo,
                name: "workspace_info",
                description: "获取工作区根目录路径，用于向用户说明文件保存位置。"));
        }

        if (enabled.Contains(AgentSkill.Search))
        {
            tools.Add(AIFunctionFactory.Create(
                WebSearch,
                name: "web_search",
                description: "联网搜索，返回若干条标题、链接和摘要。"));
        }

        if (enabled.Contains(AgentSkill.Skills))
        {
            tools.Add(AIFunctionFactory.Create(
                ListSkills,
                name: "list_skills",
                description: "列出本地技能库中可用的技能名称与说明。"));

            tools.Add(AIFunctionFactory.Create(
                ReadSkill,
                name: "read_skill",
                description: "读取某个技能的完整说明（Markdown），按其中的步骤执行任务。"));
        }

        return tools;
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

    private string ListSkills()
    {
        if (!Directory.Exists(SkillsRoot))
        {
            return $"技能库为空。把 Markdown 技能文件放到：{SkillsRoot}";
        }

        var files = Directory.EnumerateFiles(SkillsRoot, "*.md").OrderBy(name => name).ToList();
        if (files.Count == 0)
        {
            return $"技能库为空。把 Markdown 技能文件放到：{SkillsRoot}";
        }

        var builder = new StringBuilder();

        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var description = File.ReadLines(file)
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.Length > 0 && !line.StartsWith('#')) ?? string.Empty;

            builder.AppendLine($"- {name}：{Trim(description, 120)}");
        }

        return builder.ToString().TrimEnd();
    }

    private string ReadSkill(string skillName)
    {
        var name = Path.GetFileNameWithoutExtension(skillName.Trim());
        var file = Path.Combine(SkillsRoot, name + ".md");

        if (!File.Exists(file))
        {
            return $"技能不存在：{skillName}";
        }

        return Trim(File.ReadAllText(file), 32 * 1024);
    }

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
