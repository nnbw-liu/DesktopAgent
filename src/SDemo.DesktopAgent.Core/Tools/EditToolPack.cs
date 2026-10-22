using System.ComponentModel;
using System.IO;
using System.Text;
using Microsoft.Extensions.AI;
using SDemo.DesktopAgent.Editing;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 写入工具包：<c>apply_patch</c>（统一 diff、多文件、全有或全无）与 <c>write_file</c>。
/// 两个工具都要求「先读后写」：本会话读过该文件且内容 hash 未变；新建文件除外。
/// 编辑审批只给 diff，允许关闭确认（设置 → 工具设置）。
/// </summary>
public sealed class EditToolPack : IAgentToolPack
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly CheckpointStore? _checkpoints;

    public EditToolPack(CheckpointStore? checkpoints = null) => _checkpoints = checkpoints;

    public AgentToolPackDescriptor Descriptor { get; } = new(
        Id: "edits",
        Name: "代码编辑",
        Description: "用 unified diff 修改工作区文件；改已有文件前必须先读。",
        Glyph: "✎",
        Risk: AgentToolRisk.Medium,
        Order: 21,
        DefaultEnabled: true,
        WindowsOnly: false,
        Functions:
        [
            new AgentToolFunctionDescriptor(
                "apply_patch",
                "应用补丁",
                AgentToolRisk.Medium,
                RequiresApproval: true),
            new AgentToolFunctionDescriptor(
                "write_file",
                "写入文件",
                AgentToolRisk.Medium,
                RequiresApproval: true)
        ],
        BuiltIn: true);

    public IReadOnlyList<AITool> Create(ToolContext context)
    {
        var functions = new EditFunctions(context, _checkpoints);

        return
        [
            AIFunctionFactory.Create(
                functions.ApplyPatchAsync,
                name: "apply_patch",
                description: "应用 unified diff 修改已有文件或新建文件；改已有文件前必须先 read_file。path 用相对路径。"),

            AIFunctionFactory.Create(
                functions.WriteFileAsync,
                name: "write_file",
                description: "整文件写入；仅用于新建文件或必须整体重写时，改已有文件优先用 apply_patch。")
        ];
    }

    private sealed class EditFunctions(ToolContext context, CheckpointStore? checkpoints)
    {
        private const int MaxApprovalDiffChars = 60_000;

        private const string PatchFormatHint =
            "提示：只输出标准 unified diff——每个文件以 `--- a/路径` 和 `+++ b/路径` 开头，" +
            "每个 hunk 用 `@@ -旧起始,旧行数 +新起始,新行数 @@`；不需要 `*** Begin Patch` / `*** End Patch`，也不要用 `*** Update File:`。";

        public async Task<string> ApplyPatchAsync(
            [Description("完整 unified diff，可包含多个文件；路径必须是工作区相对路径。")] string patch,
            [Description("可选：期望的当前文件 SHA-256；传入时先校验。")] string? expectedHash = null,
            CancellationToken cancellationToken = default)
        {
            ParsedPatch parsed;
            try
            {
                parsed = UnifiedPatchParser.Parse(patch);
            }
            catch (InvalidOperationException ex)
            {
                return $"未写入任何文件：{ex.Message}\n{PatchFormatHint}";
            }

            string? precheck;
            try
            {
                precheck = Precheck(parsed.Files, expectedHash);
            }
            catch (InvalidOperationException ex)
            {
                return $"未写入任何文件：{ex.Message}";
            }

            if (precheck is not null)
            {
                return precheck;
            }

            if (context.Settings.Current.ConfirmFileWrite)
            {
                var decision = await context.Approvals.RequestAsync(
                    new ToolApprovalRequest(
                        ToolId: "apply_patch",
                        Kind: ToolApprovalKind.FileEdit,
                        Title: "允许应用这个补丁吗？",
                        Summary: "文件：" + string.Join('、', parsed.Files.Select(file => file.NewPath)),
                        Detail: Truncate(patch),
                        Risk: AgentToolRisk.Medium,
                        AllowSessionRemember: false,
                        SessionKey: null),
                    cancellationToken).ConfigureAwait(false);

                if (decision == ToolApprovalDecision.Deny)
                {
                    return "用户拒绝了这次补丁，未做任何修改。";
                }
            }

            if (checkpoints is not null && context.Settings.Current.Checkpoints.Enabled)
            {
                try
                {
                    checkpoints.Capture(
                        context.SessionId,
                        context.TurnId ?? context.SessionId,
                        context.WorkspaceRoot,
                        parsed.Files.Select(file => file.NewPath));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    return $"未写入任何文件：创建检查点失败：{ex.Message}";
                }
            }

            var result = new PatchApplier().Apply(context.WorkspaceRoot, parsed, expectedHash, cancellationToken);
            if (!result.Success)
            {
                return result.Message;
            }

            foreach (var change in result.Changes)
            {
                context.Reads?.RecordRead(context.SessionId, change.Path, change.NewHash);
            }

            try
            {
                checkpoints?.MarkEdited(
                    context.SessionId,
                    context.TurnId ?? context.SessionId,
                    context.WorkspaceRoot,
                    result.Changes.Select(change => change.Path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // checkpoint 的 after-hash 写失败不应回滚已经成功的编辑。
            }

            return result.Message;
        }

        public async Task<string> WriteFileAsync(
            [Description("相对工作区的文件路径；父目录不存在时会自动创建。")] string path,
            [Description("要写入的完整文本内容。")] string content,
            CancellationToken cancellationToken)
        {
            string target;
            try
            {
                target = WorkspacePath.Resolve(context.WorkspaceRoot, path, allowRoot: false);
            }
            catch (InvalidOperationException ex)
            {
                return $"未写入任何文件：{ex.Message}";
            }

            var relative = WorkspacePath.NormalizeRelative(path);
            var exists = File.Exists(target);
            var original = exists ? File.ReadAllText(target) : string.Empty;

            if (exists)
            {
                var blocked = CheckRead(relative, target);
                if (blocked is not null)
                {
                    return blocked;
                }
            }

            if (context.Settings.Current.ConfirmFileWrite)
            {
                var diff = BuildWriteDiff(relative, exists, original, content);
                var decision = await context.Approvals.RequestAsync(
                    new ToolApprovalRequest(
                        ToolId: "write_file",
                        Kind: ToolApprovalKind.FileEdit,
                        Title: exists ? "允许覆盖这个文件吗？" : "允许新建这个文件吗？",
                        Summary: $"路径：{relative}",
                        Detail: Truncate(diff),
                        Risk: AgentToolRisk.Medium,
                        AllowSessionRemember: false,
                        SessionKey: null),
                    cancellationToken).ConfigureAwait(false);

                if (decision == ToolApprovalDecision.Deny)
                {
                    return "用户拒绝了这次文件写入，未做任何修改。";
                }
            }

            if (checkpoints is not null && context.Settings.Current.Checkpoints.Enabled)
            {
                try
                {
                    checkpoints.Capture(
                        context.SessionId,
                        context.TurnId ?? context.SessionId,
                        context.WorkspaceRoot,
                        [relative]);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    return $"未写入文件：创建检查点失败：{ex.Message}";
                }
            }

            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = $"{target}.tmp-{Guid.NewGuid():N}";
            try
            {
                await File.WriteAllTextAsync(temporary, content, Utf8NoBom, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, target, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }

                return $"写入失败，未修改文件：{ex.Message}";
            }

            var change = new PatchFileChange(
                relative,
                !exists,
                Math.Max(1, content.Split('\n').Length),
                exists ? Math.Max(0, original.Split('\n').Length) : 0,
                FileReadRegistry.HashText(content));

            context.Reads?.RecordRead(context.SessionId, relative, change.NewHash);

            try
            {
                checkpoints?.MarkEdited(
                    context.SessionId,
                    context.TurnId ?? context.SessionId,
                    context.WorkspaceRoot,
                    [relative]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // 同上：检查点元数据失败不影响已写入的文件。
            }

            return PatchApplier.FormatSuccess([change]);
        }

        /// <summary>返回 null 表示可以写；否则是给模型的拒绝原因。</summary>
        private string? Precheck(IReadOnlyList<PatchFile> files, string? expectedHash)
        {
            var errors = new List<string>();

            foreach (var file in files)
            {
                var target = WorkspacePath.Resolve(context.WorkspaceRoot, file.NewPath, allowRoot: false);
                if (!File.Exists(target))
                {
                    continue;
                }

                var blocked = CheckRead(file.NewPath, target, expectedHash);
                if (blocked is not null)
                {
                    errors.Add(blocked);
                }
            }

            return errors.Count == 0
                ? null
                : "未写入任何文件：\n" + string.Join('\n', errors);
        }

        private string? CheckRead(string relative, string target, string? expectedHash = null)
        {
            var record = context.Reads?.Find(context.SessionId, relative);
            if (record is null)
            {
                return $"{relative}：还没有读过这个文件，请先 read_file 再改。";
            }

            var current = FileReadRegistry.HashFile(target);
            if (current is not null && !string.Equals(record.Hash, current, StringComparison.OrdinalIgnoreCase))
            {
                return $"{relative}：文件已被改动，请重新 read_file 后再改。";
            }

            if (!string.IsNullOrWhiteSpace(expectedHash)
                && !string.Equals(current, expectedHash.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return $"{relative}：expectedHash 与当前文件不一致，请重新 read_file。";
            }

            return null;
        }

        private static string BuildWriteDiff(string path, bool exists, string original, string updated)
        {
            var builder = new StringBuilder();
            builder.Append("--- ").Append(exists ? "a/" + path : "/dev/null").Append('\n');
            builder.Append("+++ b/").Append(path).Append('\n');

            if (!exists)
            {
                var lines = updated.Replace("\r\n", "\n").Split('\n');
                var count = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
                builder.Append("@@ -0,0 +1,").Append(count).Append(" @@\n");
                foreach (var line in lines.Take(count))
                {
                    builder.Append('+').Append(line).Append('\n');
                }

                return builder.ToString().TrimEnd('\n');
            }

            var oldLines = original.Replace("\r\n", "\n").Split('\n');
            var newLines = updated.Replace("\r\n", "\n").Split('\n');
            var prefix = 0;
            while (prefix < oldLines.Length && prefix < newLines.Length
                && string.Equals(oldLines[prefix], newLines[prefix], StringComparison.Ordinal))
            {
                prefix++;
            }

            var suffix = 0;
            while (suffix < oldLines.Length - prefix
                && suffix < newLines.Length - prefix
                && string.Equals(oldLines[^(suffix + 1)], newLines[^(suffix + 1)], StringComparison.Ordinal))
            {
                suffix++;
            }

            var removed = oldLines.Length - prefix - suffix;
            var added = newLines.Length - prefix - suffix;
            builder.Append("@@ -").Append(prefix + 1).Append(',').Append(removed)
                .Append(" +").Append(prefix + 1).Append(',').Append(added).Append(" @@\n");

            for (var index = 0; index < removed; index++)
            {
                builder.Append('-').Append(oldLines[prefix + index]).Append('\n');
            }

            for (var index = 0; index < added; index++)
            {
                builder.Append('+').Append(newLines[prefix + index]).Append('\n');
            }

            return builder.ToString().TrimEnd('\n');
        }

        private static string Truncate(string value) =>
            value.Length <= MaxApprovalDiffChars
                ? value
                : string.Concat(value.AsSpan(0, MaxApprovalDiffChars), "\n…（审批展示已截断，实际应用完整补丁）");
    }
}
