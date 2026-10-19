namespace SDemo.DesktopAgent.Models;

/// <summary>工作区：一个可以放聊天室与文件的本地目录。</summary>
public sealed class Workspace
{
    public required string Id { get; init; }

    public string Name { get; set; } = string.Empty;

    /// <summary>工作区目录（重命名不会改这里）。</summary>
    public string Path { get; set; } = string.Empty;
}
