namespace SDemo.DesktopAgent.Models;

/// <summary>输入框提交的内容：文本 + 本次附带的文件。</summary>
public sealed record ComposerSubmission(string Text, IReadOnlyList<ChatAttachment> Attachments);
