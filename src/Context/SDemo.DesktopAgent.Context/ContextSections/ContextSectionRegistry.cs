namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 上下文段注册表（P2a §3.3.1）。渲染顺序与预算分配都只在这里决定：
/// <list type="number">
/// <item>按 <c>Order → Name</c> 排序，**同样输入必得同样字节**；</item>
/// <item>渲染全部段，空段整段消失（成本 0，不是渲染空标题）；</item>
/// <item>required 段先按 Order 占预算，占不到也保留（安全事实不许被预算剪掉）；</item>
/// <item>可选段按 Order 填剩余额度，每段再按 <see cref="IAgentContextSection.MaxTokens"/> 截断。</item>
/// </list>
/// 结果同时产出**具名的贡献数组**，所以界面 / 报告能按段归因，而不是只有一个总数。
/// </summary>
public sealed class ContextSectionRegistry
{
    /// <summary>可选段至少要拿到这么多 token 才值得渲染；少于它就整段消失，避免半截标题。</summary>
    public const int MinOptionalTokens = 24;

    private readonly IReadOnlyList<IAgentContextSection> _sections;

    public ContextSectionRegistry(IEnumerable<IAgentContextSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);

        var list = sections.ToList();
        var duplicate = list
            .GroupBy(section => section.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicate is not null)
        {
            throw new ArgumentException($"上下文段重名：{duplicate.Key}", nameof(sections));
        }

        _sections = list
            .OrderBy(section => section.Order)
            .ThenBy(section => section.Name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>发行版内置段表（顺序见 <see cref="ContextSectionOrders"/>）。</summary>
    public static ContextSectionRegistry Default { get; } = new(
    [
        new WorkspaceContextSection(),
        new PolicyContextSection(),
        new ChangesContextSection(),
        new ActionsContextSection(),
        new VerificationContextSection(),
        new PlanContextSection(),
        new TimeContextSection()
    ]);

    /// <summary>已注册的段（按渲染顺序）。</summary>
    public IReadOnlyList<IAgentContextSection> Sections => _sections;

    /// <summary>
    /// 渲染一次快照，总上限为 <paramref name="totalCap"/>。
    /// 返回的正文不含快照标题（标题由调用方决定，方便它区分「消息头」与「段」的计量）。
    /// </summary>
    public ContextSectionsRender Render(ContextSectionInput input, int totalCap)
    {
        ArgumentNullException.ThrowIfNull(input);

        var cap = Math.Max(0, totalCap);
        var remaining = cap;
        var blocks = new List<(IAgentContextSection Section, string Text, int Tokens, bool Truncated)>();
        var used = 0;

        foreach (var section in _sections)
        {
            if (!section.Required)
            {
                continue;
            }

            // required 不受剩余额度约束：预算再紧也要在场，只是仍按自己的上限截断。
            var (text, truncated) = RenderSection(section, input, section.MaxTokens);
            if (text.Length == 0)
            {
                continue;
            }

            var tokens = TokensOf(section, text, input.Counter);
            blocks.Add((section, text, tokens, truncated));
            used += tokens;
            remaining = Math.Max(0, remaining - tokens);
        }

        foreach (var section in _sections)
        {
            if (section.Required)
            {
                continue;
            }

            var allowance = Math.Min(section.MaxTokens, remaining);
            if (allowance < MinOptionalTokens)
            {
                continue;
            }

            var (text, truncated) = RenderSection(section, input, allowance);
            if (text.Length == 0)
            {
                continue;
            }

            var tokens = TokensOf(section, text, input.Counter);
            blocks.Add((section, text, tokens, truncated));
            used += tokens;
            remaining = Math.Max(0, remaining - tokens);
        }

        if (blocks.Count == 0)
        {
            return ContextSectionsRender.Empty;
        }

        // 拼接按段顺序来（不是按「必填 / 可选」两趟的添加顺序），保证字节稳定。
        var ordered = blocks
            .OrderBy(block => block.Section.Order)
            .ThenBy(block => block.Section.Name, StringComparer.Ordinal)
            .ToList();

        var builder = new System.Text.StringBuilder();
        var usages = new List<ContextSectionUsage>(ordered.Count);

        foreach (var (section, text, tokens, truncated) in ordered)
        {
            if (builder.Length > 0)
            {
                builder.Append("\n\n");
            }

            builder.Append("### ").Append(section.Title).Append('\n').Append(text);

            usages.Add(new ContextSectionUsage(
                section.Name,
                section.Title,
                section.Order,
                section.Required,
                tokens,
                truncated));
        }

        return new ContextSectionsRender(builder.ToString(), usages, used);
    }

    /// <summary>渲染单段并按上限截断；段自己返回 null / 空白时成本为 0。</summary>
    private static (string Text, bool Truncated) RenderSection(
        IAgentContextSection section,
        ContextSectionInput input,
        int maxTokens)
    {
        var raw = section.Render(input);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return (string.Empty, false);
        }

        return ContextSectionText.Cap(raw, maxTokens, input.Counter);
    }

    /// <summary>整块（标题 + 正文）的 token 数：报告里的分段数字按它算，才和真正发出去的一致。</summary>
    private static int TokensOf(IAgentContextSection section, string text, ITokenCounter counter) =>
        counter.CountMessage("user", $"### {section.Title}\n{text}");
}
