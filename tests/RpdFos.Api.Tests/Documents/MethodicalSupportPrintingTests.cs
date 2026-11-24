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
/// Печать учебно-методического обеспечения (этап 7.1) по шаблону оформления РПД v3: ссылка у литературы гиперссылкой после
/// записи, порядок записей, методические указания текстом. Без базы: настоящая схема, сопоставление и файл шаблона.
/// </summary>
public class MethodicalSupportPrintingTests
{
    private static readonly DocumentTypeRegistry Registry = new();

    // В плане есть лабораторные и практические занятия: места указаний к ним печатаются, а не заменяются заглушкой.
    private static readonly DisciplineFacts Facts = new(
        new Discipline
        {
            Name = "Дисциплина",
            Semesters = [new DisciplineSemesterDetails { Semester = 1, Lectures = 16, Practice = 16, LabHours = 16, ControlTypes = ["exam"] }]
        },
        new CurriculumPlan(), new StudyDirection(), new StudyProfile(), null, []);

    private const string First = "Иванов И. И. Моделирование систем : учебник. — Москва : Станкин, 2024. — 320 с.";
    private const string Second = "Петров П. П. Основы теории систем : учебное пособие. — Москва : Наука, 2020. — 210 с.";
    private const string Link = "https://e.lanbook.com/book/123456";
    private const string LabSentence = "Методические указания к лабораторным занятиям по данной дисциплине";

    private static (Body Body, MainDocumentPart Main) Print(string payloadJson, string template = DocxBookmarksTests.Rpd)
    {
        var rpd = Registry.Get(DocumentTypeRegistry.Rpd);
        var payload = JsonDocument.Parse(payloadJson).RootElement;
        var context = new PrintContext(payload, rpd.Schema, Registry.FindSchema, Facts, new SignatoryReport([], []), 2026,
            DocumentMetadata.Hidden(rpd.Schema, DocumentSemesters.Of(payload, Facts), Registry.FindSchema));
        var result = DocxGenerator.Generate(DocxBookmarksTests.Template(template), rpd.PrintMapping, context);

        var document = WordprocessingDocument.Open(new MemoryStream(result.Content), false);
        Assert.Empty(new OpenXmlValidator(FileFormatVersions.Office2019).Validate(document).Select(e => $"{e.Path?.XPath}: {e.Description}"));
        return (document.MainDocumentPart!.Document!.Body!, document.MainDocumentPart);
    }

    private static List<Paragraph> Paragraphs(Body body) => body.Elements<Paragraph>().ToList();

    private static int IndexOf(Body body, string start) => Paragraphs(body).FindIndex(p => p.InnerText.StartsWith(start, StringComparison.Ordinal));

    private static Uri Target(MainDocumentPart main, Hyperlink link) =>
        main.HyperlinkRelationships.Single(r => r.Id == link.Id!.Value).Uri;

    [Fact]
    public void Literature_link_is_printed_after_the_record_as_a_hyperlink()
    {
        var (body, main) = Print($$"""
            { "literature": { "main": [ { "citation": "{{First}}", "url": "{{Link}}" }, { "citation": "{{Second}}" } ] } }
            """);

        var record = Paragraphs(body)[IndexOf(body, "Иванов И. И.")];
        Assert.Equal($"{First} — URL: {Link}", record.InnerText);
        var link = Assert.Single(record.Descendants<Hyperlink>());
        Assert.Equal(Link, link.InnerText);
        Assert.Equal(Link, Target(main, link).AbsoluteUri);
        // Гиперссылка выглядит ссылкой и без стиля «Hyperlink» в шаблоне; запись без ссылки печатается как раньше.
        var props = link.Descendants<RunProperties>().Single();
        Assert.Equal(("0563C1", UnderlineValues.Single), (props.Color!.Val!.Value, props.Underline!.Val!.Value));
        var plain = Paragraphs(body)[IndexOf(body, "Петров П. П.")];
        Assert.Equal(Second, plain.InnerText);
        Assert.Empty(plain.Descendants<Hyperlink>());
        // Обе записи — один нумерованный список.
        Assert.Equal(record.ParagraphProperties!.NumberingProperties!.NumberingId!.Val!.Value,
            plain.ParagraphProperties!.NumberingProperties!.NumberingId!.Val!.Value);
    }

