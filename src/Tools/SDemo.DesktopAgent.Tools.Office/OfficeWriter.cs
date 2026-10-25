using System.Globalization;
using System.IO;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 把 Markdown 写成 Word / Excel / PowerPoint。只覆盖「基本但可用」的子集：
/// 标题、段落、列表、表格、代码块、引用；Excel 用 Markdown 表格（<c>## 名称</c> = 工作表名），
/// PowerPoint 用 <c>---</c> 分页、<c>#</c> / <c>##</c> 当标题。
/// </summary>
internal static class OfficeWriter
{
    public static (string Format, int Blocks, string Detail) Write(string path, string markdown)
    {
        var format = OfficeFile.FormatOf(path);
        var blocks = MarkdownBlocks.Parse(markdown ?? string.Empty);

        var detail = format switch
        {
            "docx" => WriteWord(path, blocks),
            "xlsx" => WriteExcel(path, markdown ?? string.Empty, blocks),
            "pptx" => WritePowerPoint(path, blocks),
            _ => throw new NotSupportedException($"不支持写入 .{format}。")
        };

        return (format, blocks.Count, detail);
    }

    // ------------------------------------------------------------------ Word

    private static string WriteWord(string path, IReadOnlyList<MarkdownBlock> blocks)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = document.AddMainDocumentPart();
        mainPart.Document = new W.Document();
        var body = new W.Body();
        mainPart.Document.Append(body);

        var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
        stylesPart.Styles = CreateWordStyles();

        var paragraphs = 0;
        var tables = 0;

        foreach (var block in blocks)
        {
            switch (block)
            {
                case MarkdownHeading heading:
                    body.Append(HeadingParagraph(heading));
                    paragraphs++;
                    break;

                case MarkdownParagraph paragraph:
                    body.Append(NormalParagraph(paragraph.Text));
                    paragraphs++;
                    break;

                case MarkdownList list:
                    foreach (var item in list.Items)
                    {
                        body.Append(ListParagraph(item));
                        paragraphs++;
                    }

                    break;

                case MarkdownTable table:
                    body.Append(WordTable(table));
                    tables++;
                    break;

                case MarkdownCode code:
                    foreach (var line in code.Text.Replace("\r\n", "\n").Split('\n'))
                    {
                        body.Append(CodeParagraph(line));
                        paragraphs++;
                    }

                    break;

                case MarkdownQuote quote:
                    body.Append(QuoteParagraph(quote.Text));
                    paragraphs++;
                    break;

                case MarkdownRule:
                    body.Append(new W.Paragraph(
                        new W.ParagraphProperties(
                            new W.ParagraphBorders(
                                new W.BottomBorder
                                {
                                    Val = W.BorderValues.Single,
                                    Size = 6,
                                    Color = "C8C8C8",
                                    Space = 1
                                }))));
                    paragraphs++;
                    break;
            }
        }

        body.Append(new W.SectionProperties(
            new W.PageSize { Width = 11906, Height = 16838 },
            new W.PageMargin
            {
                Top = 1440,
                Right = 1440,
                Bottom = 1440,
                Left = 1440,
                Header = 720,
                Footer = 720,
                Gutter = 0
            }));

