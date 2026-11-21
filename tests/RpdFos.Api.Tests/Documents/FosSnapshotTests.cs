using System.Text.Json;
using System.Text.Json.Nodes;
using RpdFos.Core.Documents;
using RpdFos.Core.DocumentTypes;
using Xunit;

namespace RpdFos.Api.Tests.Documents;

/// <summary>
/// Снимок данных РПД в ФОС (этап 7, B3; FR-DOC-19, решение 8): что берётся из содержимого РПД, в каком виде, что отбрасывается
/// как не подходящее схеме, и когда «РПД изменилась» — по данным, а не по версии. Схема ФОС — та же, по которой проверяется
/// сохранение документа.
/// </summary>
public class FosSnapshotTests
{
    private static readonly DocumentTypeRegistry Registry = new();
    private static readonly DocumentValidator Validator = new(Registry);

    // Компетенции в порядке РПД; результаты обучения в снимок не идут; разделы заданы вразнобой и склеиваются по номеру.
    private const string Rpd = """
        {
          "competences": [
            { "code": "УК-1", "formulation": "Способен искать информацию",
              "indicators": [ { "code": "УК-1.2", "text": "Анализирует", "results": { "knows": "основы" } }, { "code": "УК-1.1", "text": "Ищет" } ] },
            { "code": "ПК-2", "formulation": "Способен проектировать", "indicators": [ { "code": "ПК-2.1", "text": "Проектирует" } ] },
            { "code": "ОПК-3", "indicators": [] }
          ],
          "thematic_plan": [
            { "section_number": 2, "semester": 2, "topic_title": "Моделирование", "competence_indicators": ["ПК-2.1"],
              "current_control_forms": ["Тест"] },
            { "section_number": 1, "semester": 1, "topic_title": "Введение", "competence_indicators": ["УК-1.2", "УК-1.1"],
              "current_control_forms": ["Контрольная работа", "Тест"] },
            { "section_number": 1, "semester": 2, "topic_title": "Основные понятия", "competence_indicators": ["УК-1.2"],
              "current_control_forms": ["Тест"] }
          ],
          "hours_distribution": { "semesters": [
            { "semester": 2, "control_types": ["course_work", "exam"] }, { "semester": 1, "control_types": ["credit"] } ] }
        }
        """;

    private static JsonObject Take(string rpd = Rpd) => FosSnapshot.Take(JsonNode.Parse(rpd));

    [Fact]
    public void Snapshot_has_competences_sections_and_attestation_in_the_shape_of_the_schema()
    {
        var expected = JsonNode.Parse("""
            {
              "competences": [
                { "code": "УК-1", "formulation": "Способен искать информацию",
                  "indicators": [ { "code": "УК-1.2", "text": "Анализирует" }, { "code": "УК-1.1", "text": "Ищет" } ] },
                { "code": "ПК-2", "formulation": "Способен проектировать", "indicators": [ { "code": "ПК-2.1", "text": "Проектирует" } ] },
                { "code": "ОПК-3", "indicators": [] }
              ],
              "sections": [
                { "number": 1, "title": "Введение, Основные понятия", "semesters": [1, 2], "competence_codes": ["УК-1"],
                  "indicator_codes": ["УК-1.1", "УК-1.2"], "current_control_forms": ["Контрольная работа", "Тест"] },
                { "number": 2, "title": "Моделирование", "semesters": [2], "competence_codes": ["ПК-2"],
                  "indicator_codes": ["ПК-2.1"], "current_control_forms": ["Тест"] }
              ],
              "semesters": [ { "number": 1, "control_types": ["credit"] }, { "number": 2, "control_types": ["exam", "course_work"] } ]
            }
            """)!;

        var snapshot = Take();

        Assert.True(JsonNode.DeepEquals(expected, snapshot), snapshot.ToJsonString());
    }

