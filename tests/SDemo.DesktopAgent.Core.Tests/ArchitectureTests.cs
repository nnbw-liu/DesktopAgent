using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>
/// 模块边界的守卫：这些断言不测行为，只钉住分层约定，
/// 防止以后为了省事把 MAF / OpenAI / UI 依赖又引回契约层或应用层。
/// </summary>
public sealed class ArchitectureTests
{
    [Fact]
    public void Core不直接引用AI_SDK()
    {
        var references = typeof(AgentChatService).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(references, name =>
            name.StartsWith("Microsoft.Agents", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(references, name =>
            name.Equals("OpenAI", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Contracts不引用AI_SDK与UI框架()
    {
        var references = typeof(AppSettings).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(references, name =>
            name.StartsWith("Microsoft.Agents", StringComparison.OrdinalIgnoreCase)
            || name.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Microsoft.Extensions.AI", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Microsoft.Maui", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void 沙箱契约不引用任何上层程序集()
    {
        var references = typeof(Sandbox.SandboxKind).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name ?? string.Empty)
            .Where(name => name.StartsWith("SDemo", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(references);
    }

    [Fact]
    public void 内置插件只依赖插件契约()
    {
        var references = typeof(Plugins.Translate.TranslatePlugin).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name ?? string.Empty)
            .Where(name => name.StartsWith("SDemo", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Equal(["SDemo.DesktopAgent.Plugins.Abstractions"], references);
    }
}
