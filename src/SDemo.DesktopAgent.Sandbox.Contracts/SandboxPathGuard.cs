namespace SDemo.DesktopAgent.Sandbox.Protocol;

/// <summary>协议或路径校验失败。两端都必须把它当作拒绝执行，而不是忽略。</summary>
public sealed class SandboxProtocolException : Exception
{
    public SandboxProtocolException(string message) : base(message)
    {
    }

    public SandboxProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// job.json 相对路径的唯一入口：拒绝绝对路径、UNC、设备路径、<c>..</c>，
/// 并保证结果位于指定的子目录内。宿主与 SandboxHost 必须用同一套规则。
/// </summary>
public static class SandboxPathGuard
{
    public static string ResolveUnder(string jobRoot, string? relativePath, string requiredSubdirectory)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new SandboxProtocolException("协议里的相对路径为空。");
        }

        if (relativePath.Contains('\0'))
        {
            throw new SandboxProtocolException("协议路径包含 NUL 字符。");
        }

        if (relativePath.StartsWith(@"\\", StringComparison.Ordinal)
            || relativePath.StartsWith("//", StringComparison.Ordinal)
            || relativePath.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            throw new SandboxProtocolException($"拒绝 UNC / 设备路径：{relativePath}");
        }

        if (Path.IsPathRooted(relativePath))
        {
            throw new SandboxProtocolException($"拒绝绝对路径：{relativePath}");
        }

        var normalizedRelative = relativePath.Replace('\\', '/');
        foreach (var segment in normalizedRelative.Split('/'))
        {
            if (segment is ".." or ".")
            {
                throw new SandboxProtocolException($"拒绝包含 . / .. 的路径：{relativePath}");
            }
        }

        var root = Path.GetFullPath(jobRoot);
        var allowedRoot = Path.GetFullPath(Path.Combine(root, requiredSubdirectory));
        var fullPath = Path.GetFullPath(Path.Combine(root, normalizedRelative.Replace('/', Path.DirectorySeparatorChar)));

        if (!IsUnder(allowedRoot, fullPath))
        {
            throw new SandboxProtocolException(
                $"路径逃逸：{relativePath} 不在 {requiredSubdirectory} 内。");
        }

        return fullPath;
    }

    /// <summary>判断路径是否等于根目录或位于其下（Windows 路径比较不区分大小写）。</summary>
    public static bool IsUnder(string root, string candidate)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));

        return normalizedCandidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
               || normalizedCandidate.StartsWith(
                   normalizedRoot + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }
}
