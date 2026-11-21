using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using RpdFos.Api.Tests.DocumentTypes;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using RpdFos.Core.DocumentTypes;
using RpdFos.Core.Printing;
using Xunit;

namespace RpdFos.Api.Tests.Documents;

/// <summary>
/// Таблицы форматированного текста в печати (этап 7, B4): разбор в блоки, сетка объединённых ячеек, таблица DOCX
/// с рамками на ширину места закладки. Без базы: шаблон РПД v3 и сопоставление из реестра, а где важно окружение
/// закладки (ячейка, абзац с текстом вокруг), — маленький шаблон, собранный в тесте.
/// </summary>
public class RichTextTablePrintingTests
{
    private static readonly DocumentTypeRegistry Registry = new();

    private static IReadOnlyList<PrintBlock> Parse(string html)
    {
        var id = 0;
        return RichTextBlocks.Parse(html, () => ++id);
    }

    [Fact]
    public void Table_becomes_one_block_with_rows_cells_spans_and_runs()
    {
        var blocks = Parse("<p>до</p><table><thead><tr><th colspan=\"2\">Заголовок</th></tr></thead>" +
                           "<tbody><tr><td rowspan=\"2\"><p>А</p><p>А2</p></td><td>Б <strong>жирный</strong></td></tr>" +
                           "<tr><td>В</td></tr></tbody></table>после");

        Assert.Equal(["до", "Заголовок А А2 Б жирный В", "после"], blocks.Select(b => b.PlainText.Replace(";", "")));
        var table = Assert.IsType<PrintTable>(blocks[1]);
        Assert.Equal(3, table.Rows.Count);
        var header = Assert.Single(table.Rows[0].Cells);
        Assert.Equal((2, 1, true, "Заголовок"), (header.ColSpan, header.RowSpan, header.Header, header.PlainText));
        var tall = table.Rows[1].Cells[0];
        Assert.Equal((1, 2, false), (tall.ColSpan, tall.RowSpan, tall.Header));
        Assert.Equal(["А", "А2"], tall.Paragraphs.Select(p => p.PlainText));
        Assert.Equal([false, true], table.Rows[1].Cells[1].Paragraphs.Single().Runs.Select(r => r.Bold));
    }

    [Fact]
    public void Cell_keeps_lists_and_line_breaks()
    {
        var table = Assert.IsType<PrintTable>(Assert.Single(Parse(
            "<table><tr><td><ul><li>x</li><li>y</li></ul>строка<br>ещё</td></tr></table>")));

        var cell = table.Rows[0].Cells[0];
        Assert.Equal(["x", "y", "строка", "ещё"], cell.Paragraphs.Select(p => p.PlainText));
        Assert.Equal([PrintListKind.Bullet, PrintListKind.Bullet, PrintListKind.None, PrintListKind.None],
            cell.Paragraphs.Select(p => p.List));
    }

    [Fact]
    public void Table_inside_paragraph_or_list_item_is_still_a_block()
    {
        // Разбор без doctype оставляет таблицу внутри абзаца и пункта — печать всё равно выделяет её блоком.
        var blocks = Parse("<p>до<table><tr><td>x</td></tr></table>после</p><ul><li>пункт<table><tr><td>y</td></tr></table></li></ul>");

        Assert.Equal(["до", "x", "после", "пункт", "y"], blocks.Select(b => b.PlainText));
        Assert.Equal([typeof(PrintParagraph), typeof(PrintTable), typeof(PrintParagraph), typeof(PrintParagraph), typeof(PrintTable)],
            blocks.Select(b => b.GetType()));
        Assert.Equal(PrintListKind.Bullet, Assert.IsType<PrintParagraph>(blocks[3]).List);
    }

    [Theory]
    [InlineData("<table></table>")]
    [InlineData("<table><tbody></tbody></table>")]
    [InlineData("<table><tr></tr><tr></tr></table>")]
    public void Table_without_cells_is_not_a_block(string html) => Assert.Empty(Parse(html));

    [Fact]
    public void Text_without_tables_prints_as_before()
    {
        var blocks = Parse("<p>Знания <strong>математики</strong></p>текст<ul><li>пункт</li></ul>");

        Assert.All(blocks, b => Assert.IsType<PrintParagraph>(b));
        Assert.Equal(["Знания математики", "текст", "пункт"], blocks.Select(b => b.PlainText));
    }

