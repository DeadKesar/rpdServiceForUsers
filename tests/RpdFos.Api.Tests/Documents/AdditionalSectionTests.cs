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
/// Необязательный раздел после материально-технического обеспечения (этап 7.1, шаг 4): заголовок и форматированный текст
/// автора. Проверка — текст без заголовка ошибка поля «Заголовок», пустой раздел не мешает; печать — раздел 6 после МТО
/// с заголовком автора, без текста раздела нет совсем. Без базы: настоящая схема, сопоставление и файл оформления v4.
/// </summary>
public class AdditionalSectionTests
{
    private static readonly DocumentTypeRegistry Registry = new();
    private static readonly DocumentValidator Validator = new(Registry);
    private static readonly string Rpd = Registry.Get(DocumentTypeRegistry.Rpd).SchemaJson;

    private static readonly ValidationRuleSet Rules = new([new SectionTitleForText()], TimeProvider.System);
    private static readonly DisciplineFacts PlanFacts = new(
        new Discipline { Name = "Дисциплина", Semesters = [new DisciplineSemesterDetails { Semester = 1, Lectures = 16, ControlTypes = ["exam"] }] },
        new CurriculumPlan(), new StudyDirection(), new StudyProfile(), null, []);

    // Полная проверка отправки: схема и правило раздела. Черновик — только схема и правила, которые проверяются при сохранении.
    private static IReadOnlyList<DocumentValidationError> Check(string section, ValidationMode mode = ValidationMode.Submit)
    {
        var payload = JsonDocument.Parse($$"""{ "additional_section": {{section}} }""").RootElement;
        var errors = mode == ValidationMode.Submit
            ? DocumentSubmission.Check(Rpd, payload, PlanFacts, Validator, Rules, Registry.FindSchema)
            : Validator.Validate(Rpd, payload, mode).Concat(Rules.Run(JsonDocument.Parse(Rpd).RootElement, payload, PlanFacts, Registry.FindSchema, new HashSet<string>(), onSave: true)).ToList();
        return errors.Where(e => e.Path.StartsWith("/additional_section")).ToList();
    }

    [Fact]
    public void Section_is_optional_and_not_required_by_the_schema()
    {
        var payload = JsonDocument.Parse("{}").RootElement;

        Assert.DoesNotContain(Validator.Validate(Rpd, payload, ValidationMode.Submit), e => e.Path.StartsWith("/additional_section"));
        Assert.Empty(Check("{}"));
    }

    [Fact]
    public void Section_follows_material_support_in_the_schema_and_is_not_required()
    {
        var schema = Registry.Get(DocumentTypeRegistry.Rpd).Schema;
        var names = schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();

        // Порядок свойств — порядок пунктов навигации редактора: пункт идёт сразу за МТО.
        Assert.Equal(names.IndexOf("material_support") + 1, names.IndexOf("additional_section"));
        Assert.DoesNotContain("additional_section", schema.GetProperty("required").EnumerateArray().Select(n => n.GetString()));
    }

    [Fact]
    public void Empty_section_is_not_an_error()
    {
        // Пустое поле, пустой абзац редактора и один заголовок — раздел не печатается, отправку не блокирует.
        Assert.Empty(Check("""{ "title": "", "text": "" }"""));
        Assert.Empty(Check("""{ "title": "", "text": "<p></p><p><br></p>" }"""));
        Assert.Empty(Check("""{ "title": "Особенности освоения" }"""));
        Assert.Empty(Check("""{ "title": "Особенности освоения", "text": "<p> </p>" }"""));
    }

    [Fact]
    public void Section_with_title_and_text_passes()
    {
        Assert.Empty(Check("""{ "title": "Особенности освоения дисциплины", "text": "<p>Занятия проводятся в смешанном формате.</p>" }"""));
    }

    [Theory]
    [InlineData("""{ "text": "<p>Текст без названия</p>" }""")]
    [InlineData("""{ "title": "", "text": "<p>Текст без названия</p>" }""")]
    [InlineData("""{ "title": "   ", "text": "<ul><li>Пункт</li></ul>" }""")]
    public void Text_without_title_is_an_error_of_the_title_field(string section)
    {
        var error = Assert.Single(Check(section));

        Assert.Equal("/additional_section/title", error.Path);
        Assert.Equal(IssueLevels.Error, error.Level);
    }

