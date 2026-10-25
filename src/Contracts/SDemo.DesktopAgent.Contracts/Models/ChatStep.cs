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
/// 步骤落在「过程」还是「答案」：答案永远在过程组之外，折叠过程组不会藏掉最终回答。
/// 这是结构而不是样式——渲染时按 <see cref="ChatStep.Part"/> 分流。
/// </summary>
public enum ChatGroupPart
{
    /// <summary>过程：思考 / 工具 / 宿主追问，可整组折叠。</summary>
    Process,

    /// <summary>答案：正文，永远可见。</summary>
    Answer
}

/// <summary>
/// 步骤时间线的一个节点。把「思考都放一起、工具都放一起」拆成真实顺序：
/// 思考 → 工具 → 思考 → 工具 → … → 正文。
/// </summary>
/// <param name="Kind">这一步是什么。</param>
/// <param name="Text">Reasoning / Text 的文本；Tool 步骤为空。</param>
/// <param name="ToolIndex">Tool 步骤对应 <see cref="ChatMessage.ToolTrace"/> 的下标；其它为 -1。</param>
/// <param name="StepIndex">这一步属于第几次模型请求（0 起）。宿主追问会开启新的 step。</param>
/// <param name="CallId">Tool 步骤对应的调用 id；旧数据为空。</param>
/// <param name="StartedAt">这一步开始的时间（仅过程步骤记）。</param>
/// <param name="EndedAt">这一步结束的时间；进行中为空。</param>
public sealed record ChatStep(
    ChatStepKind Kind,
    string Text,
    int ToolIndex = -1,
    int StepIndex = 0,
    string? CallId = null,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? EndedAt = null)
{
    /// <summary>这一步进过程组还是留在组外（答案）。</summary>
    public ChatGroupPart Part => Kind == ChatStepKind.Text ? ChatGroupPart.Answer : ChatGroupPart.Process;

    /// <summary>同一类步骤紧挨着到达时合并，避免每个 token 建一个节点。</summary>
    public ChatStep Append(string delta) => this with { Text = Text + delta };

    public bool CanAppend(ChatStepKind kind) =>
        Kind == kind && Kind is ChatStepKind.Reasoning or ChatStepKind.Text;
}
