using System.ComponentModel;
using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Sandbox.Protocol;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// Office 工具包：把 .docx / .xlsx / .pptx 读成 Markdown，或把 Markdown 写成这三种文档。
/// <para>
/// 读是只读低风险；写是文件副作用，走通用审批，默认要用户确认。
/// 解析在宿主进程内完成（OpenXml 不执行宏），文件大小与路径都受 <see cref="OfficeFile"/> 约束。
/// </para>
/// </summary>
public sealed class OfficeToolPack : IAgentToolPack
{
    public const string PackId = "office";
    public const string InfoFunctionId = "office_info";
    public const string ReadFunctionId = "office_read";
    public const string WriteFunctionId = "office_write";

    public AgentToolPackDescriptor Descriptor { get; } = new(
        Id: PackId,
        Name: "Office 文档",
        Description: "读取 / 生成 Word、Excel、PowerPoint；内容以 Markdown 进出，适合分析需求与产出文档。",
        Glyph: "◫",
        Risk: AgentToolRisk.Medium,
        Order: 24,
        DefaultEnabled: true,
        WindowsOnly: false,
        Functions:
        [
            new AgentToolFunctionDescriptor(InfoFunctionId, "文档结构"),
            new AgentToolFunctionDescriptor(ReadFunctionId, "读取为 Markdown"),
            new AgentToolFunctionDescriptor(
                WriteFunctionId,
                "写入文档",
                AgentToolRisk.Medium,
                RequiresApproval: true)
        ],
        BuiltIn: true);

    public IReadOnlyList<AITool> Create(ToolContext context)
    {
        var functions = new OfficeFunctions(context);

        return
        [
            AIFunctionFactory.Create(
                functions.Info,
                name: InfoFunctionId,
                description:
                    "查看 Word / Excel / PowerPoint 的结构（标题、工作表、幻灯片标题），用于决定读哪一部分。"
                    + "path 是工作区内或当前会话附件里的 .docx / .xlsx / .pptx。"),

            AIFunctionFactory.Create(
                functions.Read,
                name: ReadFunctionId,
                description:
                    "把 Word / Excel / PowerPoint 读成 Markdown。path 为 .docx / .xlsx / .pptx；"
                    + "Excel 可用 sheet（名称或序号）与 range（如 A1:F40）选范围，"
                    + "PowerPoint 可用 slides（如 1-5,8）选页；maxChars 控制返回长度。"),

            AIFunctionFactory.Create(
                functions.WriteAsync,
                name: WriteFunctionId,
                description:
                    "把 Markdown 写成 .docx / .xlsx / .pptx（按 path 扩展名决定格式，只写工作区）。"
                    + "Word：标题 / 段落 / 列表 / 表格 / 代码；Excel：Markdown 表格，## 标题为工作表名；"
                    + "PowerPoint：--- 分页，# / ## 为页标题。覆盖已有文件要 overwrite=true。")
        ];
    }

    private sealed class OfficeFunctions(ToolContext context)
    {
        public string Info(
            [Description("工作区内或当前会话附件里的 .docx / .xlsx / .pptx 路径。")] string path)
        {
            var resolved = OfficeFile.ResolveRead(context, path);
            if (resolved.Error is not null)
            {
                return Error(resolved.Error);
            }

            try
            {
                var text = OfficeReader.Describe(resolved.FullPath!);
                WriteAudit(InfoFunctionId, error: null);
                return text;
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                WriteAudit(InfoFunctionId, ex.Message);
                return Error(DescribeFailure(path, ex));
            }
        }

        public string Read(
            [Description("工作区内或当前会话附件里的 .docx / .xlsx / .pptx 路径。")] string path,
            [Description("Excel 工作表名或从 1 开始的序号；省略读全部工作表。")] string? sheet = null,
            [Description("Excel 单元格范围，如 A1:F40；省略读整张表。")] string? range = null,
            [Description("PowerPoint 页范围，如 1-5,8；省略读全部幻灯片。")] string? slides = null,
            [Description("最多返回多少字符，默认 40000，最大 120000。")] int maxChars = 0,
            [Description("Excel 每张表最多读多少行，默认 200，最大 2000。")] int maxRows = 200)
        {
            var resolved = OfficeFile.ResolveRead(context, path);
            if (resolved.Error is not null)
            {
                return Error(resolved.Error);
            }

            try
            {
                var text = OfficeReader.Read(resolved.FullPath!, sheet, range, slides, maxChars, maxRows);
                RecordRead(resolved.FullPath!);
                WriteAudit(ReadFunctionId, error: null, stdoutBytes: text.Length);
                return text;
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                WriteAudit(ReadFunctionId, ex.Message);
                return Error(DescribeFailure(path, ex));
            }
        }

        public async Task<string> WriteAsync(
            [Description("相对工作区的目标路径，扩展名决定格式（.docx / .xlsx / .pptx）。")] string path,
            [Description("Markdown 内容。Excel 必须是 Markdown 表格或 CSV；PowerPoint 用 --- 分页。")] string content,
            [Description("目标已存在时是否覆盖，默认 false。")] bool overwrite = false,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return Error("内容为空，没有写入任何文件。");
            }

