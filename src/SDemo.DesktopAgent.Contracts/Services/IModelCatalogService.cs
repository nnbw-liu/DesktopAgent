namespace SDemo.DesktopAgent.Services;

/// <summary>模型目录获取失败时的异常，Message 可直接显示给用户。</summary>
public sealed class ModelCatalogException(string message) : Exception(message);

/// <summary>从 OpenAI 兼容服务读取模型列表（GET {baseUrl}/models）。</summary>
public interface IModelCatalogService
{
    Task<IReadOnlyList<string>> GetModelsAsync(string baseUrl, string apiKey, CancellationToken cancellationToken = default);
}
