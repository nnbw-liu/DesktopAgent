using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 宿主上下文段（P2a §3.3.1）。每个段是一个可以单独开关、单独计量、单独测试的上下文事实来源：
/// 段有名字（归因 / 去重 / 覆盖）、有中央顺序（渲染字节稳定）、有 required（预算再紧也不丢）、
/// 有单段上限（超了段内截断，而不是把整块挤爆）。
/// <para>
/// 段是唯一事实源：提示词注入、<see cref="ContextReport"/> 的分段归因、界面分解面板都读它，
/// 不要为任何展示再写一套分类或估算，否则面板和预算会对不上。
/// </para>
/// </summary>
public interface IAgentContextSection
{
    /// <summary>稳定名字（workspace / policy / time …），用于归因、去重与作用域覆盖。</summary>
    string Name { get; }

    /// <summary>渲染时的一行标题（中文）。</summary>
    string Title { get; }

    /// <summary>排序位；由 <see cref="ContextSectionOrders"/> 统一分配，段与段之间不抢位置。</summary>
    int Order { get; }

    /// <summary>预算抑制时也不许丢（安全事实）。用户显式关掉整段是另一条路径，不受这个标志约束。</summary>
    bool Required { get; }

    /// <summary>本段的 token 上限（超出由段内自行截断，并标记 Truncated）。</summary>
    int MaxTokens { get; }

    /// <summary>渲染正文；返回 null 或空白表示这一段此刻不存在，成本严格为 0（不是渲染一个空标题）。</summary>
    string? Render(ContextSectionInput input);
}

/// <summary>
/// 中央顺序表（DSH 的 <c>CONTEXT_ORDERS</c> 对应物）。加一段只改这里，
/// 排序规则是 <c>Order → Name</c>，因此**同样输入必得同样字节**——这是 golden test 与提示词缓存的前提。
/// </summary>
public static class ContextSectionOrders
{
    public const int Workspace = 100;
    public const int Policy = 110;
    public const int Changes = 120;
    public const int Actions = 125;
    public const int Verification = 130;
    public const int Plan = 140;
    public const int Time = 150;
}

/// <summary>
/// 一次快照渲染需要的全部宿主事实。段只读它，不自己去读设置 / 扫盘 / 取系统时间，
/// 这样每一段都能用构造好的输入做纯函数测试。
/// </summary>
public sealed record ContextSectionInput(
    ITokenCounter Counter,
    ContextSettings Settings,
    AgentMode Mode,
    ModelProfile Profile,
    WorkspaceState? State,
    AgentPolicyFacts Policy,
    DateTimeOffset Now,
    string TimeZoneId,
    DateTimeOffset? LastModelVisibleAt);

/// <summary>一个段在这一次渲染里的结果：名字、token、是否因预算被截断。</summary>
public sealed record ContextSectionUsage(
    string Name,
    string Title,
    int Order,
    bool Required,
    int Tokens,
    bool Truncated);

/// <summary>快照渲染结果：已按段拼好的正文 + 每段的归因。</summary>
public sealed record ContextSectionsRender(
    string Text,
    IReadOnlyList<ContextSectionUsage> Sections,
    int Tokens)
{
    public static ContextSectionsRender Empty { get; } = new(string.Empty, [], 0);

    /// <summary>有没有任何一段被渲染出来。</summary>
    public bool Any => Sections.Count > 0;

    /// <summary>某一段的 token 数；没有这一段时为 0。</summary>
    public int TokensOf(string name) =>
        Sections.FirstOrDefault(section => string.Equals(section.Name, name, StringComparison.Ordinal))?.Tokens ?? 0;
}
