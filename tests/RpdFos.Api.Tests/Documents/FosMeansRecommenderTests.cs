using System.Text.Json;
using System.Text.Json.Nodes;
using RpdFos.Core.Documents;
using RpdFos.Core.DocumentTypes;
using Xunit;

namespace RpdFos.Api.Tests.Documents;

/// <summary>
/// Подбор типовых оценочных средств (этап 7, B6; FR-VAL-08): три входа — форма текущего контроля, вид занятий и вид аттестации
/// разделов, где формируется индикатор. Подбор только добавляет: индикаторам, у которых средство уже есть, ничего не предлагается.
/// </summary>
public class FosMeansRecommenderTests
{
    // УК-1.1 и УК-1.2 — в разделе 1 (семестр 1: зачёт), ПК-2.1 — в разделе 2 (семестр 2: экзамен и курсовая работа),
    // ОПК-3.1 нет ни в одном разделе.
    private const string Link = """
        {
          "competences": [
            { "code": "УК-1", "indicators": [ { "code": "УК-1.1" }, { "code": "УК-1.2" } ] },
            { "code": "ПК-2", "indicators": [ { "code": "ПК-2.1" } ] },
            { "code": "ОПК-3", "indicators": [ { "code": "ОПК-3.1" } ] }
          ],
          "sections": [
            { "number": 1, "semesters": [1], "indicator_codes": ["УК-1.1", "УК-1.2"],
              "current_control_forms": ["Тестирование", "Устный опрос на семинаре", "Групповая игра"], "class_kinds": ["lecture", "lab"] },
            { "number": 2, "semesters": [2], "indicator_codes": ["ПК-2.1"],
              "current_control_forms": ["Контрольная работа"], "class_kinds": ["practice"] }
          ],
          "semesters": [ { "number": 1, "control_types": ["credit"] }, { "number": 2, "control_types": ["exam", "course_work"] } ]
        }
        """;

    private static readonly JsonNode Snapshot = JsonNode.Parse(Link)!;

    private static Dictionary<string, string[]> ByTitle(MeansSuggestion suggestion) =>
        suggestion.Rows.ToDictionary(r => (string)r["title"]!, r => r["indicator_codes"]!.AsArray().Select(c => (string)c!).ToArray());

    [Fact]
    public void Indicators_get_means_from_control_forms_class_kinds_and_attestation_of_their_sections()
    {
        var result = FosMeansRecommender.Suggest(Snapshot, []);
        var rows = ByTitle(result);

        // Формы контроля: «Тестирование» и «Устный опрос на семинаре» узнаны, «Групповая игра» — нет.
        Assert.Equal(["УК-1.1", "УК-1.2"], rows["Тестирование"]);
        Assert.Equal(["УК-1.1", "УК-1.2"], rows["Устный опрос"]);
        Assert.Equal(["Групповая игра"], result.UnmatchedForms);
        // Вид занятий — запасной вход: лекции дали бы тот же устный опрос (тип уже есть), лабораторные — отчёт.
        Assert.Equal(["УК-1.1", "УК-1.2"], rows["Отчёт по лабораторной работе"]);
        Assert.Equal(["ПК-2.1"], rows["Контрольная работа"]);
        Assert.Equal(["ПК-2.1"], rows["Практическое задание"]);
        // Аттестация по семестрам раздела.
        Assert.Equal(["ПК-2.1", "ОПК-3.1"], rows["Вопросы к экзамену"]);
        Assert.Equal(["ПК-2.1", "ОПК-3.1"], rows["Курсовая работа"]);
        // Индикатор вне разделов — аттестация всех семестров РПД; зачёт у него общий с индикаторами раздела 1.
        Assert.Equal(["УК-1.1", "УК-1.2", "ОПК-3.1"], rows["Вопросы к зачёту"]);
        Assert.Equal(4, result.Indicators);
        Assert.Empty(result.WithoutSource);
    }

    [Fact]
    public void Rows_are_drafts_with_type_title_description_and_indicators_but_without_criteria()
    {
        var rows = FosMeansRecommender.Suggest(Snapshot, []).Rows;

        Assert.All(rows, r =>
        {
            Assert.NotEmpty((string)r["means_type"]!);
            Assert.NotEmpty((string)r["title"]!);
            Assert.NotEmpty((string)r["description"]!);
            Assert.NotEmpty(r["indicator_codes"]!.AsArray());
            Assert.Null(r["criteria"]);
        });
        Assert.Contains(rows, r => (string)r["means_type"]! == "test");
    }

