using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml.Wordprocessing;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using RpdFos.Core.DocumentTypes;
using Xunit;

namespace RpdFos.Api.Tests.Documents;

/// <summary>
/// Наименование раздела дисциплины в РПД (этап 8, A12): обязательное поле тем новой схемы, правило «одно наименование на
/// раздел», печать таблиц 3.1 и 3.2 по разделам и снимок РПД в ФОС. Схемы и документы прежних шаблонов — без наименований —
/// ведут себя как раньше. Без базы.
/// </summary>
public class SectionTitlesTests
{
    private static readonly DocumentTypeRegistry Registry = new();
    private static readonly DocumentValidator Validator = new(Registry);
    private static readonly string Rpd = Registry.Get(DocumentTypeRegistry.Rpd).SchemaJson;

    // Схема шаблона, созданного до этапа 8: без поля наименования и без правила.
    private static readonly string OldRpd = WithoutSectionTitle(Rpd);

    private static string WithoutSectionTitle(string schema)
    {
        var node = JsonNode.Parse(schema)!;
        var plan = node["properties"]!["thematic_plan"]!;
        var items = plan["items"]!;
        items["properties"]!.AsObject().Remove("section_title");
        var required = items["required"]!.AsArray();
        required.Remove(required.First(n => (string?)n == "section_title"));
        var rules = plan["x-validation-rule"]!.AsArray();
        rules.Remove(rules.First(n => (string?)n == "section_title_same_in_section"));
        return node.ToJsonString();
    }

    // Ошибки поля наименования у единственной темы; extra — свойства темы, дописанные к обязательным.
    private static List<DocumentValidationError> Errors(string schema, string extra, ValidationMode mode = ValidationMode.Submit)
    {
        var payload = JsonDocument.Parse(
            $$"""{ "thematic_plan": [ { "section_number": 1, "semester": 1, "topic_title": "Основы моделирования", "hours": { "lectures": 4 }{{extra}} } ] }""").RootElement;
        return Validator.Validate(schema, payload, mode).Where(e => e.Path.StartsWith("/thematic_plan/0/section_title")).ToList();
    }

    [Fact]
    public void New_schema_requires_the_section_title_on_submission_but_not_in_a_draft()
    {
        var error = Assert.Single(Errors(Rpd, ""));

        Assert.Equal("/thematic_plan/0/section_title", error.Path);
        // Черновик с пустым полем сохраняется: наименование можно вписать позже, блокирует его только отправка.
        Assert.Empty(Errors(Rpd, "", ValidationMode.Draft));
        Assert.Empty(Errors(Rpd, ", \"section_title\": \"Моделирование систем\""));
    }

    [Theory]
    [InlineData(", \"section_title\": \"\"")]
    [InlineData(", \"section_title\": \"Я\"")]
    public void Empty_or_too_short_title_is_a_field_error(string extra) => Assert.Single(Errors(Rpd, extra));

    [Fact]
    public void Schema_of_a_template_made_before_the_field_has_no_title_and_asks_for_none()
    {
        Assert.Empty(Errors(OldRpd, ""));
        // Схема нового шаблона описывает поле, прежняя — нет: редактор по этому решает, показывать ли его.
        Assert.True(HasTitle(Rpd));
        Assert.False(HasTitle(OldRpd));

        static bool HasTitle(string schema) => JsonDocument.Parse(schema).RootElement.GetProperty("properties").GetProperty("thematic_plan")
            .GetProperty("items").GetProperty("properties").TryGetProperty("section_title", out _);
    }

    private static List<DocumentValidationError> Rule(string topics)
    {
        var payload = JsonDocument.Parse($$"""{ "thematic_plan": {{topics}} }""").RootElement;
        var facts = new DisciplineFacts(new Discipline { Name = "Дисциплина" }, new CurriculumPlan(), new StudyDirection(), new StudyProfile(), null, []);
        return new SectionTitleSameInSection()
            .Check(new RuleContext("/thematic_plan", payload.GetProperty("thematic_plan"), payload, facts, DateTimeOffset.UtcNow)).ToList();
    }

    [Fact]
    public void Topics_of_one_section_carry_the_same_title_and_the_error_stands_at_the_differing_topic()
    {
        Assert.Empty(Rule("""
            [ { "section_number": 1, "section_title": "Введение" }, { "section_number": 1, "section_title": " Введение " },
              { "section_number": 2, "section_title": "Моделирование" } ]
            """));

        var error = Assert.Single(Rule("""
            [ { "section_number": 1, "section_title": "Введение" }, { "section_number": 2, "section_title": "Моделирование" },
              { "section_number": 1, "section_title": "Введение в предмет" } ]
            """));
        Assert.Equal("/thematic_plan/2/section_title", error.Path);
        Assert.Contains("«Введение»", error.Message);
        Assert.Equal(IssueLevels.Error, error.Level);

        // Пустое наименование — дело схемы, правило его не повторяет.
        Assert.Empty(Rule("""[ { "section_number": 1, "section_title": "Введение" }, { "section_number": 1 }, { "section_number": 1, "section_title": "" } ]"""));
    }

    [Fact]
    public void Rule_is_in_the_schema_of_new_templates_and_in_the_catalog()
    {
        Assert.Contains("section_title_same_in_section", ValidationRules.Names);
        Assert.Contains("section_title_same_in_section",
            JsonDocument.Parse(Rpd).RootElement.GetProperty("properties").GetProperty("thematic_plan").GetProperty("x-validation-rule")
                .EnumerateArray().Select(n => n.GetString()));
    }

