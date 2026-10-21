using System.IO;
using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Sandbox.Protocol;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

public sealed class SandboxPathGuardTests
{
    [Theory]
    [InlineData("input/script.ps1")]
    [InlineData("input/sub/file.txt")]
    [InlineData("input")]
    public void 允许input子目录内的相对路径(string relativePath)
    {
        var root = Path.Combine(Path.GetTempPath(), "job");

        var resolved = SandboxPathGuard.ResolveUnder(root, relativePath, "input");

        Assert.StartsWith(Path.Combine(root, "input"), resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("C:\\Windows\\System32\\cmd.exe")]
    [InlineData("\\\\server\\share\\file.txt")]
    [InlineData("..\\outside.txt")]
    [InlineData("input\\..\\..\\outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("input/sub/../../outside.txt")]
    public void 拒绝绝对路径UNC与目录穿越(string relativePath)
    {
        var root = Path.Combine(Path.GetTempPath(), "job");

        Assert.Throws<SandboxProtocolException>(() => SandboxPathGuard.ResolveUnder(root, relativePath, "input"));
    }

    [Fact]
    public void 拒绝work子目录用于input()
    {
        var root = Path.Combine(Path.GetTempPath(), "job");

        Assert.Throws<SandboxProtocolException>(() => SandboxPathGuard.ResolveUnder(root, "work/script.ps1", "input"));
    }
}

public sealed class WorkspaceSnapshotTests
{
    [Fact]
    public async Task 快照复制文本文件并跳过git与构建目录()
    {
        using var temp = new TempDirectory();
        var workspace = Path.Combine(temp.Root, "workspace");
        var job = Path.Combine(temp.Root, "job");
        Write(workspace, "a.txt", "hello");
        Write(workspace, "sub/b.txt", "bee");
        Write(workspace, ".git/config", "[core]");
        Write(workspace, "bin/out.dll", "binary");
        Directory.CreateDirectory(Path.Combine(workspace, "empty"));

        var manifest = await WorkspaceSnapshot.CreateAsync(
            workspace,
            Path.Combine(job, "base"),
            Path.Combine(job, "work"),
            new WorkspaceCopyOptions());

        Assert.Equal(["a.txt", "sub/b.txt"], manifest.Entries.Select(entry => entry.Path));
        Assert.All(manifest.Entries, entry => Assert.False(string.IsNullOrEmpty(entry.Sha256)));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(job, "work", "a.txt")));
        Assert.Equal("bee", File.ReadAllText(Path.Combine(job, "work", "sub", "b.txt")));
        Assert.False(Directory.Exists(Path.Combine(job, "work", ".git")));
        Assert.False(Directory.Exists(Path.Combine(job, "work", "bin")));
    }

    [Fact]
    public async Task 超过大小上限时明确报错()
    {
        using var temp = new TempDirectory();
        var workspace = Path.Combine(temp.Root, "workspace");
        Write(workspace, "large.txt", new string('x', 4096));

        var exception = await Assert.ThrowsAsync<WorkspaceTooLargeException>(() =>
            WorkspaceSnapshot.CreateAsync(
                workspace,
                Path.Combine(temp.Root, "base"),
                Path.Combine(temp.Root, "work"),
                new WorkspaceCopyOptions { MaxBytes = 1024 }));

        Assert.Contains("工作区过大", exception.Message);
        Assert.Equal(4096, exception.ScannedBytes);
    }

    [Fact]
    public async Task manifest可保存并读回()
    {
        using var temp = new TempDirectory();
        var workspace = Path.Combine(temp.Root, "workspace");
        Write(workspace, "a.txt", "hello");
        var job = Path.Combine(temp.Root, "job");

        var manifest = await WorkspaceSnapshot.CreateAsync(
            workspace,
            Path.Combine(job, "base"),
            Path.Combine(job, "work"),
            new WorkspaceCopyOptions());

        var path = Path.Combine(job, SandboxProtocol.ManifestFileName);
        manifest.Save(path);
        var loaded = WorkspaceManifest.Load(path);

        Assert.Equal(1, loaded.ProtocolVersion);
        Assert.Single(loaded.Entries);
        Assert.Equal("a.txt", loaded.Entries[0].Path);
        Assert.False(loaded.Entries[0].Applied);
    }

    internal static void Write(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}

public sealed class WorkspaceDiffTests
{
    [Fact]
    public async Task 计算新增修改删除与二进制变更()
    {
        using var temp = new TempDirectory();
        var workspace = Path.Combine(temp.Root, "workspace");
        var job = Path.Combine(temp.Root, "job");
        WorkspaceSnapshotTests.Write(workspace, "a.txt", "one\ntwo\n");
        WorkspaceSnapshotTests.Write(workspace, "sub/b.txt", "bee");
        WorkspaceSnapshotTests.Write(workspace, "blob.bin", "abc");
        WriteBytes(workspace, "blob.bin", [0, 1, 2, 3]);

        var manifest = await WorkspaceSnapshot.CreateAsync(
            workspace,
            Path.Combine(job, "base"),
            Path.Combine(job, "work"),
            new WorkspaceCopyOptions());

        WorkspaceSnapshotTests.Write(Path.Combine(job, "work"), "a.txt", "one\nTWO\nthree\n");
        WorkspaceSnapshotTests.Write(Path.Combine(job, "work"), "new.txt", "brand new\n");
        File.Delete(Path.Combine(job, "work", "sub", "b.txt"));
        WriteBytes(Path.Combine(job, "work"), "blob.bin", [0, 9, 9, 9]);

        var changes = await WorkspaceDiff.ComputeAsync(
            Path.Combine(job, "work"),
            Path.Combine(job, "base"),
            manifest);

        var byPath = changes.ToDictionary(change => change.RelativePath, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(SandboxChangeKind.Modified, byPath["a.txt"].Kind);
        Assert.Contains("+TWO", byPath["a.txt"].Diff);
        Assert.Contains("+three", byPath["a.txt"].Diff);
        Assert.Equal(SandboxChangeKind.Added, byPath["new.txt"].Kind);
        Assert.Contains("+brand new", byPath["new.txt"].Diff);
        Assert.Equal(SandboxChangeKind.Deleted, byPath["sub/b.txt"].Kind);
        Assert.True(byPath["blob.bin"].IsBinary);
        Assert.Null(byPath["blob.bin"].Diff);
    }

    private static void WriteBytes(string root, string relativePath, byte[] bytes)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }
}

public sealed class ChangeApplierTests
{
    [Fact]
    public async Task 应用修改后更新真实工作区并标记manifest()
    {
        using var temp = new TempDirectory();
        var workspace = Path.Combine(temp.Root, "workspace");
        var job = Path.Combine(temp.Root, "job");
        WorkspaceSnapshotTests.Write(workspace, "a.txt", "old\n");
        var manifest = await SnapshotAsync(workspace, job);
        WorkspaceSnapshotTests.Write(Path.Combine(job, "work"), "a.txt", "new\n");
        var changes = await WorkspaceDiff.ComputeAsync(Path.Combine(job, "work"), Path.Combine(job, "base"), manifest);

        var result = await new ChangeApplier().ApplyAsync(
            workspace,
            Path.Combine(job, "work"),
            manifest,
            changes,
            new HashSet<string> { "a.txt" });

        Assert.Equal(["a.txt"], result.Applied);
        Assert.Equal("new\n", File.ReadAllText(Path.Combine(workspace, "a.txt")));
        var saved = WorkspaceManifest.Load(Path.Combine(job, SandboxProtocol.ManifestFileName));
        Assert.True(saved.Entries.Single().Applied);
    }

    [Fact]
    public async Task 工作区在快照后被改动时报告冲突且不覆盖()
    {
        using var temp = new TempDirectory();
        var workspace = Path.Combine(temp.Root, "workspace");
        var job = Path.Combine(temp.Root, "job");
        WorkspaceSnapshotTests.Write(workspace, "a.txt", "old\n");
        var manifest = await SnapshotAsync(workspace, job);
        WorkspaceSnapshotTests.Write(Path.Combine(job, "work"), "a.txt", "sandbox\n");
        WorkspaceSnapshotTests.Write(workspace, "a.txt", "user edited\n");
        var changes = await WorkspaceDiff.ComputeAsync(Path.Combine(job, "work"), Path.Combine(job, "base"), manifest);

        var result = await new ChangeApplier().ApplyAsync(
            workspace,
            Path.Combine(job, "work"),
            manifest,
            changes,
            new HashSet<string> { "a.txt" });

        Assert.Equal(["a.txt"], result.Conflicts);
        Assert.Empty(result.Applied);
        Assert.Equal("user edited\n", File.ReadAllText(Path.Combine(workspace, "a.txt")));
    }

    [Fact]
    public async Task 任一文件冲突时所有选中变更整体中止()
    {
        using var temp = new TempDirectory();
        var workspace = Path.Combine(temp.Root, "workspace");
        var job = Path.Combine(temp.Root, "job");
        WorkspaceSnapshotTests.Write(workspace, "a.txt", "old\n");
        WorkspaceSnapshotTests.Write(workspace, "safe.txt", "old\n");
        var manifest = await SnapshotAsync(workspace, job);
        WorkspaceSnapshotTests.Write(Path.Combine(job, "work"), "a.txt", "sandbox\n");
        WorkspaceSnapshotTests.Write(Path.Combine(job, "work"), "safe.txt", "sandbox\n");
        WorkspaceSnapshotTests.Write(workspace, "a.txt", "user edited\n");
        var changes = await WorkspaceDiff.ComputeAsync(Path.Combine(job, "work"), Path.Combine(job, "base"), manifest);

        var result = await new ChangeApplier().ApplyAsync(
            workspace,
            Path.Combine(job, "work"),
            manifest,
            changes,
            new HashSet<string> { "a.txt", "safe.txt" });

        Assert.Equal(["a.txt"], result.Conflicts);
        Assert.Empty(result.Applied);
        Assert.Equal("old\n", File.ReadAllText(Path.Combine(workspace, "safe.txt")));
    }

    [Fact]
    public async Task 新增文件可直接写入且删除仅跳过()
    {
        using var temp = new TempDirectory();
        var workspace = Path.Combine(temp.Root, "workspace");
        var job = Path.Combine(temp.Root, "job");
        WorkspaceSnapshotTests.Write(workspace, "a.txt", "old\n");
        var manifest = await SnapshotAsync(workspace, job);
        WorkspaceSnapshotTests.Write(Path.Combine(job, "work"), "new/n.txt", "hello\n");
        File.Delete(Path.Combine(job, "work", "a.txt"));
        var changes = await WorkspaceDiff.ComputeAsync(Path.Combine(job, "work"), Path.Combine(job, "base"), manifest);

        var result = await new ChangeApplier().ApplyAsync(
            workspace,
            Path.Combine(job, "work"),
            manifest,
            changes,
            new HashSet<string> { "new/n.txt", "a.txt" });

        Assert.Equal(["new/n.txt"], result.Applied);
        Assert.Equal(["a.txt"], result.Skipped);
        Assert.Equal("hello\n", File.ReadAllText(Path.Combine(workspace, "new", "n.txt")));
        Assert.True(File.Exists(Path.Combine(workspace, "a.txt")));
    }

    [Fact]
    public async Task 拒绝目录穿越的变更路径()
    {
        using var temp = new TempDirectory();
        var workspace = Path.Combine(temp.Root, "workspace");
        var job = Path.Combine(temp.Root, "job");
        WorkspaceSnapshotTests.Write(workspace, "a.txt", "old\n");
        var manifest = await SnapshotAsync(workspace, job);
        var outside = Path.Combine(temp.Root, "outside.txt");
        File.WriteAllText(outside, "evil");
        var changes = new[] { new SandboxChange("../outside.txt", SandboxChangeKind.Added, 4, false, null) };

        var result = await new ChangeApplier().ApplyAsync(
            workspace,
            Path.Combine(job, "work"),
            manifest,
            changes,
            new HashSet<string> { "../outside.txt" });

        Assert.Equal(["../outside.txt"], result.Skipped);
        Assert.Equal("evil", File.ReadAllText(outside));
    }

    private static async Task<WorkspaceManifest> SnapshotAsync(string workspace, string job)
    {
        var manifest = await WorkspaceSnapshot.CreateAsync(
            workspace,
            Path.Combine(job, "base"),
            Path.Combine(job, "work"),
            new WorkspaceCopyOptions());
        manifest.Save(Path.Combine(job, SandboxProtocol.ManifestFileName));
        return manifest;
    }
}
