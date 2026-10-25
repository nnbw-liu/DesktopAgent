namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 输入框 <c>@</c> 补全的候选来源：只给相对路径（目录以 <c>/</c> 结尾），绝不读取文件内容。
/// 实现负责枚举、排除与缓存。
/// </summary>
public interface IComposerReferenceIndex
{
    /// <summary>工作区里的相对路径；没有工作区时返回空列表。</summary>
    IReadOnlyList<string> List(string? rootPath);

    /// <summary>丢掉缓存（切换工作区 / 新建文件后调用）。</summary>
    void Invalidate();
}
