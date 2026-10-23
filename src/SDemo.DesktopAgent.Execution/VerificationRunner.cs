using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Execution;

/// <summary>宿主手动验证入口：用户点「运行验证」时执行已保存的构建 / 测试命令。</summary>
public interface IVerificationRunner
{
    Task<VerificationRecord?> RunAsync(
        ChatSession session,
        string workspaceRoot,
        CancellationToken cancellationToken = default);
}

/// <summary>非 Windows / 未配置命令时的降级：返回 null，由 UI 提示无法验证。</summary>
public sealed class UnavailableVerificationRunner : IVerificationRunner
{
    public Task<VerificationRecord?> RunAsync(
        ChatSession session,
        string workspaceRoot,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<VerificationRecord?>(null);
}
