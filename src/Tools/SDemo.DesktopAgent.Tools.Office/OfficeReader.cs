using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 把 .docx / .xlsx / .pptx 读成 Markdown。只做「内容可读」：
/// 标题层级、段落、列表、表格、幻灯片文本与备注；不还原字体、颜色、动画。
/// </summary>
internal static partial class OfficeReader
{
    public const int DefaultMaxChars = 40_000;
    public const int MaxMaxChars = 120_000;

    public static string Read(
        string path,
        string? sheet = null,
        string? range = null,
        string? slides = null,
        int maxChars = 0,
        int maxRows = 200)
    {
        var format = OfficeFile.FormatOf(path);
        var limit = maxChars <= 0 ? DefaultMaxChars : Math.Clamp(maxChars, 1_000, MaxMaxChars);

        var markdown = format switch
        {
            "docx" => ReadWord(path, limit),
            "xlsx" => ReadExcel(path, sheet, range, Math.Clamp(maxRows, 1, 2_000), limit),
            "pptx" => ReadPowerPoint(path, slides, limit),
            _ => throw new NotSupportedException(OfficeFile.DisplayFormat("." + format))
        };

        if (markdown.Length <= limit)
        {
            return markdown;
        }

        return string.Concat(
            markdown.AsSpan(0, limit),
            "\n\n…（内容超过 ",
            limit.ToString("N0", CultureInfo.InvariantCulture),
            " 字符，已截断；可用 sheet / slides / range / maxChars 分批读取）");
    }

    /// <summary>只读结构：工作表、幻灯片标题、Word 标题，先看结构再决定读哪一段。</summary>
    public static string Describe(string path)
    {
        var format = OfficeFile.FormatOf(path);
        var info = new FileInfo(path);

        return format switch
        {
            "docx" => DescribeWord(path, info),
            "xlsx" => DescribeExcel(path, info),
            "pptx" => DescribePowerPoint(path, info),
            _ => $"错误：不支持的文件类型：{path}"
        };
    }

    private static string Header(string fileName, string format, long bytes)
    {
        var builder = new StringBuilder();
        builder.Append("# ").AppendLine(fileName);
        builder.Append("> ").Append(format)
            .Append(" · ").Append(ByteSize.FormatSize(bytes))
            .AppendLine();
        return builder.ToString();
    }

    // ------------------------------------------------------------------ Word

    private static string ReadWord(string path, int limit)
    {
        using var document = WordprocessingDocument.Open(path, false);
        var body = document.MainDocumentPart?.Document?.Body
            ?? throw new InvalidDataException("Word 文档没有正文。");

        var builder = new StringBuilder(Header(Path.GetFileName(path), "Word", new FileInfo(path).Length));
        var first = true;

        foreach (var element in body.ChildElements)
        {
            switch (element)
            {
                case W.Paragraph paragraph:
                {
                    var text = paragraph.InnerText.Trim();
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    var level = HeadingLevel(paragraph);
                    if (level > 0)
                    {
                        builder.Append('\n').Append('#', Math.Clamp(level, 1, 6)).Append(' ').AppendLine(text);
                        first = false;
                        continue;
                    }

                    if (IsList(paragraph, out var listLevel, out var ordered))
                    {
                        builder.Append(' ', listLevel * 2)
                            .Append(ordered ? "1. " : "- ")
                            .AppendLine(text);
                        first = false;
                        continue;
                    }

                    // 手打的圆点 / 编号（很多模板不用 Word 列表样式）：读回时还原成 Markdown 列表。
                    if (TryManualList(text, out var marker, out var itemText))
                    {
                        builder.Append(marker).AppendLine(itemText);
                        first = false;
                        continue;
                    }

                    if (!first)
                    {
                        builder.AppendLine();
                    }

                    builder.AppendLine(text);
                    first = false;
                    break;
                }

                case W.Table table:
                {
                    if (!first)
                    {
                        builder.AppendLine();
                    }

                    AppendWordTable(builder, table);
                    first = false;
                    break;
                }
            }

            if (builder.Length > limit * 2)
            {
                break;
            }
        }

        return builder.ToString();
    }

