using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using RpdFos.Core.DocumentTypes;
using Xunit;

namespace RpdFos.Api.Tests.DocumentTypes;

/// <summary>
/// Вторая версия файла оформления ФОС (этап 7, B2; скрипт tests/fixtures/make-fos-v2.cs): образцы Положения убраны,
/// содержимое разделов, банк заданий и билеты — закладки. Проверяется сам файл; сопоставление закладок и проверка
/// перед публикацией — в PrintMappingTests и TemplateCheckTests.
/// </summary>
public class FosTemplateV2Tests
{
    private static WordprocessingDocument Open(string file = DocxBookmarksTests.Fos) =>
        WordprocessingDocument.Open(new MemoryStream(DocxBookmarksTests.Template(file)), false);

    private static List<OpenXmlElement> Blocks(WordprocessingDocument document) =>
        document.MainDocumentPart!.Document!.Body!.ChildElements.ToList();

    // Номер блока верхнего уровня, в котором стоит элемент.
    private static int BlockOf(List<OpenXmlElement> blocks, OpenXmlElement element) =>
        blocks.FindIndex(b => b == element || b.Descendants().Contains(element));

    [Fact]
    public void Samples_and_instructions_of_the_regulation_are_removed()
    {
        using var document = Open();
        var body = document.MainDocumentPart!.Document!.Body!;
        var text = body.InnerText;

        // Образцы приложений 3–7, подписи к ним и сноски-указания — в настоящих ФОС их нет.
        string[] gone =
        [
            "Приложение №", "к Положению", "Образец", "Пример оформления", "Форма экзаменационного билета",
            "ПРИМЕРЫ ОФОРМЛЕНИЯ", "Наименования разделов (тем)", "Структурные компоненты", "Структура билета определяется",
            "Португалия", "Куликовская битва", "1…"
        ];
        Assert.All(gone, phrase => Assert.DoesNotContain(phrase, text));
        // Звёздочки сносок в заголовках таблиц паспорта.
        Assert.DoesNotContain('*', string.Concat(body.Elements<Table>().Select(t => t.InnerText)));
        // Образцы были таблицами 9×6, 5×4 и двумя таблицами тестов; остались две таблицы паспорта.
        Assert.Equal(2, body.Descendants<Table>().Count());
    }

    [Fact]
    public void Original_template_keeps_samples()
    {
        // Исходный файл не меняется: v2 получен скриптом, а не правкой оригинала.
        using var document = Open(DocxBookmarksTests.FosOriginal);

        Assert.Contains("Образец оформления", document.MainDocumentPart!.Document!.Body!.InnerText);
        Assert.Equal(6, document.MainDocumentPart.Document.Body.Descendants<Table>().Count());
    }

