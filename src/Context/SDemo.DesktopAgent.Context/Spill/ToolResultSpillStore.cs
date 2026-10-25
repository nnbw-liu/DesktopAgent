using System.Security.Cryptography;
using System.Text;

namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 一次工具输出落盘的结果（P2c）。只描述「存到哪儿、原来多长」，预览与取回指引由调用方拼。
/// </summary>
/// <param name="RelativePath">相对工作区的路径；模型用 <c>read_file</c> 读它。</param>
/// <param name="FullPath">宿主侧绝对路径（诊断 / 测试用，不给模型）。</param>
/// <param name="TotalChars">原文长度（字符）。</param>
public sealed record ToolResultSpill(string RelativePath, string FullPath, int TotalChars);

/// <summary>
/// 大工具输出的存储 seam（照 DSH 的 spill）：全文存起来，上下文里换成「头尾预览 + 位置 + 取回指引」。
/// <para>
/// 必须是**尽力而为**：<see cref="Save"/> 返回 <c>null</c> 表示这次没存下，调用方退回纯截断——
/// 一次成功的工具调用不能因为写盘失败而变成错误（§1.6）。
/// </para>
/// </summary>
public interface IToolResultSpillStore
{
    /// <summary>把全文存起来；失败返回 <c>null</c>。</summary>
    ToolResultSpill? Save(string callId, string text);
}

/// <summary>
/// 落盘到工作区内的 <c>.agent/spill</c>：随工作区移动，沙箱副本也看得到，
/// 而且模型能用现成的 <c>read_file</c> 按行回读（不需要给工具面加新工具）。
/// <para>
/// 两条副作用都处理掉了：目录里写一份 <c>.gitignore</c>（内容是 <c>*</c>），
/// 免得几百个日志文件污染用户的 <c>git status</c>；文件数超过 <see cref="MaxFiles"/> 时
/// 按最后写入时间清理最旧的那批，长期会话不会无限长。
/// </para>
/// </summary>
public sealed class WorkspaceToolResultSpillStore : IToolResultSpillStore
{
    /// <summary>工作区内的落盘目录（相对路径，统一用 <c>/</c>）。</summary>
    public const string RelativeDirectory = ".agent/spill";

    /// <summary>保留的日志文件上限；超出后按最后写入时间清理最旧的。</summary>
    public const int MaxFiles = 200;

    private const string DirectoryName = ".agent";
    private const string SubDirectoryName = "spill";
    private const string GitIgnoreName = ".gitignore";

    private readonly string _root;

    public WorkspaceToolResultSpillStore(string workspaceRoot)
    {
        _root = workspaceRoot ?? string.Empty;
    }

    public ToolResultSpill? Save(string callId, string text)
    {
        if (string.IsNullOrWhiteSpace(_root) || string.IsNullOrEmpty(text))
        {
            return null;
        }

        try
        {
            var directory = Path.Combine(_root, DirectoryName, SubDirectoryName);
            Directory.CreateDirectory(directory);
            EnsureIgnored(directory);

            var full = Path.Combine(directory, FileName(callId, text));

            // 同一次生成里每次请求都会走到这里：内容没变就不重写（幂等，也避免刷新时间戳）。
            var bytes = Encoding.UTF8.GetBytes(text);

            if (!File.Exists(full) || new FileInfo(full).Length != bytes.Length)
            {
                File.WriteAllBytes(full, bytes);
            }

            Prune(directory);

            return new ToolResultSpill(
                $"{RelativeDirectory}/{Path.GetFileName(full)}",
                full,
                text.Length);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException)
        {
            // 只读工作区 / 路径过长 / 权限不足：落盘失败不是工具失败。
            return null;
        }
    }

    /// <summary>
    /// 文件名只用 <c>callId</c>：同一个调用落一次，重复请求命中同一个文件。
    /// 没有 callId 或全是非法字符时退回内容 hash，仍然稳定。
    /// </summary>
    internal static string FileName(string callId, string text)
    {
        var safe = new StringBuilder(64);

        foreach (var character in callId ?? string.Empty)
        {
            if (char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
            {
                safe.Append(character);
            }
            else if (safe.Length > 0 && safe[^1] != '_')
            {
                safe.Append('_');
            }

            if (safe.Length >= 80)
            {
                break;
            }
        }

        var name = safe.ToString().Trim('.', '_');
        if (name.Length == 0)
        {
            name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
        }

        return name + ".log";
    }

    /// <summary>在工作区里留一份 <c>.agent/.gitignore</c>，内容 <c>*</c>：整棵目录树对 git 不可见。</summary>
    private static void EnsureIgnored(string spillDirectory)
    {
        var root = Path.GetDirectoryName(spillDirectory);
        if (string.IsNullOrEmpty(root))
        {
            return;
        }

        var path = Path.Combine(root, GitIgnoreName);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, "*\n", new UTF8Encoding(false));
        }
    }

    private static void Prune(string directory)
    {
        try
        {
            var files = new DirectoryInfo(directory).GetFiles("*.log");
            if (files.Length <= MaxFiles)
            {
                return;
            }

            foreach (var stale in files
                         .OrderByDescending(file => file.LastWriteTimeUtc)
                         .Skip(MaxFiles))
            {
                stale.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响这一次落盘。
        }
    }
}
