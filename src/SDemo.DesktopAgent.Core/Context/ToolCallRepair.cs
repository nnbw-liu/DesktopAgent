using System.Text.Json;
using System.Text.Json.Nodes;

namespace SDemo.DesktopAgent.Context;

/// <summary>工具调用修复结果；失败时 <see cref="Error"/> 给模型看。</summary>
public sealed record ToolCallRepairResult(bool Success, string? Json, bool Repaired, string? Error);

/// <summary>
/// 小模型工具调用参数的保守修复：去 ```json 围栏、截取平衡对象、修尾逗号 / 单引号、
/// 把 "3" / "true" 还原成数字 / 布尔。无法修复就返回错误，不瞎猜。
/// </summary>
public static class ToolCallRepair
{
    public static ToolCallRepairResult Repair(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new ToolCallRepairResult(false, null, false, "工具参数为空。");
        }

        var original = raw;
        var text = raw.Trim();

        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = text.IndexOf('\n');
            text = firstLine >= 0 ? text[(firstLine + 1)..] : text;
            var closing = text.LastIndexOf("```", StringComparison.Ordinal);
            if (closing >= 0)
            {
                text = text[..closing];
            }

            text = text.Trim();
        }

        var start = text.IndexOf('{');
        if (start < 0)
        {
            return new ToolCallRepairResult(false, null, false, "没有找到 JSON 对象。");
        }

        var end = FindBalancedEnd(text, start);
        if (end < 0)
        {
            return new ToolCallRepairResult(false, null, false, "JSON 对象括号不匹配。");
        }

        text = text[start..(end + 1)];

        try
        {
            var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            if (node is null)
            {
                return new ToolCallRepairResult(false, null, false, "JSON 解析为空。");
            }

            CorrectTypes(node);
            var json = node.ToJsonString();
            return new ToolCallRepairResult(true, json, !string.Equals(json, original.Trim(), StringComparison.Ordinal), null);
        }
        catch (JsonException)
        {
            // 单引号是本地小模型最常见的坏格式；只在没有双引号冲突时保守替换。
            if (text.Contains('\'') && !text.Contains('"'))
            {
                try
                {
                    var repaired = text.Replace('\'', '"');
                    var node = JsonNode.Parse(repaired);
                    if (node is not null)
                    {
                        CorrectTypes(node);
                        var json = node.ToJsonString();
                        return new ToolCallRepairResult(true, json, true, null);
                    }
                }
                catch (JsonException)
                {
                }
            }

            return new ToolCallRepairResult(false, null, false, "JSON 解析失败，请传合法对象。");
        }
    }

    private static void CorrectTypes(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(pair => pair.Key).ToArray())
                {
                    if (obj[key] is JsonValue value)
                    {
                        if (value.GetValueKind() != JsonValueKind.String)
                        {
                            continue;
                        }

                        var text = value.GetValue<string>();
                        if (int.TryParse(text, out var number))
                        {
                            obj[key] = JsonValue.Create(number);
                        }
                        else if (bool.TryParse(text, out var boolean))
                        {
                            obj[key] = JsonValue.Create(boolean);
                        }
                    }
                    else if (obj[key] is { } child)
                    {
                        CorrectTypes(child);
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is not null)
                    {
                        CorrectTypes(item);
                    }
                }

                break;
        }
    }

    private static int FindBalancedEnd(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var index = start; index < text.Length; index++)
        {
            var character = text[index];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
            }
            else if (character == '{')
            {
                depth++;
            }
            else if (character == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return index;
                }
            }
        }

        return -1;
    }
}
