using System.Text.Json;
using SDemo.DesktopAgent.Sandbox.Protocol;

namespace SDemo.DesktopAgent.Sandbox;

/// <summary>工作区快照里的一条文件记录。路径统一使用 <c>/</c> 分隔。</summary>
public sealed record WorkspaceManifestEntry(
    string Path,
    long Length,
    DateTime LastWriteUtc,
    string? Sha256,
    bool Applied = false);

/// <summary>
/// 宿主侧的 manifest：记录快照时工作区的文件清单，供 diff 与冲突检测使用。
/// 放在 job 根目录，沙箱进程没有读取权限。
/// </summary>
public sealed record WorkspaceManifest
{
    public int ProtocolVersion { get; init; } = 1;

    public IReadOnlyList<WorkspaceManifestEntry> Entries { get; init; } = [];

    /// <summary>因符号链接 / junction 而被跳过的相对路径。</summary>
    public IReadOnlyList<string> Skipped { get; init; } = [];

    public static WorkspaceManifest Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<WorkspaceManifest>(json, SandboxJson.Options)
               ?? throw new InvalidDataException($"manifest 为空或格式不正确：{path}");
    }

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, SandboxJson.Options);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json, new System.Text.UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }
}
