using System.Text.Json;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using RpdFos.Api.Tests.DocumentTypes;
using RpdFos.Core.Documents;
using RpdFos.Core.DocumentTypes;
using RpdFos.Core.Printing;
using Xunit;
using Entities = RpdFos.Core.Data.Entities;

namespace RpdFos.Api.Tests.Documents;

/// <summary>
/// Оглавление РПД строит генератор (этап 8, A13): строки — по заголовкам, которые напечатаны, ссылками на их закладки и номером
/// страницы полем PAGEREF; в DOCX номера вписаны, строки стоят в поле TOC. Файл оформления v5; прежние файлы печатаются как раньше.
/// Номера страниц по настоящему PDF — в <see cref="Integration.TocPdfTests"/>.
/// </summary>
public class TocPrintingTests
{
    private static (Body Body, DocxResult Result) Open(DocxResult result)
    {
        var document = WordprocessingDocument.Open(new MemoryStream(result.Content), false);
        Assert.Empty(new OpenXmlValidator(FileFormatVersions.Office2019).Validate(document).Select(e => $"{e.Path?.XPath}: {e.Description}"));
        return (document.MainDocumentPart!.Document!.Body!, result);
    }

    private static List<string> LineTexts(Body body) => TocSample.Lines(body).Select(p => TocSample.WithoutPage(TocSample.Plain(p))).ToList();

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, true, false)]
    public void Lines_are_the_printed_headings(bool labs, bool practice, bool courseProject, bool section6)
    {
        var (body, result) = Open(TocSample.Print(labs, practice, courseProject, section6));

        Assert.Equal(TocSample.Expected(section6), LineTexts(body));
        Assert.DoesNotContain("AutofillToc", body.InnerText);
        // Других заголовков в документе нет: уровень структуры есть только у тех, на кого ссылаются строки, и ссылаются они по порядку.
        var printed = body.Elements<Paragraph>().Where(p => p.ParagraphProperties?.OutlineLevel is not null && p.InnerText.Trim().Length > 0).ToList();
        var linked = TocSample.Lines(body).Select(line => Target(body, line)).ToList();
        Assert.Equal(printed, linked);
        // Номера разделов 1–3 ставит список Word, в тексте заголовка их нет; у остальных строка — текст заголовка.
        Assert.All(LineTexts(body).Zip(linked), pair => Assert.EndsWith(TocSample.Bare(TocSample.Plain(pair.Second)), TocSample.Bare(pair.First)));
        Assert.Empty(result.Report.Warnings);
        // Условный раздел, которого в плане нет, печатается заголовком и формулировкой (FR-TPL-14) — поэтому он есть и в оглавлении.
        var heading = body.Elements<Paragraph>().Single(p => p.InnerText == "3.3 Лабораторные работы");
        if (labs) Assert.IsType<Table>(heading.NextSibling());
        else Assert.Equal("Лабораторные работы учебным планом не предусмотрены.", heading.NextSibling<Paragraph>()!.InnerText);
    }

    [Fact]
    public void Line_refers_to_the_bookmark_of_its_heading_and_prints_its_page()
    {
        var (body, _) = Open(TocSample.Print(true, true, true, true));

        foreach (var line in TocSample.Lines(body))
        {
            var link = line.Elements<Hyperlink>().Single();
            var anchor = link.Anchor!.Value!;
            Assert.Equal($" PAGEREF {anchor} \\h ", link.Descendants<FieldCode>().Single().Text);
            Assert.Equal([FieldCharValues.Begin, FieldCharValues.Separate, FieldCharValues.End], link.Descendants<FieldChar>().Select(f => f.FieldCharType!.Value));
            var start = Assert.Single(body.Descendants<BookmarkStart>(), b => b.Name == anchor);
            var heading = (Paragraph)start.Parent!;
            Assert.Contains(heading.Elements<BookmarkEnd>(), e => e.Id!.Value == start.Id!.Value);
        }
        var ids = body.Descendants<BookmarkStart>().Select(b => b.Id!.Value).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        // В DOCX для конвертера поля TOC нет: внутри него LibreOffice не вычисляет PAGEREF. Номера страниц ещё не вписаны.
        Assert.DoesNotContain(body.Descendants<FieldCode>(), f => f.Text.Contains("TOC"));
        Assert.All(TocSample.Lines(body), line => Assert.EndsWith("\t", TocSample.Plain(line)));
    }

    [Fact]
    public void Lines_take_the_look_of_the_sample_of_their_level()
    {
        var (body, _) = Open(TocSample.Print(true, true, true, true));
        var template = WordprocessingDocument.Open(new MemoryStream(DocxBookmarksTests.Template(DocxBookmarksTests.Rpd)), false).MainDocumentPart!.Document!.Body!;
        var samples = template.Elements<Paragraph>().Where(p => p.InnerText == "AutofillToc").Select(p => p.ParagraphProperties!.OuterXml).ToList();

        Assert.Equal(2, samples.Count);
        Assert.All(TocSample.Lines(body), line =>
            Assert.Equal(samples[Regex.IsMatch(line.InnerText, @"^\d+\.\d") ? 1 : 0],
                line.ParagraphProperties!.OuterXml));
    }

    [Fact]
    public void Section_that_is_not_printed_has_no_line()
    {
        // Файл оформления, в котором раздел 3.3 без лабораторных не печатается совсем, как раздел 6 без текста.
        var template = WithLabBlock();
        var mapping = new Dictionary<string, BookmarkTarget>(new DocumentTypeRegistry().Get(DocumentTypeRegistry.Rpd).PrintMapping)
        {
            ["AutofillLabSection"] = new(BookmarkTargetKinds.Block, "/lab_works", BlockModes.Present)
        };

        var without = TocSample.Print(false, true, false, false, template, mapping);
        var with = TocSample.Print(true, true, false, false, template, mapping);

        Assert.Equal(TocSample.Expected(false).Where(l => !l.StartsWith("3.3.")), without.Toc!.Lines.Select(Text));
        Assert.Equal(TocSample.Expected(false), with.Toc!.Lines.Select(Text));

        static string Text(TocLine line) => line.Number + (line.Level == 1 ? "\t" : " ") + line.Title;
    }

    [Theory]
    [InlineData(DocxBookmarksTests.RpdV4)]
    [InlineData(DocxBookmarksTests.RpdV3)]
    [InlineData(DocxBookmarksTests.RpdV2)]
    public void Former_files_keep_their_static_contents(string file)
    {
        var template = DocxBookmarksTests.Template(file);
        var (body, result) = Open(TocSample.Print(true, true, true, true, template));
        var source = WordprocessingDocument.Open(new MemoryStream(template), false).MainDocumentPart!.Document!.Body!;

        Assert.Null(result.Toc);
        // Поле TOC и строки файла — без изменений: номера страниц и «Обновить поле» — как раньше.
        Assert.Equal(Contents(source), Contents(body));
        Assert.Equal(20, Contents(body).Count); // «Содержание» и 19 строк

        static List<string> Contents(Body b) => b.Elements<Paragraph>().SkipWhile(p => p.InnerText != "Содержание").TakeWhile(p => p.InnerText.Length > 0)
            .Select(p => p.OuterXml).ToList();
    }

    [Fact]
    public void Layout_file_v5_has_levels_at_headings_only()
    {
        var body = WordprocessingDocument.Open(new MemoryStream(DocxBookmarksTests.Template(DocxBookmarksTests.Rpd)), false).MainDocumentPart!.Document!.Body!;

        var leveled = body.Elements<Paragraph>().Where(p => p.ParagraphProperties?.OutlineLevel is not null).ToList();
        Assert.Equal(20, leveled.Count);
        Assert.All(leveled, p => Assert.True(p.ParagraphProperties!.NumberingProperties is not null || char.IsDigit(p.InnerText[0]), p.InnerText));
        // «4.1Литература» исправлено: между номером и названием пробел.
        Assert.Equal(7, leveled.Count(p => Regex.IsMatch(p.InnerText, @"^4\.\d \p{L}")));
        // Закладки и поля прежнего оглавления убраны, страницы начинаются разрывом, а не пустыми абзацами.
        Assert.DoesNotContain(body.Descendants<BookmarkStart>(), b => b.Name!.Value!.StartsWith("_Toc"));
        Assert.Empty(body.Descendants<FieldChar>());
        var starts = body.Elements<Paragraph>().Where(p => p.ParagraphProperties?.PageBreakBefore is not null).ToList();
        Assert.Equal(["Содержание", "Цели и задачи освоения дисциплины", "ЛИСТ"], starts.Select(p => p.InnerText));
        Assert.All(starts, p => Assert.NotEqual("", p.PreviousSibling<Paragraph>()!.InnerText));
        Assert.DoesNotContain(body.Descendants<Break>(), b => b.Type?.Value == BreakValues.Page);
        Assert.Equal("AutofillToc", body.Elements<Paragraph>().Single(p => p.InnerText == "Содержание").NextSibling<Paragraph>()!.InnerText);
    }

    // --- заголовки: стили, автоматические номера, таблицы (файл собирается в тесте) ---

    [Fact]
    public void Headings_come_from_paragraph_and_style_levels_with_their_numbers()
    {
        var result = Generate(
            Heading("Глава первая", style: "Derived", list: 1),           // уровень и номер — от предка стиля и списка
            Heading("Параграф", level: 1, list: 1, listLevel: 1),
            Heading("Ещё параграф", level: 1, list: 1, listLevel: 1),
            Heading("Глава вторая", level: 0, list: 1),
            Heading("Параграф второй главы", level: 1, list: 1, listLevel: 1),
            Heading("7. Своим номером:", level: 0),
            Heading("3D-модели", level: 0),                               // цифра в названии — не номер раздела
            Heading("8.", level: 0),                                      // черновик: название ещё не введено
            Heading("Глубже оглавления", level: 2),
            Heading("Обычный текст"),
            Heading("", level: 0));

        Assert.Equal(
        [
            (1, "1.", "Глава первая"), (2, "1.1.", "Параграф"), (2, "1.2.", "Ещё параграф"), (1, "2.", "Глава вторая"),
            (2, "2.1.", "Параграф второй главы"), (1, "7.", "Своим номером"), (1, "", "3D-модели"), (1, "8.", "")
        ], result.Toc!.Lines.Select(l => (l.Level, l.Number, l.Title)));
        Assert.Empty(result.Report.Warnings);
    }

    [Fact]
    public void List_numbers_continue_through_tables_and_restart_where_the_list_says()
    {
        var table = new Table(new TableRow(new TableCell(Heading("Строка таблицы", level: 0, list: 1))));
        var result = Generate(Heading("Первый", level: 0, list: 1), table, Heading("Третий", level: 0, list: 1), Heading("Заново", level: 0, list: 2),
            Heading("Дальше", level: 0, list: 2));

        // Абзац таблицы — не заголовок раздела, но номер списка занимает; у второго списка своё начало.
        Assert.Equal([("1.", "Первый"), ("3.", "Третий"), ("5.", "Заново"), ("6.", "Дальше")], result.Toc!.Lines.Select(l => (l.Number, l.Title)));
    }

    [Fact]
    public void Heading_numbered_not_with_digits_goes_without_a_number_and_a_warning()
    {
        var result = Generate(Heading("Приложение", level: 0, list: 3));

        Assert.Equal(("", "Приложение"), (result.Toc!.Lines.Single().Number, result.Toc.Lines.Single().Title));
        Assert.Contains("Автоматический номер заголовка «Приложение» в оглавление не попал: считаются только цифры.", result.Report.Warnings);
    }

    [Fact]
    public void Document_without_headings_prints_empty_contents()
    {
        var result = Generate(Heading("Просто текст"));

        Assert.Empty(result.Toc!.Lines);
        using var document = WordprocessingDocument.Open(new MemoryStream(result.Content), false);
        Assert.Equal(["Содержание", "", "Просто текст"], document.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().Select(p => p.InnerText));
    }

    [Fact]
    public void Contents_outside_the_body_or_the_second_one_stay_with_a_warning()
    {
        var result = Generate(Heading("Раздел", level: 0), Sample("AutofillToc2", 2));

        Assert.Single(result.Toc!.Lines);
        Assert.Contains(result.Report.Warnings, w => w.StartsWith("Закладка AutofillToc2: оглавление печатается одно"));
    }

    // --- страницы и вариант для Word ---

    private static DocumentToc Toc(params string[] headings) =>
        new("1-2", headings.Select((h, i) => new TocLine($"_Toc{i}", 1, "", h, h)).ToList());

    [Fact]
    public void Pages_are_taken_from_pdf_bookmarks_in_document_order()
    {
        // Закладка заголовка с автоматическим номером начинается с номера; пробел в PDF бывает и неразрывным.
        Assert.Equal([3, 4, 7], DocxToc.Pages(Toc("Введение", "1.1 Цели:", "Итог"),
            [new("1. Введение", 3), new($"1.1{(char)0xA0}Цели:", 4), new("Лишняя закладка", 5), new("2. Итог", 7)]));
        // Одинаковые названия — по порядку, а не первой подходящей закладкой.
        Assert.Equal([3, 5, 9], DocxToc.Pages(Toc("Цели", "Итог", "Цели"), [new("1. Цели", 3), new("2. Итог", 5), new("3. Цели", 9)]));
        // Закладки нет — номер неизвестен только у этой строки.
        Assert.Equal([3, null, 7], DocxToc.Pages(Toc("Введение", "Цели", "Итог"), [new("1. Введение", 3), new("3. Итог", 7)]));
        Assert.Equal([null], DocxToc.Pages(Toc("Введение"), []));
    }

    [Fact]
    public void Word_gets_page_numbers_inside_a_contents_field()
    {
        var result = TocSample.Print(true, true, true, true);
        var pages = Enumerable.Range(0, result.Toc!.Lines.Count).Select(i => (int?)(i + 3)).ToList();
        var (body, _) = Open(result with { Content = DocxToc.ForWord(result.Content, result.Toc, pages) });
        var lines = TocSample.Lines(body);

        Assert.Equal(TocSample.Expected(true).Select((l, i) => $"{l}\t{i + 3}"), lines.Select(TocSample.Plain));
        // Поле TOC — от первой строки до пустого абзаца после последней; вложенные PAGEREF закрыты внутри своих строк.
        Assert.Equal(" TOC \\o \"1-2\" \\h \\z \\u ", lines[0].Descendants<FieldCode>().First().Text);
        var types = body.Descendants<FieldChar>().Select(f => f.FieldCharType!.Value).ToList();
        Assert.Equal(types.Count(t => t == FieldCharValues.Begin), types.Count(t => t == FieldCharValues.End));
        Assert.Equal(FieldCharValues.End, lines[^1].NextSibling<Paragraph>()!.Descendants<FieldChar>().Single().FieldCharType!.Value);
        Assert.Equal("", lines[^1].NextSibling<Paragraph>()!.InnerText);
        // Номера известны — Word не спрашивает про обновление полей.
        Assert.Null(Settings(body).GetFirstChild<UpdateFieldsOnOpen>());
    }

    [Fact]
    public void Word_is_asked_to_update_fields_when_some_pages_are_unknown()
    {
        var result = TocSample.Print(false, false, false, false);
        var pages = Enumerable.Range(0, result.Toc!.Lines.Count).Select(i => i == 4 ? null : (int?)3).ToList();
        var (body, _) = Open(result with { Content = DocxToc.ForWord(result.Content, result.Toc, pages) });

        Assert.Equal("3.\tСтруктура и содержание дисциплины\t", TocSample.Plain(TocSample.Lines(body)[4]));
        Assert.True(Settings(body).GetFirstChild<UpdateFieldsOnOpen>()!.Val!.Value);
    }

    [Theory]
    [InlineData("1-1", 1)]
    [InlineData("1-9", 9)]
    [InlineData("2-3", null)]
    [InlineData("1-0", null)]
    [InlineData("1-10", null)]
    [InlineData("", null)]
    public void Levels_are_counted_from_the_first(string levels, int? depth) => Assert.Equal(depth, TocLevels.Depth(levels));

    private static Settings Settings(Body body) => ((Document)body.Parent!).MainDocumentPart!.DocumentSettingsPart!.Settings!;

    private static Paragraph Target(Body body, Paragraph line)
    {
        var anchor = line.Elements<Hyperlink>().Single().Anchor!.Value;
        return (Paragraph)body.Descendants<BookmarkStart>().Single(b => b.Name == anchor).Parent!;
    }

    // Раздел 3.3 v5 — в блоке, который печатается только с лабораторными: от заголовка до абзаца после таблицы.
    private static byte[] WithLabBlock()
    {
        using var stream = new MemoryStream();
        stream.Write(DocxBookmarksTests.Template(DocxBookmarksTests.Rpd));
        using (var document = WordprocessingDocument.Open(stream, true))
        {
            var body = document.MainDocumentPart!.Document!.Body!;
            var heading = body.Elements<Paragraph>().Single(p => p.InnerText == "3.3 Лабораторные работы");
            heading.ParagraphProperties!.InsertAfterSelf(new BookmarkStart { Id = "9001", Name = "AutofillLabSection1" });
            heading.NextSibling<Table>()!.NextSibling<Paragraph>()!.AppendChild(new BookmarkEnd { Id = "9001" });
        }
        return stream.ToArray();
    }

    private static Paragraph Heading(string text, int? level = null, int? list = null, int listLevel = 0, string? style = null)
    {
        var properties = new ParagraphProperties();
        if (style is not null) properties.ParagraphStyleId = new ParagraphStyleId { Val = style };
        if (list is { } id)
            properties.NumberingProperties = new NumberingProperties(new NumberingLevelReference { Val = listLevel }, new NumberingId { Val = id });
        if (level is { } l) properties.OutlineLevel = new OutlineLevel { Val = l };
        var paragraph = new Paragraph(properties);
        if (text.Length > 0) paragraph.AppendChild(new Run(new Text(text)));
        return paragraph;
    }

    private static Paragraph Sample(string name, int id) => new(
        new ParagraphProperties(new Tabs(new TabStop { Val = TabStopValues.Right, Leader = TabStopLeaderCharValues.Dot, Position = 9000 })),
        new BookmarkStart { Id = id.ToString(), Name = name }, new Run(new Text("AutofillToc")), new BookmarkEnd { Id = id.ToString() });

    // Файл оформления из «Содержания», одного образца строки и переданного тела: список 1 — «1.» и «1.1», список 2 — той же формы со
    // своим началом, список 3 — буквами; стиль «Derived» основан на «Base» с уровнем структуры 1.
    private static DocxResult Generate(params OpenXmlElement[] content)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.AddNewPart<StyleDefinitionsPart>().Styles = new Styles(
                new Style(new StyleName { Val = "Base" }, new StyleParagraphProperties(new OutlineLevel { Val = 0 })) { Type = StyleValues.Paragraph, StyleId = "Base" },
                new Style(new StyleName { Val = "Derived" }, new BasedOn { Val = "Base" }) { Type = StyleValues.Paragraph, StyleId = "Derived" });
            main.AddNewPart<NumberingDefinitionsPart>().Numbering = new Numbering(
                new AbstractNum(Level(0, NumberFormatValues.Decimal, "%1."), Level(1, NumberFormatValues.Decimal, "%1.%2")) { AbstractNumberId = 1 },
                new AbstractNum(Level(0, NumberFormatValues.LowerLetter, "%1)")) { AbstractNumberId = 2 },
                new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 1 },
                new NumberingInstance(new AbstractNumId { Val = 1 },
                    new LevelOverride(new StartOverrideNumberingValue { Val = 5 }) { LevelIndex = 0 }) { NumberID = 2 },
                new NumberingInstance(new AbstractNumId { Val = 2 }) { NumberID = 3 });
            var body = new Body(new Paragraph(new Run(new Text("Содержание"))), Sample("AutofillToc1", 1));
            foreach (var element in content) body.AppendChild(element);
            main.Document = new Document(body);
        }
        var mapping = new Dictionary<string, BookmarkTarget> { ["AutofillToc"] = new(BookmarkTargetKinds.Toc, "1-2") };
        var context = new PrintContext(JsonDocument.Parse("{}").RootElement, JsonDocument.Parse("{}").RootElement, _ => null,
            new DisciplineFacts(new Entities.Discipline { Name = "Дисциплина" }, new Entities.CurriculumPlan(),
                new Entities.StudyDirection(), new Entities.StudyProfile(), null, []),
            new SignatoryReport([], []), 2026, []);
        return DocxGenerator.Generate(stream.ToArray(), mapping, context);

        static Level Level(int index, NumberFormatValues format, string text) =>
            new(new StartNumberingValue { Val = 1 }, new NumberingFormat { Val = format }, new LevelText { Val = text }) { LevelIndex = index };
    }
}
