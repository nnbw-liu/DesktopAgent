using System.ComponentModel;
using System.IO;
using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Sandbox;
using SDemo.DesktopAgent.Sandbox.Protocol;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.Platform.Windows.Tools;

/// <summary>
/// PDF 工具包（S5）：<c>pdf_info</c> / <c>pdf_read</c>。
/// 解析不在宿主进程里做，而是复制到沙箱 job 后由 <c>SandboxHost</c> 执行：
/// 损坏 / 加密 / 解析器崩溃都不会拖垮主应用；沙箱不可用时工具直接不可用。
/// 只读，没有写副作用，因此不需要审批。
/// </summary>
public sealed class PdfToolPack : IAgentToolPack
{
    public const string PackId = "pdf";
    public const string InfoFunctionId = "pdf_info";
    public const string ReadFunctionId = "pdf_read";

    public AgentToolPackDescriptor Descriptor { get; } = new(
        Id: PackId,
        Name: "PDF",
        Description: "在沙箱中查看 / 读取 PDF 文本；只能访问工作区与会话附件，不能联网。",
        Glyph: "¶",
        Risk: AgentToolRisk.Low,
        Order: 25,
        DefaultEnabled: true,
        WindowsOnly: true,
        Functions:
        [
            new AgentToolFunctionDescriptor(InfoFunctionId, "PDF 信息"),
            new AgentToolFunctionDescriptor(ReadFunctionId, "读取 PDF")
        ]);

    public IReadOnlyList<AITool> Create(ToolContext context)
    {
        // 每次 Run 创建一个绑定 context 的函数对象，避免单例 Pack 持有上一个会话的附件目录。
        var functions = new PdfFunctions(context, this);

        return
        [
            AIFunctionFactory.Create(
                functions.PdfInfoAsync,
                name: InfoFunctionId,
                description:
                    "查看 PDF 的页数、是否加密、是否有文本层。path 为工作区内或当前会话附件里的 .pdf 路径。"),

            AIFunctionFactory.Create(
                functions.PdfReadAsync,
                name: ReadFunctionId,
                description:
                    "读取 PDF 指定页的文本（沙箱内解析，无网络）。pages 形如 1-5,8，默认前 5 页；"
                    + "扫描件没有文本层，需要 OCR（未启用）。")
        ];
    }

    /// <summary>公开给集成测试：与 AIFunction 调用走同一条校验 / 审计路径。</summary>
    public async Task<string> PdfInfoAsync(
        ToolContext context,
        string path,
        CancellationToken cancellationToken = default)
    {
        var resolved = Resolve(context, path);
        if (resolved.Error is not null)
        {
            WriteAudit(context, InfoFunctionId, resolved.Error, extraction: null);
            return Error(resolved.Error);
        }

        var extraction = await ExtractAsync(context, resolved.FullPath!, cancellationToken)
            .ConfigureAwait(false);

        var text = PdfResultFormatter.FormatInfo(resolved.DisplayName!, extraction);
        WriteAudit(context, InfoFunctionId, extraction.Ok ? null : text, extraction);
        return text;
    }

    /// <summary>公开给集成测试：与 AIFunction 调用走同一条校验 / 审计路径。</summary>
    public async Task<string> PdfReadAsync(
        ToolContext context,
        string path,
        string? pages = null,
        int maxChars = 0,
        CancellationToken cancellationToken = default)
    {
        var resolved = Resolve(context, path);
        if (resolved.Error is not null)
        {
            WriteAudit(context, ReadFunctionId, resolved.Error, extraction: null);
            return Error(resolved.Error);
        }

        var extraction = await ExtractAsync(context, resolved.FullPath!, pages, maxChars, cancellationToken)
            .ConfigureAwait(false);

        var text = PdfResultFormatter.FormatRead(resolved.DisplayName!, extraction);
        WriteAudit(context, ReadFunctionId, extraction.Ok ? null : text, extraction);
        return text;
    }

    private static async Task<PdfExtraction> ExtractAsync(
        ToolContext context,
        string fullPath,
        CancellationToken cancellationToken) =>
        await context.SandboxJobs.ExtractPdfAsync(
                fullPath,
                pages: null,
                maxChars: PdfLimits.DefaultChars,
                cancellationToken)
            .ConfigureAwait(false);

    private static async Task<PdfExtraction> ExtractAsync(
        ToolContext context,
        string fullPath,
        string? pages,
        int maxChars,
        CancellationToken cancellationToken) =>
        await context.SandboxJobs.ExtractPdfAsync(
                fullPath,
                pages,
                maxChars <= 0 ? PdfLimits.DefaultChars : Math.Clamp(maxChars, 1, PdfLimits.MaxChars),
                cancellationToken)
            .ConfigureAwait(false);

    private static void WriteAudit(
        ToolContext context,
        string toolId,
        string? error,
        PdfExtraction? extraction)
    {
        context.Audit.Write(new ToolAuditEntry
        {
            SessionId = context.SessionId,
            ToolId = toolId,
            Approval = "NotRequired",
            Error = error,
            ChangeCount = 0,
            StdoutBytes = extraction is null ? 0 : extraction.Pages.Sum(page => page.Text.Length),
            // PDF 没有脚本，也不记录页内容：只保留可解释的状态。
            ScriptSha256 = null
        });
    }

    private static string Error(string message) =>
        message.StartsWith("ERROR:", StringComparison.Ordinal) ? message : $"ERROR: {message}";

