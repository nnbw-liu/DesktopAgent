namespace SDemo.DesktopAgent.Models;

/// <summary>任务步骤状态。</summary>
public enum TodoStatus
{
    Pending,
    InProgress,
    Completed
}

/// <summary>一条 todo；todo_write 的语义是整体替换，不是追加。</summary>
public sealed record TodoItem(string Content, TodoStatus Status)
{
    /// <summary>计划最多保留的条数；工具与状态包共用同一上限。</summary>
    public const int MaxItems = 20;

    public string Icon => Status switch
    {
        TodoStatus.Completed => "☑",
        TodoStatus.InProgress => "◐",
        _ => "☐"
    };
}
