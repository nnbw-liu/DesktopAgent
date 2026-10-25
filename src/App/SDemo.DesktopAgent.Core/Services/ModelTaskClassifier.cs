using System.Text.RegularExpressions;
using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Services;

/// <summary>规则判定的结果：任务类型 + 置信度 + 复杂度 + 可读理由。</summary>
/// <param name="Task">任务类型。</param>
/// <param name="Confidence">0–1；低于混合阈值时才会问路由模型。</param>
/// <param name="Complexity">复杂度 0–100，用于解释与评测，不直接决定模型。</param>
/// <param name="Reason">一句话理由。</param>
public sealed record ModelTaskClassification(
    ModelTaskKind Task,
    double Confidence,
    int Complexity,
    string Reason);

/// <summary>
/// 成本档 0 的模型路由分类器：不调模型、不读文件，只用用户原句、会话长度、
/// 工具包与技能选择判断「闲聊 / 简单 / 代码 / 复杂」。
/// <para>
/// 它和 <see cref="TurnClassifier"/> 的分工：TurnClassifier 回答「要不要工具」，
/// 这里回答「值不值得上更强的模型」。两者可以同时判成 Task，但一个简单单文件改动
/// 属于 Code，一个跨模块重构属于 Complex。
/// </para>
/// </summary>
public static class ModelTaskClassifier
{
    /// <summary>超过这个字符数，即使没有复杂词也按复杂任务处理（中文约 200 字）。</summary>
    public const int LongInputChars = 220;

    /// <summary>会话估算输入超过这个 token 数时，优先使用大窗口 / 强模型。</summary>
    public const int LongContextTokens = 12_000;

    /// <summary>明确的复杂任务信号：架构、排查、重构、迁移、跨文件等。</summary>
    private static readonly string[] ComplexPhrases =
    [
        "架构", "设计方案", "技术方案", "整体设计", "系统设计", "重构", "迁移", "拆分模块",
        "根因", "排查", "定位问题", "为什么", "性能优化", "安全审计", "代码审查", "review",
        "多文件", "跨文件", "整个项目", "全项目", "端到端", "完整实现", "详细分析", "深度分析",
        "权衡", "取舍", "规划", "roadmap", "architecture", "refactor", "migrate", "root cause",
        "multi-file", "end-to-end", "benchmark", "investigate"
    ];

    /// <summary>工程 / 代码信号：足够让简单问答升级成 Code。</summary>
    private static readonly string[] CodePhrases =
    [
        "代码", "函数", "方法", "类", "接口", "文件", "项目", "仓库", "编译", "构建", "测试",
        "报错", "异常", "堆栈", "日志", "补丁", "提交", "依赖", "脚本", "命令", "终端",
        "数据库", "sql", "接口文档", "前端", "后端", "组件", "样式", "bug", "fix", "debug",
        "compile", "build", "test", "stack trace", "exception", "commit", "dependency",
        "api", "sdk", "typescript", "javascript", "python", "csharp", "dotnet", "java"
    ];

