using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// todo_write（P5）：整体替换当前会话的计划，最多 20 项、每项 200 字符、最多 1 个 in_progress。
/// 计划存到 <see cref="ChatSession.Todos"/>，由 ChatRoom 面板与状态包展示。
/// </summary>
public sealed class TodoToolPack : IAgentToolPack
{
    public const string PackId = "todo";
    public const string FunctionId = "todo_write";
    public const int MaxItems = 20;
    public const int MaxContentChars = 200;

    public AgentToolPackDescriptor Descriptor { get; } = new(
        Id: PackId,
        Name: "任务计划",
        Description: "维护当前任务的多步计划（整体替换，最多 20 项）。",
        Glyph: "☑",
        Risk: AgentToolRisk.Low,
        Order: 5,
        DefaultEnabled: true,
        WindowsOnly: false,
        Functions:
        [
            new AgentToolFunctionDescriptor(
                FunctionId,
                "更新任务计划",
                AgentToolRisk.Low,
                RequiresApproval: false)
        ],
        BuiltIn: true);

    public IReadOnlyList<AITool> Create(ToolContext context)
    {
        var functions = new TodoFunctions(context);
        return
        [
            AIFunctionFactory.Create(
                functions.TodoWrite,
                name: FunctionId,
                description:
                    "整体替换当前任务计划。多步任务先列出步骤；每完成一步更新状态；"
                    + "结束前所有项应为 completed 或说明未完成原因。")
        ];
    }

    /// <summary>公开给测试。</summary>
    public static string Write(ChatSession? session, IReadOnlyList<TodoDraft>? items)
    {
        if (session is null)
        {
            return "ERROR: 当前没有可写的会话。";
        }

        if (items is null || items.Count == 0)
        {
            return "ERROR: 至少需要 1 项计划。";
        }

        if (items.Count > MaxItems)
        {
            return $"ERROR: 计划最多 {MaxItems} 项，当前 {items.Count} 项。";
        }

        var warning = new StringBuilder();
        var result = new List<TodoItem>();
        var seenInProgress = false;

        foreach (var draft in items)
        {
            var content = draft.Content?.Trim() ?? string.Empty;
            if (content.Length == 0)
            {
                return "ERROR: 计划项内容不能为空。";
            }

            if (content.Length > MaxContentChars)
            {
                return $"ERROR: 计划项不能超过 {MaxContentChars} 字符：{content[..40]}…";
            }

            var status = ParseStatus(draft.Status);
            if (status == TodoStatus.InProgress)
            {
                if (seenInProgress)
                {
                    status = TodoStatus.Pending;
                    warning.Append("（只保留第一个 in_progress，其余已降级为 pending）");
                }
                else
                {
                    seenInProgress = true;
                }
            }

            result.Add(new TodoItem(content, status));
        }

        session.Todos = result;
        var completed = result.Count(item => item.Status == TodoStatus.Completed);
        var builder = new StringBuilder();
        builder.Append("已更新计划：").Append(completed).Append('/').Append(result.Count).AppendLine(" 完成");

        foreach (var item in result)
        {
            builder.Append(item.Icon).Append(' ').AppendLine(item.Content);
        }

        if (warning.Length > 0)
        {
            builder.AppendLine().Append(warning);
        }

        return builder.ToString().TrimEnd();
    }

    private static TodoStatus ParseStatus(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "in_progress" or "in-progress" or "进行中" => TodoStatus.InProgress,
        "completed" or "done" or "已完成" => TodoStatus.Completed,
        _ => TodoStatus.Pending
    };

    private sealed class TodoFunctions(ToolContext context)
    {
        public string TodoWrite(
            [Description("完整计划列表（整体替换）。每项 content ≤200 字符，status=pending/in_progress/completed。")]
            IReadOnlyList<TodoDraft>? items = null) =>
            Write(context.Session, items);
    }
}

/// <summary>todo_write 的输入项；用类而不是 record 是为了让 AIFunction 生成可写 schema。</summary>
public sealed class TodoDraft
{
    public string Content { get; set; } = string.Empty;

    public string Status { get; set; } = "pending";
}
