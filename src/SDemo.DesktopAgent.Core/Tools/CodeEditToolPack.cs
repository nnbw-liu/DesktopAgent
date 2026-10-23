using System.ComponentModel;
using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Editing;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// P6 单工具模式：一个 <c>code_edit</c> 覆盖读 / 搜 / 改，内部转调现有工具，
/// 因此读前写、审批、路径 jail 的行为完全一致。默认关闭，只在评测 / Compact 下由用户开启。
/// </summary>
public sealed class CodeEditToolPack : IAgentToolPack
{
    public const string PackId = "code_edit";
    public const string FunctionId = "code_edit";

    private readonly CheckpointStore? _checkpoints;

    public CodeEditToolPack(CheckpointStore? checkpoints = null) => _checkpoints = checkpoints;

    public AgentToolPackDescriptor Descriptor { get; } = new(
        Id: PackId,
        Name: "单工具编辑",
        Description: "read / search / patch 三合一的精简工具，适合小模型。",
        Glyph: "✎",
        Risk: AgentToolRisk.Medium,
        Order: 22,
        DefaultEnabled: false,
        WindowsOnly: false,
        Functions:
        [
            new AgentToolFunctionDescriptor(FunctionId, "读写改一体", AgentToolRisk.Medium, RequiresApproval: true)
        ],
        BuiltIn: true);

    public IReadOnlyList<AITool> Create(ToolContext context)
    {
        var functions = new CodeEditFunctions(context, _checkpoints);
        return
        [
            AIFunctionFactory.Create(
                functions.CodeEditAsync,
                name: FunctionId,
                description:
                    "action=read 读取 path（offset/limit）；action=search 用 query 搜索；"
                    + "action=patch 用 patch 修改文件。一次只做一个动作。")
        ];
    }

    private sealed class CodeEditFunctions(ToolContext context, CheckpointStore? checkpoints)
    {
        public async Task<string> CodeEditAsync(
            [Description("read / search / patch")] string action,
            [Description("相对工作区的文件路径；search 时可留空。")] string? path = null,
            [Description("搜索正则；仅 search 使用。")] string? query = null,
            [Description("unified diff；仅 patch 使用。")] string? patch = null,
            [Description("起始行号，默认 1。")] int offset = 1,
            [Description("读取行数，默认 400。")] int limit = 400,
            CancellationToken cancellationToken = default)
        {
            var file = new FileToolPack().Create(context).OfType<AIFunction>()
                .First(function => function.Name == "read_file");
            var search = new FileToolPack().Create(context).OfType<AIFunction>()
                .First(function => function.Name == "search_files");
            var edit = new EditToolPack(checkpoints).Create(context).OfType<AIFunction>()
                .First(function => function.Name == "apply_patch");

            object? result = action.Trim().ToLowerInvariant() switch
            {
                "read" => await file.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
                {
                    ["path"] = path ?? string.Empty,
                    ["offset"] = offset,
                    ["limit"] = limit
                }), cancellationToken),
                "search" => await search.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
                {
                    ["pattern"] = query ?? string.Empty,
                    ["glob"] = string.IsNullOrWhiteSpace(path) ? "**/*" : path
                }), cancellationToken),
                "patch" => await edit.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
                {
                    ["patch"] = patch ?? string.Empty
                }), cancellationToken),
                _ => "ERROR: action 必须是 read / search / patch。"
            };

            return result?.ToString() ?? string.Empty;
        }
    }
}
