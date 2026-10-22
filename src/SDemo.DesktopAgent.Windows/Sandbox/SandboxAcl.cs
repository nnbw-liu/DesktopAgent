using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
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

    /// <summary>
    /// 把目录标记为 Low integrity，并让子对象继承。
    /// AppContainer 进程默认以 Low integrity 运行；只加 ACL 不降低标签时，写操作仍会被
    /// 强制完整性策略拒绝（S1 的 work 目录未测写，S2 端到端才暴露）。
    /// </summary>
    public static void SetLowIntegrity(string path)
    {
        IntPtr lowSid = IntPtr.Zero;
        IntPtr acl = IntPtr.Zero;
        try
        {
            if (!NativeMethods.ConvertStringSidToSid("S-1-16-4096", out lowSid))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建 Low integrity SID。");
            }

            var sidLength = NativeMethods.GetLengthSid(lowSid);
            var aclSize = 8 + 8 + sidLength;
            acl = Marshal.AllocHGlobal(aclSize);

            // 手工构造 SYSTEM_MANDATORY_LABEL_ACE（type 0x11）。不能用 AddAccessAllowedAceEx：
            // 那会产生普通 ACCESS_ALLOWED_ACE，SetNamedSecurityInfo 会忽略它。
            var aclHeader = new byte[aclSize];
            aclHeader[0] = NativeMethods.AclRevision;
            aclHeader[1] = 0;
            BitConverter.GetBytes((ushort)aclSize).CopyTo(aclHeader, 2);
            BitConverter.GetBytes((ushort)0).CopyTo(aclHeader, 4);
            BitConverter.GetBytes((ushort)(8 + sidLength)).CopyTo(aclHeader, 6);

            const int aceOffset = 8;
            aclHeader[aceOffset] = 0x11;
            aclHeader[aceOffset + 1] = (byte)(NativeMethods.ContainerInheritAce | NativeMethods.ObjectInheritAce);
            BitConverter.GetBytes((ushort)(8 + sidLength)).CopyTo(aclHeader, aceOffset + 2);
            BitConverter.GetBytes(NativeMethods.SystemMandatoryLabelNoWriteUp).CopyTo(aclHeader, aceOffset + 4);

            var sidBytes = new byte[sidLength];
            Marshal.Copy(lowSid, sidBytes, 0, sidLength);
            sidBytes.CopyTo(aclHeader, aceOffset + 8);
            Marshal.Copy(aclHeader, 0, acl, aclSize);

            var error = NativeMethods.SetNamedSecurityInfo(
                path,
                NativeMethods.SeFileObject,
                NativeMethods.LabelSecurityInformation,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                acl);

            if (error != 0)
            {
                throw new Win32Exception((int)error, $"无法设置 Low integrity 标签：{path}");
            }
        }
        finally
        {
            if (acl != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(acl);
            }

            if (lowSid != IntPtr.Zero)
            {
                NativeMethods.LocalFree(lowSid);
            }
        }
    }

    /// <summary>添加一条显式 ACE；同样的身份 / 继承方式已存在时不重复添加（避免 ACL 膨胀）。</summary>
    internal static void Grant(string path, SecurityIdentifier sid, FileSystemRights rights, bool inherit)
    {
        var info = new DirectoryInfo(path);
        var security = info.GetAccessControl();
        var inheritance = inherit
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None;

        if (HasRule(security, sid, inheritance, rights))
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

    private static bool HasRule(
        DirectorySecurity security,
        SecurityIdentifier sid,
        InheritanceFlags inheritance,
        FileSystemRights rights)
    {
        foreach (AuthorizationRule rule in security
                     .GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier)))
        {
            if (rule is FileSystemAccessRule fileRule
                && fileRule.IdentityReference is SecurityIdentifier identity
                && identity.Equals(sid)
                && fileRule.AccessControlType == AccessControlType.Allow
                && fileRule.InheritanceFlags == inheritance
                // 关键：只比较 SID + 继承方式会漏掉“已继承 RX、但还需要 Modify”的情况，
                // 导致 work 目录实际只有只读权限。必须确认权限位已覆盖所需权限。
                && (fileRule.FileSystemRights & rights) == rights)
            {
                return true;
            }
        }

        return false;
    }
}
