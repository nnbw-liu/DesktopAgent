namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 任务档（P1b §3.2.3）：这一轮属于什么任务。
/// <para>
/// 它是「曝光面稳定」的抓手：任务档在**进入任务时**定一次，档内的工具集是固定的；
/// 相邻两轮如果没有新的任务信号（「继续」「再试一次」），就沿用上一轮的档，
/// 而不是因为这一句话没命中关键词就把工具收掉一半。
/// </para>
/// <para>
/// <see cref="General"/> 表示「没有任务信号」：此时只暴露核心集，和 M4 之前的行为一致。
/// </para>
/// </summary>
public enum ToolTaskProfile
{
    /// <summary>没有任务信号（寒暄 / 追问）：只给核心集。</summary>
    General,

    /// <summary>读与定位：先看再答，不改文件。</summary>
    Read,

    /// <summary>改代码：读 + 三种写文件工具。</summary>
    Edit,

    /// <summary>构建 / 测试 / 跑命令。</summary>
    Run,

    /// <summary>沙箱里的 PowerShell 脚本。</summary>
    Shell,

    /// <summary>PDF 文档。</summary>
    Document,

    /// <summary>Word / Excel / PowerPoint 文档。</summary>
    Office,

    /// <summary>联网搜索。</summary>
    Web,

    /// <summary>跨会话回忆。</summary>
    Memory,

    /// <summary>多步计划。</summary>
    Plan
}
