using System.IO;
using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Editing;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>
/// Office 工具包：Markdown ↔ Word / Excel / PowerPoint 的基本读写。
/// 重点不是像素级还原，而是「写进去能读回来、结构还在、危险路径写不进去」。
/// </summary>
public sealed class OfficeToolPackTests
{
    [Fact]
    public async Task Word写入后能读回标题段落列表与表格()
    {
        using var temp = new TempDirectory();
        var (pack, context) = Create(temp.Root);
        var markdown = """
            # 需求总结

            这是一段**加粗**的说明。

            ## 背景

            - 现状 A
            - 现状 B

            | 模块 | 说明 |
            | --- | --- |
            | 登录 | 账号密码 |
            | 导出 | CSV |
            """;

        var write = await InvokeAsync(pack, context, "office_write", ("path", "需求.docx"), ("content", markdown));
        Assert.Contains("已写入 需求.docx", write, StringComparison.Ordinal);

        var read = await InvokeAsync(pack, context, "office_read", ("path", "需求.docx"));
        Assert.Contains("# 需求总结", read, StringComparison.Ordinal);
        Assert.Contains("## 背景", read, StringComparison.Ordinal);
        Assert.Contains("- 现状 A", read, StringComparison.Ordinal);
        Assert.Contains("| 模块 | 说明 |", read, StringComparison.Ordinal);
        Assert.Contains("| 登录 | 账号密码 |", read, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Excel写入后按工作表读回Markdown表格()
    {
        using var temp = new TempDirectory();
        var (pack, context) = Create(temp.Root);
        var markdown = """
            # 需求清单

            ## 一期

            | 编号 | 需求 | 优先级 |
            | --- | --- | --- |
            | 1 | 登录 | 高 |
            | 2 | 导出 | 中 |

            ## 二期

            | 编号 | 需求 |
            | --- | --- |
            | 3 | 报表 |
            """;

        var write = await InvokeAsync(pack, context, "office_write", ("path", "需求.xlsx"), ("content", markdown));
        Assert.Contains("工作表 2", write, StringComparison.Ordinal);

        var read = await InvokeAsync(pack, context, "office_read", ("path", "需求.xlsx"));
        Assert.Contains("## 一期", read, StringComparison.Ordinal);
        Assert.Contains("## 二期", read, StringComparison.Ordinal);
        Assert.Contains("| 编号 | 需求 | 优先级 |", read, StringComparison.Ordinal);
        Assert.Contains("| 1 | 登录 | 高 |", read, StringComparison.Ordinal);

        // 只读指定工作表。
        var second = await InvokeAsync(pack, context, "office_read", ("path", "需求.xlsx"), ("sheet", "二期"));
        Assert.Contains("| 3 | 报表 |", second, StringComparison.Ordinal);
        Assert.DoesNotContain("登录", second, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PPT写入后按页读回标题与要点()
    {
        using var temp = new TempDirectory();
        var (pack, context) = Create(temp.Root);
        var markdown = """
            # 项目背景

            - 现状
            - 问题

            ---

            # 需求分析

            - 登录
            - 导出
            """;

        var write = await InvokeAsync(pack, context, "office_write", ("path", "评审.pptx"), ("content", markdown));
        Assert.Contains("幻灯片 2", write, StringComparison.Ordinal);

        var read = await InvokeAsync(pack, context, "office_read", ("path", "评审.pptx"));
        Assert.Contains("## 第 1 页 · 项目背景", read, StringComparison.Ordinal);
        Assert.Contains("- 现状", read, StringComparison.Ordinal);
        Assert.Contains("## 第 2 页 · 需求分析", read, StringComparison.Ordinal);
        Assert.Contains("- 登录", read, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 结构查询给出工作表与幻灯片标题()
    {
        using var temp = new TempDirectory();
        var (pack, context) = Create(temp.Root);

        await InvokeAsync(pack, context, "office_write", ("path", "结构.xlsx"),
            ("content", "## 数据\n\n| a | b |\n| --- | --- |\n| 1 | 2 |"));
        await InvokeAsync(pack, context, "office_write", ("path", "结构.pptx"),
            ("content", "# 第一页\n\n- x\n\n---\n\n# 第二页\n\n- y"));

        var excel = await InvokeAsync(pack, context, "office_info", ("path", "结构.xlsx"));
        Assert.Contains("工作表 1", excel, StringComparison.Ordinal);
        Assert.Contains("数据", excel, StringComparison.Ordinal);

        var ppt = await InvokeAsync(pack, context, "office_info", ("path", "结构.pptx"));
        Assert.Contains("幻灯片 2", ppt, StringComparison.Ordinal);
        Assert.Contains("第一页", ppt, StringComparison.Ordinal);
        Assert.Contains("第二页", ppt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 拒绝工作区外的读取路径()
    {
        using var temp = new TempDirectory();
        var (pack, context) = Create(temp.Root);
        var outside = Path.Combine(Path.GetTempPath(), "sdemo-outside-" + Guid.NewGuid().ToString("N") + ".docx");
        await File.WriteAllTextAsync(outside, "x");

        try
        {
            var result = await InvokeAsync(pack, context, "office_read", ("path", outside));
            Assert.StartsWith("错误", result, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task 覆盖已有文件需要显式overwrite()
    {
        using var temp = new TempDirectory();
        var (pack, context) = Create(temp.Root);

        await InvokeAsync(pack, context, "office_write", ("path", "a.docx"), ("content", "# 一"));
        var blocked = await InvokeAsync(pack, context, "office_write", ("path", "a.docx"), ("content", "# 二"));
        Assert.Contains("overwrite=true", blocked, StringComparison.Ordinal);

        var allowed = await InvokeAsync(pack, context, "office_write",
            ("path", "a.docx"), ("content", "# 二"), ("overwrite", true));
        Assert.Contains("已写入", allowed, StringComparison.Ordinal);

        var read = await InvokeAsync(pack, context, "office_read", ("path", "a.docx"));
        Assert.Contains("# 二", read, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 用户拒绝审批时不落盘()
    {
        using var temp = new TempDirectory();
        var settings = new StubSettingsService();
        settings.Current.ConfirmFileWrite = true;
        var approvals = new ToolApprovalService();
        var (pack, context) = Create(temp.Root, settings, approvals);

        var task = InvokeAsync(pack, context, "office_write", ("path", "审批.docx"), ("content", "# 标题"));
        await WaitUntilAsync(() => approvals.Current is not null);
        approvals.Resolve(ToolApprovalDecision.Deny);
        var result = await task;

        Assert.Contains("用户拒绝", result, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(temp.Root, "审批.docx")));
    }

    [Fact]
    public async Task 上传附件提取器把PPT转成Markdown()
    {
        using var temp = new TempDirectory();
        var (pack, context) = Create(temp.Root);
        var path = Path.Combine(temp.Root, "上传.pptx");

        await InvokeAsync(pack, context, "office_write", ("path", "上传.pptx"),
            ("content", "# 背景\n\n- 现状\n\n---\n\n# 目标\n\n- 目标一"));

        var extractor = new OfficeAttachmentExtractor();
        var result = await extractor.ExtractAsync(path);

        Assert.NotNull(result);
        Assert.Equal("pptx", result!.Format);
        Assert.Contains("背景", result.Text, StringComparison.Ordinal);
        Assert.Contains("- 目标一", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void 工具包描述里包含三个Office函数()
    {
        var pack = new OfficeToolPack();

        Assert.Equal("office", pack.Descriptor.Id);
        Assert.True(pack.Descriptor.BuiltIn);
        Assert.Equal(3, pack.Descriptor.Functions.Count);
        Assert.Contains(pack.Descriptor.Functions, function => function.Id == "office_write" && function.RequiresApproval);
    }

    [Fact]
    public async Task 生成的三种文档都通过OpenXml结构校验()
    {
        using var temp = new TempDirectory();
        var (pack, context) = Create(temp.Root);

        await InvokeAsync(pack, context, "office_write", ("path", "校验.docx"),
            ("content", "# 标题\n\n正文\n\n| a | b |\n| --- | --- |\n| 1 | 2 |"));
        await InvokeAsync(pack, context, "office_write", ("path", "校验.xlsx"),
            ("content", "## 数据\n\n| a | b |\n| --- | --- |\n| 1 | 2 |"));
        await InvokeAsync(pack, context, "office_write", ("path", "校验.pptx"),
            ("content", "# 第一页\n\n- 内容\n\n---\n\n# 第二页\n\n- 内容"));

        var validator = new DocumentFormat.OpenXml.Validation.OpenXmlValidator();

        using (var word = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(
                   Path.Combine(temp.Root, "校验.docx"), false))
        {
            Assert.Empty(validator.Validate(word));
        }

        using (var excel = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(
                   Path.Combine(temp.Root, "校验.xlsx"), false))
        {
            Assert.Empty(validator.Validate(excel));
        }

        using (var ppt = DocumentFormat.OpenXml.Packaging.PresentationDocument.Open(
                   Path.Combine(temp.Root, "校验.pptx"), false))
        {
            var errors = validator.Validate(ppt).ToArray();
            Assert.True(errors.Length == 0, string.Join("\n", errors.Select(error => error.Description)));
        }
    }

    private static (OfficeToolPack Pack, ToolContext Context) Create(
        string root,
        StubSettingsService? settings = null,
        IToolApprovalService? approvals = null)
    {
        var createdSettings = settings is null;
        settings ??= new StubSettingsService();
        approvals ??= new ToolApprovalService();

        // 默认设置里写文件要确认；纯读写用例先关掉，审批路径由专用用例覆盖。
        if (createdSettings && settings.Current.ConfirmFileWrite)
        {
            settings.Current.ConfirmFileWrite = false;
        }

        var context = new ToolContext(
            "session",
            root,
            [],
            settings,
            approvals,
            new StubSandboxJobService(),
            new NullAuditLog(),
            new FileReadRegistry());

        return (new OfficeToolPack(), context);
    }

    private static async Task<string> InvokeAsync(
        IAgentToolPack pack,
        ToolContext context,
        string toolName,
        params (string Key, object? Value)[] arguments)
    {
        var tool = Assert.IsAssignableFrom<AIFunction>(
            pack.Create(context).Single(function => function.Name == toolName));

        var values = arguments.ToDictionary(pair => pair.Key, pair => pair.Value);
        var result = await tool.InvokeAsync(new AIFunctionArguments(values));
        return result?.ToString() ?? string.Empty;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("等待审批请求超时。");
    }
}