    // Сетка: каждая строка покрывает все столбцы. Запись клетки: текст@столбец, «xN» — ширина в столбцах,
    // «↓» — начало объединения вниз, «|» — продолжение, «·» — пустое место строки.
    private static PrintTableCell Cell(string text, int col = 1, int row = 1) => new([PrintParagraph.Plain(text)], col, row);

    private static PrintTableRow Row(params PrintTableCell[] cells) => new(cells);

    private static string Describe(PrintTableGrid? grid) => grid is null ? "нет" :
        $"{grid.Columns}: " + string.Join(" / ", grid.Rows.Select(row => string.Join(" ", row.Select(c =>
            $"{c.Cell?.PlainText ?? "·"}@{c.Column}{(c.Span > 1 ? "x" + c.Span : "")}{c.Merge switch { MergeKind.Restart => "↓", MergeKind.Continue => "|", _ => "" }}"))));

    [Fact]
    public void Grid_of_plain_table() =>
        Assert.Equal("2: a@0 b@1 / c@0 d@1",
            Describe(PrintTableGrid.Build(new PrintTable([Row(Cell("a"), Cell("b")), Row(Cell("c"), Cell("d"))]))));

    [Fact]
    public void Grid_with_column_span() =>
        Assert.Equal("2: a@0x2 / b@0 c@1",
            Describe(PrintTableGrid.Build(new PrintTable([Row(Cell("a", col: 2)), Row(Cell("b"), Cell("c"))]))));

    [Fact]
    public void Grid_with_row_span_continues_the_cell_in_the_next_row() =>
        Assert.Equal("2: a@0↓ b@1 / a@0| c@1",
            Describe(PrintTableGrid.Build(new PrintTable([Row(Cell("a", row: 2), Cell("b")), Row(Cell("c"))]))));

    [Fact]
    public void Grid_with_both_spans() =>
        Assert.Equal("3: a@0x2↓ b@2 / a@0x2| c@2",
            Describe(PrintTableGrid.Build(new PrintTable([Row(Cell("a", col: 2, row: 2), Cell("b")), Row(Cell("c"))]))));

    [Fact]
    public void Grid_pads_short_rows_with_empty_places() =>
        Assert.Equal("3: a@0 b@1 c@2 / d@0 ·@1 ·@2",
            Describe(PrintTableGrid.Build(new PrintTable([Row(Cell("a"), Cell("b"), Cell("c")), Row(Cell("d"))]))));

    [Fact]
    public void Row_span_past_the_end_stops_at_the_last_row() =>
        Assert.Equal("2: a@0 b@1",
            Describe(PrintTableGrid.Build(new PrintTable([Row(Cell("a", row: 5), Cell("b"))]))));

    [Fact]
    public void Cell_does_not_overlap_a_place_taken_from_above() =>
        // В HTML такое пересечение — ошибка разметки, в DOCX сетка одна: ячейка занимает столько, сколько свободно.
        Assert.Equal("2: a@0 b@1↓ / c@0 b@1|",
            Describe(PrintTableGrid.Build(new PrintTable([Row(Cell("a"), Cell("b", row: 2)), Row(Cell("c", col: 2))]))));

    [Fact]
    public void Grid_is_limited_by_word_column_count()
    {
        PrintTable Wide(int cells) => new([Row(Enumerable.Range(0, cells).Select(i => Cell(i.ToString())).ToArray())]);

        Assert.Equal(63, PrintTableGrid.Build(Wide(63))!.Columns);
        Assert.Null(PrintTableGrid.Build(Wide(64)));
        Assert.Null(PrintTableGrid.Build(new PrintTable([Row(Cell("a", col: 64))])));
        Assert.Null(PrintTableGrid.Build(new PrintTable([])));
    }

    // Печать в DOCX

    private static PrintContext Context(string schemaJson, string payloadJson)
    {
        var schema = JsonDocument.Parse(schemaJson).RootElement;
        var payload = JsonDocument.Parse(payloadJson).RootElement;
        var facts = new DisciplineFacts(
            new Discipline { Name = "Дисциплина", Semesters = [new DisciplineSemesterDetails { Semester = 1, Lectures = 16, ControlTypes = ["exam"] }] },
            new CurriculumPlan(), new StudyDirection(), new StudyProfile(), null, []);
        return new PrintContext(payload, schema, Registry.FindSchema, facts, new SignatoryReport([], []), 2026,
            DocumentMetadata.Hidden(schema, facts.Semesters, Registry.FindSchema));
    }

