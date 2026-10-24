namespace SDemo.DesktopAgent.Models;

/// <summary>输入框提交的内容：文本 + 本次附带的文件 + 用户点名的技能。</summary>
/// <param name="Text">原始输入（`/name` 这类字面量保留，模型也能看到）。</param>
/// <param name="Attachments">本次附带的文件。</param>
/// <param name="InvokedSkill">用户用 `/技能` 显式点名、或点了技能 chip 的技能名；正文由上下文层注入。</param>
public sealed record ComposerSubmission(
    string Text,
    IReadOnlyList<ChatAttachment> Attachments,
    string? InvokedSkill = null);