    [Fact]
    public void Table_alone_counts_as_text()
    {
        // Раздел из одной таблицы — тоже содержимое: заголовок обязателен.
        var error = Assert.Single(Check("""{ "text": "<table><tbody><tr><td>Ячейка</td></tr></tbody></table>" }"""));

        Assert.Equal("/additional_section/title", error.Path);
    }

    [Fact]
    public void Picture_alone_counts_as_text()
    {
        var error = Assert.Single(Check("""{ "text": "<p><img src=\"/api/images/66666666-7777-4888-9999-000000000000\"></p>" }"""));

        Assert.Equal("/additional_section/title", error.Path);
    }

    [Fact]
    public void Blank_paragraphs_with_non_breaking_spaces_are_not_text()
    {
        // Редактор оставляет «&nbsp;» в пустом абзаце: печатать из него нечего.
        Assert.Empty(Check("""{ "text": "<p>&nbsp;</p><p>&nbsp; </p>" }"""));
    }

    [Fact]
    public void Missing_title_does_not_block_a_draft()
    {
        // Заголовок можно вписать после текста: сохранение черновика не отвергается.
        Assert.Empty(Check("""{ "text": "<p>Текст без названия</p>" }""", ValidationMode.Draft));
    }

    [Fact]
    public void Title_has_a_length_limit()
    {
        Assert.Single(Check($$"""{ "title": "{{new string('а', 201)}}" }""", ValidationMode.Draft));
    }

    // --- печать ---

    private static readonly DisciplineFacts Facts = new(
        new Discipline { Name = "Дисциплина", Semesters = [new DisciplineSemesterDetails { Semester = 1, Lectures = 16, ControlTypes = ["exam"] }] },
        new CurriculumPlan(), new StudyDirection(), new StudyProfile(), null, []);

    private static (Body Body, PrintReport Report) Print(string payloadJson, string template = DocxBookmarksTests.Rpd)
    {
        var rpd = Registry.Get(DocumentTypeRegistry.Rpd);
        var payload = JsonDocument.Parse(payloadJson).RootElement;
        var context = new PrintContext(payload, rpd.Schema, Registry.FindSchema, Facts, new SignatoryReport([], []), 2026,
            DocumentMetadata.Hidden(rpd.Schema, DocumentSemesters.Of(payload, Facts), Registry.FindSchema));
        var result = DocxGenerator.Generate(DocxBookmarksTests.Template(template), rpd.PrintMapping, context);

        var document = WordprocessingDocument.Open(new MemoryStream(result.Content), false);
        Assert.Empty(new OpenXmlValidator(FileFormatVersions.Office2019).Validate(document).Select(e => $"{e.Path?.XPath}: {e.Description}"));
        return (document.MainDocumentPart!.Document!.Body!, result.Report);
    }

    private static List<string> Texts(Body body) => body.Elements<Paragraph>().Select(p => p.InnerText).ToList();

    private static List<string> NonEmpty(Body body) => Texts(body).Where(t => t.Trim().Length > 0).ToList();

    private const string Support = "\"material_support\": [\"Лаборатория моделирования\"]";

    [Fact]
    public void Section_is_printed_after_material_support_with_the_authors_title()
    {
        var (body, report) = Print($$"""
            { {{Support}},
              "additional_section": { "title": "Особенности освоения дисциплины", "text": "<p>Первый абзац.</p><p>Второй абзац.</p>" } }
            """);

        var texts = NonEmpty(body);
        var heading5 = texts.FindLastIndex(t => t.StartsWith("5.", StringComparison.Ordinal) && t.Contains("Материально-техническое"));
        // Лаборатория — пункт МТО; следом — заголовок с номером 6 и абзацы текста; дальше — лист согласования.
        Assert.Equal(["Лаборатория моделирования", "6.Особенности освоения дисциплины", "Первый абзац.", "Второй абзац."], texts.Skip(heading5 + 2).Take(4));
        Assert.DoesNotContain(report.Unfilled, u => u.StartsWith("AutofillAdditional"));
        Assert.DoesNotContain(report.Warnings, w => w.Contains("AutofillAdditional"));
        // Заголовок оформлен как заголовок раздела 5: тот же стиль, уровень структуры и выравнивание.
        var h5 = body.Elements<Paragraph>().Last(p => p.InnerText.StartsWith("5.", StringComparison.Ordinal));
        var h6 = body.Elements<Paragraph>().Single(p => p.InnerText.StartsWith("6.", StringComparison.Ordinal));
        Assert.Equal(h5.ParagraphProperties!.ParagraphStyleId!.Val, h6.ParagraphProperties!.ParagraphStyleId!.Val);
        Assert.Equal(h5.ParagraphProperties.OutlineLevel!.Val, h6.ParagraphProperties.OutlineLevel!.Val);
        Assert.Equal(h5.ParagraphProperties.Justification!.Val, h6.ParagraphProperties.Justification!.Val);
        Assert.All(h6.Descendants<Run>(), r => Assert.NotNull(r.RunProperties?.Bold));
    }

