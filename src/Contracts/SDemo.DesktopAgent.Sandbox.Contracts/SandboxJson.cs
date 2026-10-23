using System.Text.Json;
using System.Text.Json.Serialization;

namespace SDemo.DesktopAgent.Sandbox.Protocol;

/// <summary>
/// job.json / result.json 的共享 JSON 选项：camelCase 属性名 + 字符串枚举。
/// 两端必须用同一份选项，否则 <c>mode</c> 会被一端写成数字、另一端读不出来。
/// </summary>
public static class SandboxJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
