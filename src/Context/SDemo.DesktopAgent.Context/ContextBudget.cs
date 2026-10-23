using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 一次请求的预算（§3.4）：
/// <code>
/// reserveOut = min(profile.MaxOutputTokens, max(1024, window / 4))
/// safety     = max(256, window / 20)
/// total      = floor((window - reserveOut - safety) * settings.BudgetRatio)
/// fixedCap   = floor(total * settings.FixedCapRatio)
/// compact    = floor(total * 0.60)          // P2 用
/// </code>
/// 所有数值都夹在合理范围内，避免 0 / 负数预算把裁剪器逼进死循环。
/// </summary>
public sealed record ContextBudget(
    int Window,
    int ReservedOutput,
    int Safety,
    int Total,
    int FixedCap,
    int CompactTarget)
{
    /// <summary>
    /// 工具 schema 的独立上限（§3 预算账本）：4K 1K / 8K 1.5K / 32K 3K。
    /// 路由预留与宿主侧实测都用它收口，避免 30 个工具吃掉整个窗口。
    /// </summary>
    public int ToolSchemaLimit => Window switch
    {
        <= 4096 => 1000,
        <= 8192 => 1500,
        _ => 3000
    };

    /// <summary>技能索引的固定上限（§3 账本 200–300；M2 验收放宽到 400）。</summary>
    public const int SkillIndexTokenLimit = 400;

    /// <summary>M3：工作区知识库只注入一句索引（本工作区有 N 条归档会话），上限 120 token。</summary>
    public const int KnowledgeTokenLimit = 120;

    /// <summary>本轮技能索引最多占多少 token；超出只截断索引，不丢技能全文。</summary>
    public int SkillIndexLimit => SkillIndexTokenLimit;

    /// <summary>本轮知识库索引行最多占多少 token。</summary>
    public int KnowledgeLimit => KnowledgeTokenLimit;

    /// <summary>按模型档案与设置计算预算。</summary>
    public static ContextBudget Create(ModelProfile profile, ContextSettings settings)
    {
        var window = Math.Max(1024, profile.ContextWindow);

        var reservedOutput = Math.Min(
            Math.Max(256, profile.MaxOutputTokens),
            Math.Max(1024, window / 4));

        var safety = Math.Max(256, window / 20);
        var available = Math.Max(256, window - reservedOutput - safety);
        var ratio = Math.Clamp(settings.BudgetRatio, 0.1, 0.95);

        var total = (int)Math.Floor(available * ratio);

        // 输入预算不能吃掉安全余量，也不能小到连一条消息都放不下。
        total = Math.Clamp(total, Math.Min(256, available), available);

        var fixedCap = (int)Math.Floor(total * Math.Clamp(settings.FixedCapRatio, 0.1, 0.9));
        fixedCap = Math.Clamp(fixedCap, 64, total);

        var compact = (int)Math.Floor(total * 0.60);

        return new ContextBudget(window, reservedOutput, safety, total, fixedCap, compact);
    }

    /// <summary>溢出重试时按比例缩小预算；窗口 / 预留 / 余量保持原值，报告里能看出前后差异。</summary>
    public ContextBudget Scale(double scale)
    {
        var clamped = Math.Clamp(scale, 0.1, 1.0);
        var total = Math.Max(128, (int)Math.Floor(Total * clamped));

        return this with
        {
            Total = total,
            FixedCap = Math.Max(64, (int)Math.Floor(FixedCap * clamped)),
            CompactTarget = Math.Max(64, (int)Math.Floor(CompactTarget * clamped))
        };
    }
}