    [Fact]
    public void Applied_snapshot_passes_the_schema_and_lists_the_indicators_of_all_competences()
    {
        var fos = new JsonObject();
        FosSnapshot.Apply(fos, Guid.Parse("7f0c1a5e-3b3f-4a64-9d0e-6a1f2b6c7d10"), 4, DateTimeOffset.Parse("2026-11-21T10:00:00+03:00"), Take());

        var schema = Registry.Get(DocumentTypeRegistry.Fos).SchemaJson;
        var errors = Validator.Validate(schema, JsonDocument.Parse(fos.ToJsonString()).RootElement, ValidationMode.Submit)
            .Where(e => e.Path.StartsWith("/linked_rpd") || e.Path.StartsWith("/competence_indicators")).ToList();
        Assert.Empty(errors);

        // Связь: РПД, версия и время снимка; список индикаторов — по компетенциям РПД в её порядке.
        Assert.Equal("7f0c1a5e-3b3f-4a64-9d0e-6a1f2b6c7d10", (string)fos["linked_rpd"]!["rpd_document_id"]!);
        var (version, at) = FosSnapshot.Taken(fos["linked_rpd"]);
        Assert.Equal(4, version);
        Assert.Equal(DateTimeOffset.Parse("2026-11-21T07:00:00Z"), at);
        Assert.Equal(new[] { "УК-1.2", "УК-1.1", "ПК-2.1" }, fos["competence_indicators"]!.AsArray().Select(i => (string)i!["code"]!));
        Assert.Equal(Guid.Parse("7f0c1a5e-3b3f-4a64-9d0e-6a1f2b6c7d10"), FosSnapshot.RpdIdOf(fos.ToJsonString()));
    }

