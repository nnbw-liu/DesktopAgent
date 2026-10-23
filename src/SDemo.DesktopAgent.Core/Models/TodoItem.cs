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
    public string Icon => Status switch
    {
        TodoStatus.Completed => "☑",
        TodoStatus.InProgress => "◐",
        _ => "☐"
    };
}
