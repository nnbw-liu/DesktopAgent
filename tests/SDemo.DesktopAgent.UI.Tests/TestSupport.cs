using System.IO;
using SDemo.DesktopAgent.Execution;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.UI.Tests;

/// <summary>一次性临时目录；测试结束递归删除。</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "sdemo-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清不掉不影响测试结果。
        }
    }
}

/// <summary>组件测试用的 IAppPaths：全部落在临时目录。</summary>
internal sealed class StubAppPaths : IAppPaths
{
    public StubAppPaths(string dataRoot)
    {
        DataRoot = dataRoot;
        SettingsFile = Path.Combine(dataRoot, "settings.json");
        ChatsRoot = Path.Combine(dataRoot, "chats");
        UploadsRoot = Path.Combine(dataRoot, "uploads");
        SkillsRoot = Path.Combine(dataRoot, "skills");
        PluginsRoot = Path.Combine(dataRoot, "plugins");
        DefaultWorkspaceRoot = Path.Combine(dataRoot, "workspace");
        SandboxRoot = Path.Combine(dataRoot, "sandbox");
    }

    public string DataRoot { get; }

    public string SettingsFile { get; }

    public string ChatsRoot { get; }

    public string UploadsRoot { get; }

    public string SkillsRoot { get; }

    public string PluginsRoot { get; }

    public string DefaultWorkspaceRoot { get; }

    public string SandboxRoot { get; }
}

/// <summary>记录打开请求的启动器；组件测试不真的起浏览器。</summary>
internal sealed class RecordingFileLauncherService : IFileLauncherService
{
    public List<string> Opened { get; } = [];

    public List<string> Revealed { get; } = [];

    public bool Result { get; set; } = true;

    public Task<bool> OpenFileAsync(string path)
    {
        Opened.Add(path);
        return Task.FromResult(Result);
    }

    public Task<bool> RevealFileAsync(string path)
    {
        Revealed.Add(path);
        return Task.FromResult(Result);
    }
}

/// <summary>组件测试用的文件夹选择器：永远取消。</summary>
internal sealed class NullFolderPickerService : IFolderPickerService
{
    public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) =>
        Task.FromResult<string?>(null);
}

/// <summary>可配置结果的验证器：记录被调用次数，不真的跑构建。</summary>
internal sealed class FakeVerificationRunner(VerificationRecord? result) : SDemo.DesktopAgent.Execution.IVerificationRunner
{
    public int Calls { get; private set; }

    public Task<VerificationRecord?> RunAsync(
        ChatSession session,
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(result);
    }
}