    private static Body Validated(byte[] content)
    {
        var docx = WordprocessingDocument.Open(new MemoryStream(content), false);
        Assert.Empty(new OpenXmlValidator(FileFormatVersions.Office2019).Validate(docx)
            .Select(e => $"{e.Path?.XPath}: {e.Description}"));
        return docx.MainDocumentPart!.Document!.Body!;
    }

    // «Цели освоения»: абзац закладки в теле шаблона РПД v3, прямое оформление Times New Roman 12 с красной строкой.
    private static Body PrintGoals(string html)
    {
        var rpd = Registry.Get(DocumentTypeRegistry.Rpd);
        var context = Context(rpd.SchemaJson, JsonSerializer.Serialize(new { goals_and_objectives = new { goals = html } }));
        return Validated(DocxGenerator.Generate(DocxBookmarksTests.Template(DocxBookmarksTests.Rpd), rpd.PrintMapping, context).Content);
    }

    private const string TextSchema = """{ "type": "object", "properties": { "text": { "type": "string", "x-ui-widget": "richtext" } } }""";

    private static readonly Dictionary<string, BookmarkTarget> TextMapping = new() { ["AutofillText"] = new(BookmarkTargetKinds.Field, "/text") };

    // Абзац только с закладкой (заглушка — единственный текст) или с текстом вокруг неё.
    private static Paragraph Bookmarked(string before = "", string after = "")
    {
        var paragraph = new Paragraph();
        if (before.Length > 0) paragraph.AppendChild(new Run(new Text(before) { Space = SpaceProcessingModeValues.Preserve }));
        paragraph.Append(new BookmarkStart { Name = "AutofillText1", Id = "1" }, new Run(new Text("AutofillText")), new BookmarkEnd { Id = "1" });
        if (after.Length > 0) paragraph.AppendChild(new Run(new Text(after) { Space = SpaceProcessingModeValues.Preserve }));
        return paragraph;
    }

    // Страница A4 с полями 3 и 1,5 см: текст — 9355 twips.
    private static SectionProperties Page() => new(
        new PageSize { Width = 11906U, Height = 16838U },
        new PageMargin { Top = 1134, Right = 850U, Bottom = 1134, Left = 1701U, Header = 708U, Footer = 708U, Gutter = 0U });

