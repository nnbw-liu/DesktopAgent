namespace SDemo.DesktopAgent.Models;

/// <summary>一次生成里的一步：思考、工具调用、正文，按发生顺序排列。</summary>
public enum ChatStepKind
{
    /// <summary>思维链片段（reasoning_content）。</summary>
    Reasoning,

    /// <summary>一次工具调用；具体内容在消息的 <see cref="ChatMessage.ToolTrace"/> 里按索引取。</summary>
    Tool,

    /// <summary>正文片段（模型说话的文本）。</summary>
    Text,

    /// <summary>宿主追问（无修改回灌 / 自动验证）。只用于界面提示，不新开一条回复。</summary>
    HostPrompt
}

/// <summary>
/// 步骤时间线的一个节点。把「思考都放一起、工具都放一起」拆成真实顺序：
/// 思考 → 工具 → 思考 → 工具 → … → 正文。
/// </summary>
/// <param name="Kind">这一步是什么。</param>
/// <param name="Text">Reasoning / Text 的文本；Tool 步骤为空。</param>
/// <param name="ToolIndex">Tool 步骤对应 <see cref="ChatMessage.ToolTrace"/> 的下标；其它为 -1。</param>
public sealed record ChatStep(ChatStepKind Kind, string Text, int ToolIndex = -1)
{
    /// <summary>同一类步骤紧挨着到达时合并，避免每个 token 建一个节点。</summary>
    public ChatStep Append(string delta) => this with { Text = Text + delta };

    public bool CanAppend(ChatStepKind kind) => Kind == kind && Kind != ChatStepKind.Tool;
}