    [Fact]
    public void Section_text_keeps_lists_and_tables()
    {
        var (body, _) = Print("""
            { "additional_section": { "title": "Приложение к программе", "text":
              "<ul><li>Первый пункт</li><li>Второй пункт</li></ul><table><tbody><tr><td>Показатель</td><td>Значение</td></tr><tr><td>Часы</td><td>72</td></tr></tbody></table>" } }
            """);

        var table = body.Descendants<Table>().Single(t => t.InnerText.Contains("Показатель"));
        Assert.Equal([["Показатель", "Значение"], ["Часы", "72"]],
            table.Elements<TableRow>().Select(r => r.Elements<TableCell>().Select(c => c.InnerText).ToList()));
        var texts = Texts(body);
        Assert.True(texts.IndexOf("Первый пункт") > texts.FindIndex(t => t.StartsWith("6.", StringComparison.Ordinal)));
        Assert.Equal(2, body.Elements<Paragraph>().Count(p => p.ParagraphProperties?.NumberingProperties is not null && p.InnerText.EndsWith("пункт")));
        // Таблица стоит после заголовка раздела, а не где-то ещё.
        Assert.True(body.Elements().ToList().IndexOf(table) > body.Elements().ToList().IndexOf(body.Elements<Paragraph>().Single(p => p.InnerText.StartsWith("6."))));
    }

    [Theory]
    [InlineData("""{ }""")]
    [InlineData("""{ "additional_section": { "title": "", "text": "" } }""")]
    [InlineData("""{ "additional_section": { "title": "Только заголовок" } }""")]
    [InlineData("""{ "additional_section": { "title": "Заголовок без текста", "text": "<p><br></p>" } }""")]
    public void Section_without_text_is_not_printed_at_all(string payload)
    {
        var (body, report) = Print(payload);
        var (former, _) = Print(payload, DocxBookmarksTests.RpdV3);

        // Ни заголовка, ни заглушки «не предусмотрено», ни лишних пустых абзацев: печать совпадает с печатью по прежнему файлу.
        Assert.DoesNotContain(NonEmpty(body), t => t.StartsWith("6.", StringComparison.Ordinal) || t.Contains("Только заголовок") || t.Contains("Заголовок без текста"));
        Assert.Equal(Texts(former), Texts(body));
        Assert.DoesNotContain(report.Unfilled, u => u.StartsWith("AutofillAdditional"));
    }

    [Fact]
    public void Document_by_a_former_file_without_the_block_prints_without_the_section()
    {
        // Файл оформления v3 блока не содержит: данные раздела не мешают печати, предупреждений о нём нет.
        var (body, report) = Print("""
            { "additional_section": { "title": "Особенности освоения", "text": "<p>Текст.</p>" } }
            """, DocxBookmarksTests.RpdV3);

        Assert.DoesNotContain(NonEmpty(body), t => t.Contains("Особенности освоения"));
        Assert.DoesNotContain(report.Warnings, w => w.Contains("AutofillAdditional"));
    }

    [Fact]
    public void Block_bookmarks_of_the_section_are_mapped()
    {
        var mapping = Registry.Get(DocumentTypeRegistry.Rpd).PrintMapping;

        Assert.Equal((BookmarkTargetKinds.Block, "/additional_section", BlockModes.Present),
            (mapping["AutofillAdditionalSection"].Kind, mapping["AutofillAdditionalSection"].Target, mapping["AutofillAdditionalSection"].Part));
        Assert.Equal("/additional_section/title", mapping["AutofillAdditionalTitle"].Target);
        Assert.Equal("/additional_section/text", mapping["AutofillAdditionalText"].Target);
    }
}
