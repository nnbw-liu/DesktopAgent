namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 一轮请求的「先决定做什么」：先分对话 / 问答 / 任务，再决定要不要工具、要哪些工具。
/// 判定本身不读文件、不调模型，规则见 <c>TurnClassifier</c>。
/// </summary>
public enum TurnIntent
{
    /// <summary>闲聊、寒暄、身份/致谢、明确要求「不要用工具」：本轮不暴露工具，直接回答。</summary>
    Chat = 0,

    /// <summary>问答 / 解释 / 定位：可能需要读文件或检索，但不需要改文件、跑命令。</summary>
    Answer = 1,

    /// <summary>要动文件、跑构建测试或执行命令的工单式任务。</summary>
    Task = 2
}
