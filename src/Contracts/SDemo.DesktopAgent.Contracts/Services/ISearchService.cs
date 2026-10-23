using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Services;

/// <summary>联网搜索。默认走必应 RSS（免密钥），也可以在设置里换成自定义搜索 API。</summary>
public interface ISearchService
{
    Task<string> SearchAsync(string query, CancellationToken cancellationToken = default);
}