    // Тело маленького шаблона собирает тест: абзац закладки отдаётся ему, чтобы поставить его куда нужно.
    private static Body PrintBody(string html, Func<Paragraph, OpenXmlElement[]> content)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(new Body(content(Bookmarked()).Append(Page()).ToArray()));
            main.Document.Save();
        }
        var context = Context(TextSchema, JsonSerializer.Serialize(new { text = html }));
        return Validated(DocxGenerator.Generate(stream.ToArray(), TextMapping, context).Content);
    }

    private static Body PrintText(string html, string before, string after) =>
        PrintBody(html, _ => [Bookmarked(before, after)]);

    private static string Merge(TableCell cell) => cell.TableCellProperties?.VerticalMerge is { } m
        ? m.Val?.Value == MergedCellValues.Restart ? "restart" : "continue"
        : "none";

    private static int TextWidth(Body body)
    {
        var section = body.Elements<SectionProperties>().Last();
        return (int)(section.GetFirstChild<PageSize>()!.Width!.Value - section.GetFirstChild<PageMargin>()!.Left!.Value -
                     section.GetFirstChild<PageMargin>()!.Right!.Value);
    }

    [Fact]
    public void Table_is_printed_with_borders_merges_and_formatting()
    {
        var body = PrintGoals("<p>Перед таблицей</p><table><thead><tr><th colspan=\"2\">Заголовок</th><th>Итого</th></tr></thead>" +
                              "<tbody><tr><td rowspan=\"2\"><p>А</p><p><strong>жирный</strong> и <em>курсив</em></p></td><td>Б</td><td>В</td></tr>" +
                              "<tr><td>Г</td><td>Д</td></tr></tbody></table><p>После таблицы</p>");

        var table = body.Descendants<Table>().Single(t => t.InnerText.Contains("Заголовок"));
        var rows = table.Elements<TableRow>().ToList();

        // Три столбца сетки; каждая строка покрывает все три: заголовок на два столбца и «Итого», ячейка «А» — на две строки.
        Assert.Equal(3, rows.Count);
        Assert.Equal(3, table.GetFirstChild<TableGrid>()!.Elements<GridColumn>().Count());
        Assert.Equal([2, 1], rows[0].Elements<TableCell>().Select(c => c.TableCellProperties?.GridSpan?.Val?.Value ?? 1));
        Assert.Equal(["restart", "none", "none"], rows[1].Elements<TableCell>().Select(Merge));
        Assert.Equal(["continue", "none", "none"], rows[2].Elements<TableCell>().Select(Merge));
        Assert.Equal(["Б", "В"], rows[1].Elements<TableCell>().Skip(1).Select(c => c.InnerText));
        Assert.Equal(["", "Г", "Д"], rows[2].Elements<TableCell>().Select(c => c.InnerText));

        // Рамки со всех сторон и между ячейками, ширина — текст страницы, столбцы поровну.
        var borders = table.GetFirstChild<TableProperties>()!.TableBorders!;
        Assert.Equal(6, borders.ChildElements.Count);
        Assert.All(borders.ChildElements.Cast<BorderType>(), b => Assert.Equal(BorderValues.Single, b.Val!.Value));
        var width = int.Parse(table.GetFirstChild<TableProperties>()!.TableWidth!.Width!.Value!);
        Assert.InRange(width, TextWidth(body) - 3, TextWidth(body));
        Assert.Single(table.GetFirstChild<TableGrid>()!.Elements<GridColumn>().Select(c => c.Width!.Value).Distinct());

        // Заголовок — полужирным и по центру; абзацы ячейки, полужирный и курсив — как в тексте вне таблицы.
        var head = rows[0].Elements<TableCell>().First();
        Assert.All(head.Descendants<Run>(), r => Assert.NotNull(r.RunProperties?.Bold));
        Assert.Equal(JustificationValues.Center, head.Descendants<Paragraph>().Single().ParagraphProperties!.Justification!.Val!.Value);
        var tall = rows[1].Elements<TableCell>().First();
        Assert.Equal(["А", "жирный и курсив"], tall.Elements<Paragraph>().Select(p => p.InnerText));
        Assert.NotNull(tall.Descendants<Run>().Single(r => r.InnerText == "жирный").RunProperties?.Bold);
        Assert.NotNull(tall.Descendants<Run>().Single(r => r.InnerText == "курсив").RunProperties?.Italic);
        Assert.Null(tall.Descendants<Run>().Single(r => r.InnerText == " и ").RunProperties?.Bold);

        // Шрифт — как у заглушки закладки; красная строка и выравнивание по ширине абзаца закладки в ячейку не попадают.
        Assert.All(table.Descendants<Run>(), r => Assert.Equal("Times New Roman", r.RunProperties!.RunFonts!.Ascii!.Value));
        Assert.All(table.Descendants<Paragraph>(), p => Assert.Null(p.ParagraphProperties!.Indentation));
        Assert.Equal(JustificationValues.Left, tall.Elements<Paragraph>().First().ParagraphProperties!.Justification!.Val!.Value);

        // Текст до и после — абзацами рядом с таблицей, заглушки закладки не осталось.
        Assert.Equal("Перед таблицей", Assert.IsType<Paragraph>(table.PreviousSibling()).InnerText);
        Assert.Equal("После таблицы", Assert.IsType<Paragraph>(table.NextSibling()).InnerText);
        Assert.DoesNotContain("AutofillGoals", body.InnerText);
    }

    [Fact]
    public void Table_before_the_text_stands_before_the_bookmark_paragraph()
    {
        var body = PrintGoals("<table><tr><td>Первая</td></tr></table><p>Текст после</p>");

        var table = body.Descendants<Table>().Single(t => t.InnerText == "Первая");
        Assert.Equal("Текст после", Assert.IsType<Paragraph>(table.NextSibling()).InnerText);
    }

    [Fact]
    public void Value_made_only_of_a_table_leaves_a_paragraph_after_it()
    {
        var body = PrintGoals("<table><tr><td>Единственная</td></tr></table>");

        var table = body.Descendants<Table>().Single(t => t.InnerText == "Единственная");
        // Абзац закладки с её метками остаётся после таблицы пустым.
        var after = Assert.IsType<Paragraph>(table.NextSibling());
        Assert.Equal("", after.InnerText);
        Assert.Contains(after.ChildElements, c => c is BookmarkStart);
    }

    [Fact]
    public void Tables_in_a_row_are_not_glued_together()
    {
        var body = PrintGoals("<table><tr><td>Первая</td></tr></table><table><tr><td>Вторая</td></tr></table>");

        var first = body.Descendants<Table>().Single(t => t.InnerText == "Первая");
        var second = body.Descendants<Table>().Single(t => t.InnerText == "Вторая");
        // Word слил бы таблицы без абзаца между ними в одну.
        Assert.IsType<Paragraph>(first.NextSibling());
        Assert.Same(second, first.NextSibling()!.NextSibling());
    }

    [Fact]
    public void Table_in_a_cell_takes_the_cell_width_and_is_followed_by_a_paragraph()
    {
        // Закладка — последний абзац ячейки шириной 5000: таблица не шире ячейки, ячейка не кончается таблицей.
        var body = PrintBody("<table><tr><td>a</td><td>b</td></tr></table>", bookmarked => [
            new Table(
                new TableProperties(new TableWidth { Width = "5000", Type = TableWidthUnitValues.Dxa }),
                new TableGrid(new GridColumn { Width = "5000" }),
                new TableRow(new TableCell(
                    new TableCellProperties(new TableCellWidth { Width = "5000", Type = TableWidthUnitValues.Dxa }), bookmarked))),
            new Paragraph()]);

        var outer = body.Elements<Table>().Single();
        var cell = outer.Descendants<TableCell>().First();
        var inner = cell.Elements<Table>().Single();
        Assert.Equal("ab", inner.InnerText);
        Assert.InRange(int.Parse(inner.GetFirstChild<TableProperties>()!.TableWidth!.Width!.Value!), 5000 - 216 - 2, 5000 - 216);
        Assert.IsType<Paragraph>(cell.ChildElements.Last());
    }

    [Fact]
    public void Table_wider_than_word_allows_is_printed_as_lines_of_text()
    {
        var cells = string.Concat(Enumerable.Range(1, 64).Select(i => $"<td>{i}</td>"));

        var body = PrintGoals($"<table><tr>{cells}</tr><tr><td>конец</td></tr></table>");

        Assert.DoesNotContain(body.Descendants<Table>(), t => t.InnerText.Contains("конец"));
        Assert.Contains(body.Descendants<Paragraph>(), p => p.InnerText.StartsWith("1 | 2 | 3 | ") && p.InnerText.EndsWith(" | 63 | 64"));
        Assert.Contains(body.Descendants<Paragraph>(), p => p.InnerText == "конец");
    }

    [Fact]
    public void Placeholder_inside_a_sentence_gets_the_table_as_text()
    {
        // Таблицу в середину предложения не вставить: слова остаются в строке, файл остаётся верным.
        var body = PrintText("<table><tr><td>а</td><td>б</td></tr><tr><td>в</td><td>г</td></tr></table>", before: "Перед ", after: " после");

        Assert.Empty(body.Descendants<Table>());
        Assert.Equal("Перед а б; в г после", body.Descendants<Paragraph>().Select(p => p.InnerText).Single(t => t.StartsWith("Перед")));
    }

    [Fact]
    public void Text_without_tables_is_printed_without_tables_and_with_lists_as_before()
    {
        var body = PrintGoals("<p>Первый <strong>абзац</strong></p><p>Второй</p><ul><li>пункт</li></ul>");

        Assert.DoesNotContain(body.Descendants<Table>(), t => t.InnerText.Contains("абзац"));
        var paragraphs = body.Descendants<Paragraph>().Where(p => p.InnerText is "Первый абзац" or "Второй" or "пункт").ToList();
        Assert.Equal(["Первый абзац", "Второй", "пункт"], paragraphs.Select(p => p.InnerText));
        Assert.NotNull(paragraphs[0].Descendants<Run>().Single(r => r.InnerText == "абзац").RunProperties?.Bold);
        Assert.NotNull(paragraphs[2].ParagraphProperties?.NumberingProperties);
    }
}