    [Fact]
    public void Headings_of_new_sections_follow_the_regulation()
    {
        using var document = Open();
        var paragraphs = document.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().Select(p => p.InnerText.Trim()).ToList();

        // Названия разделов — по ответам автора (П8): «Методические материалы…» и «Критерии формирования оценок».
        Assert.Contains("Текущий контроль", paragraphs);
        Assert.Contains("Методические материалы, определяющие процедуры оценивания", paragraphs);
        Assert.Contains("Критерии формирования оценок", paragraphs);
        Assert.DoesNotContain(paragraphs, p => p.Contains("не предусмотрено", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Highlight_of_template_hints_does_not_reach_print()
    {
        using var document = Open();

        Assert.Empty(document.MainDocumentPart!.Document!.Body!.Descendants<Highlight>());
    }

    [Fact]
    public void File_passes_open_xml_validation()
    {
        using var document = Open();

        var errors = new OpenXmlValidator(FileFormatVersions.Office2019).Validate(document)
            .Select(e => $"{e.Path?.XPath}: {e.Description}").ToList();

        Assert.Empty(errors);
    }

    [Fact]
    public void Bookmarks_are_paired_and_ids_are_unique()
    {
        using var document = Open();
        var body = document.MainDocumentPart!.Document!.Body!;

        var starts = body.Descendants<BookmarkStart>().Select(s => s.Id!.Value).ToList();
        var ends = body.Descendants<BookmarkEnd>().Select(e => e.Id!.Value).ToList();

        // Висячая метка конца или начала — файл, который Word чинит при открытии.
        Assert.Equal(starts.Count, starts.Distinct().Count());
        Assert.Equal(starts.Order(), ends.Order());
        // Закладка Word _GoBack — след редактирования, в шаблон не входит.
        Assert.DoesNotContain(body.Descendants<BookmarkStart>(), s => s.Name == "_GoBack");
    }

    [Fact]
    public void Content_follows_in_the_order_of_the_printed_document()
    {
        using var document = Open();
        var names = Blocks(document).SelectMany(b => b.Descendants<BookmarkStart>()).Select(s => DocxBookmarks.BaseName(s.Name!.Value!)).ToList();

        // Паспорт (две таблицы), текущий контроль, методические материалы, банк заданий, критерии, билеты.
        string[] order =
        [
            "AutofillPassportSections", "AutofillCompetenciesTable", "AutofillCurrentControl", "AutofillMeans",
            "AutofillBankGroup", "AutofillBankTitle", "AutofillBankTasks", "AutofillBankCompilers", "AutofillScale",
            "AutofillExaming", "AutofillTicket"
        ];
        Assert.Equal(order, names.Where(order.Contains).ToList());
    }

    [Fact]
    public void Bank_part_block_runs_from_emblem_to_section_break()
    {
        using var document = Open();
        var blocks = Blocks(document);
        var group = blocks.SelectMany(b => b.Descendants<BookmarkStart>()).Single(s => s.Name == "AutofillBankGroup1");
        var end = blocks.SelectMany(b => b.Descendants<BookmarkEnd>()).Single(e => e.Id!.Value == group.Id!.Value);
        var from = BlockOf(blocks, group);
        var to = BlockOf(blocks, end);

        // Блок части копируется целиком: шапка вуза (эмблема) в начале, разрыв раздела в конце — каждая часть с новой страницы.
        Assert.NotEmpty(blocks[from].Descendants<Drawing>());
        Assert.NotNull(((Paragraph)blocks[to]).ParagraphProperties?.SectionProperties);
        foreach (var inner in new[] { "AutofillBankTitle1", "AutofillBankTasks1", "AutofillBankCompilers1", "AutofillDepartment4", "AutofillDiscipline5" })
        {
            var index = BlockOf(blocks, blocks.SelectMany(b => b.Descendants<BookmarkStart>()).Single(s => s.Name == inner));
            Assert.InRange(index, from, to);
        }
        // Один блок на все виды заданий: заголовки трёх приложений Положения в нём не сохранились.
        Assert.DoesNotContain(blocks.Skip(from).Take(to - from + 1), b => b.InnerText.Contains("Комплект"));
    }

    [Fact]
    public void Tickets_shell_holds_ticket_block_and_starts_with_section_break()
    {
        using var document = Open();
        var blocks = Blocks(document);
        BookmarkStart Start(string name) => blocks.SelectMany(b => b.Descendants<BookmarkStart>()).Single(s => s.Name == name);
        BookmarkEnd End(BookmarkStart s) => blocks.SelectMany(b => b.Descendants<BookmarkEnd>()).Single(e => e.Id!.Value == s.Id!.Value);
        var shell = Start("AutofillExaming1");
        var ticket = Start("AutofillTicket1");

        // Оболочка — всё приложение 6: без билетов её убирают вместе с разрывом раздела, и документ не кончается пустой страницей.
        var shellParagraph = (Paragraph)blocks[BlockOf(blocks, shell)];
        Assert.NotNull(shellParagraph.ParagraphProperties?.SectionProperties);
        Assert.True(BlockOf(blocks, shell) < BlockOf(blocks, ticket));
        Assert.True(BlockOf(blocks, End(ticket)) <= BlockOf(blocks, End(shell)));
        // Билет — страница, как в форме Положения.
        var ticketParagraph = (Paragraph)blocks[BlockOf(blocks, ticket)];
        Assert.NotNull(ticketParagraph.ParagraphProperties?.PageBreakBefore);
        // После билета в документе ничего нет: ни образцов, ни приложения 7.
        Assert.IsType<SectionProperties>(blocks[^1]);
        Assert.Equal(BlockOf(blocks, End(shell)), blocks.Count - 2);
        // Шапка билета: учебный год, номер, вопросы, подпись заведующего.
        foreach (var inner in new[] { "AutofillAcademicYear1", "AutofillTicketNumber1", "AutofillTicketQuestions1", "AutofillPositionKafName2" })
            Assert.InRange(BlockOf(blocks, Start(inner)), BlockOf(blocks, ticket), BlockOf(blocks, End(ticket)));
    }

    [Theory]
    [InlineData("AutofillCurrentControl1")]
    [InlineData("AutofillMeans1")]
    [InlineData("AutofillBankTitle1")]
    [InlineData("AutofillBankTasks1")]
    [InlineData("AutofillScale1")]
    [InlineData("AutofillAcademicYear1")]
    [InlineData("AutofillTicketNumber1")]
    [InlineData("AutofillTicketQuestions1")]
    [InlineData("AutofillPositionKafName2")]
    public void Content_bookmark_wraps_placeholder_named_after_it(string name)
    {
        // Заглушка — имя закладки без номера: так заполняет генератор (DocxGenerator.Place).
        using var document = Open();
        var start = document.MainDocumentPart!.Document!.Body!.Descendants<BookmarkStart>().Single(s => s.Name == name);
        var inside = start.ElementsAfter().TakeWhile(e => e is not BookmarkEnd end || end.Id!.Value != start.Id!.Value)
            .SelectMany(e => e.Descendants<Text>()).Select(t => t.Text);

        Assert.Equal(DocxBookmarks.BaseName(name), string.Concat(inside));
    }

    [Fact]
    public void Section_breaks_separate_title_passport_means_bank_and_tickets()
    {
        using var document = Open();
        var blocks = Blocks(document);

        // Титул, лист согласования, паспорт, текущий контроль и материалы, часть банка, критерии; билеты — последний раздел.
        var breaks = blocks.OfType<Paragraph>().Count(p => p.ParagraphProperties?.SectionProperties is not null);
        Assert.Equal(6, breaks);
        // Колонтитулы первого раздела наследуются остальными: у разрывов после него своих нет.
        var withHeaders = blocks.OfType<Paragraph>().Select(p => p.ParagraphProperties?.SectionProperties)
            .Where(s => s is not null && s.Elements<HeaderReference>().Any()).ToList();
        Assert.Single(withHeaders);
    }
}