            var resolved = OfficeFile.ResolveWrite(context, path);
            if (resolved.Error is not null)
            {
                return Error(resolved.Error);
            }

            var target = resolved.FullPath!;
            var exists = File.Exists(target);

            if (exists && !overwrite)
            {
                return Error($"{path} 已存在；确认要覆盖时传 overwrite=true。");
            }

            if (context.Scope?.ConfirmFileWrite ?? context.Settings.Current.ConfirmFileWrite)
            {
                var decision = await context.Approvals.RequestAsync(
                    new ToolApprovalRequest(
                        ToolId: WriteFunctionId,
                        Kind: ToolApprovalKind.FileWrite,
                        Title: exists ? "允许覆盖这个 Office 文件吗？" : "允许创建这个 Office 文件吗？",
                        Summary: $"路径：{Relative(target)} · 格式：{OfficeFile.DisplayFormat(Path.GetExtension(target))}",
                        Detail: $"目标：{Relative(target)}\n\n{Truncate(content, 5000)}",
                        Risk: AgentToolRisk.Medium,
                        AllowSessionRemember: false,
                        SessionKey: null),
                    cancellationToken).ConfigureAwait(false);

                if (decision == ToolApprovalDecision.Deny)
                {
                    WriteAudit(WriteFunctionId, "用户拒绝了写入。", approval: "Deny");
                    return Error("用户拒绝了这次写入，未创建或修改文件。");
                }
            }

            var directory = Path.GetDirectoryName(target)!;
            // 临时文件保留目标扩展名：Writer 按扩展名选格式，最后的 Describe 也要能打开它。
            var temporary = Path.Combine(
                directory,
                $"~{Path.GetFileNameWithoutExtension(target)}.{Guid.NewGuid():N}{Path.GetExtension(target)}");

            try
            {
                Directory.CreateDirectory(directory);
                var (_, blocks, detail) = OfficeWriter.Write(temporary, content);

                // OfficeWriter 之外再兜底校验一次：临时文件必须能作为对应格式打开。
                OfficeReader.Describe(temporary);

                File.Move(temporary, target, overwrite: true);
                try
                {
                    RecordRead(target);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 读前写登记只是增强：登记失败不能把「已经写成功」说成失败。
                }

                WriteAudit(WriteFunctionId, error: null, changes: 1);
                return $"已写入 {Relative(target)}（{OfficeFile.DisplayFormat(Path.GetExtension(target))}，{detail}，{blocks} 个 Markdown 块）。";
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                TryDelete(temporary);
                WriteAudit(WriteFunctionId, ex.Message);
                return Error($"写入失败，未修改目标文件：{ex.Message}");
            }
        }

        private void RecordRead(string fullPath)
        {
            if (context.Reads is null
                || string.IsNullOrWhiteSpace(context.WorkspaceRoot)
                || !SandboxPathGuard.IsUnder(context.WorkspaceRoot, fullPath))
            {
                return;
            }

            var relative = SDemo.DesktopAgent.Editing.WorkspacePath.Relative(context.WorkspaceRoot, fullPath);
            var hash = SDemo.DesktopAgent.Editing.FileReadRegistry.HashFile(fullPath);
            if (hash is not null)
            {
                context.Reads.RecordRead(context.SessionId, relative, hash);
            }
        }

        private string Relative(string fullPath)
        {
            try
            {
                return string.IsNullOrWhiteSpace(context.WorkspaceRoot)
                    ? Path.GetFileName(fullPath)
                    : SDemo.DesktopAgent.Editing.WorkspacePath.Relative(context.WorkspaceRoot, fullPath);
            }
            catch (InvalidOperationException)
            {
                return Path.GetFileName(fullPath);
            }
        }

        private void WriteAudit(
            string toolId,
            string? error,
            string approval = "NotRequired",
            int stdoutBytes = 0,
            int changes = 0) =>
            context.Audit.Write(new ToolAuditEntry
            {
                SessionId = context.SessionId,
                ToolId = toolId,
                Approval = approval,
                Error = error,
                StdoutBytes = stdoutBytes,
                ChangeCount = changes
            });
    }

    private static string DescribeFailure(string path, Exception exception) => exception switch
    {
        FileFormatException => $"{path} 不是可读的 Office 文件（可能已损坏、被加密，或是旧版 .doc/.xls/.ppt）。",
        InvalidDataException => exception.Message,
        _ => $"{path} 读取失败：{exception.Message}"
    };

    private static bool IsExpected(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or FileFormatException
            or OpenXmlPackageException
            or NotSupportedException
            or ArgumentException;

    private static string Error(string message) =>
        message.StartsWith("错误", StringComparison.Ordinal) ? message : "错误：" + message;

    private static string Truncate(string text, int limit) =>
        text.Length <= limit ? text : string.Concat(text.AsSpan(0, limit), "\n…（已截断）");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时文件删不掉不影响结果。
        }
    }
}