        mainPart.Document.Save();
        return $"段落 {paragraphs} · 表格 {tables}";
    }

    private static W.Styles CreateWordStyles()
    {
        var styles = new W.Styles(
            new W.DocDefaults(
                new W.RunPropertiesDefault(
                    new W.RunPropertiesBaseStyle(
                        new W.RunFonts { Ascii = "Calibri", EastAsia = "Microsoft YaHei", HighAnsi = "Calibri" },
                        new W.FontSize { Val = "21" })),
                new W.ParagraphPropertiesDefault(
                    new W.ParagraphPropertiesBaseStyle(
                        new W.SpacingBetweenLines
                        {
                            After = "120",
                            Line = "276",
                            LineRule = W.LineSpacingRuleValues.Auto
                        }))),
            new W.Style(
                new W.StyleName { Val = "Normal" },
                new W.StyleParagraphProperties(),
                new W.StyleRunProperties(new W.FontSize { Val = "21" }))
            {
                Type = W.StyleValues.Paragraph,
                StyleId = "Normal",
                Default = true
            });

        for (var level = 1; level <= 6; level++)
        {
            styles.Append(HeadingStyle(level));
        }

        return styles;
    }

    /// <summary>内置 Heading1–6 样式：Word 的大纲 / 导航会识别，读回时也能还原标题层级。</summary>
    private static W.Style HeadingStyle(int level)
    {
        var size = level switch
        {
            1 => "36",
            2 => "30",
            3 => "26",
            4 => "23",
            _ => "21"
        };

        return new W.Style(
            new W.StyleName { Val = "heading " + level.ToString(CultureInfo.InvariantCulture) },
            new W.BasedOn { Val = "Normal" },
            new W.NextParagraphStyle { Val = "Normal" },
            new W.StyleParagraphProperties(
                new W.KeepNext(),
                new W.SpacingBetweenLines
                {
                    Before = level == 1 ? "320" : "240",
                    After = "120"
                },
                new W.OutlineLevel { Val = level - 1 }),
            new W.StyleRunProperties(
                new W.Bold(),
                new W.Color { Val = "1F3864" },
                new W.FontSize { Val = size }))
        {
            Type = W.StyleValues.Paragraph,
            StyleId = "Heading" + level.ToString(CultureInfo.InvariantCulture),
            CustomStyle = false
        };
    }

    private static W.Paragraph HeadingParagraph(MarkdownHeading heading)
    {
        var size = heading.Level switch
        {
            1 => "36",
            2 => "30",
            3 => "26",
            4 => "23",
            _ => "21"
        };

        var properties = new W.ParagraphProperties(
            new W.ParagraphStyleId { Val = "Heading" + heading.Level.ToString(CultureInfo.InvariantCulture) },
            new W.SpacingBetweenLines
            {
                Before = heading.Level == 1 ? "320" : "240",
                After = "120"
            });

        return new W.Paragraph(properties, StyledRun(heading.Text, bold: true, size: size, color: "1F3864"));
    }

    private static W.Paragraph NormalParagraph(string text)
    {
        var paragraph = new W.Paragraph(new W.ParagraphProperties());
        foreach (var run in InlineRuns(text))
        {
            paragraph.Append(run);
        }

        return paragraph;
    }

    private static W.Paragraph ListParagraph(MarkdownListItem item)
    {
        var properties = new W.ParagraphProperties(
            new W.Indentation
            {
                Left = ((item.Level + 1) * 420).ToString(CultureInfo.InvariantCulture),
                Hanging = "240"
            },
            new W.SpacingBetweenLines { After = "60" });

        var marker = item.Ordered ? "1. " : "• ";
        var paragraph = new W.Paragraph(properties);
        paragraph.Append(new W.Run(new W.Text(marker) { Space = SpaceProcessingModeValues.Preserve }));

        foreach (var run in InlineRuns(item.Text))
        {
            paragraph.Append(run);
        }

        return paragraph;
    }

    private static W.Paragraph CodeParagraph(string line)
    {
        var text = new W.Text(line) { Space = SpaceProcessingModeValues.Preserve };

        return new W.Paragraph(
            new W.ParagraphProperties(
                new W.Shading { Val = W.ShadingPatternValues.Clear, Fill = "F3F3F3" },
                new W.SpacingBetweenLines { After = "0", Line = "240", LineRule = W.LineSpacingRuleValues.Auto },
                new W.Indentation { Left = "240" }),
            new W.Run(
                new W.RunProperties(
                    new W.RunFonts { Ascii = "Consolas", HighAnsi = "Consolas", EastAsia = "Consolas" },
                    new W.FontSize { Val = "18" }),
                text));
    }

    private static W.Paragraph QuoteParagraph(string text)
    {
        var paragraph = new W.Paragraph(
            new W.ParagraphProperties(
                new W.Indentation { Left = "480" },
                new W.ParagraphBorders(
                    new W.LeftBorder
                    {
                        Val = W.BorderValues.Single,
                        Size = 12,
                        Color = "A0A0A0",
                        Space = 8
                    })));

        foreach (var run in InlineRuns(text))
        {
            run.RunProperties ??= new W.RunProperties();
            run.RunProperties.Italic = new W.Italic();
            run.RunProperties.Color = new W.Color { Val = "555555" };
            paragraph.Append(run);
        }

        return paragraph;
    }

    private static W.Run StyledRun(string text, bool bold, string size, string? color = null)
    {
        var properties = new W.RunProperties();
        if (bold)
        {
            properties.Bold = new W.Bold();
        }

        properties.FontSize = new W.FontSize { Val = size };
        if (color is not null)
        {
            properties.Color = new W.Color { Val = color };
        }

        return new W.Run(properties, new W.Text(text) { Space = SpaceProcessingModeValues.Preserve });
    }

    private static IEnumerable<W.Run> InlineRuns(string text)
    {
        foreach (var span in MarkdownBlocks.ParseInline(text))
        {
            if (span.Text.Length == 0)
            {
                continue;
            }

            var properties = new W.RunProperties();
            if (span.Bold)
            {
                properties.Bold = new W.Bold();
            }

            if (span.Italic)
            {
                properties.Italic = new W.Italic();
            }

            if (span.Code)
            {
                properties.RunFonts = new W.RunFonts
                {
                    Ascii = "Consolas",
                    HighAnsi = "Consolas",
                    EastAsia = "Consolas"
                };
                properties.Shading = new W.Shading
                {
                    Val = W.ShadingPatternValues.Clear,
                    Fill = "F0F0F0"
                };
            }

            yield return new W.Run(
                properties,
                new W.Text(span.Text) { Space = SpaceProcessingModeValues.Preserve });
        }
    }

    private static W.Table WordTable(MarkdownTable block)
    {
        var rows = block.Rows;
        var columns = rows.Max(row => row.Count);

        var table = new W.Table(
            new W.TableProperties(
                new W.TableWidth { Width = "0", Type = W.TableWidthUnitValues.Auto },
                new W.TableBorders(
                    new W.TopBorder { Val = W.BorderValues.Single, Size = 4, Color = "BFBFBF" },
                    new W.LeftBorder { Val = W.BorderValues.Single, Size = 4, Color = "BFBFBF" },
                    new W.BottomBorder { Val = W.BorderValues.Single, Size = 4, Color = "BFBFBF" },
                    new W.RightBorder { Val = W.BorderValues.Single, Size = 4, Color = "BFBFBF" },
                    new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Size = 4, Color = "D9D9D9" },
                    new W.InsideVerticalBorder { Val = W.BorderValues.Single, Size = 4, Color = "D9D9D9" })),
            new W.TableGrid(Enumerable.Range(0, columns)
                .Select(_ => new W.GridColumn { Width = (9000 / columns).ToString(CultureInfo.InvariantCulture) })
                .ToArray()));

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var tableRow = new W.TableRow();

            for (var column = 0; column < columns; column++)
            {
                var value = column < row.Count ? row[column] : string.Empty;
                var cell = new W.TableCell(
                    new W.TableCellProperties(
                        new W.Shading
                        {
                            Val = W.ShadingPatternValues.Clear,
                            Fill = rowIndex == 0 ? "F2F5FA" : "FFFFFF"
                        },
                        new W.TableCellVerticalAlignment { Val = W.TableVerticalAlignmentValues.Center }),
                    new W.Paragraph(
                        new W.ParagraphProperties(new W.SpacingBetweenLines { After = "0" }),
                        StyledRun(value, rowIndex == 0, "20")));

                tableRow.Append(cell);
            }

            table.Append(tableRow);
        }

        return table;
    }

    // ------------------------------------------------------------------ Excel

    private static string WriteExcel(string path, string markdown, IReadOnlyList<MarkdownBlock> blocks)
    {
        var sheets = CollectSheets(markdown, blocks);
        if (sheets.Count == 0)
        {
            throw new InvalidDataException(
                "Excel 写入需要 Markdown 表格或 CSV：表格第一行是表头，第二行是 |---| 分隔行；"
                + "用 `## 工作表名` 指定工作表名。");
        }

        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new S.Workbook();
        var sheetsNode = new S.Sheets();
        workbookPart.Workbook.Append(sheetsNode);

        var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = CreateExcelStyles();

        uint sheetId = 1;

        foreach (var (name, rows) in sheets)
        {
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new S.SheetData();
            var worksheet = new S.Worksheet();
            worksheet.Append(CreateColumns(rows));
            worksheet.Append(sheetData);
            worksheetPart.Worksheet = worksheet;

            for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                var row = new S.Row { RowIndex = (uint)(rowIndex + 1) };

                for (var column = 0; column < rows[rowIndex].Count; column++)
                {
                    row.Append(CreateCell(rows[rowIndex][column], rowIndex + 1, column + 1, rowIndex == 0));
                }

                sheetData.Append(row);
            }

            worksheetPart.Worksheet.Save();
            sheetsNode.Append(new S.Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = sheetId++,
                Name = name
            });
        }

        workbookPart.Workbook.Save();

        var totalRows = sheets.Sum(sheet => sheet.Rows.Count);
        return $"工作表 {sheets.Count} · 行 {totalRows}";
    }

    private static List<(string Name, List<IReadOnlyList<string>> Rows)> CollectSheets(
        string markdown,
        IReadOnlyList<MarkdownBlock> blocks)
    {
        var sheets = new List<(string, List<IReadOnlyList<string>>)>();
        var currentName = "Sheet1";
        var foundTable = false;

        foreach (var block in blocks)
        {
            switch (block)
            {
                case MarkdownHeading { Level: >= 2 } heading when !string.IsNullOrWhiteSpace(heading.Text):
                    currentName = SanitizeSheetName(heading.Text);
                    break;

                case MarkdownTable table:
                {
                    foundTable = true;
                    var name = UniqueSheetName(sheets.Select(sheet => sheet.Item1), currentName);
                    sheets.Add((name, table.Rows.ToList()));
                    currentName = "Sheet" + (sheets.Count + 1).ToString(CultureInfo.InvariantCulture);
                    break;
                }
            }
        }

        if (foundTable)
        {
            return sheets;
        }

        // 没有 Markdown 表格时退回 CSV / TSV：Excel 用户经常直接贴一段分隔文本。
        var csv = ParseDelimited(markdown);
        if (csv.Count > 0)
        {
            sheets.Add(("Sheet1", csv));
        }

        return sheets;
    }

    private static List<IReadOnlyList<string>> ParseDelimited(string text)
    {
        var rows = new List<IReadOnlyList<string>>();
        var lines = (text ?? string.Empty)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (lines.Length == 0 || lines.All(line => line.StartsWith('#') || line.StartsWith('>')))
        {
            return rows;
        }

        var separator = lines[0].Contains('\t') ? '\t'
            : lines[0].Contains(';') ? ';'
            : ',';

        foreach (var line in lines)
        {
            if (line.StartsWith('#') || line.StartsWith('>') || line.StartsWith("```"))
            {
                continue;
            }

            rows.Add(line.Split(separator).Select(cell => MarkdownBlocks.CleanInline(cell.Trim())).ToArray());
        }

        return rows;
    }

    private static S.Stylesheet CreateExcelStyles() =>
        new(
            new S.Fonts(
                new S.Font(new S.FontSize { Val = 11 }, new S.FontName { Val = "Microsoft YaHei" }),
                new S.Font(
                    new S.Bold(),
                    new S.FontSize { Val = 11 },
                    new S.FontName { Val = "Microsoft YaHei" }))
            { Count = 2U },
            new S.Fills(
                new S.Fill(new S.PatternFill { PatternType = S.PatternValues.None }),
                new S.Fill(new S.PatternFill { PatternType = S.PatternValues.Gray125 }))
            { Count = 2U },
            new S.Borders(new S.Border()) { Count = 1U },
            new S.CellStyleFormats(new S.CellFormat()) { Count = 1U },
            new S.CellFormats(
                new S.CellFormat { FontId = 0, FillId = 0, BorderId = 0, ApplyFont = true },
                new S.CellFormat { FontId = 1, FillId = 0, BorderId = 0, ApplyFont = true })
            { Count = 2U },
            new S.CellStyles(new S.CellStyle { Name = "Normal", FormatId = 0, BuiltinId = 0 }));

    private static S.Columns CreateColumns(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var columns = new S.Columns();
        var count = rows.Count == 0 ? 1 : rows.Max(row => row.Count);

        for (var index = 1; index <= count; index++)
        {
            var width = rows
                .Select(row => index - 1 < row.Count ? VisualWidth(row[index - 1]) : 0)
                .DefaultIfEmpty(6)
                .Max();

            columns.Append(new S.Column
            {
                Min = (uint)index,
                Max = (uint)index,
                Width = Math.Clamp(width + 2, 8, 58),
                CustomWidth = true
            });
        }

        return columns;
    }

    private static int VisualWidth(string text) =>
        text.Sum(character => character > 0x7F ? 2 : 1);

    private static S.Cell CreateCell(string value, int row, int column, bool header)
    {
        var reference = ColumnName(column) + row.ToString(CultureInfo.InvariantCulture);

        // 纯数字写成数值，方便在 Excel 里排序 / 计算；其余（含前导 0、日期、长数字）一律文本，避免被改形。
        if (!header
            && value.Length > 0
            && value.Length < 16
            && !(value.Length > 1 && value[0] == '0')
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && !value.Contains('e', StringComparison.OrdinalIgnoreCase))
        {
            return new S.Cell
            {
                CellReference = reference,
                StyleIndex = 0,
                DataType = S.CellValues.Number,
                CellValue = new S.CellValue(number.ToString("R", CultureInfo.InvariantCulture))
            };
        }

        var cell = new S.Cell
        {
            CellReference = reference,
            StyleIndex = (uint)(header ? 1 : 0),
            DataType = S.CellValues.InlineString
        };
        cell.InlineString = new S.InlineString(new S.Text(value) { Space = SpaceProcessingModeValues.Preserve });
        return cell;
    }

    private static string ColumnName(int column)
    {
        var name = string.Empty;

        while (column > 0)
        {
            column--;
            name = (char)('A' + column % 26) + name;
            column /= 26;
        }

        return name;
    }

    private static string SanitizeSheetName(string name)
    {
        var cleaned = new string(name
            .Where(character => character is not ('[' or ']' or ':' or '*' or '?' or '/' or '\\'))
            .ToArray())
            .Trim();

        if (cleaned.Length == 0)
        {
            cleaned = "Sheet";
        }

        return cleaned.Length <= 31 ? cleaned : cleaned[..31];
    }

    private static string UniqueSheetName(IEnumerable<string> existing, string name)
    {
        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name))
        {
            return name;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = name.Length + suffix.ToString(CultureInfo.InvariantCulture).Length > 31
                ? name[..(31 - suffix.ToString(CultureInfo.InvariantCulture).Length)] + suffix
                : name + suffix;

            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    // ------------------------------------------------------------------ PowerPoint

    private static string WritePowerPoint(string path, IReadOnlyList<MarkdownBlock> blocks)
    {
        var slides = SplitSlides(blocks);
        if (slides.Count == 0)
        {
            slides.Add([new MarkdownHeading(1, "空白演示文稿")]);
        }

        using var document = PresentationDocument.Create(path, PresentationDocumentType.Presentation);
        var presentationPart = document.AddPresentationPart();
        presentationPart.Presentation = new P.Presentation();

        var slideMasterPart = presentationPart.AddNewPart<SlideMasterPart>();
        var slideLayoutPart = slideMasterPart.AddNewPart<SlideLayoutPart>();
        slideLayoutPart.SlideLayout = CreateSlideLayout();
        slideMasterPart.SlideMaster = CreateSlideMaster(slideMasterPart.GetIdOfPart(slideLayoutPart));
        AddTheme(slideMasterPart);

        var slideList = new P.SlideIdList();
        presentationPart.Presentation.Append(
            new P.SlideMasterIdList(new P.SlideMasterId
            {
                Id = 2147483648U,
                RelationshipId = presentationPart.GetIdOfPart(slideMasterPart)
            }),
            slideList,
            new P.SlideSize { Cx = 12192000, Cy = 6858000 },
            new P.NotesSize { Cx = 6858000, Cy = 9144000 });

        uint slideId = 256;

        foreach (var slideBlocks in slides)
        {
            var slidePart = presentationPart.AddNewPart<SlidePart>();
            slidePart.Slide = CreateSlide(slideBlocks);
            slidePart.AddPart(slideLayoutPart);

            slideList.Append(new P.SlideId
            {
                Id = slideId++,
                RelationshipId = presentationPart.GetIdOfPart(slidePart)
            });
        }

        presentationPart.Presentation.Save();
        return $"幻灯片 {slides.Count} 页";
    }

    private static List<List<MarkdownBlock>> SplitSlides(IReadOnlyList<MarkdownBlock> blocks)
    {
        var hasRules = blocks.Any(block => block is MarkdownRule);
        var slides = new List<List<MarkdownBlock>>();
        var current = new List<MarkdownBlock>();

        foreach (var block in blocks)
        {
            if (block is MarkdownRule)
            {
                if (current.Count > 0)
                {
                    slides.Add(current);
                }

                current = [];
                continue;
            }

            // 没写 --- 时，一级标题天然是一页的开头（第一页之前的内容仍算第一页）。
            if (!hasRules && block is MarkdownHeading { Level: 1 } && current.Count > 0)
            {
                slides.Add(current);
                current = [];
            }

            current.Add(block);
        }

        if (current.Count > 0)
        {
            slides.Add(current);
        }

        return slides;
    }

    private static P.Slide CreateSlide(IReadOnlyList<MarkdownBlock> blocks)
    {
        var title = blocks.OfType<MarkdownHeading>().FirstOrDefault()?.Text ?? "Untitled";
        var body = new List<(int Level, string Text)>();

        foreach (var block in blocks)
        {
            switch (block)
            {
                case MarkdownHeading heading when heading.Text != title:
                    body.Add((0, heading.Text));
                    break;

                case MarkdownParagraph paragraph:
                    body.Add((0, paragraph.Text));
                    break;

                case MarkdownList list:
                    foreach (var item in list.Items)
                    {
                        body.Add((item.Level, (item.Ordered ? "• " : "• ") + item.Text));
                    }

                    break;

                case MarkdownCode code:
                    foreach (var line in code.Text.Replace("\r\n", "\n").Split('\n'))
                    {
                        body.Add((1, line));
                    }

                    break;

                case MarkdownQuote quote:
                    body.Add((0, quote.Text));
                    break;

                case MarkdownTable table:
                    foreach (var row in table.Rows)
                    {
                        body.Add((1, string.Join("  |  ", row)));
                    }

                    break;
            }
        }

        var tree = new P.ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1U, Name = string.Empty },
                new P.NonVisualGroupShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties(new A.TransformGroup()),
            TextBoxShape(2, "Title", 609600, 457200, 10972800, 1143000, 3200, true, title),
            TextBoxShape(3, "Body", 609600, 1828800, 10972800, 4343400, 1800, false, string.Empty, body));

        return new P.Slide(new P.CommonSlideData(tree));
    }

    private static P.Shape TextBoxShape(
        uint id,
        string name,
        long x,
        long y,
        long width,
        long height,
        int fontSize,
        bool bold,
        string title,
        IReadOnlyList<(int Level, string Text)>? body = null)
    {
        var textBody = new P.TextBody(
            new A.BodyProperties { Wrap = A.TextWrappingValues.Square },
            new A.ListStyle());

        if (body is null)
        {
            textBody.Append(new A.Paragraph(
                new A.ParagraphProperties(
                    new A.DefaultRunProperties
                    {
                        FontSize = fontSize,
                        Bold = bold,
                        Language = "zh-CN"
                    }),
                new A.Run(new A.Text(title))));
        }
        else
        {
            foreach (var (level, text) in body)
            {
                var paragraphProperties = new A.ParagraphProperties
                {
                    Level = Math.Clamp(level + 1, 0, 5),
                    LeftMargin = 342900 + (Math.Clamp(level, 0, 4) * 342900),
                    Indent = -228600
                };
                paragraphProperties.Append(new A.DefaultRunProperties
                {
                    FontSize = fontSize,
                    Language = "zh-CN"
                });

                textBody.Append(new A.Paragraph(
                    paragraphProperties,
                    new A.Run(new A.Text(text))));
            }
        }

        return new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = name },
                new P.NonVisualShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.ShapeProperties(
                new A.Transform2D(
                    new A.Offset { X = x, Y = y },
                    new A.Extents { Cx = width, Cy = height }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle },
                new A.NoFill()),
            textBody);
    }

    private static P.SlideMaster CreateSlideMaster(string layoutRelationshipId)
    {
        var tree = new P.ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1U, Name = string.Empty },
                new P.NonVisualGroupShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties(new A.TransformGroup()),
            PlaceholderShape(2, "Title Placeholder 1", P.PlaceholderValues.Title, 0U,
                609600, 457200, 10972800, 1143000, 3200, "Click to edit Master title style"),
            PlaceholderShape(3, "Text Placeholder 2", P.PlaceholderValues.Body, 1U,
                609600, 1828800, 10972800, 4343400, 1800, "Click to edit Master text styles"));

        return new P.SlideMaster(
            new P.CommonSlideData(tree),
            new P.ColorMap
            {
                Background1 = A.ColorSchemeIndexValues.Light1,
                Text1 = A.ColorSchemeIndexValues.Dark1,
                Background2 = A.ColorSchemeIndexValues.Light2,
                Text2 = A.ColorSchemeIndexValues.Dark2,
                Accent1 = A.ColorSchemeIndexValues.Accent1,
                Accent2 = A.ColorSchemeIndexValues.Accent2,
                Accent3 = A.ColorSchemeIndexValues.Accent3,
                Accent4 = A.ColorSchemeIndexValues.Accent4,
                Accent5 = A.ColorSchemeIndexValues.Accent5,
                Accent6 = A.ColorSchemeIndexValues.Accent6,
                Hyperlink = A.ColorSchemeIndexValues.Hyperlink,
                FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink
            },
            new P.SlideLayoutIdList(new P.SlideLayoutId
            {
                Id = 2147483649U,
                RelationshipId = layoutRelationshipId
            }));
    }

    private static P.SlideLayout CreateSlideLayout()
    {
        var tree = new P.ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1U, Name = string.Empty },
                new P.NonVisualGroupShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties(new A.TransformGroup()),
            PlaceholderShape(2, "Title 1", P.PlaceholderValues.Title, 0U,
                609600, 457200, 10972800, 1143000, 3200, "Click to edit Master title style"),
            PlaceholderShape(3, "Content Placeholder 2", P.PlaceholderValues.Body, 1U,
                609600, 1828800, 10972800, 4343400, 1800, "Click to edit Master text styles"));

        return new P.SlideLayout(new P.CommonSlideData(tree) { Name = "Title and Content" })
        {
            Type = P.SlideLayoutValues.Title,
            Preserve = true
        };
    }

    private static P.Shape PlaceholderShape(
        uint id,
        string name,
        P.PlaceholderValues type,
        uint index,
        long x,
        long y,
        long width,
        long height,
        int fontSize,
        string text)
    {
        var placeholder = new P.PlaceholderShape { Type = type };
        if (index > 0)
        {
            placeholder.Index = index;
        }

        return new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = name },
                new P.NonVisualShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties(placeholder)),
            new P.ShapeProperties(
                new A.Transform2D(
                    new A.Offset { X = x, Y = y },
                    new A.Extents { Cx = width, Cy = height }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }),
            new P.TextBody(
                new A.BodyProperties(),
                new A.ListStyle(),
                new A.Paragraph(
                    new A.ParagraphProperties(
                        new A.DefaultRunProperties { FontSize = fontSize }),
                    new A.Run(new A.Text(text)))));
    }

    private static void AddTheme(SlideMasterPart slideMasterPart)
    {
        var themePart = slideMasterPart.AddNewPart<ThemePart>();
        themePart.Theme = new A.Theme { Name = "Office Theme" };
        themePart.Theme.InnerXml = ThemeXml;
        themePart.Theme.Save();
    }

    private const string ThemeXml = """
        <a:themeElements xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
          <a:clrScheme name="Office">
            <a:dk1><a:sysClr val="windowText" lastClr="000000"/></a:dk1>
            <a:lt1><a:sysClr val="window" lastClr="FFFFFF"/></a:lt1>
            <a:dk2><a:srgbClr val="44546A"/></a:dk2>
            <a:lt2><a:srgbClr val="E7E6E6"/></a:lt2>
            <a:accent1><a:srgbClr val="4472C4"/></a:accent1>
            <a:accent2><a:srgbClr val="ED7D31"/></a:accent2>
            <a:accent3><a:srgbClr val="A5A5A5"/></a:accent3>
            <a:accent4><a:srgbClr val="FFC000"/></a:accent4>
            <a:accent5><a:srgbClr val="5B9BD5"/></a:accent5>
            <a:accent6><a:srgbClr val="70AD47"/></a:accent6>
            <a:hlink><a:srgbClr val="0563C1"/></a:hlink>
            <a:folHlink><a:srgbClr val="954F72"/></a:folHlink>
          </a:clrScheme>
          <a:fontScheme name="Office">
            <a:majorFont>
              <a:latin typeface="Calibri Light"/>
              <a:ea typeface=""/>
              <a:cs typeface=""/>
            </a:majorFont>
            <a:minorFont>
              <a:latin typeface="Calibri"/>
              <a:ea typeface=""/>
              <a:cs typeface=""/>
            </a:minorFont>
          </a:fontScheme>
          <a:fmtScheme name="Office">
            <a:fillStyleLst>
              <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
              <a:solidFill><a:schemeClr val="phClr"><a:tint val="50000"/></a:schemeClr></a:solidFill>
              <a:solidFill><a:schemeClr val="phClr"><a:shade val="80000"/></a:schemeClr></a:solidFill>
            </a:fillStyleLst>
            <a:lnStyleLst>
              <a:ln w="6350" cap="flat" cmpd="sng" algn="ctr"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:prstDash val="solid"/></a:ln>
              <a:ln w="12700" cap="flat" cmpd="sng" algn="ctr"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:prstDash val="solid"/></a:ln>
              <a:ln w="19050" cap="flat" cmpd="sng" algn="ctr"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:prstDash val="solid"/></a:ln>
            </a:lnStyleLst>
            <a:effectStyleLst>
              <a:effectStyle><a:effectLst/></a:effectStyle>
              <a:effectStyle><a:effectLst/></a:effectStyle>
              <a:effectStyle><a:effectLst/></a:effectStyle>
            </a:effectStyleLst>
            <a:bgFillStyleLst>
              <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
              <a:solidFill><a:schemeClr val="phClr"><a:tint val="95000"/></a:schemeClr></a:solidFill>
              <a:solidFill><a:schemeClr val="phClr"><a:shade val="90000"/></a:schemeClr></a:solidFill>
            </a:bgFillStyleLst>
          </a:fmtScheme>
        </a:themeElements>
        <a:objectDefaults xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"/>
        <a:extraClrSchemeLst xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"/>
        """;
}