    /// <summary>文件扩展名 / 路径 / 代码围栏：几乎一定是工程任务。</summary>
    private static readonly Regex CodeShape = new(
        @"```|(?:^|\s)[\w./-]+\.(?:cs|csproj|sln|ts|tsx|js|jsx|py|java|go|rs|cpp|h|json|ya?ml|xml|sql|ps1|sh)\b|"
        + @"(?:^|\s)(?:src|tests?|docs|tools)/[\w./-]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>概念性提问：通常一句话就能答，不应因为含「依赖 / 接口」等词被升级成 Code。</summary>
    private static readonly Regex ConceptQuestion = new(
        @"^(?:请|麻烦)?(?:解释|说明|介绍|讲讲|说一下|什么是|为什么|怎么理解|如何理解)|"
        + @"^(?:what is|what are|explain|how does|why does)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>故障排查类问题：即使以「为什么」开头，也仍然属于代码任务。</summary>
    private static readonly string[] FailurePhrases =
    [
        "报错", "失败", "异常", "堆栈", "日志", "崩溃", "无法启动", "不工作", "跑不起来"
    ];

    /// <summary>多步骤 / 多要求：句子里的连接词越多，越像复杂任务。</summary>
    private static readonly string[] MultiStepPhrases =
    [
        "并且", "同时", "然后", "接着", "再", "还要", "另外", "顺便", "以及", "和",
        "第一步", "第二步", "先", "之后", "最后"
    ];

    /// <summary>判断一轮输入属于哪类任务。</summary>
    /// <param name="request">路由输入。</param>
    /// <param name="estimatedInputTokens">当前会话估算输入 token（可传 0）。</param>
    public static ModelTaskClassification Classify(
        ModelRoutingRequest request,
        int estimatedInputTokens = 0)
    {
        var text = (request.UserInput ?? string.Empty).Trim();
        var lower = text.ToLowerInvariant();
        var tools = request.EnabledToolPacks;

        if (text.Length == 0)
        {
            return new ModelTaskClassification(ModelTaskKind.Chat, 1.0, 0, "空输入");
        }

        // 用户明确要求「不要用工具」或短寒暄：直接走最便宜的档。
        if (TurnClassifier.Classify(text, request.HasSelectedSkill) == TurnIntent.Chat
            && !LooksLikeCode(lower, tools))
        {
            return new ModelTaskClassification(ModelTaskKind.Chat, 0.95, 5, "寒暄 / 闲聊短句");
        }

        var complexHits = CountHits(lower, ComplexPhrases);
        var codeHits = CountHits(lower, CodePhrases);
        var codeShape = CodeShape.IsMatch(lower);
        var multiStepHits = CountHits(lower, MultiStepPhrases);
        var longInput = text.Length >= LongInputChars;
        var longContext = estimatedInputTokens >= LongContextTokens;
        var action = TurnClassifier.HasImperativeAction(text);
        var editsSelected = tools?.Contains("edits") == true || tools?.Contains("command") == true;

        // 概念性提问：没有动手指令、没有文件路径、也不是故障排查时，按简单问答处理。
        // 这样「解释一下什么是依赖注入」不会因为「依赖」二字被当成改代码。
        if (!action
            && !CodeShape.IsMatch(lower)
            && !ContainsAny(lower, FailurePhrases)
            && ConceptQuestion.IsMatch(text))
        {
            return new ModelTaskClassification(
                ModelTaskKind.Simple,
                0.5,
                Math.Clamp(18 + text.Length / 12, 0, 100),
                "概念性提问");
        }

        // 复杂任务优先：明确复杂词、超长输入、长上下文、或「代码 + 多步骤 + 有长度」。
        if (complexHits >= 2 || longContext || (complexHits >= 1 && (longInput || codeHits >= 2)))
        {
            var complexity = Math.Clamp(78 + complexHits * 4 + (longInput ? 8 : 0) + (longContext ? 10 : 0), 0, 100);
            return new ModelTaskClassification(
                ModelTaskKind.Complex,
                0.85,
                complexity,
                complexHits > 0 ? "命中架构 / 排查 / 多文件等复杂信号" : "长上下文任务");
        }

        if (longInput && (codeHits > 0 || codeShape || editsSelected) && multiStepHits >= 2)
        {
            return new ModelTaskClassification(
                ModelTaskKind.Complex,
                0.7,
                Math.Clamp(82 + multiStepHits * 3, 0, 100),
                "长输入且包含多个步骤 / 要求");
        }

        // 代码任务：代码词、代码形状、明确动手指令、或启用了写入 / 命令类工具且要动手。
        if (codeShape || codeHits > 0 || (action && editsSelected))
        {
            var complexity = Math.Clamp(
                50 + codeHits * 3 + (codeShape ? 12 : 0) + Math.Min(12, multiStepHits * 3),
                0,
                100);

            return new ModelTaskClassification(
                ModelTaskKind.Code,
                codeShape || codeHits >= 2 ? 0.85 : 0.72,
                complexity,
                codeShape ? "命中代码 / 文件路径" : "命中代码编辑信号");
        }

        // 明确要动手但没识别出代码词：仍然按代码任务，避免把实现类请求丢给闲聊模型。
        if (action)
        {
            return new ModelTaskClassification(
                ModelTaskKind.Code,
                0.62,
                Math.Clamp(45 + multiStepHits * 4, 0, 100),
                "明确的动手指令");
        }

        // 默认简单问答；置信度故意低于混合阈值，拿不准时让路由模型再看一眼。
        var simpleComplexity = Math.Clamp(18 + text.Length / 12 + multiStepHits * 2, 0, 100);
        return new ModelTaskClassification(
            ModelTaskKind.Simple,
            0.45,
            simpleComplexity,
            "未命中工程 / 复杂任务信号");
    }

    private static bool LooksLikeCode(string lower, IReadOnlySet<string>? tools) =>
        CodeShape.IsMatch(lower)
        || CountHits(lower, CodePhrases) > 0
        || tools?.Contains("edits") == true;

    private static int CountHits(string text, IReadOnlyList<string> phrases) =>
        phrases.Count(phrase => text.Contains(phrase, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsAny(string text, IReadOnlyList<string> words) =>
        words.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
}