    /// <summary>
    /// 路径 jail：允许根 = 当前工作区 + 会话附件目录（<see cref="ToolContext.ReadOnlyRoots"/>）。
    /// 相对路径按允许根依次解析；绝对路径必须落在某个允许根内。
    /// 逐段拒绝重解析点，避免符号链接 / junction 逃逸。
    /// </summary>
    private static PdfPath Resolve(ToolContext context, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return PdfPath.Fail("PDF 路径为空。");
        }

        var value = path.Trim();

        if (value.Contains('\0'))
        {
            return PdfPath.Fail("PDF 路径包含非法字符。");
        }

        if (value.StartsWith(@"\\", StringComparison.Ordinal)
            || value.StartsWith("//", StringComparison.Ordinal)
            || value.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return PdfPath.Fail($"拒绝 UNC / 设备路径：{path}");
        }

        var roots = new List<string>();

        if (!string.IsNullOrWhiteSpace(context.WorkspaceRoot))
        {
            roots.Add(context.WorkspaceRoot);
        }

        roots.AddRange(context.ReadOnlyRoots.Where(root => !string.IsNullOrWhiteSpace(root)));

        if (roots.Count == 0)
        {
            return PdfPath.Fail("没有允许读取的根目录。");
        }

        if (Path.IsPathRooted(value))
        {
            string full;
            try
            {
                full = Path.GetFullPath(value);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return PdfPath.Fail($"PDF 路径无效：{path}");
            }

            var root = roots.FirstOrDefault(candidate => SandboxPathGuard.IsUnder(candidate, full));
            return root is null
                ? PdfPath.Fail($"PDF 路径不在允许的工作区或会话附件目录内：{path}")
                : Validate(full, root, path, LimitFor(context, root));
        }

        var segments = value
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment != ".")
            .ToArray();

        if (segments.Any(segment => segment == ".."))
        {
            return PdfPath.Fail($"PDF 路径不能包含 ..：{path}");
        }

        foreach (var root in roots)
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var full = Path.GetFullPath(
                Path.Combine(normalizedRoot, string.Join(Path.DirectorySeparatorChar, segments)));

            if (!SandboxPathGuard.IsUnder(normalizedRoot, full))
            {
                return PdfPath.Fail($"PDF 路径超出允许根目录：{path}");
            }

            if (File.Exists(full))
            {
                return Validate(full, normalizedRoot, path, LimitFor(context, normalizedRoot));
            }
        }

        return PdfPath.Fail($"PDF 文件不存在：{path}");
    }

    private static PdfPath Validate(string fullPath, string root, string original, long sizeLimit)
    {
        try
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var relative = Path.GetRelativePath(normalizedRoot, fullPath);

            var current = normalizedRoot;
            foreach (var segment in relative.Split(
                         Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment is "." or "..")
                {
                    return PdfPath.Fail($"PDF 路径不能包含 . / ..：{original}");
                }

                current = Path.Combine(current, segment);

                if (!File.Exists(current) && !Directory.Exists(current))
                {
                    break;
                }

                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return PdfPath.Fail($"PDF 路径包含符号链接或 junction：{original}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return PdfPath.Fail($"PDF 路径无效：{original}");
        }

        if (!File.Exists(fullPath))
        {
            return PdfPath.Fail($"PDF 文件不存在：{original}");
        }

        if (!string.Equals(Path.GetExtension(fullPath), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return PdfPath.Fail($"只支持 .pdf 文件：{original}");
        }

        var length = new FileInfo(fullPath).Length;
        if (length <= 0)
        {
            return PdfPath.Fail($"PDF 文件为空：{original}");
        }

        // 附件最多 25MB（上传时就限制），工作区里的工具读取上限 50MB；超过直接报错，不尝试解析。
        if (length > sizeLimit)
        {
            return PdfPath.Fail(
                $"PDF 过大（{length / 1024 / 1024} MB），上限 {sizeLimit / 1024 / 1024} MB：{original}");
        }

        return new PdfPath(fullPath, Path.GetFileName(fullPath), Error: null);
    }

    /// <summary>工作区里的 PDF 允许 50MB；会话附件目录（只读根）沿用 25MB 上传上限。</summary>
    private static long LimitFor(ToolContext context, string root)
    {
        if (string.IsNullOrWhiteSpace(context.WorkspaceRoot))
        {
            return PdfLimits.MaxAttachmentBytes;
        }

        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.WorkspaceRoot)),
            StringComparison.OrdinalIgnoreCase)
            ? PdfLimits.MaxToolFileBytes
            : PdfLimits.MaxAttachmentBytes;
    }

    private sealed record PdfPath(string? FullPath, string? DisplayName, string? Error)
    {
        public static PdfPath Fail(string error) => new(null, null, error);
    }

    private sealed class PdfFunctions(ToolContext context, PdfToolPack pack)
    {
        public Task<string> PdfInfoAsync(
            [Description("工作区内或当前会话附件里的 .pdf 路径。")] string path,
            CancellationToken cancellationToken = default) =>
            pack.PdfInfoAsync(context, path, cancellationToken);

        public Task<string> PdfReadAsync(
            [Description("工作区内或当前会话附件里的 .pdf 路径。")] string path,
            [Description("页范围，形如 1-5,8；省略时读前 5 页，单次最多 50 页。")] string? pages = null,
            [Description("最大字符数，1–40000；省略时 20000。")] int maxChars = 0,
            CancellationToken cancellationToken = default) =>
            pack.PdfReadAsync(context, path, pages, maxChars, cancellationToken);
    }
}