    [Fact]
    public void Values_the_schema_would_not_accept_stay_out_of_the_snapshot()
    {
        // Иначе каждое сохранение ФОС отказывало бы из-за чужого значения: схема проверяет документ целиком.
        var snapshot = Take("""
            {
              "competences": [
                { "code": "ОК-1", "indicators": [] },
                { "code": "УК-1", "indicators": [ { "code": "УК-1.1.1.1" }, { "code": "УК-1.1" }, { "code": "УК-1.1", "text": "повтор" } ] },
                { "code": "УК-1", "indicators": [ { "code": "УК-1.5" } ] }
              ],
              "thematic_plan": [
                { "section_number": 0, "semester": 1, "topic_title": "Нулевой" },
                { "section_number": 1, "semester": 13, "topic_title": " Тема ", "competence_indicators": ["УК-9", "ОК-1.1"], "current_control_forms": [" ", "Тест"] }
              ],
              "hours_distribution": { "semesters": [ { "semester": 0 }, { "semester": 1, "control_types": ["exam", "other", "exam"] }, { "semester": 1 } ] }
            }
            """);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            { "competences": [ { "code": "УК-1", "indicators": [ { "code": "УК-1.1" } ] } ],
              "sections": [ { "number": 1, "title": "Тема", "semesters": [], "competence_codes": [], "indicator_codes": [],
                              "current_control_forms": ["Тест"] } ],
              "semesters": [ { "number": 1, "control_types": ["exam"] } ] }
            """), snapshot), snapshot.ToJsonString());
    }

    [Fact]
    public void Rpd_without_content_gives_an_empty_snapshot()
    {
        foreach (var rpd in new[] { "{}", """{ "competences": null, "thematic_plan": [], "hours_distribution": "x" }""" })
        {
            var snapshot = Take(rpd);
            Assert.Empty(snapshot["competences"]!.AsArray());
            Assert.Empty(snapshot["sections"]!.AsArray());
            Assert.Empty(snapshot["semesters"]!.AsArray());
        }
        Assert.Empty(FosSnapshot.Indicators(Take("{}")));
    }

    [Fact]
    public void Rpd_changed_compares_data_not_version_or_the_order_of_properties()
    {
        var fos = new JsonObject();
        FosSnapshot.Apply(fos, Guid.NewGuid(), 4, DateTimeOffset.UtcNow, Take());
        // Хранилище jsonb не сохраняет порядок свойств: снимок, прочитанный из базы, выглядит иначе, но то же.
        var stored = Reversed(JsonNode.Parse(fos["linked_rpd"]!.ToJsonString())!);

        // Версия РПД выросла от смены статуса, время снимка другое — данные те же: РПД не изменилась.
        Assert.False(FosSnapshot.Differs(stored, Take()));
        var again = new JsonObject();
        FosSnapshot.Apply(again, Guid.NewGuid(), 9, DateTimeOffset.UtcNow.AddDays(3), Take());
        Assert.False(FosSnapshot.Differs(again["linked_rpd"], Take()));

        // Индикатор добавлен, тема переименована, вид аттестации сменился, раздел исчез — каждое меняет данные.
        Assert.True(FosSnapshot.Differs(stored, Take(Rpd.Replace("""{ "code": "УК-1.1", "text": "Ищет" }""",
            """{ "code": "УК-1.1", "text": "Ищет" }, { "code": "УК-1.3", "text": "Новый" }"""))));
        Assert.True(FosSnapshot.Differs(stored, Take(Rpd.Replace("Моделирование", "Имитационное моделирование"))));
        Assert.True(FosSnapshot.Differs(stored, Take(Rpd.Replace("\"credit\"", "\"graded_credit\""))));
        Assert.True(FosSnapshot.Differs(stored, Take(Rpd.Replace("\"section_number\": 2", "\"section_number\": 3"))));
        // Правки, которые снимок не наследует (часы, описание темы), его не затрагивают.
        Assert.False(FosSnapshot.Differs(stored, Take(Rpd.Replace("\"topic_title\": \"Введение\"",
            "\"topic_title\": \"Введение\", \"topic_description\": \"текст\", \"hours\": { \"lectures\": 4 }"))));
        // Снимка в ФОС нет вовсе — РПД с данными изменилась, пустая — нет.
        Assert.True(FosSnapshot.Differs(null, Take()));
        Assert.False(FosSnapshot.Differs(null, Take("{}")));
    }

    [Fact]
    public void Changes_name_added_and_removed_indicators_and_sections()
    {
        var fos = new JsonObject();
        FosSnapshot.Apply(fos, Guid.NewGuid(), 1, DateTimeOffset.UtcNow, Take());
        var changed = Take(Rpd
            .Replace("""{ "code": "ПК-2.1", "text": "Проектирует" }""", """{ "code": "ПК-2.2", "text": "Строит" }, { "code": "УК-1.10", "text": "Десятый" }""")
            .Replace("\"section_number\": 2", "\"section_number\": 4"));

        var changes = FosSnapshot.Changes(fos["linked_rpd"], changed);

        // Коды сравниваются как числа: УК-1.10 после УК-1.3, а не перед УК-1.2.
        Assert.Equal(new[] { "УК-1.10", "ПК-2.2" }, changes.AddedIndicators);
        Assert.Equal(new[] { "ПК-2.1" }, changes.RemovedIndicators);
        Assert.Equal(new[] { 4 }, changes.AddedSections);
        Assert.Equal(new[] { 2 }, changes.RemovedSections);
        Assert.True(changes.Any);
        Assert.False(FosSnapshot.Changes(fos["linked_rpd"], Take()).Any);

        var notice = FosRefresh.Notice(changes);
        Assert.Contains("Добавлены индикаторы: УК-1.10, ПК-2.2.", notice);
        Assert.Contains("Исчезли из РПД: индикаторы ПК-2.1; разделы 2.", notice);
        Assert.Contains("не удалены", notice);
    }

    [Fact]
    public void Snapshot_fields_are_read_only_for_everyone_and_are_not_plan_fields()
    {
        // linked_rpd и competence_indicators — не из учебного плана (метка «из плана» и предупреждение «отличается от плана» им
        // не нужны), но закрыты для всех: меняются только кнопкой «Обновить из РПД».
        var schema = Registry.Get(DocumentTypeRegistry.Fos).Schema;
        var plan = PlanFields.Paths(schema, Registry.FindSchema);
        var readOnly = DocumentMetadata.ReadOnly(schema, DocumentStatuses.Draft, Registry.FindSchema);

        Assert.DoesNotContain(plan, p => p.StartsWith("/linked_rpd") || p.StartsWith("/competence_indicators"));
        Assert.Contains("/linked_rpd", readOnly);
        Assert.Contains("/competence_indicators", readOnly);
        Assert.Contains(plan, p => p.StartsWith("/title_page"));
    }

    // Объект с обратным порядком свойств на всех уровнях.
    private static JsonNode Reversed(JsonNode node) => node switch
    {
        JsonObject o => new JsonObject(o.Reverse().Select(p => KeyValuePair.Create(p.Key, (JsonNode?)Reversed(p.Value!)))),
        JsonArray a => new JsonArray(a.Select(i => Reversed(i!)).ToArray()),
        _ => node.DeepClone()
    };
}
