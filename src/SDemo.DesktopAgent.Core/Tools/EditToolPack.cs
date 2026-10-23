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
                "replace_text",
                "精确替换",
                AgentToolRisk.Medium,
                RequiresApproval: true),
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
                functions.ReplaceTextAsync,
                name: "replace_text",
                description:
                    "把文件中的一段原文精确替换成新文本（old_text 必须与文件内容完全一致，含缩进与换行）。"
                    + "改已有文件前必须先 read_file；只替换第一处。适合小模型做小改动，不会重写整个文件。"),

            AIFunctionFactory.Create(
                functions.ApplyPatchAsync,
                name: "apply_patch",
                description:
                    "应用 unified diff 修改已有文件或新建文件；改已有文件前必须先 read_file。path 用相对路径。"
                    + "格式示例：--- a/path / +++ b/path / @@ -3,2 +3,3 @@ / 上下文行以空格开头 / -旧行 / +新行。"
                    + "行号按 read_file 的行号；把文件末尾的 } 也作为上下文行；解析失败会返回原因，按原因修一次再提交。"),

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

        public async Task<string> ReplaceTextAsync(
            [Description("相对工作区的文件路径。")] string path,
            [Description("要替换的原文，必须与文件中的内容逐字符一致（含缩进）。")] string oldText,
            [Description("替换后的新文本。")] string newText,
            CancellationToken cancellationToken = default)
        {
            string target;
            try
            {
                target = WorkspacePath.Resolve(context.WorkspaceRoot, path, allowRoot: false);
            }
            catch (InvalidOperationException ex)
            {
                return $"未替换任何内容：{ex.Message}";
            }

            if (!File.Exists(target))
            {
                return $"未替换任何内容：{path} 不存在，请先 read_file 或确认路径。";
            }

            var relative = WorkspacePath.NormalizeRelative(path);
            var blocked = CheckRead(relative, target);
            if (blocked is not null)
            {
                return blocked;
            }

            if (string.IsNullOrEmpty(oldText))
            {
                return "未替换任何内容：old_text 不能为空。";
            }

            var original = await File.ReadAllTextAsync(target, cancellationToken).ConfigureAwait(false);

            // 文件可能是 CRLF，而模型（以及 read_file 的渲染）看到的是 LF；
            // 精确匹配必须按 LF 归一化，否则永远找不到 old_text。
            var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var normalized = original.Replace("\r\n", "\n").Replace('\r', '\n');
            var oldNormalized = oldText.Replace("\r\n", "\n").Replace('\r', '\n');
            var newNormalized = newText.Replace("\r\n", "\n").Replace('\r', '\n');

            var index = normalized.IndexOf(oldNormalized, StringComparison.Ordinal);
            var fuzzy = false;

            if (index < 0)
            {
                // 小模型经常把缩进写错（8 空格写成 4 空格，或方法右括号少缩进）。
                // 退一步按「逐行 Trim 后完全一致」定位；内容必须整段匹配，避免改错位置。
                if (!TryFindTrimmedBlock(normalized, oldNormalized, out index, out var matchedLength))
                {
                    return $"未替换任何内容：在 {relative} 中找不到 old_text。"
                        + "请重新 read_file，并保证 old_text 与文件内容逐字符一致（含缩进和空行）。";
                }

                oldNormalized = normalized.Substring(index, matchedLength);
                fuzzy = true;
            }

            // 小模型常见的「插入变重复」：new_text = 原文 + 新内容，第二次调用时新内容已存在。
            if (newNormalized.Length > oldNormalized.Length
                && newNormalized.Contains(oldNormalized, StringComparison.Ordinal)
                && normalized.Contains(newNormalized, StringComparison.Ordinal))
            {
                return $"未替换任何内容：这段新内容已经存在于 {relative} 中，疑似重复插入。"
                    + "请先 read_file 确认当前内容；如果确实要再插入一份，请把 old_text 选得更精确。";
            }

            // 更隐蔽的重复：换了不同的 old_text 锚点，但把同一个成员/语句又加了一遍
            // （任务 A 实测：第二次给 AppSettings 加同样的属性 → CS0102）。
            var oldLines = oldNormalized.Split('\n')
                .Select(line => line.Trim())
                .ToHashSet(StringComparer.Ordinal);
            var remainingLines = normalized.Remove(index, oldNormalized.Length)
                .Split('\n')
                .Select(line => line.Trim())
                .ToHashSet(StringComparer.Ordinal);
            foreach (var line in newNormalized.Split('\n').Select(value => value.Trim()))
            {
                if (line.Length >= 16
                    && !oldLines.Contains(line)
                    && remainingLines.Contains(line)
                    && LooksLikeDeclaration(line))
                {
                    return $"未替换任何内容：new_text 里的「{Truncate(line)}」已经存在于 {relative} 的其他位置，"
                        + "疑似重复添加。请 read_file 确认；只补缺失的部分，不要重复已有成员。";
                }
            }

            var updatedNormalized = string.Concat(
                normalized.AsSpan(0, index),
                newNormalized,
                normalized.AsSpan(index + oldNormalized.Length));
            var updated = newline == "\r\n"
                ? updatedNormalized.Replace("\n", "\r\n")
                : updatedNormalized;

            if (context.Settings.Current.ConfirmFileWrite)
            {
                var decision = await context.Approvals.RequestAsync(
                    new ToolApprovalRequest(
                        ToolId: "replace_text",
                        Kind: ToolApprovalKind.FileEdit,
                        Title: "允许替换这段文本吗？",
                        Summary: $"路径：{relative}",
                        Detail: Truncate(BuildWriteDiff(relative, true, original, updated)),
                        Risk: AgentToolRisk.Medium,
                        AllowSessionRemember: false,
                        SessionKey: null),
                    cancellationToken).ConfigureAwait(false);

                if (decision == ToolApprovalDecision.Deny)
                {
                    return "用户拒绝了这次替换，未做任何修改。";
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
                    return $"未替换任何内容：创建检查点失败：{ex.Message}";
                }
            }

            var temporary = $"{target}.tmp-{Guid.NewGuid():N}";
            try
            {
                await File.WriteAllTextAsync(temporary, updated, Utf8NoBom, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, target, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }

                return $"替换失败，未修改文件：{ex.Message}";
            }

            var change = new PatchFileChange(
                relative,
                false,
                Math.Max(1, newText.Split('\n').Length),
                Math.Max(1, oldText.Split('\n').Length),
                FileReadRegistry.HashText(updated));

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
                // 检查点元数据失败不影响已写入的文件。
            }

            var outcome = PatchApplier.FormatSuccess([change]);
            return fuzzy
                ? outcome + "\n（old_text 的缩进与文件不一致，已按逐行 Trim 匹配定位）"
                : outcome;
        }

        public async Task<string> ApplyPatchAsync(
            [Description("完整 unified diff，可包含多个文件；路径必须是工作区相对路径。")] string patch,
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
                precheck = Precheck(parsed.Files);
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

            var result = new PatchApplier().Apply(context.WorkspaceRoot, parsed, null, cancellationToken);
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
        private string? Precheck(IReadOnlyList<PatchFile> files)
        {
            var errors = new List<string>();

            foreach (var file in files)
            {
                var target = WorkspacePath.Resolve(context.WorkspaceRoot, file.NewPath, allowRoot: false);
                if (!File.Exists(target))
                {
                    continue;
                }

                var blocked = CheckRead(file.NewPath, target);
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

        private static bool LooksLikeDeclaration(string line) =>
            line.StartsWith("public ", StringComparison.Ordinal)
            || line.StartsWith("private ", StringComparison.Ordinal)
            || line.StartsWith("protected ", StringComparison.Ordinal)
            || line.StartsWith("internal ", StringComparison.Ordinal)
            || line.StartsWith("static ", StringComparison.Ordinal)
            || line.StartsWith("class ", StringComparison.Ordinal)
            || line.StartsWith("record ", StringComparison.Ordinal)
            || line.StartsWith("struct ", StringComparison.Ordinal)
            || line.StartsWith("interface ", StringComparison.Ordinal)
            || line.StartsWith("enum ", StringComparison.Ordinal);

        private static bool TryFindTrimmedBlock(string text, string oldText, out int index, out int length)
        {
            index = -1;
            length = 0;
            var fileLines = text.Split('\n');
            var oldLines = oldText.Split('\n');
            if (oldLines.Length == 0 || oldLines.Length > fileLines.Length)
            {
                return false;
            }

            var trimmed = oldLines.Select(line => line.Trim()).ToArray();

            for (var start = 0; start + oldLines.Length <= fileLines.Length; start++)
            {
                var match = true;
                for (var offset = 0; offset < trimmed.Length; offset++)
                {
                    if (!string.Equals(fileLines[start + offset].Trim(), trimmed[offset], StringComparison.Ordinal))
                    {
                        match = false;
                        break;
                    }
                }

                if (!match)
                {
                    continue;
                }

                index = 0;
                for (var line = 0; line < start; line++)
                {
                    index += fileLines[line].Length + 1;
                }

                length = 0;
                for (var line = 0; line < oldLines.Length; line++)
                {
                    length += fileLines[start + line].Length;
                    if (line < oldLines.Length - 1)
                    {
                        length++;
                    }
                }

                return true;
            }

            return false;
        }

        private static string Truncate(string value) =>
            value.Length <= MaxApprovalDiffChars
                ? value
                : string.Concat(value.AsSpan(0, MaxApprovalDiffChars), "\n…（审批展示已截断，实际应用完整补丁）");
    }
}
