using System.Text.Json;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using RpdFos.Core.DocumentTypes;
using RpdFos.Core.Printing;
using Xunit;

namespace RpdFos.Api.Tests.Documents;

/// <summary>Печать без базы (этап 5, B5): форматированный текст в абзацы, формы слов, имя файла.</summary>
public class PrintingTests
{
    [Fact]
    public void Rich_text_becomes_paragraphs_runs_and_lists()
    {
        var id = 0;
        var paragraphs = RichTextBlocks.Parse(
            "<p>Знания <strong>математики</strong> и <em>физики</em></p>текст вне абзаца<br>после переноса" +
            "<ul><li>Алгебра</li><li>Геометрия</li></ul><ol><li>Первый</li></ol>", () => ++id);

        Assert.Equal(["Знания математики и физики", "текст вне абзаца", "после переноса", "Алгебра", "Геометрия", "Первый"],
            paragraphs.Select(p => p.PlainText));
        Assert.Equal([false, true, false, false], paragraphs[0].Runs.Select(r => r.Bold));
        Assert.True(paragraphs[0].Runs[3].Italic);
        // Каждый список — со своим номером: нумерация второго списка начинается заново.
        Assert.Equal((PrintListKind.Bullet, 1), (paragraphs[3].List, paragraphs[3].ListId));
        Assert.Equal(paragraphs[3].ListId, paragraphs[4].ListId);
        Assert.Equal((PrintListKind.Numbered, 2), (paragraphs[5].List, paragraphs[5].ListId));
    }

    [Theory]
    [InlineData("Обязательная часть", WordForms.Dat, "обязательной части")]
    [InlineData("Часть, формируемая участниками образовательных отношений", WordForms.Dat,
        "части, формируемой участниками образовательных отношений")]
    [InlineData("Магистратура", WordForms.Gen, "магистратуры")]
    [InlineData("очная", WordForms.Ins, "очной")]
    [InlineData("магистр", WordForms.Acc, "магистра")]
    [InlineData("Зачёт с оценкой", WordForms.Prep, "зачёте с оценкой")]
    [InlineData("Любое значение", WordForms.Nom, "Любое значение")]
    public void Closed_sets_are_declined_by_dictionary(string value, string form, string expected) =>
        Assert.Equal(expected, Declension.Form(value, form));

    [Fact]
    public void Unknown_value_has_no_form() => Assert.Null(Declension.Form("Программная инженерия", WordForms.Gen));

    [Fact]
    public void Fields_print_by_schema()
    {
        var registry = new DocumentTypeRegistry();
        var schema = JsonDocument.Parse(registry.Get(DocumentTypeRegistry.Rpd).SchemaJson).RootElement;
        var payload = JsonDocument.Parse("""
            {
              "literature": { "main": [ { "citation": "Советов Б. Я. Моделирование систем. — М. : Юрайт, 2023. — 343 с.", "year": 2023 } ],
                              "internet_resources": [ { "title": "ЭБС", "url": "https://example.org" } ] },
              "approval_sheet": { "department_protocol": { "number": "3", "date": "2026-10-05" } },
              "course_work": { "kind": "course_project" }
            }
            """).RootElement;
        var facts = new DisciplineFacts(
            new Discipline { Name = "Дисциплина", Semesters = [new DisciplineSemesterDetails { Semester = 1, Lectures = 16, ControlTypes = ["exam"] }] },
            new CurriculumPlan(), new StudyDirection(), new StudyProfile(), null, []);
        var context = new PrintContext(payload, schema, registry.FindSchema, facts, new SignatoryReport([], []), 2026,
            DocumentMetadata.Hidden(schema, facts.Semesters, registry.FindSchema));

        // Литература — нумерованным списком, только библиографическая запись: год в ней уже есть.
        var main = context.Field("/literature/main");
        Assert.Equal(("Советов Б. Я. Моделирование систем. — М. : Юрайт, 2023. — 343 с.", PrintListKind.Numbered),
            (main.Paragraphs.Single().PlainText, main.Paragraphs[0].List));
        Assert.Equal("ЭБС // Режим доступа URL: https://example.org", context.Field("/literature/internet_resources").Inline);
        Assert.Equal("«05» октября 2026 г.", context.Field("/approval_sheet/department_protocol/date").Inline);
        // Курсового проекта по плану нет — стандартная формулировка вместо содержимого раздела.
        Assert.Equal("В учебном плане курсовое проектирование для данной дисциплины не предусмотрено.", context.Field("/course_work").Inline);
        Assert.True(context.Field("/goals_and_objectives/goals").IsEmpty);
    }

    [Fact]
    public void Section_hidden_by_correction_prints_stub_and_sources_follow_document()
    {
        // По плану — лабораторные и экзамен; методист обнулил лабораторные и добавил курсовой проект.
        var registry = new DocumentTypeRegistry();
        var schema = JsonDocument.Parse(registry.Get(DocumentTypeRegistry.Rpd).SchemaJson).RootElement;
        var payload = JsonDocument.Parse("""
            {
              "hours_distribution": { "semesters": [ { "semester": 1, "lectures": 16, "lab_hours": 0, "control_types": ["exam", "course_project"] } ] },
              "lab_works": [ { "number": 1, "title": "Исследование модели", "hours": 16 } ],
              "course_work": { "kind": "course_project", "topics": ["Модель системы"] }
            }
            """).RootElement;
        var facts = new DisciplineFacts(
            new Discipline
            {
                Name = "Дисциплина",
                Semesters = [new DisciplineSemesterDetails { Semester = 1, Lectures = 16, LabHours = 16, ControlTypes = ["exam"] }]
            },
            new CurriculumPlan(), new StudyDirection(), new StudyProfile(), null, []);
        var context = new PrintContext(payload, schema, registry.FindSchema, facts, new SignatoryReport([], []), 2026,
            DocumentMetadata.Hidden(schema, DocumentSemesters.Of(payload, facts), registry.FindSchema));

        Assert.Equal("Лабораторные работы учебным планом не предусмотрены.", context.Field("/lab_works").Inline);
        Assert.Equal("Курсовой проект", context.Source("parser.discipline.course_work_kind"));
        Assert.Equal("Экзамен, Курсовой проект", context.Source("parser.discipline.attestation"));
        Assert.Null(context.Stub("/course_work"));
    }

    [Theory]
    [InlineData("""{ "title_page": { "academic_year": "2026/2027" } }""", "РПД_Моделирование систем_2026-2027.docx")]
    [InlineData("""{ "title_page": { "academic_year": "2026" } }""", "РПД_Моделирование систем_2025.docx")]
    [InlineData("{}", "РПД_Моделирование систем_2025.docx")]
    public void File_name_carries_academic_year_from_title_page(string payload, string expected)
    {
        var document = new Document { Payload = payload, CreatedAt = new DateTimeOffset(2025, 9, 1, 0, 0, 0, TimeSpan.Zero) };

        Assert.Equal(expected, DocumentPrinting.FileName(document, "Моделирование: систем", DocumentPrinting.Period(document), "docx"));
    }
}
