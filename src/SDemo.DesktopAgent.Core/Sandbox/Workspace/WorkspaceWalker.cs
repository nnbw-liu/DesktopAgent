using System.IO;

namespace SDemo.DesktopAgent.Sandbox;

/// <summary>快照 / diff / 变更计算共用的文件枚举器。</summary>
internal static class WorkspaceWalker
{
    internal sealed record WorkspaceFile(
        string FullPath,
        string RelativePath,
        long Length,
        DateTime LastWriteUtc);

    internal static List<WorkspaceFile> EnumerateFiles(
        string root,
        IReadOnlySet<string> excludedDirectories,
        ICollection<string>? skipped = null)
    {
        var files = new List<WorkspaceFile>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
                var isReparsePoint = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
                var relative = Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');

                if (isReparsePoint)
                {
                    skipped?.Add(relative);
                    continue;
                }

                if (isDirectory)
                {
                    if (excludedDirectories.Contains(entry.Name))
                    {
                        continue;
                    }

                    pending.Push((DirectoryInfo)entry);
                    continue;
                }

                var file = (FileInfo)entry;
                files.Add(new WorkspaceFile(file.FullName, relative, file.Length, file.LastWriteTimeUtc));
            }
        }

        return files;
    }
}
