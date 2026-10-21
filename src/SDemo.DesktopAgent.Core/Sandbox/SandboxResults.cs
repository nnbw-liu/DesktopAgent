using SDemo.DesktopAgent.Sandbox;

namespace SDemo.DesktopAgent.Services;

/// <summary>沙箱结果的构造辅助，避免每个运行器重复写 NotAvailable 样板。</summary>
internal static class SandboxResults
{
    public static SandboxResult NotAvailable(string jobId, SandboxKind kind, string reason) =>
        new()
        {
            JobId = jobId,
            Kind = kind,
            ExitCode = -1,
            Failure = SandboxFailureCode.NotAvailable,
            Message = reason
        };
}