    private const string Plan = """
        [ { "section_number": 1, "section_title": "Введение в моделирование", "semester": 1, "topic_title": "Понятие модели", "topic_description": "Модель и оригинал.",
            "week": "1–2", "competence_indicators": ["УК-1.1"], "current_control_forms": ["Р"],
            "hours": { "lectures": 4, "practice": 2, "lab_hours": 0, "self_study": 6 } },
          { "section_number": 1, "section_title": "Введение в моделирование", "semester": 1, "topic_title": "Виды моделей", "topic_description": "Классификация.",
            "week": "3", "competence_indicators": ["УК-1.1", "УК-1.2"], "current_control_forms": ["Р", "КР"],
            "hours": { "lectures": 4, "practice": 2, "lab_hours": 0, "self_study": 6 } },
          { "section_number": 2, "section_title": "Построение моделей", "semester": 1, "topic_title": "Построение моделей", "topic_description": "Этапы построения.",
            "hours": { "lectures": 16, "practice": 4, "lab_hours": 0, "self_study": 12 } } ]
        """;

    [Fact]
    public void Table_3_1_prints_a_row_per_section_with_summed_hours_and_the_union_of_the_topics_data()
    {
        var table = PartitionPrintingTests.Partition(PartitionPrintingTests.Semester(1, 24, 8, 0, 24, 36, "exam"), null, Plan, out _);

        // № | раздел | семестр | неделя | лекции | лабораторные | практические | СР/контроль | индикаторы | формы контроля
        Assert.Equal(
            [
                ["1", "Введение в моделирование", "1", "1–2, 3", "8", "-", "4", "12", "УК-1.1, УК-1.2", "Р, КР"],
                ["2", "Построение моделей", "", "", "16", "-", "4", "12", "", ""],
                ["", "Промежуточная аттестация", "", "", "24", "-", "8", "24/36", "", "экзамен"],
                ["", "Итого (час.)", "", "", "24", "-", "8", "24/36", "", ""]
            ],
            PartitionPrintingTests.Rows(table));
    }

    [Fact]
    public void Table_3_2_prints_a_row_per_section_with_a_paragraph_per_topic()
    {
        Assert.Equal(
            [
                ["1", "Введение в моделирование", "Понятие модели: Модель и оригинал.|Виды моделей: Классификация."],
                // Раздел из одной темы с тем же названием — тема не повторяется, остаётся содержание.
                ["2", "Построение моделей", "Этапы построения."]
            ],
            Contents(Plan));
    }

    [Fact]
    public void Plan_without_titles_prints_a_row_per_topic_as_before()
    {
        var rows = Contents("""
            [ { "section_number": 1, "semester": 1, "topic_title": "Понятие модели", "topic_description": "Модель и оригинал.", "hours": { "lectures": 24 } },
              { "section_number": 1, "semester": 1, "topic_title": "Виды моделей", "hours": { "lectures": 0 } } ]
            """);

        Assert.Equal([["1", "Понятие модели", "Модель и оригинал."], ["1", "Виды моделей", ""]], rows);
        var table = PartitionPrintingTests.Partition(PartitionPrintingTests.Semester(1, 24, 0, 0, 0, 0, "exam"), null,
            """[ { "section_number": 1, "semester": 1, "topic_title": "Понятие модели", "hours": { "lectures": 24 } } ]""", out _);
        Assert.Equal("Понятие модели", PartitionPrintingTests.Rows(table)[0][1]);
    }

    // Строки таблицы 3.2: ячейки; абзацы ячейки — через «|».
    private static List<List<string>> Contents(string topics)
    {
        var body = PartitionPrintingTests.Print(PartitionPrintingTests.Semester(1, 24, 8, 0, 24, 36, "exam"), null, topics);
        var table = body.Descendants<Table>().Single(t => t.Elements<TableRow>().First().InnerText.Contains("Наименование раздела дисциплины"));
        return table.Elements<TableRow>().Skip(1)
            .Select(r => r.Elements<TableCell>()
                .Select(c => string.Join("|", c.Elements<Paragraph>().Select(p => p.InnerText).Where(t => t.Length > 0))).ToList())
            .ToList();
    }

    [Fact]
    public void Snapshot_names_a_section_by_its_title_and_by_its_topics_when_the_rpd_has_none()
    {
        var titled = FosSnapshot.Take(Payload(Plan));
        Assert.Equal(["Введение в моделирование", "Построение моделей"], titled["sections"]!.AsArray().Select(s => (string)s!["title"]!));

        var plain = FosSnapshot.Take(Payload(
            """[ { "section_number": 1, "topic_title": "Понятие модели" }, { "section_number": 1, "topic_title": "Виды моделей" } ]"""));
        Assert.Equal("Понятие модели, Виды моделей", (string)plain["sections"]![0]!["title"]!);

        // Переименование раздела в РПД — расхождение со снимком («РПД изменилась»); переименование темы при наименовании — нет.
        Assert.True(FosSnapshot.Differs(titled, FosSnapshot.Take(Payload(Plan.Replace("Введение в моделирование", "Основы моделей")))));
        Assert.False(FosSnapshot.Differs(titled, FosSnapshot.Take(Payload(Plan.Replace("Виды моделей", "Классификация моделей")))));
    }

    private static JsonNode Payload(string plan) => JsonNode.Parse($$"""{ "thematic_plan": {{plan}} }""")!;
}
