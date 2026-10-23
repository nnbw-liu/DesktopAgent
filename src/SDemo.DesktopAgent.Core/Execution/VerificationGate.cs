using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Execution;

/// <summary>验证门状态：有编辑轨迹但之后没有成功验证时为 dirty。</summary>
public sealed record VerificationGateState(
    bool Dirty,
    int ChangedFiles,
    DateTime? LastEditAt,
    VerificationRecord? LastVerification)
{
    public bool LastVerificationFailed =>
        LastVerification is { Success: false } && Dirty;
}

/// <summary>
/// 只统计本次会话的工具轨迹（不扫描文件系统，也不把外部修改算成 dirty）：
/// <c>lastEditAt &gt; lastVerifiedAt</c> 即未验证；一次成功验证会清掉徽标。
/// </summary>
public static class VerificationGate
{
    private static readonly HashSet<string> WriteTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "write_file", "apply_patch", "edit_file", "create_file", "replace_in_file"
    };

    public static VerificationGateState Evaluate(ChatSession? session)
    {
        if (session is null)
        {
            return new VerificationGateState(false, 0, null, null);
        }

        DateTime? lastEdit = null;
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var message in session.Messages)
        {
            foreach (var trace in message.ToolTrace)
            {
                if (trace.Edits is { Count: > 0 })
                {
                    foreach (var edit in trace.Edits)
                    {
                        files.Add(edit.Path);
                    }

                    if (lastEdit is null || trace.At > lastEdit)
                    {
                        lastEdit = trace.At;
                    }
                }
                else if (WriteTools.Contains(trace.ToolId)
                         && trace.ArgsDigest is { } digest
                         && !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(digest);
                    if (lastEdit is null || trace.At > lastEdit)
                    {
                        lastEdit = trace.At;
                    }
                }
            }
        }

        var verification = session.LastVerification;
        var verifiedAt = verification is { Success: true } ? verification.At : (DateTime?)null;
        var dirty = lastEdit is not null
                    && (verifiedAt is null || lastEdit > verifiedAt);

        return new VerificationGateState(dirty, files.Count, lastEdit, verification);
    }
}
