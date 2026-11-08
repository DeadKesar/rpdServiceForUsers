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
                ["", "Промежуточная аттестация", "", "", "20", "12", "8", "68/36", "", "экзамен"],
                ["3", "Командная работа в проекте", "3", "", "20", "-", "20", "68", "", "Р"],
                ["", "Промежуточная аттестация", "", "", "20", "-", "20", "68/0", "", "зачёт"],
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
                ["", "Промежуточная аттестация", "", "", "24", "16", "8", "24/36", "", "экзамен"],
                ["", "Итого (час.)", "", "", "24", "16", "8", "24/36", "", ""]
            ],
            Rows(table));
    }

    [Fact]
    public void Kind_absent_in_every_semester_is_a_dash_in_total_too()
    {
        // ФТД.В.01: 1 и 2 семестр, зачёты, лабораторных нет ни в одном.
        var table = Partition(Semester(1, 8, 8, 0, 20, 0, "credit"), Semester(2, 8, 8, 0, 20, 0, "credit"),
            """
            [ { "section_number": 1, "semester": 1, "topic_title": "Цели обучения", "hours": { "lectures": 8, "practice": 8, "self_study": 20 } },
              { "section_number": 2, "semester": 2, "topic_title": "Портфолио", "hours": { "lectures": 8, "practice": 8, "self_study": 20 } } ]
            """, out var caption);

        Assert.Equal("Разделы дисциплины, изучаемые в 1-2 семестре:", caption);
        Assert.Equal(["", "Итого (час.)", "", "", "16", "-", "16", "40/0", "", ""], Rows(table)[^1]);
    }

    [Fact]
    public void Laboriousness_prints_dashes_for_zero_and_lowercase_attestation_without_total()
    {
        // ФТД.В.01: лабораторных и контроля нет — «-»; у вида промежуточного контроля «всего» пусто, виды — строчными.
        var body = Print(Semester(1, 8, 8, 0, 20, 0, "credit"), Semester(2, 8, 8, 0, 20, 0, "graded_credit"), "[]");
        var table = body.Descendants<Table>().First(t => t.InnerText.Contains("Общая трудоёмкость"));
        List<string> Cells(string start) => table.Elements<TableRow>().First(r => r.InnerText.StartsWith(start))
            .Elements<TableCell>().Skip(1).Select(c => c.InnerText).ToList();

        Assert.Equal(["-", "-", "-"], Cells("Лабораторные"));
        Assert.Equal(["-", "-", "-"], Cells("Промежуточный контроль"));
        Assert.Equal(["8", "8", "16"], Cells("Лекции"));
        Assert.Equal(["зачёт", "зачёт с оценкой", ""], Cells("Вид промежуточного контроля"));
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
        var body = Print(first, second, topics);
        caption = body.Elements<Paragraph>().First(p => p.InnerText.StartsWith("Разделы дисциплины, изучаемые")).InnerText;
        return body.Descendants<Table>().Single(t => t.InnerText.Contains("Раздел дисциплины"));
    }

    [Fact]
    public void Kind_without_sections_flag_prints_only_its_name()
    {
        // Флажок «Разделы» снят — у вида нет перечня (номера сброшены), в печати только название (этап 6, A6).
        var body = Print(Semester(1, 24, 8, 16, 24, 36, "exam"), null,
            """[ { "section_number": 1, "topic_title": "Тема", "hours": { "lectures": 24, "practice": 8, "lab_hours": 16, "self_study": 24 } } ]""",
            kinds: """
                [ { "kind": "самостоятельное изучение разделов", "hours": [ { "semester": 1, "hours": 10 } ] },
                  { "kind": "подготовка к текущему контролю", "sections": [1], "hours": [ { "semester": 1, "hours": 14 } ] } ]
                """);

        var rows = body.Descendants<Table>().First(t => t.InnerText.Contains("Общая трудоёмкость"))
            .Elements<TableRow>().Select(r => r.Elements<TableCell>().First().InnerText).Where(t => t.StartsWith("- ")).ToList();
        Assert.Equal(["- самостоятельное изучение разделов", "- подготовка к текущему контролю 1"], rows);
    }

    private static Body Print(DisciplineSemesterDetails first, DisciplineSemesterDetails? second, string topics, Discipline? discipline = null,
        string kinds = "[]")
    {
        var semesters = second is null ? new[] { first } : [first, second];
        var hours = string.Join(", ", semesters.Select(s =>
            $$"""{ "semester": {{s.Semester}}, "lectures": {{s.Lectures}}, "practice": {{s.Practice}}, "lab_hours": {{s.LabHours}}, "self_study": {{s.SelfStudy}}, "control": {{s.Control}}, "control_types": ["{{s.ControlTypes[0]}}"] }"""));
        discipline ??= new Discipline { Name = "Дисциплина" };
        var payload = JsonDocument.Parse($$"""{ "hours_distribution": { "total_credits": {{discipline.TotalCredits}}, "total_hours": {{discipline.TotalHours}}, "semesters": [ {{hours}} ], "self_study_kinds": {{kinds}} }, "thematic_plan": {{topics}} }""").RootElement;
        var schema = JsonDocument.Parse(Registry.Get(DocumentTypeRegistry.Rpd).SchemaJson).RootElement;
        discipline.Semesters = semesters.ToList();
        var facts = new DisciplineFacts(discipline,
            new CurriculumPlan(), new StudyDirection(), new StudyProfile(), null, []);
        var context = new PrintContext(payload, schema, Registry.FindSchema, facts, new SignatoryReport([], []), 2026,
            DocumentMetadata.Hidden(schema, DocumentSemesters.Of(payload, facts), Registry.FindSchema));

        var result = DocxGenerator.Generate(DocxBookmarksTests.Template(DocxBookmarksTests.Rpd),
            Registry.Get(DocumentTypeRegistry.Rpd).PrintMapping, context);
        var docx = WordprocessingDocument.Open(new MemoryStream(result.Content), false);
        Assert.Empty(new OpenXmlValidator(FileFormatVersions.Office2019).Validate(docx));
        return docx.MainDocumentPart!.Document!.Body!;
    }

    [Fact]
    public void Zero_credit_elective_prints_laboriousness_in_hours_like_university_rpd()
    {
        // ПФК: 0 з. е., 328 ч — как в РПД вуза: «составляет 328 академических часов в рамках элективных дисциплин (модулей)».
        var body = Print(Semester(1, 0, 24, 0, 30, 0, "credit"), null, "[]",
            new Discipline { Name = "Прикладная физическая культура", TotalCredits = 0, TotalHours = 328, PartType = PartTypes.ElectiveItem });

        var text = body.Descendants<Paragraph>().Select(p => p.InnerText).Single(t => t.Contains("Общая трудоемкость дисциплины"));
        Assert.Equal("Общая трудоемкость дисциплины составляет 328 академических часов в рамках элективных дисциплин (модулей).", text.Trim());
    }

    [Fact]
    public void Credits_are_printed_as_before()
    {
        var body = Print(Semester(1, 24, 8, 16, 24, 36, "exam"), null, "[]",
            new Discipline { Name = "Дисциплина", TotalCredits = 3, TotalHours = 108 });

        var text = body.Descendants<Paragraph>().Select(p => p.InnerText).Single(t => t.Contains("Общая трудоемкость дисциплины"));
        Assert.Equal("Общая трудоемкость дисциплины составляет 3 зачетных единиц (108 академических часов).", text.Trim());
    }

    // Строки данных — после трёх строк шапки (заголовки и нумерация столбцов).
    private static List<List<string>> Rows(Table table) =>
        table.Elements<TableRow>().Skip(3).Select(r => r.Elements<TableCell>().Select(c => c.InnerText).ToList()).ToList();

    private static List<string> Merges(Table table) =>
        table.Elements<TableRow>().Skip(3).Select(r => r.Elements<TableCell>().ElementAt(2).TableCellProperties?.VerticalMerge is { } m
            ? m.Val?.Value == MergedCellValues.Restart ? "restart" : "continue"
            : "").ToList();
}