    private static string DescribeWord(string path, FileInfo info)
    {
        using var document = WordprocessingDocument.Open(path, false);
        var body = document.MainDocumentPart?.Document?.Body;

        var paragraphs = body?.Descendants<W.Paragraph>().Count() ?? 0;
        var tables = body?.Descendants<W.Table>().Count() ?? 0;
        var headings = body?.Elements<W.Paragraph>()
            .Select(paragraph => (Level: HeadingLevel(paragraph), Text: paragraph.InnerText.Trim()))
            .Where(item => item.Level > 0 && item.Text.Length > 0)
            .Take(30)
            .ToArray() ?? [];

        var builder = new StringBuilder(Header(Path.GetFileName(path), "Word", info.Length));
        builder.AppendLine().Append("段落 ").Append(paragraphs)
            .Append(" · 表格 ").Append(tables)
            .Append(" · 标题 ").Append(headings.Length)
            .AppendLine();

        if (headings.Length > 0)
        {
            builder.AppendLine().AppendLine("## 标题结构");
            foreach (var (level, text) in headings)
            {
                builder.Append(' ', Math.Max(0, level - 1) * 2).Append("- ").AppendLine(text);
            }
        }

        return builder.ToString();
    }

    private static void AppendWordTable(StringBuilder builder, W.Table table)
    {
        var rows = table.Elements<W.TableRow>()
            .Select(row => row.Elements<W.TableCell>()
                .Select(cell => cell.InnerText.ReplaceLineEndings("<br>").Trim())
                .ToArray())
            .Where(row => row.Length > 0)
            .ToArray();

        if (rows.Length == 0)
        {
            return;
        }

        var columns = rows.Max(row => row.Length);
        builder.Append("| ").AppendJoin(" | ", Enumerable.Range(0, columns).Select(index =>
            index < rows[0].Length ? EscapeCell(rows[0][index]) : string.Empty)).AppendLine(" |");
        builder.Append("| ").AppendJoin(" | ", Enumerable.Range(0, columns).Select(_ => "---")).AppendLine(" |");

        foreach (var row in rows.Skip(1))
        {
            builder.Append("| ").AppendJoin(" | ", Enumerable.Range(0, columns).Select(index =>
                index < row.Length ? EscapeCell(row[index]) : string.Empty)).AppendLine(" |");
        }
    }

    private static int HeadingLevel(W.Paragraph paragraph)
    {
        var style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;

        if (!string.IsNullOrWhiteSpace(style))
        {
            var match = HeadingStylePattern().Match(style);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var level))
            {
                return Math.Clamp(level, 1, 6);
            }

            if (style.Equals("Title", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }
        }

        // 很多文档（以及我们写出的文件在没带样式时）用手动加粗 + 大字号当标题。
        // 只在「整段都加粗且字号明显大于正文、篇幅很短」时认，避免把强调句当标题。
        var runs = paragraph.Elements<W.Run>()
            .Where(run => !string.IsNullOrWhiteSpace(run.InnerText))
            .ToArray();
        var text = paragraph.InnerText.Trim();

        if (runs.Length > 0
            && text.Length is > 0 and <= 80
            && runs.All(run => run.RunProperties?.Bold is not null)
            && int.TryParse(runs[0].RunProperties?.FontSize?.Val?.Value, out var size)
            && size >= 28)
        {
            return size >= 34 ? 1 : 2;
        }

