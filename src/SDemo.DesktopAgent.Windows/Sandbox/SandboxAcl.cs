using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SDemo.DesktopAgent.Platform.Windows.Sandbox;

/// <summary>
/// 只给沙箱目录授予“够用”的 ACL：
/// 根 / job 根只给穿越；input 与 host 给读+执行；work / home / output 给修改。
/// 不授予 Network / 凭据等 capability，也不动工作区本身的 ACL。
/// </summary>
public static class SandboxAcl
{
    public static void GrantTraverse(string path, SecurityIdentifier sid) =>
        Grant(path, sid, FileSystemRights.Traverse | FileSystemRights.ReadAttributes, inherit: false);

    public static void GrantReadExecute(string path, SecurityIdentifier sid) =>
        Grant(path, sid, FileSystemRights.ReadAndExecute, inherit: true);

    public static void GrantModify(string path, SecurityIdentifier sid) =>
        Grant(path, sid, FileSystemRights.Modify, inherit: true);

    /// <summary>添加一条显式 ACE；同样的身份 / 继承方式已存在时不重复添加（避免 ACL 膨胀）。</summary>
    internal static void Grant(string path, SecurityIdentifier sid, FileSystemRights rights, bool inherit)
    {
        var info = new DirectoryInfo(path);
        var security = info.GetAccessControl();
        var inheritance = inherit
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None;

        if (HasRule(security, sid, inheritance))
        {
            return;
        }

        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            rights,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    /// <summary>读取目录上给指定 SID 的显式 / 继承 ACE（测试与诊断用）。</summary>
    public static IReadOnlyList<FileSystemAccessRule> GetRules(string path, SecurityIdentifier sid)
    {
        var security = new DirectoryInfo(path).GetAccessControl();
        return security
            .GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => rule.IdentityReference is SecurityIdentifier identity && identity.Equals(sid))
            .ToArray();
    }

    private static bool HasRule(DirectorySecurity security, SecurityIdentifier sid, InheritanceFlags inheritance)
    {
        foreach (AuthorizationRule rule in security
                     .GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier)))
        {
            if (rule is FileSystemAccessRule fileRule
                && fileRule.IdentityReference is SecurityIdentifier identity
                && identity.Equals(sid)
                && fileRule.AccessControlType == AccessControlType.Allow
                && fileRule.InheritanceFlags == inheritance)
            {
                return true;
            }
        }

        return false;
    }
}
