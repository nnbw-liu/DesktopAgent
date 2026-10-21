using System.IO;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace SDemo.DesktopAgent.Platform.Windows.Sandbox;

/// <summary>
/// 沙箱目录布局：<c>{root}\host</c>（SandboxHost 自包含发布）与 <c>{root}\jobs\{jobId}\</c>。
/// 负责创建 job 目录并施加最小 ACL；路径校验保证工具不能把请求指向沙箱外。
/// </summary>
public sealed class SandboxDirectory
{
    public const string HostFolderName = "host";
    public const string JobsFolderName = "jobs";
    public const string BaseFolderName = "base";
    public const string InputFolderName = "input";
    public const string WorkFolderName = "work";
    public const string HomeFolderName = "home";
    public const string OutputFolderName = "output";

    private static readonly Regex JobIdPattern = new("^[A-Za-z0-9-]{8,64}$", RegexOptions.Compiled);

    public SandboxDirectory(string root) => Root = Path.GetFullPath(root);

    public string Root { get; }

    public string HostDirectory => Path.Combine(Root, HostFolderName);

    public string JobsDirectory => Path.Combine(Root, JobsFolderName);

    public static bool IsValidJobId(string? jobId) =>
        !string.IsNullOrEmpty(jobId) && JobIdPattern.IsMatch(jobId);

    public SandboxJobLayout GetLayout(string jobId)
    {
        if (!IsValidJobId(jobId))
        {
            throw new ArgumentException("jobId 必须是 8–64 个字母 / 数字 / 连字符。", nameof(jobId));
        }

        var jobRoot = Path.Combine(JobsDirectory, jobId);
        return new SandboxJobLayout(
            jobId,
            jobRoot,
            Path.Combine(jobRoot, InputFolderName),
            Path.Combine(jobRoot, WorkFolderName),
            Path.Combine(jobRoot, HomeFolderName),
            Path.Combine(jobRoot, OutputFolderName));
    }

    /// <summary>创建根目录 / jobs / host，并给这些目录加上 Package SID 的穿越 / 读执行权限。</summary>
    public void EnsureRoot(SecurityIdentifier sid)
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(JobsDirectory);
        Directory.CreateDirectory(HostDirectory);

        EnsureAncestorsTraverse(sid);
        SandboxAcl.GrantTraverse(Root, sid);
        SandboxAcl.GrantTraverse(JobsDirectory, sid);
        SandboxAcl.GrantReadExecute(HostDirectory, sid);
    }

    /// <summary>创建 job 目录并施加 ACL，返回布局。重复调用是幂等的。</summary>
    public SandboxJobLayout EnsureJob(string jobId, SecurityIdentifier sid)
    {
        var layout = GetLayout(jobId);
        Directory.CreateDirectory(layout.Root);
        Directory.CreateDirectory(layout.InputDirectory);
        Directory.CreateDirectory(layout.WorkDirectory);
        Directory.CreateDirectory(layout.HomeDirectory);
        Directory.CreateDirectory(layout.OutputDirectory);

        SandboxAcl.GrantTraverse(layout.Root, sid);
        SandboxAcl.GrantReadExecute(layout.InputDirectory, sid);
        SandboxAcl.GrantModify(layout.WorkDirectory, sid);
        SandboxAcl.GrantModify(layout.HomeDirectory, sid);
        SandboxAcl.GrantModify(layout.OutputDirectory, sid);

        // AppContainer 是 Low integrity，必须把可写目录也降为 Low，否则 ACL 通过但写入仍被拒绝。
        SandboxAcl.SetLowIntegrity(layout.WorkDirectory);
        SandboxAcl.SetLowIntegrity(layout.HomeDirectory);
        SandboxAcl.SetLowIntegrity(layout.OutputDirectory);

        // home 下的 AppData / temp 必须在 home 的继承 ACE 之后创建，否则拿不到继承权限。
        Directory.CreateDirectory(Path.Combine(layout.HomeDirectory, "AppData", "Local"));
        Directory.CreateDirectory(Path.Combine(layout.HomeDirectory, "AppData", "Roaming"));
        Directory.CreateDirectory(Path.Combine(layout.HomeDirectory, "temp"));

        return layout;
    }

    /// <summary>把路径规范化为绝对路径，并判断是否位于沙箱根目录内。</summary>
    public bool IsUnderRoot(string path, out string normalizedPath)
    {
        try
        {
            normalizedPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            normalizedPath = string.Empty;
            return false;
        }

        return normalizedPath.Equals(Root, StringComparison.OrdinalIgnoreCase)
               || normalizedPath.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public void DeleteJob(string jobId)
    {
        var layout = GetLayout(jobId);
        try
        {
            if (Directory.Exists(layout.Root))
            {
                Directory.Delete(layout.Root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响结果；S6 由 SandboxJanitor 统一处理。
        }
    }

    /// <summary>
    /// 沙箱根目录的上级（%LOCALAPPDATA%\SDemo、DesktopAgent）只加“穿越 + 读属性”，
    /// 不加读取 / 列举 / 写入；权限不足时不强改，交给启动失败的诊断报告。
    /// </summary>
    private void EnsureAncestorsTraverse(SecurityIdentifier sid)
    {
        var directory = Directory.GetParent(Root);
        for (var depth = 0; depth < 2 && directory is not null; depth++)
        {
            var path = directory.FullName;
            if (path.Equals(Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            try
            {
                SandboxAcl.GrantTraverse(path, sid);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                break;
            }

            directory = directory.Parent;
        }
    }
}

/// <summary>一个 job 的目录约定；所有路径都是绝对路径。</summary>
public sealed record SandboxJobLayout(
    string JobId,
    string Root,
    string InputDirectory,
    string WorkDirectory,
    string HomeDirectory,
    string OutputDirectory);
