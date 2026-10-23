using System.IO;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.Sandbox.Tests;

internal sealed class P4TempDirectory : IDisposable
{
    public P4TempDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "sdemo-p4-tests", Guid.NewGuid().ToString("N"));
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
        }
    }
}

internal sealed class P4StubSettings : ISettingsService
{
    public AppSettings Current { get; } = new();

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public void Save()
    {
    }
}

internal sealed class P4StubAudit : IToolAuditLog
{
    public List<ToolAuditEntry> Entries { get; } = [];

    public void Write(ToolAuditEntry entry) => Entries.Add(entry);
}
