using System.Text.Json;
using System.Text.Json.Nodes;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using RpdFos.Core.DocumentTypes;
using RpdFos.Core.Templates;
using Xunit;

namespace RpdFos.Api.Tests.Documents;

/// <summary>
/// Перенос ввода автора из основы («Создать на основе», этап 7, A11; решение 4) на схемах и содержимом реального типа РПД:
/// что берётся заново, что переносится, что не переносится и названо в списке. Без базы данных.
/// </summary>
public class DocumentBasingTests
{
    private static readonly DocumentTypeRegistry Registry = new();
    private static readonly DocumentTypeDefinition Rpd = Registry.Get(DocumentTypeRegistry.Rpd);
    private static readonly Guid AuthorId = Guid.NewGuid();

    [Fact]
    public void Author_input_is_carried_by_matching_fields_and_fields_from_the_plan_are_taken_anew()
    {
        var facts = Facts("Новая дисциплина");
        var result = Transfer(BasePayload(), facts);
        var p = result.Payload;

        // Ввод автора — как был.
        Assert.Equal("<p>Цели автора</p>", Text(p, "goals_and_objectives", "goals"));
        Assert.Equal(["Задача 1", "Задача 2"], Lines(p, "goals_and_objectives", "objectives"));
        Assert.Equal("<p>Знать</p>", Text(p, "learning_outcomes", "knows"));
        Assert.Equal("Основы моделирования", Text(p["thematic_plan"]![0]!, "topic_title"));
        Assert.Equal(["Лекции"], Lines(p, "education_technologies"));
        Assert.Equal("Отчёт", Text(p, "practical_classes", "0", "control_form"));
        Assert.Equal(["Модель системы"], Lines(p, "course_work", "topics"));
        Assert.Equal("course_project", Text(p, "course_work", "kind"));
        Assert.Equal(2024, (int)p["literature"]!["main"]![0]!["year"]!);

        // План и справочники — заново, а не по основе: название, место в структуре, часы цели.
        Assert.Equal("Новая дисциплина", Text(p, "title_page", "discipline_name"));
        Assert.Equal("Б1.О.05", Text(p, "title_page", "discipline_code"));
        Assert.Equal("Б1.О.05", Text(p, "place_in_structure", "index"));
        Assert.Equal(16, (int)p["hours_distribution"]!["semesters"]![0]!["lab_hours"]!);
        Assert.Equal(3, (int)p["hours_distribution"]!["semesters"]![0]!["semester"]!);

        // Всё перенесено или названо: здесь не перенесено только то, что относится к самому документу.
        Assert.DoesNotContain(result.NotTransferred, n => n.Contains("не предусмотрено") || n.Contains("нет в схеме"));
    }

    [Fact]
    public void An_empty_base_changes_nothing_and_names_nothing()
    {
        var facts = Facts();
        var fresh = Fresh(facts);
        var before = fresh.ToJsonString();

        var result = DocumentBasing.Transfer(fresh, Rpd.Schema, Rpd.Schema, DefaultsOf(Rpd), [], facts, AuthorId, Hidden(facts),
            Registry.FindSchema);

        Assert.Equal(before, result.Payload.ToJsonString());
        Assert.Empty(result.NotTransferred);
    }

