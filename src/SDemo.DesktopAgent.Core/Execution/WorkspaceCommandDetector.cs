using System.IO;

namespace SDemo.DesktopAgent.Execution;

/// <summary>工作区构建 / 测试命令建议（只探测根目录，结果要用户确认后保存）。</summary>
public sealed record WorkspaceCommandSuggestion(string? BuildCommand, string? TestCommand);

/// <summary>按仓库形态给出默认 dotnet / npm 命令；不做执行。</summary>
public static class WorkspaceCommandDetector
{
    public static WorkspaceCommandSuggestion Detect(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return new WorkspaceCommandSuggestion(null, null);
        }

        var solution = Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (solution is not null)
        {
            var name = Path.GetFileName(solution);
            return new WorkspaceCommandSuggestion(
                $"dotnet build \"{name}\" -v minimal",
                $"dotnet test \"{name}\" --no-build");
        }

        var project = Directory.EnumerateFiles(root, "*.csproj", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (project is not null)
        {
            var name = Path.GetFileName(project);
            return new WorkspaceCommandSuggestion(
                $"dotnet build \"{name}\" -v minimal",
                $"dotnet test \"{name}\" --no-build");
        }

        if (File.Exists(Path.Combine(root, "package.json")))
        {
            var pnpm = File.Exists(Path.Combine(root, "pnpm-lock.yaml"));
            var runner = pnpm ? "pnpm" : "npm";
            return new WorkspaceCommandSuggestion(
                $"{runner} run build",
                $"{runner} test");
        }

        return new WorkspaceCommandSuggestion(null, null);
    }
}