    [Theory]
    [InlineData(Link)]
    [InlineData(Link + "/")]
    public void Address_written_in_the_record_is_not_printed_twice(string address)
    {
        var citation = $"Сидоров С. С. Теория систем. — Москва : Наука, 2020. — Режим доступа: {Link} (дата обращения: 01.09.2026).";
        var (body, main) = Print($$"""{ "literature": { "main": [ { "citation": "{{citation}}", "url": "{{address}}" } ] } }""");

        var record = Paragraphs(body)[IndexOf(body, "Сидоров")];
        Assert.Equal(citation, record.InnerText);
        // Адрес в тексте сам становится ссылкой — остальной текст записи остаётся обычным.
        var link = Assert.Single(record.Descendants<Hyperlink>());
        Assert.Equal(Link, link.InnerText);
        Assert.Equal(address, Target(main, link).AbsoluteUri);
    }

    [Fact]
    public void Longer_address_in_the_record_is_not_the_same_address()
    {
        // В тексте — страница книги, в поле — сайт: это разные адреса, поле печатается после записи.
        var citation = $"Сидоров С. С. Теория систем. — Режим доступа: {Link}/page/2.";
        var (body, _) = Print($$"""{ "literature": { "main": [ { "citation": "{{citation}}", "url": "{{Link}}" } ] } }""");

        Assert.Equal($"{citation} — URL: {Link}", Paragraphs(body)[IndexOf(body, "Сидоров")].InnerText);
    }

    [Fact]
    public void Records_are_printed_in_document_order()
    {
        // Порядок записей всех списков раздела — порядок в документе; нумерацию ведёт Word.
        var (body, _) = Print($$"""
            { "literature": { "main": [ { "citation": "{{Second}}" }, { "citation": "{{First}}" } ],
                              "additional": [ { "citation": "Я. Вторая дополнительная запись по порядку" }, { "citation": "А. Первая дополнительная запись по порядку" } ],
                              "software": [ "Яндекс Браузер", "Астра Линукс" ] },
              "material_support": [ "Ярославская лаборатория", "Аудитория с доской" ] }
            """);

        static void AssertBefore(Body body, string earlier, string later) =>
            Assert.True(IndexOf(body, earlier) is var a and >= 0 && IndexOf(body, later) is var b and >= 0 && a < b, $"{earlier} → {later}");
        AssertBefore(body, "Петров П. П.", "Иванов И. И.");
        AssertBefore(body, "Я. Вторая", "А. Первая");
        AssertBefore(body, "Яндекс Браузер", "Астра Линукс");
        AssertBefore(body, "Ярославская лаборатория", "Аудитория с доской");
    }

    [Fact]
    public void Guideline_with_link_only_is_printed_as_before_with_the_template_phrase()
    {
        var (body, main) = Print("""{ "literature": { "guidelines": { "lab_works": { "url": "https://edu.example.org/labs" } } } }""");

        var phrase = IndexOf(body, LabSentence);
        Assert.True(phrase >= 0);
        var address = Paragraphs(body)[phrase + 1];
        Assert.Equal("https://edu.example.org/labs", address.InnerText);
        Assert.Equal("https://edu.example.org/labs", Target(main, Assert.Single(address.Descendants<Hyperlink>())).AbsoluteUri);
    }

