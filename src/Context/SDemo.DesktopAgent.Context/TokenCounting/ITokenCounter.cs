namespace SDemo.DesktopAgent.Context;

/// <summary>
/// token 计量接口。P1 只做启发式估算，准确度靠 <see cref="TokenCalibrationStore"/> 按 provider/model 校准。
/// 所有进入上下文的内容都必须能通过它估算（总纲 §6.4）。
/// </summary>
public interface ITokenCounter
{
    /// <summary>一段文本的 token 估算（不含消息开销）。</summary>
    int Count(string? text);

    /// <summary>一条消息的 token 估算：正文 + 固定开销。</summary>
    /// <param name="role">user / assistant 等角色，保留给未来按角色差异估算。</param>
    /// <param name="text">消息正文（已包含附件、工具轨迹的渲染结果）。</param>
    /// <param name="extraOverhead">额外开销，例如多模态 / 工具定义。</param>
    int CountMessage(string role, string? text, int extraOverhead = 0);
}