    [Fact]
    public void Competences_come_from_the_target_matrix_and_chosen_indicators_stay_by_code_and_the_unpaired_are_named()
    {
        // Матрица цели: ОПК-1 с индикаторами 1.1 и 1.3, УК-2; ПК-3 в матрице нет.
        var facts = Facts(competences: [Competence("ОПК-1", "ОПК-1.1", "ОПК-1.3"), Competence("УК-2", "УК-2.1")]);

        var result = Transfer(BasePayload(), facts);
        var competences = result.Payload["competences"]!.AsArray();

        // В документе — компетенции матрицы цели; их формулировки — из справочника, не из основы.
        Assert.Equal(["ОПК-1", "УК-2"], competences.Select(c => (string)c!["code"]!));
        Assert.Equal("Формулировка ОПК-1", (string)competences[0]!["formulation"]!);
        // Индикатор с парой остался: формулировка — из справочника, результаты обучения — ввод автора.
        var kept = Assert.Single(competences[0]!["indicators"]!.AsArray())!;
        Assert.Equal("ОПК-1.1", (string)kept["code"]!);
        Assert.Equal("Текст нового справочника ОПК-1.1", (string)kept["text"]!);
        Assert.Equal("знает", (string)kept["results"]!["knows"]!);
        Assert.Empty(competences[1]!["indicators"]!.AsArray());

        // Индикатор без пары назван: у компетенции нет такого индикатора; компетенции нет в матрице.
        Assert.Contains(result.NotTransferred, n => n.Contains("индикатор ОПК-1.2 не перенесён") && n.Contains("нет такого индикатора"));
        Assert.Contains(result.NotTransferred, n => n.Contains("индикатор ПК-3.1 не перенесён") && n.Contains("компетенции ПК-3 нет в матрице"));
        Assert.DoesNotContain(result.NotTransferred, n => n.Contains("ОПК-1.1"));
    }

    [Fact]
    public void Sections_hidden_for_the_target_are_not_carried_and_are_named_when_the_base_had_content()
    {
        // В плане цели нет лабораторных и курсового проекта, практические есть.
        var facts = Facts(labHours: 0, controlTypes: ["exam"]);
        var result = Transfer(BasePayload(), facts);
        var p = result.Payload;

        Assert.Null(p["lab_works"]);
        Assert.Null(p["course_work"]);
        Assert.NotNull(p["practical_classes"]);
        Assert.Contains(result.NotTransferred, n => n.StartsWith("Лабораторные работы:") && n.Contains("не предусмотрено учебным планом"));
        Assert.Contains(result.NotTransferred, n => n.StartsWith("Курсовой проект (курсовая работа):"));
        Assert.DoesNotContain(result.NotTransferred, n => n.StartsWith("Практические занятия"));
    }

    [Fact]
    public void A_hidden_section_without_content_in_the_base_is_not_mentioned()
    {
        var facts = Facts(labHours: 0, controlTypes: ["exam"]);
        var basePayload = BasePayload();
        basePayload["lab_works"] = new JsonArray();
        basePayload.Remove("course_work");

        var result = Transfer(basePayload, facts);

        Assert.DoesNotContain(result.NotTransferred, n => n.Contains("не предусмотрено"));
    }

    [Fact]
    public void Developers_and_the_department_protocol_are_not_carried()
    {
        var result = Transfer(BasePayload(), Facts());
        var sheet = result.Payload["approval_sheet"]!;

        // Составитель в новом документе один — автор; протокол заседания кафедры пуст: он относится к новому документу.
        var developer = Assert.Single(sheet["developers"]!.AsArray())!;
        Assert.Equal(AuthorId.ToString(), (string)developer["user_id"]!);
        Assert.Null(sheet["department_protocol"]);
        Assert.Contains(result.NotTransferred, n => n.StartsWith("Лист согласования → Составители:") && n.EndsWith("(2)"));
        Assert.Contains(result.NotTransferred, n => n.StartsWith("Лист согласования → Протокол заседания кафедры:"));
    }

    [Fact]
    public void Developers_are_not_named_when_the_author_was_the_only_one_and_a_missing_protocol_is_not_named()
    {
        var basePayload = BasePayload();
        basePayload["approval_sheet"] = JsonNode.Parse($$"""{ "developers": [ { "user_id": "{{AuthorId}}", "name_for_document": "А. А. Авторов", "position": "доцент" } ] }""");

        var result = Transfer(basePayload, Facts());

        Assert.DoesNotContain(result.NotTransferred, n => n.Contains("Лист согласования"));
    }

