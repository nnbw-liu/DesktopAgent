using Microsoft.EntityFrameworkCore;
using SDemo.DesktopAgent.Models;

namespace SDemo.DesktopAgent.Data;

/// <summary>
/// 单个聊天数据库的上下文。每个工作区一个 SQLite 文件、历史聊天一个文件，
/// 文件里的表结构完全一样，只是数据范围不同。
/// </summary>
public sealed class ChatDbContext(DbContextOptions<ChatDbContext> options) : DbContext(options)
{
    public DbSet<SessionRecord> Sessions => Set<SessionRecord>();

    public DbSet<MessageRecord> Messages => Set<MessageRecord>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var session = model.Entity<SessionRecord>();
        session.ToTable("sessions");
        session.HasKey(record => record.Id);
        session.Property(record => record.Title).IsRequired();
        session.Property(record => record.Meta);
        session.Property(record => record.WorkspaceId);
        session.HasIndex(record => record.UpdatedAt);
        session.HasMany(record => record.Messages)
            .WithOne()
            .HasForeignKey(message => message.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        var message = model.Entity<MessageRecord>();
        message.ToTable("messages");
        message.HasKey(record => record.Id);
        message.Property(record => record.SessionId).IsRequired();
        message.Property(record => record.Role).HasConversion<string>().IsRequired();
        message.Property(record => record.Content).IsRequired();
        message.Property(record => record.Reasoning).IsRequired();
        message.Property(record => record.AttachmentsJson);
        message.Property(record => record.CreatedAt);
        message.Property(record => record.DurationMs);
        message.Property(record => record.InputTokens);
        message.Property(record => record.OutputTokens);
        message.Property(record => record.ToolCallsJson);
        message.HasIndex(record => record.SessionId);
    }
}

/// <summary>数据库里的会话行（和 <see cref="ChatSession"/> 领域模型分开，避免 EF 干扰 UI 模型）。</summary>
public sealed class SessionRecord
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Meta { get; set; }

    public string? WorkspaceId { get; set; }

    public bool IsPinned { get; set; }

    public DateTime UpdatedAt { get; set; }

    public List<MessageRecord> Messages { get; set; } = [];
}

/// <summary>数据库里的消息行；<see cref="Order"/> 保证加载时顺序和发送顺序一致。</summary>
public sealed class MessageRecord
{
    public string Id { get; set; } = string.Empty;

    public string SessionId { get; set; } = string.Empty;

    public int Order { get; set; }

    public ChatMessageRole Role { get; set; }

    public string Content { get; set; } = string.Empty;

    public string Reasoning { get; set; } = string.Empty;

    /// <summary>附件列表的 JSON；空表示没有附件。</summary>
    public string? AttachmentsJson { get; set; }

    /// <summary>消息时间（老库为空时回退到会话更新时间）。</summary>
    public DateTime? CreatedAt { get; set; }

    /// <summary>助手消息的生成耗时（毫秒）。</summary>
    public long DurationMs { get; set; }

    public int InputTokens { get; set; }

    public int OutputTokens { get; set; }

    /// <summary>工具调用次数的 JSON，例如 {"web_search":2}。</summary>
    public string? ToolCallsJson { get; set; }
}
