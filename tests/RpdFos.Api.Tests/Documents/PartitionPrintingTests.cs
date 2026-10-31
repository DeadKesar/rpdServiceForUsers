using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using DocumentFormat.OpenXml;
using RpdFos.Api.Tests.DocumentTypes;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using RpdFos.Core.DocumentTypes;
using RpdFos.Core.Printing;
using Xunit;

namespace RpdFos.Api.Tests.Documents;

/// <summary>
/// Печать «Разделов дисциплины» по семестрам (этап 5.2, B2) — по образцу РПД вуза: группы тем по семестрам,
/// строка «Промежуточная аттестация» после каждой, «Итого (час.)» в конце. Без базы: оформление v3 и сопоставление
/// по умолчанию из реестра.
/// </summary>
public class PartitionPrintingTests
{
    private static readonly DocumentTypeRegistry Registry = new();

    [Fact]
    public void Two_semesters_print_two_groups_with_attestation_rows_and_total()
    {
        // Часы Б1.О.14 «Проектная деятельность» по плану 09.04.04: 2 семестр — экзамен, 3 — зачёт, лабораторных в 3-м нет.
        var table = Partition(Semester(2, 20, 8, 12, 68, 36, "exam"), Semester(3, 20, 20, 0, 68, 0, "credit"),
            """
            [ { "section_number": 1, "semester": 2, "topic_title": "Инициация проекта", "competence_indicators": ["УК-2.1"],
                "hours": { "lectures": 10, "practice": 4, "lab_hours": 6, "self_study": 34 } },
              { "section_number": 2, "semester": 2, "topic_title": "Планирование проекта",
                "hours": { "lectures": 10, "practice": 4, "lab_hours": 6, "self_study": 34 } },
              { "section_number": 3, "semester": 3, "topic_title": "Командная работа в проекте", "current_control_forms": ["Р"],
                "hours": { "lectures": 20, "practice": 20, "self_study": 68 } } ]
            """, out var caption);

        Assert.Equal("Разделы дисциплины, изучаемые в 2-3 семестре:", caption);
        // № | раздел | семестр | неделя | лекции | лабораторные | практические | СР/контроль | индикаторы | формы контроля
        Assert.Equal(
            [
                ["1", "Инициация проекта", "2", "", "10", "6", "4", "34", "УК-2.1", ""],
                ["2", "Планирование проекта", "", "", "10", "6", "4", "34", "", ""],
                ["", "Промежуточная аттестация", "", "", "20", "12", "8", "68/36", "", "Экзамен"],
                ["3", "Командная работа в проекте", "3", "", "20", "-", "20", "68", "", "Р"],
                ["", "Промежуточная аттестация", "", "", "20", "-", "20", "68/0", "", "Зачёт"],
                ["", "Итого (час.)", "", "", "40", "12", "28", "136/36", "", ""]
            ],
            Rows(table));
        // «Семестр» объединён по вертикали на темы и строку аттестации своей группы.
        Assert.Equal(["restart", "continue", "continue", "restart", "continue", ""], Merges(table));
    }

    [Fact]
    public void One_semester_prints_one_group_and_topics_without_semester_join_it()
    {
        // Б1.О.10: 1 семестр, экзамен. Тема без семестра (шаблон до этапа 5.2) относится к единственному семестру.
        var table = Partition(Semester(1, 24, 8, 16, 24, 36, "exam"), null,
            """
            [ { "section_number": 1, "topic_title": "Основы моделирования", "hours": { "lectures": 24, "practice": 8, "lab_hours": 16, "self_study": 24 } } ]
            """, out var caption);

        Assert.Equal("Разделы дисциплины, изучаемые в 1 семестре:", caption);
        Assert.Equal(
            [
                ["1", "Основы моделирования", "1", "", "24", "16", "8", "24", "", ""],
                ["", "Промежуточная аттестация", "", "", "24", "16", "8", "24/36", "", "Экзамен"],
                ["", "Итого (час.)", "", "", "24", "16", "8", "24/36", "", ""]
            ],
            Rows(table));
    }

    [Theory]
    [InlineData(new[] { 1 }, "1")]
    [InlineData(new[] { 2, 3 }, "2-3")]
    [InlineData(new[] { 1, 3 }, "1, 3")]
    [InlineData(new[] { 4, 1, 2 }, "1-2, 4")]
    public void Semesters_in_caption_are_ranges_or_lists(int[] semesters, string expected) =>
        Assert.Equal(expected, PlanTitles.Semesters(semesters));

    private static DisciplineSemesterDetails Semester(int n, int lectures, int practice, int labs, int selfStudy, int control, string type) =>
        new()
        {
            Semester = n, Lectures = lectures, Practice = practice, LabHours = labs, SelfStudy = selfStudy, Control = control,
            ContactHours = lectures + practice + labs, ControlTypes = [type]
        };

    private static Table Partition(DisciplineSemesterDetails first, DisciplineSemesterDetails? second, string topics, out string caption)
    {
        var semesters = second is null ? new[] { first } : [first, second];
        var hours = string.Join(", ", semesters.Select(s =>
            $$"""{ "semester": {{s.Semester}}, "lectures": {{s.Lectures}}, "practice": {{s.Practice}}, "lab_hours": {{s.LabHours}}, "self_study": {{s.SelfStudy}}, "control": {{s.Control}}, "control_types": ["{{s.ControlTypes[0]}}"] }"""));
        var payload = JsonDocument.Parse($$"""{ "hours_distribution": { "semesters": [ {{hours}} ] }, "thematic_plan": {{topics}} }""").RootElement;
        var schema = JsonDocument.Parse(Registry.Get(DocumentTypeRegistry.Rpd).SchemaJson).RootElement;
        var facts = new DisciplineFacts(new Discipline { Name = "Дисциплина", Semesters = semesters.ToList() },
            new CurriculumPlan(), new StudyDirection(), new StudyProfile(), null, []);
        var context = new PrintContext(payload, schema, Registry.FindSchema, facts, new SignatoryReport([], []), 2026,
            DocumentMetadata.Hidden(schema, DocumentSemesters.Of(payload, facts), Registry.FindSchema));

        var result = DocxGenerator.Generate(DocxBookmarksTests.Template(DocxBookmarksTests.Rpd),
            Registry.Get(DocumentTypeRegistry.Rpd).PrintMapping, context);
        var docx = WordprocessingDocument.Open(new MemoryStream(result.Content), false);
        Assert.Empty(new OpenXmlValidator(FileFormatVersions.Office2019).Validate(docx));
        var body = docx.MainDocumentPart!.Document!.Body!;
        caption = body.Elements<Paragraph>().First(p => p.InnerText.StartsWith("Разделы дисциплины, изучаемые")).InnerText;
        return body.Descendants<Table>().Single(t => t.InnerText.Contains("Раздел дисциплины"));
    }

    // Строки данных — после трёх строк шапки (заголовки и нумерация столбцов).
    private static List<List<string>> Rows(Table table) =>
        table.Elements<TableRow>().Skip(3).Select(r => r.Elements<TableCell>().Select(c => c.InnerText).ToList()).ToList();

    private static List<string> Merges(Table table) =>
        table.Elements<TableRow>().Skip(3).Select(r => r.Elements<TableCell>().ElementAt(2).TableCellProperties?.VerticalMerge is { } m
            ? m.Val?.Value == MergedCellValues.Restart ? "restart" : "continue"
            : "").ToList();
}
