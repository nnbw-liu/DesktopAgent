using System.Text;
using System.Text.Json;
using SDemo.DesktopAgent.Sandbox.Protocol;

namespace SDemo.DesktopAgent.SandboxHost;

/// <summary>
/// result.json / pdf.json 的原子写：先写 .tmp，再 Move 覆盖，宿主只轮询最终文件。
/// 任何情况下都不让宿主读到半截 JSON。
/// </summary>
internal static class ResultWriter
{
    public static void WriteAtomic<T>(string path, T document)
    {
        var json = JsonSerializer.Serialize(document, SandboxJson.Options);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json, new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }
}