    [Fact]
    public void Standing_items_of_the_university_follow_the_new_template_unless_the_author_changed_them()
    {
        // Шаблон цели обновил список программного обеспечения; в основе он стандартный — по умолчанию шаблона основы.
        var baseDefaults = JsonNode.Parse(Rpd.DefaultPayloadJson!)!;
        var targetDefaults = JsonNode.Parse(Rpd.DefaultPayloadJson!)!;
        targetDefaults["literature"]!["software"] = new JsonArray("Новое программное обеспечение вуза");

        var basePayload = BasePayload();
        basePayload["literature"]!["software"] = baseDefaults["literature"]!["software"]!.DeepClone();
        // Материально-техническое обеспечение автор дополнил — это его ввод.
        var support = baseDefaults["material_support"]!.DeepClone().AsArray();
        support.Add("Лаборатория моделирования");
        basePayload["material_support"] = support;

        var facts = Facts();
        var result = Transfer(basePayload, facts, targetDefaults: targetDefaults.ToJsonString(), baseDefaults: baseDefaults);

        Assert.Equal(["Новое программное обеспечение вуза"], Lines(result.Payload, "literature", "software"));
        Assert.Equal(3, result.Payload["material_support"]!.AsArray().Count);
        Assert.Equal("Лаборатория моделирования", (string)result.Payload["material_support"]![2]!);
        Assert.DoesNotContain(result.NotTransferred, n => n.Contains("программное") || n.Contains("Программное"));
    }

    [Fact]
    public void A_standard_link_in_a_hidden_place_is_not_named_but_a_link_the_author_changed_is()
    {
        // Ссылка на указания к лабораторным — постоянный пункт вуза; в плане цели лабораторных нет, место скрыто.
        var baseDefaults = JsonNode.Parse(Rpd.DefaultPayloadJson!)!;
        baseDefaults["literature"]!["guidelines"] = JsonNode.Parse("""{ "lab_works": "https://edu.example/lab", "independent_work": "https://edu.example/self" }""");
        var facts = Facts(labHours: 0, controlTypes: ["exam"]);

        var standard = BasePayload();
        standard["literature"]!["guidelines"] = baseDefaults["literature"]!["guidelines"]!.DeepClone();
        Assert.DoesNotContain(Transfer(standard, facts, baseDefaults: baseDefaults).NotTransferred, n => n.Contains("лабораторным"));

        var changed = BasePayload();
        changed["literature"]!["guidelines"] = JsonNode.Parse("""{ "lab_works": "https://kafedra.example/my-lab", "independent_work": "https://edu.example/self" }""");
        var result = Transfer(changed, facts, baseDefaults: baseDefaults);
        Assert.Contains(result.NotTransferred, n => n.Contains("лабораторным") && n.Contains("не предусмотрено"));
        // Ссылка на указания к самостоятельной работе — не скрыта и не менялась: остаётся значением нового шаблона.
        Assert.Null(result.Payload["literature"]!["guidelines"]?["lab_works"]);
    }

    [Fact]
    public void Plan_discipline_references_are_kept_only_when_the_discipline_is_in_the_target_plan()
    {
        var inPlan = new PlanDisciplineCandidate(Guid.NewGuid(), "Б1.О.01", "Математика", [1]);
        var facts = Facts(neighbours: [inPlan]);
        var basePayload = BasePayload();
        basePayload["place_in_structure"] = JsonNode.Parse($$"""
            {
              "prerequisites": [
                { "discipline_id": "{{inPlan.Id}}", "code": "Б1.О.01", "name": "Математика" },
                { "discipline_id": "{{Guid.NewGuid()}}", "code": "Б1.О.02", "name": "Физика" } ],
              "prior_outcomes": { "knows": "<p>Основы</p>" }
            }
            """);

        var result = Transfer(basePayload, facts);
        var place = result.Payload["place_in_structure"]!;

        var kept = Assert.Single(place["prerequisites"]!.AsArray())!;
        Assert.Equal(inPlan.Id.ToString(), (string)kept["discipline_id"]!);
        Assert.Equal("<p>Основы</p>", Text(place, "prior_outcomes", "knows"));
        // Дисциплины другого плана названы; поля из плана (индекс, блок) — заново.
        Assert.Contains(result.NotTransferred, n => n.Contains("«Б1.О.02 Физика»") && n.Contains("нет среди подходящих дисциплин"));
        Assert.Equal("Б1.О.05", Text(place, "index"));
    }

