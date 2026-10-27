using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace SDemo.DesktopAgent.Services;

/// <summary>联网搜索工具包。默认关闭；搜索后端同「设置 → 工具 → 联网搜索」。</summary>
public sealed class SearchToolPack : IAgentToolPack
{
    private readonly ISearchService _search;

    public SearchToolPack(ISearchService search) => _search = search;

    public AgentToolPackDescriptor Descriptor { get; } = new(
        Id: "search",
        Name: "联网搜索",
        Description: "调用当前配置的搜索后端，返回标题、链接与摘要。",
        Glyph: "⌕",
        Risk: AgentToolRisk.Low,
        Order: 10,
        DefaultEnabled: true,
        WindowsOnly: false,
        Functions:
        [
            new AgentToolFunctionDescriptor("web_search", "联网搜索")
        ]);

    public IReadOnlyList<AITool> Create(ToolContext context)
    {
        var functions = new SearchFunctions(_search);

        return
        [
            AIFunctionFactory.Create(
                functions.WebSearchAsync,
                name: "web_search",
                description: "联网搜索，返回若干条标题、链接和摘要。")
        ];
    }

    private sealed class SearchFunctions(ISearchService search)
    {
        public Task<string> WebSearchAsync(
            [Description("搜索关键词。")] string query,
            CancellationToken cancellationToken) =>
            search.SearchAsync(query, cancellationToken);
    }
}