    [Fact]
    public void Indicators_that_already_have_means_get_nothing_and_the_rest_are_still_suggested()
    {
        var result = FosMeansRecommender.Suggest(Snapshot, ["УК-1.1", "УК-1.2"]);
        var all = result.Rows.SelectMany(r => r["indicator_codes"]!.AsArray().Select(c => (string)c!)).ToHashSet();

        Assert.DoesNotContain("УК-1.1", all);
        Assert.DoesNotContain("УК-1.2", all);
        Assert.Contains("ПК-2.1", all);
        Assert.Equal(2, result.Indicators);
        // Формы раздела 1 здесь никому не нужны — о них подбор не жалуется.
        Assert.Empty(result.UnmatchedForms);
    }

    [Fact]
    public void Nothing_is_suggested_when_every_indicator_is_covered()
    {
        var result = FosMeansRecommender.Suggest(Snapshot, ["УК-1.1", "УК-1.2", "ПК-2.1", "ОПК-3.1"]);

        Assert.Empty(result.Rows);
        Assert.Contains("предлагать нечего", result.Notice);
    }

    [Fact]
    public void An_indicator_with_no_sections_and_no_attestation_is_reported_not_guessed()
    {
        var bare = JsonNode.Parse("""{ "competences": [ { "code": "УК-1", "indicators": [ { "code": "УК-1.1" } ] } ], "sections": [], "semesters": [] }""");

        var result = FosMeansRecommender.Suggest(bare, []);

        Assert.Empty(result.Rows);
        Assert.Equal(["УК-1.1"], result.WithoutSource);
        Assert.Contains("УК-1.1", result.Notice);
    }

    [Fact]
    public void Missing_snapshot_gives_an_empty_suggestion()
    {
        Assert.Empty(FosMeansRecommender.Suggest(null, []).Rows);
        Assert.Empty(FosMeansRecommender.Suggest(new JsonObject(), []).Rows);
    }

    [Fact]
    public void Suggested_rows_pass_the_schema_of_the_means_section()
    {
        var registry = new DocumentTypeRegistry();
        var validator = new DocumentValidator(registry);
        var rows = FosMeansRecommender.Suggest(Snapshot, []).Rows.Select(r => (JsonNode)r.DeepClone()).ToArray();
        var payload = new JsonObject { ["evaluation_means"] = new JsonArray(rows) };

        var errors = validator.Validate(registry.Get(DocumentTypeRegistry.Fos).SchemaJson, JsonDocument.Parse(payload.ToJsonString()).RootElement, ValidationMode.Submit)
            .Where(e => e.Path.StartsWith("/evaluation_means")).ToList();

        Assert.Empty(errors);
    }

    [Fact]
    public void Every_type_of_the_schema_has_a_russian_title_and_every_mapped_type_is_in_the_schema()
    {
        var registry = new DocumentTypeRegistry();
        var type = registry.Get(DocumentTypeRegistry.Fos).Schema.GetProperty("properties").GetProperty("evaluation_means")
            .GetProperty("items").GetProperty("properties").GetProperty("means_type");
        var types = type.GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToList();
        var titles = type.GetProperty("x-enum-titles").EnumerateObject().Select(p => p.Name).ToList();

        Assert.Equal(types.Order(), titles.Order());
        // Каждое соответствие подбора даёт тип, который схема примет.
        using var stream = typeof(DocumentTypeRegistry).Assembly.GetManifestResourceStream("DocumentTypes.fos.means-suggestions.json")!;
        var map = JsonNode.Parse(stream)!;
        var mapped = map["control_forms"]!.AsArray().Select(e => (string)e!["means_type"]!)
            .Concat(map["class_kinds"]!.AsObject().Select(e => (string)e.Value!["means_type"]!))
            .Concat(map["attestation"]!.AsObject().Select(e => (string)e.Value!["means_type"]!));
        Assert.All(mapped, m => Assert.Contains(m, types));
    }
}