    [Fact]
    public void A_field_missing_from_the_new_schema_or_of_another_type_is_named_and_not_carried()
    {
        // Схема цели: раздел «Результаты обучения» убран, «Образовательные технологии» стали строкой.
        var schema = JsonNode.Parse(Rpd.SchemaJson)!;
        schema["properties"]!.AsObject().Remove("learning_outcomes");
        schema["properties"]!["education_technologies"]!["type"] = "string";
        var target = JsonDocument.Parse(schema.ToJsonString()).RootElement;
        var facts = Facts();
        var fresh = Fresh(facts, target);

        var result = DocumentBasing.Transfer(fresh, target, Rpd.Schema, DefaultsOf(Rpd), BasePayload(), facts, AuthorId,
            Hidden(facts, target), Registry.FindSchema);

        Assert.Null(result.Payload["learning_outcomes"]);
        Assert.Null(result.Payload["education_technologies"]);
        Assert.Contains(result.NotTransferred, n => n.StartsWith("Результаты обучения по дисциплине:") && n.Contains("нет в схеме нового шаблона"));
        Assert.Contains(result.NotTransferred, n => n.StartsWith("Образовательные технологии:") && n.Contains("тип поля"));
        // Остальное перенесено.
        Assert.Equal("<p>Цели автора</p>", Text(result.Payload, "goals_and_objectives", "goals"));
    }

    // ───── вспомогательное ─────

    private static BasingResult Transfer(
        JsonObject basePayload, DisciplineFacts facts, string? targetDefaults = null, JsonNode? baseDefaults = null)
    {
        var fresh = Fresh(facts, defaults: targetDefaults);
        return DocumentBasing.Transfer(fresh, Rpd.Schema, Rpd.Schema, baseDefaults ?? DefaultsOf(Rpd), basePayload, facts, AuthorId,
            Hidden(facts), Registry.FindSchema);
    }

    /// <summary>Содержимое нового документа «с нуля» — как собирает создание: план, справочники, умолчания шаблона.</summary>
    private static JsonObject Fresh(DisciplineFacts facts, JsonElement? schema = null, string? defaults = null)
    {
        var target = schema ?? Rpd.Schema;
        var payload = DocumentPrefill.Build(target, facts, new DocumentAuthor(AuthorId, "А. А. Авторов", "доцент"), Registry.FindSchema);
        TemplateDefaults.Apply(payload, defaults ?? Rpd.DefaultPayloadJson, TemplateDefaults.Fields(target, Registry.FindSchema),
            Hidden(facts, target));
        return payload;
    }

    private static IReadOnlyList<string> Hidden(DisciplineFacts facts, JsonElement? schema = null) =>
        DocumentMetadata.Hidden(schema ?? Rpd.Schema, facts.Semesters, Registry.FindSchema);

    private static JsonNode? DefaultsOf(DocumentTypeDefinition type) => JsonNode.Parse(type.DefaultPayloadJson!);