        return 0;
    }

    private static bool TryManualList(string text, out string marker, out string content)
    {
        var bullet = ManualBulletPattern().Match(text);
        if (bullet.Success)
        {
            marker = "- ";
            content = bullet.Groups["text"].Value.Trim();
            return true;
        }

        var number = ManualNumberPattern().Match(text);
        if (number.Success)
        {
            marker = number.Groups["number"].Value + ". ";
            content = number.Groups["text"].Value.Trim();
            return true;
        }

        marker = string.Empty;
        content = text;
        return false;
    }

    private static bool IsList(W.Paragraph paragraph, out int level, out bool ordered)
    {
        var numbering = paragraph.ParagraphProperties?.NumberingProperties;
        level = numbering?.NumberingLevelReference?.Val?.Value ?? 0;
        ordered = false;

        if (numbering is null)
        {
            return false;
        }

        level = Math.Clamp(level, 0, 8);
        return true;
    }

    // ------------------------------------------------------------------ Excel

    private static string ReadExcel(string path, string? sheetName, string? range, int maxRows, int limit)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        var workbookPart = document.WorkbookPart
            ?? throw new InvalidDataException("Excel 文件没有工作簿。");

        var workbook = workbookPart.Workbook;
        var sheets = workbook.Sheets?.Elements<S.Sheet>().ToArray() ?? [];
        if (sheets.Length == 0)
        {
            return Header(Path.GetFileName(path), "Excel", new FileInfo(path).Length) + "\n（没有工作表）";
        }

        var selected = SelectSheets(sheets, sheetName);
        var sharedStrings = SharedStrings(workbookPart);
        var stylesheet = workbookPart.WorkbookStylesPart?.Stylesheet;
        var columnRange = ParseRange(range);

        var builder = new StringBuilder(Header(Path.GetFileName(path), "Excel", new FileInfo(path).Length));

        foreach (var sheet in selected)
        {
            var name = sheet.Name?.Value ?? "Sheet";
            var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!);
            var worksheet = worksheetPart.Worksheet;
            var rows = worksheet.GetFirstChild<S.SheetData>()?.Elements<S.Row>().ToArray() ?? [];

            builder.AppendLine().Append("## ").Append(name).AppendLine();

            var table = BuildSheetTable(rows, sharedStrings, stylesheet, columnRange, maxRows);
            if (table.Count == 0)
            {
                builder.AppendLine("（空工作表）");
                continue;
            }

            AppendTable(builder, table);
            if (table.Count >= maxRows)
            {
                builder.Append("（只显示前 ").Append(maxRows).AppendLine(" 行；可用 maxRows / range 继续读）");
            }
        }

        return builder.ToString();
    }

    private static string DescribeExcel(string path, FileInfo info)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        var workbookPart = document.WorkbookPart
            ?? throw new InvalidDataException("Excel 文件没有工作簿。");
        var sheets = workbookPart.Workbook.Sheets?.Elements<S.Sheet>().ToArray() ?? [];

        var builder = new StringBuilder(Header(Path.GetFileName(path), "Excel", info.Length));
        builder.AppendLine().Append("工作表 ").Append(sheets.Length).AppendLine();

        foreach (var sheet in sheets)
        {
            var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!);
            var rows = worksheetPart.Worksheet.GetFirstChild<S.SheetData>()?.Elements<S.Row>().ToArray() ?? [];
            var columns = rows.Length == 0
                ? 0
                : rows.Max(row => row.Elements<S.Cell>().Count());
            var dimension = worksheetPart.Worksheet.SheetDimension?.Reference?.Value;

            builder.Append("- ").Append(sheet.Name?.Value ?? "Sheet")
                .Append("：").Append(rows.Length).Append(" 行 × ").Append(columns).Append(" 列");
            if (!string.IsNullOrWhiteSpace(dimension))
            {
                builder.Append("（范围 ").Append(dimension).Append('）');
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static IReadOnlyList<S.Sheet> SelectSheets(IReadOnlyList<S.Sheet> sheets, string? sheetName)
    {
        if (string.IsNullOrWhiteSpace(sheetName))
        {
            return sheets;
        }

        var query = sheetName.Trim();

        if (int.TryParse(query, out var index) && index >= 1 && index <= sheets.Count)
        {
            return [sheets[index - 1]];
        }

        var matched = sheets.FirstOrDefault(sheet =>
            string.Equals(sheet.Name?.Value, query, StringComparison.OrdinalIgnoreCase));

        return matched is null
            ? sheets
            : [matched];
    }

    private static string[] SharedStrings(WorkbookPart workbookPart) =>
        workbookPart.SharedStringTablePart?.SharedStringTable?
            .Elements<S.SharedStringItem>()
            .Select(item => item.InnerText)
            .ToArray() ?? [];

    private static List<IReadOnlyList<string>> BuildSheetTable(
        IReadOnlyList<S.Row> rows,
        IReadOnlyList<string> sharedStrings,
        S.Stylesheet? stylesheet,
        (int StartRow, int EndRow, int StartColumn, int EndColumn)? range,
        int maxRows)
    {
        var table = new List<IReadOnlyList<string>>();
        var startRow = range?.StartRow ?? 0;
        var endRow = range?.EndRow;
        var startColumn = range?.StartColumn ?? 0;
        var endColumn = range?.EndColumn;
        var taken = 0;

        foreach (var row in rows.OrderBy(row => row.RowIndex?.Value ?? 0))
        {
            var rowIndex = (int)(row.RowIndex?.Value ?? 0) - 1;
            if (rowIndex < startRow)
            {
                continue;
            }

            if (endRow is int lastRow && rowIndex > lastRow)
            {
                break;
            }

            if (taken >= maxRows)
            {
                break;
            }

            var cells = row.Elements<S.Cell>()
                .Select(cell => (Column: ColumnIndex(cell.CellReference?.Value), Cell: cell))
                .Where(item => item.Column >= startColumn && (endColumn is null || item.Column <= endColumn))
                .OrderBy(item => item.Column)
                .ToArray();

            if (cells.Length == 0)
            {
                continue;
            }

            var width = cells.Max(item => item.Column) + 1;
            var values = new string[width];
            Array.Fill(values, string.Empty);

            foreach (var (column, cell) in cells)
            {
                values[column] = CellText(cell, sharedStrings, stylesheet);
            }

            var first = startColumn;
            var lastColumn = endColumn is int end ? Math.Min(end, width - 1) : width - 1;
            if (lastColumn < first)
            {
                continue;
            }

            table.Add(values[first..(lastColumn + 1)]);
            taken++;
        }

        return table;
    }

    private static string CellText(S.Cell cell, IReadOnlyList<string> sharedStrings, S.Stylesheet? stylesheet)
    {
        var type = cell.DataType?.Value;
        var raw = cell.CellValue?.InnerText ?? string.Empty;

        if (type == S.CellValues.SharedString)
        {
            return int.TryParse(raw, out var index)
                   && index >= 0
                   && index < sharedStrings.Count
                ? sharedStrings[index]
                : string.Empty;
        }

        if (type == S.CellValues.InlineString)
        {
            return cell.InlineString?.InnerText ?? string.Empty;
        }

        if (type == S.CellValues.Boolean)
        {
            return raw is "1" or "true" ? "TRUE" : "FALSE";
        }

        if (string.IsNullOrEmpty(raw))
        {
            // 公式没有缓存值时至少让模型看到公式本身。
            var formula = cell.CellFormula?.InnerText;
            return string.IsNullOrWhiteSpace(formula) ? string.Empty : "=" + formula;
        }

        if ((type is null || type == S.CellValues.Number)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            if (IsDateFormat(cell.StyleIndex?.Value ?? 0, stylesheet))
            {
                try
                {
                    var date = DateTime.FromOADate(number);
                    return date.TimeOfDay == TimeSpan.Zero
                        ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                        : date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                }
                catch (ArgumentException)
                {
                    // 超出 OADate 范围：按原始数字返回。
                }
            }

            return raw;
        }

        return raw;
    }

    private static bool IsDateFormat(uint styleIndex, S.Stylesheet? stylesheet)
    {
        var formats = stylesheet?.CellFormats?.Elements<S.CellFormat>().ToArray();
        if (formats is null || styleIndex >= formats.Length)
        {
            return false;
        }

        var id = formats[styleIndex].NumberFormatId?.Value ?? 0;

        if (id is >= 14 and <= 22 or >= 27 and <= 36 or >= 45 and <= 47 or >= 50 and <= 58)
        {
            return true;
        }

        var custom = stylesheet?.NumberingFormats?.Elements<S.NumberingFormat>()
            .FirstOrDefault(format => format.NumberFormatId?.Value == id)?
            .FormatCode?.Value;

        return custom is not null
               && (custom.Contains("yy", StringComparison.OrdinalIgnoreCase)
                   || custom.Contains("dd", StringComparison.OrdinalIgnoreCase)
                   || custom.Contains("m/d", StringComparison.OrdinalIgnoreCase));
    }

    private static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return 0;
        }

        var column = 0;
        foreach (var character in reference)
        {
            if (character is >= 'A' and <= 'Z')
            {
                column = column * 26 + (character - 'A' + 1);
            }
            else if (character is >= 'a' and <= 'z')
            {
                column = column * 26 + (character - 'a' + 1);
            }
            else
            {
                break;
            }
        }

        return Math.Max(0, column - 1);
    }

    private static (int StartRow, int EndRow, int StartColumn, int EndColumn)? ParseRange(string? range)
    {
        if (string.IsNullOrWhiteSpace(range))
        {
            return null;
        }

        var match = RangePattern().Match(range.Trim());
        if (!match.Success)
        {
            return null;
        }

        var startColumn = ColumnIndex(match.Groups[1].Value);
        var startRow = match.Groups[2].Success
            ? Math.Max(0, int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) - 1)
            : 0;
        var endColumn = match.Groups[3].Success ? ColumnIndex(match.Groups[3].Value) : startColumn;
        var endRow = match.Groups[4].Success
            ? (int?)Math.Max(startRow, int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) - 1)
            : null;

        return (startRow, endRow ?? int.MaxValue, startColumn, Math.Max(startColumn, endColumn));
    }

    // ------------------------------------------------------------------ PowerPoint

    private static string ReadPowerPoint(string path, string? slides, int limit)
    {
        using var document = PresentationDocument.Open(path, false);
        var presentationPart = document.PresentationPart
            ?? throw new InvalidDataException("PowerPoint 文件没有演示文稿。");

        var slideIds = presentationPart.Presentation.SlideIdList?.Elements<P.SlideId>().ToArray() ?? [];
        var wanted = ParseSlideNumbers(slides, slideIds.Length);

        var builder = new StringBuilder(
            Header(Path.GetFileName(path), "PowerPoint", new FileInfo(path).Length));
        builder.Append("> 共 ").Append(slideIds.Length).AppendLine(" 页");

        for (var index = 0; index < slideIds.Length; index++)
        {
            if (!wanted.Contains(index + 1))
            {
                continue;
            }

            var slidePart = (SlidePart)presentationPart.GetPartById(slideIds[index].RelationshipId!.Value!);
            AppendSlide(builder, slidePart, index + 1);
        }

        return builder.ToString();
    }

    private static string DescribePowerPoint(string path, FileInfo info)
    {
        using var document = PresentationDocument.Open(path, false);
        var presentationPart = document.PresentationPart
            ?? throw new InvalidDataException("PowerPoint 文件没有演示文稿。");
        var slideIds = presentationPart.Presentation.SlideIdList?.Elements<P.SlideId>().ToArray() ?? [];

        var builder = new StringBuilder(Header(Path.GetFileName(path), "PowerPoint", info.Length));
        builder.AppendLine().Append("幻灯片 ").Append(slideIds.Length).AppendLine();

        for (var index = 0; index < slideIds.Length; index++)
        {
            var slidePart = (SlidePart)presentationPart.GetPartById(slideIds[index].RelationshipId!.Value!);
            var title = SlideTitle(slidePart) ?? "（无标题）";
            var shapes = slidePart.Slide.Descendants<P.Shape>().Count();
            var images = slidePart.ImageParts.Count();

            builder.Append("- 第 ").Append(index + 1).Append(" 页：").Append(title)
                .Append("（").Append(shapes).Append(" 个形状");
            if (images > 0)
            {
                builder.Append("、").Append(images).Append(" 张图");
            }

            builder.AppendLine("）");
        }

        return builder.ToString();
    }

    private static void AppendSlide(StringBuilder builder, SlidePart slidePart, int number)
    {
        var title = SlideTitle(slidePart);
        builder.AppendLine().Append("## 第 ").Append(number).Append(" 页");
        if (!string.IsNullOrWhiteSpace(title))
        {
            builder.Append(" · ").Append(title);
        }

        builder.AppendLine();

        var bodyLines = new List<string>();
        var tables = new List<IReadOnlyList<IReadOnlyList<string>>>();
        var titleConsumed = false;

        foreach (var shape in slidePart.Slide.Descendants<P.Shape>())
        {
            var placeholderType = shape.NonVisualShapeProperties?
                .ApplicationNonVisualDrawingProperties?
                .PlaceholderShape?.Type?.Value;

            if (IsTitleOrSubtitle(placeholderType))
            {
                continue;
            }

            // 没有标题占位符时 SlideTitle 把第一个文本框当标题；正文收集时跳过它，
            // 否则读回来会出现「标题同时也是一条 bullet」。
            var shapeText = shape.TextBody?.InnerText.Trim();
            if (!titleConsumed
                && !string.IsNullOrWhiteSpace(title)
                && string.Equals(shapeText, title, StringComparison.Ordinal))
            {
                titleConsumed = true;
                continue;
            }

            foreach (var paragraph in shape.TextBody?.Elements<A.Paragraph>() ?? [])
            {
                var text = paragraph.InnerText.Trim();
                if (text.Length == 0)
                {
                    continue;
                }

                // 模板 / 我们写出的文件常带手打圆点；统一成 Markdown 列表，避免出现「- • 现状」。
                text = LeadingBulletPattern().Replace(text, string.Empty).Trim();
                if (text.Length == 0)
                {
                    continue;
                }

                var level = paragraph.ParagraphProperties?.Level?.Value ?? 0;
                bodyLines.Add(new string(' ', Math.Clamp(level, 0, 4) * 2) + "- " + text);
            }
        }

        foreach (var graphicFrame in slidePart.Slide.Descendants<P.GraphicFrame>())
        {
            foreach (var table in graphicFrame.Descendants<A.Table>())
            {
                var rows = table.Elements<A.TableRow>()
                    .Select(row => (IReadOnlyList<string>)row.Elements<A.TableCell>()
                        .Select(cell => CellText(cell))
                        .ToArray())
                    .ToArray();
                if (rows.Length > 0)
                {
                    tables.Add(rows);
                }
            }
        }

        foreach (var line in bodyLines)
        {
            builder.AppendLine(line);
        }

        foreach (var table in tables)
        {
            builder.AppendLine();
            AppendTable(builder, table);
        }

        var notes = SlideNotes(slidePart);
        if (!string.IsNullOrWhiteSpace(notes))
        {
            builder.AppendLine().Append("> 备注：").AppendLine(notes.ReplaceLineEndings(" "));
        }

        var images = slidePart.ImageParts.Count();
        if (images > 0)
        {
            builder.Append("（本页含 ").Append(images).AppendLine(" 张图片，未提取图片内容）");
        }
    }

    private static bool IsTitle(P.PlaceholderValues? type) =>
        type is { } value
        && (value.Equals(P.PlaceholderValues.Title) || value.Equals(P.PlaceholderValues.CenteredTitle));

    private static bool IsTitleOrSubtitle(P.PlaceholderValues? type) =>
        IsTitle(type)
        || (type is { } value && value.Equals(P.PlaceholderValues.SubTitle));

    private static string? SlideTitle(SlidePart slidePart)
    {        foreach (var shape in slidePart.Slide.Descendants<P.Shape>())
        {
            var placeholderType = shape.NonVisualShapeProperties?
                .ApplicationNonVisualDrawingProperties?
                .PlaceholderShape?.Type?.Value;

            if (IsTitle(placeholderType))
            {
                var title = shape.TextBody?.InnerText.Trim();
                if (!string.IsNullOrWhiteSpace(title))
                {
                    return title;
                }
            }
        }

        // 没有标题占位符：用第一个有文字的形状第一行（很多模板就是纯文本框）。
        foreach (var shape in slidePart.Slide.Descendants<P.Shape>())
        {
            var text = shape.TextBody?.InnerText.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                var firstLine = text.Split('\n')[0].Trim();
                if (firstLine.Length > 0)
                {
                    return firstLine;
                }
            }
        }

        return null;
    }

    private static string SlideNotes(SlidePart slidePart)
    {
        var notesSlide = slidePart.NotesSlidePart?.NotesSlide;
        if (notesSlide is null)
        {
            return string.Empty;
        }

        var lines = new List<string>();

        foreach (var shape in notesSlide.Descendants<P.Shape>())
        {
            var placeholderType = shape.NonVisualShapeProperties?
                .ApplicationNonVisualDrawingProperties?
                .PlaceholderShape?.Type?.Value;

            if (placeholderType == P.PlaceholderValues.SlideNumber)
            {
                continue;
            }

            var text = shape.TextBody?.InnerText.Trim();
            if (!string.IsNullOrWhiteSpace(text) && text != "‹#›")
            {
                lines.Add(text);
            }
        }

        return string.Join(" ", lines);
    }

    private static string CellText(A.TableCell cell) =>
        string.Join(" ", (cell.TextBody?.Elements<A.Paragraph>() ?? [])
            .Select(paragraph => paragraph.InnerText.Trim())
            .Where(text => text.Length > 0));

    private static HashSet<int> ParseSlideNumbers(string? range, int count)
    {
        var result = new HashSet<int>();
        if (count == 0)
        {
            return result;
        }

        if (string.IsNullOrWhiteSpace(range))
        {
            for (var index = 1; index <= count; index++)
            {
                result.Add(index);
            }

            return result;
        }

        foreach (var part in range.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Contains('-'))
            {
                var pieces = part.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (pieces.Length == 2
                    && int.TryParse(pieces[0], out var start)
                    && int.TryParse(pieces[1], out var end))
                {
                    for (var index = Math.Max(1, start); index <= Math.Min(count, end); index++)
                    {
                        result.Add(index);
                    }
                }
            }
            else if (int.TryParse(part, out var page) && page >= 1 && page <= count)
            {
                result.Add(page);
            }
        }

        if (result.Count == 0)
        {
            for (var index = 1; index <= count; index++)
            {
                result.Add(index);
            }
        }

        return result;
    }

    // ------------------------------------------------------------------ shared

    private static void AppendTable(StringBuilder builder, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var columns = rows.Max(row => row.Count);
        builder.Append("| ").AppendJoin(" | ", Enumerable.Range(0, columns)
            .Select(index => index < rows[0].Count ? EscapeCell(rows[0][index]) : string.Empty)).AppendLine(" |");
        builder.Append("| ").AppendJoin(" | ", Enumerable.Range(0, columns).Select(_ => "---")).AppendLine(" |");

        foreach (var row in rows.Skip(1))
        {
            builder.Append("| ").AppendJoin(" | ", Enumerable.Range(0, columns)
                .Select(index => index < row.Count ? EscapeCell(row[index]) : string.Empty)).AppendLine(" |");
        }
    }

    private static string EscapeCell(string text) =>
        text.Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings("<br>");

    [GeneratedRegex(@"^(?:Heading|标题)\s*(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeadingStylePattern();

    [GeneratedRegex(
        @"^([A-Za-z]+)(\d+)?(?::([A-Za-z]+)(\d+)?)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex RangePattern();

    [GeneratedRegex(@"^(?<bullet>[•·▪◦])\s+(?<text>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ManualBulletPattern();

    [GeneratedRegex(@"^(?<number>\d{1,3})[.)、]\s*(?<text>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ManualNumberPattern();

    [GeneratedRegex(@"^[•·▪◦]\s*", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingBulletPattern();
}