    [Fact]
    public void Guideline_text_is_printed_by_paragraphs_with_the_link_after_it_and_without_the_phrase()
    {
        var (body, main) = Print("""
            { "literature": { "guidelines": {
                "lab_works": { "text": "Лабораторный практикум, ч. 1 : методические указания. — Москва : Станкин, 2022.\r\n\nЛабораторный практикум, ч. 2",
                               "url": "https://edu.example.org/labs" },
                "practical_classes": { "url": "https://edu.example.org/practice" } } } }
            """);

        // Фраза шаблона к названиям указаний не подходит: убрана, заголовок раздела остаётся перед текстом.
        Assert.True(IndexOf(body, LabSentence) < 0);
        var first = IndexOf(body, "Лабораторный практикум, ч. 1");
        var paragraphs = Paragraphs(body);
        Assert.Contains("Методические указания к лабораторным занятиям", paragraphs[first - 1].InnerText);
        Assert.Equal("Лабораторный практикум, ч. 1 : методические указания. — Москва : Станкин, 2022.", paragraphs[first].InnerText);
        // Пустая строка ввода абзаца не даёт; ссылка — в конце последней строки, как у литературы.
        var last = paragraphs[first + 1];
        Assert.Equal("Лабораторный практикум, ч. 2 — URL: https://edu.example.org/labs", last.InnerText);
        Assert.Equal("https://edu.example.org/labs", Target(main, Assert.Single(last.Descendants<Hyperlink>())).AbsoluteUri);
        // Соседнее место, где указана одна ссылка, печатается как раньше — с фразой.
        Assert.True(IndexOf(body, "Методические указания к практическим занятиям по данной дисциплине") >= 0);
    }

    [Fact]
    public void Dropping_the_phrase_keeps_bookmarks_and_table_of_contents_anchors_whole()
    {
        // В файле v4 в абзаце фразы шаблона стоят и метки статического оглавления (_Toc…): они переезжают, оглавление не теряет якорей.
        var before = Marks(Print("""{ "literature": { "guidelines": { "lab_works": { "url": "https://edu.example.org/labs" } } } }""", DocxBookmarksTests.RpdV4).Body);
        var after = Marks(Print("""{ "literature": { "guidelines": { "lab_works": { "text": "Практикум" } } } }""", DocxBookmarksTests.RpdV4).Body);

        Assert.Contains("_Toc453665076", after.Names);
        Assert.Equal(before.Names.Order(), after.Names.Order());
        Assert.Equal(before.Starts.Order(), after.Starts.Order());
        Assert.Equal(before.Ends.Order(), after.Ends.Order());
    }

    private static (List<string> Names, List<string> Starts, List<string> Ends) Marks(Body body) =>
        (body.Descendants<BookmarkStart>().Select(b => b.Name!.Value!).ToList(),
         body.Descendants<BookmarkStart>().Select(b => b.Id!.Value!).ToList(),
         body.Descendants<BookmarkEnd>().Select(b => b.Id!.Value!).ToList());

    [Fact]
    public void Guideline_of_a_prior_template_prints_as_before()
    {
        // Документ по прежнему шаблону хранит указания строкой-ссылкой: печать не меняется — фраза и адрес обычным текстом.
        var (body, _) = Print("""{ "literature": { "guidelines": { "lab_works": "https://edu.example.org/labs" } } }""");

        var phrase = IndexOf(body, LabSentence);
        Assert.True(phrase >= 0);
        var address = Paragraphs(body)[phrase + 1];
        Assert.Equal("https://edu.example.org/labs", address.InnerText);
        Assert.Empty(address.Descendants<Hyperlink>());
    }

    [Fact]
    public void Place_not_provided_by_the_plan_keeps_its_stub()
    {
        // Практических занятий в плане нет — вместо указаний формулировка, как и раньше, что бы ни лежало в содержимом.
        var facts = new DisciplineFacts(
            new Discipline { Name = "Дисциплина", Semesters = [new DisciplineSemesterDetails { Semester = 1, Lectures = 16, LabHours = 16 }] },
            new CurriculumPlan(), new StudyDirection(), new StudyProfile(), null, []);
        var rpd = Registry.Get(DocumentTypeRegistry.Rpd);
        var payload = JsonDocument.Parse("""{ "literature": { "guidelines": { "practical_classes": { "text": "Сборник задач" } } } }""").RootElement;
        var context = new PrintContext(payload, rpd.Schema, Registry.FindSchema, facts, new SignatoryReport([], []), 2026,
            DocumentMetadata.Hidden(rpd.Schema, DocumentSemesters.Of(payload, facts), Registry.FindSchema));

        Assert.Equal("Практические занятия учебным планом не предусмотрены.", context.Field("/literature/guidelines/practical_classes").Inline);
    }
}