    private static DisciplineFacts Facts(
        string name = "Новая дисциплина", int labHours = 16, string[]? controlTypes = null, IEnumerable<CompetenceCandidate>? competences = null,
        IEnumerable<PlanDisciplineCandidate>? neighbours = null) => new(
        new Discipline
        {
            Id = Guid.NewGuid(), Name = name, Code = "Б1.О.05", TotalHours = 108, TotalCredits = 3,
            Semesters =
            [
                new DisciplineSemesterDetails
                {
                    Semester = 3, Lectures = 16, Practice = 16, LabHours = labHours, SelfStudy = 40, Credits = 3,
                    ControlTypes = [.. controlTypes ?? ["exam", "course_project"]]
                }
            ]
        },
        new CurriculumPlan { Year = 2024 },
        new StudyDirection { Code = "09.04.04", Name = "Программная инженерия", EducationLevel = EducationLevels.Master },
        new StudyProfile { Name = "Профиль" }, null, [.. competences ?? [Competence("ОПК-1", "ОПК-1.1", "ОПК-1.3")]], [.. neighbours ?? []]);

    private static CompetenceCandidate Competence(string code, params string[] indicators) => new(
        Guid.NewGuid(), code, $"Формулировка {code}", code.StartsWith("УК") ? CompetenceCategories.Universal : CompetenceCategories.GeneralProfessional,
        indicators.Select(i => new IndicatorCandidate(Guid.NewGuid(), i, $"Текст нового справочника {i}", null)).ToList());

    /// <summary>Содержимое основы: ввод автора, поля из прежнего плана, два составителя и протокол кафедры.</summary>
    private static JsonObject BasePayload() => JsonNode.Parse($$"""
        {
          "title_page": { "discipline_name": "Старая дисциплина", "academic_year": "2023/2024" },
          "goals_and_objectives": { "goals": "<p>Цели автора</p>", "objectives": ["Задача 1", "Задача 2"] },
          "learning_outcomes": { "knows": "<p>Знать</p>" },
          "competences": [
            { "code": "ОПК-1", "formulation": "Старая формулировка", "indicators": [
                { "code": "ОПК-1.1", "text": "Старый текст", "results": { "knows": "знает" } },
                { "code": "ОПК-1.2", "text": "Такого индикатора в новой матрице нет" } ] },
            { "code": "ПК-3", "formulation": "ПК-3 в матрице цели нет", "indicators": [ { "code": "ПК-3.1", "text": "Текст" } ] }
          ],
          "thematic_plan": [ { "section_number": 1, "semester": 3, "topic_title": "Основы моделирования", "hours": { "lectures": 16 } } ],
          "practical_classes": [ { "number": 1, "title": "Построение модели", "hours": 16, "control_form": "Отчёт" } ],
          "lab_works": [ { "number": 1, "title": "Исследование модели", "hours": 16 } ],
          "course_work": { "kind": "course_project", "topics": ["Модель системы"] },
          "education_technologies": ["Лекции"],
          "literature": { "main": [ { "citation": "Петров П. П. Моделирование систем : учебник. — Москва : Наука, 2024. — 320 с.", "year": 2024 } ], "additional": [] },
          "approval_sheet": {
            "developers": [
              { "user_id": "{{Guid.NewGuid()}}", "name_for_document": "И. И. Иванов", "position": "доцент" },
              { "user_id": "{{Guid.NewGuid()}}", "name_for_document": "П. П. Петров", "position": "профессор" } ],
            "department_protocol": { "number": "5", "date": "2024-05-01" }
          }
        }
        """)!.AsObject();

    private static string? Text(JsonNode node, params string[] path)
    {
        foreach (var segment in path)
            node = (int.TryParse(segment, out var index) ? node[index] : node[segment])!;
        return node.GetValue<string>();
    }

    private static string[] Lines(JsonNode node, params string[] path)
    {
        JsonNode? current = node;
        foreach (var segment in path) current = current is null ? null : int.TryParse(segment, out var index) ? current[index] : current[segment];
        return current switch
        {
            JsonArray a => a.Select(n => n!.GetValue<string>()).ToArray(),
            JsonValue v => [v.GetValue<string>()],
            _ => []
        };
    }
}
