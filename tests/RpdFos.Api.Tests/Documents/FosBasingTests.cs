using System.Text.Json.Nodes;
using RpdFos.Core.Documents;
using Xunit;

namespace RpdFos.Api.Tests.Documents;

/// <summary>
/// Приведение перенесённого ФОС к РПД цели (этап 7, B10): индикаторы заданий и средств и строки паспорта остаются по
/// существующим кодам, а чего в РПД цели нет — убирается и называется в списке неперенесённого.
/// </summary>
public class FosBasingTests
{
    // РПД цели: компетенции УК-1 и ПК-2, индикаторы УК-1.1 и ПК-2.1, разделы 1 и 2.
    private const string Target = """
        { "linked_rpd": {
            "competences": [ { "code": "УК-1", "indicators": [ { "code": "УК-1.1" } ] }, { "code": "ПК-2", "indicators": [ { "code": "ПК-2.1" } ] } ],
            "sections": [ { "number": 1 }, { "number": 2 } ] },
          "competence_indicators": [ { "code": "УК-1.1" }, { "code": "ПК-2.1" } ] }
        """;

    private static JsonObject Fos(string rest)
    {
        var fos = JsonNode.Parse(Target)!.AsObject();
        foreach (var (name, value) in JsonNode.Parse(rest)!.AsObject().ToList())
        {
            fos[name] = value!.DeepClone();
        }
        return fos;
    }

    [Fact]
    public void Nothing_is_dropped_when_every_code_and_section_is_in_the_target_rpd()
    {
        var fos = Fos("""
            { "task_bank": [ { "group": "Тест", "tasks": [ { "number": 1, "indicator_codes": ["УК-1.1", "ПК-2.1"] } ] } ],
              "evaluation_means": [ { "title": "Тест", "indicator_codes": ["УК-1.1"] } ],
              "passport": { "sections": [ { "section_number": 2, "competence_codes": ["УК-1"], "tasks": [] } ],
                "indicators": [ { "competence_code": "УК-1", "indicator_code": "УК-1.1", "rpd_section_numbers": [1, 2], "tasks": [] } ] } }
            """);
        var before = fos.ToJsonString();

        Assert.Empty(FosBasing.Adapt(fos));
        Assert.Equal(before, fos.ToJsonString());
    }

    [Fact]
    public void Indicators_of_tasks_and_means_that_are_absent_in_the_target_are_removed_and_named()
    {
        var fos = Fos("""
            { "task_bank": [
                { "group": "Вопросы", "tasks": [ { "number": 1, "indicator_codes": ["УК-1.1", "УК-3.1"] }, { "number": 2, "indicator_codes": ["УК-3.1", "УК-4.2"] },
                                                  { "number": 3, "indicator_codes": ["УК-5.1"] } ] },
                { "group": "Тест", "tasks": [ { "number": 1, "indicator_codes": ["ПК-2.1"] } ] } ],
              "evaluation_means": [ { "title": "Реферат", "indicator_codes": ["ПК-2.1", "ПК-9.9"] } ] }
            """);

        var notes = FosBasing.Adapt(fos);

        // Оставшиеся коды — те, что есть в РПД; пустой список индикаторов задания убирается совсем.
        var tasks = fos["task_bank"]![0]!["tasks"]!.AsArray();
        Assert.Equal(["УК-1.1"], tasks[0]!["indicator_codes"]!.AsArray().Select(c => (string)c!));
        Assert.Null(tasks[1]!["indicator_codes"]);
        Assert.Null(tasks[2]!["indicator_codes"]);
        Assert.Equal(["ПК-2.1"], fos["task_bank"]![1]!["tasks"]![0]!["indicator_codes"]!.AsArray().Select(c => (string)c!));
        Assert.Equal(["ПК-2.1"], fos["evaluation_means"]![0]!["indicator_codes"]!.AsArray().Select(c => (string)c!));

        // Список — по группе и по средству, коды по порядку: человек видит, откуда что убрано.
        Assert.Contains("Банк заданий → «Вопросы»: индикаторы УК-3.1, УК-4.2, УК-5.1 убраны из заданий — их нет в РПД", notes);
        Assert.Contains("Оценочные средства → «Реферат»: индикаторы ПК-9.9 убраны — их нет в РПД", notes);
        Assert.Equal(2, notes.Count);
    }

    [Fact]
    public void Passport_rows_follow_the_existing_codes_and_sections_and_the_rest_goes_to_the_list()
    {
        var fos = Fos("""
            { "passport": {
                "sections": [ { "section_number": 1, "competence_codes": ["УК-1", "УК-7"], "tasks": [ { "group": "Тест" } ] },
                              { "section_number": 5, "competence_codes": ["УК-1"], "tasks": [] } ],
                "indicators": [ { "competence_code": "УК-1", "indicator_code": "УК-1.1", "rpd_section_numbers": [1, 4, 7], "tasks": [ { "group": "Тест" } ] },
                                { "competence_code": "УК-7", "indicator_code": "УК-7.1", "tasks": [ { "group": "Тест" } ] } ] } }
            """);

        var notes = FosBasing.Adapt(fos);

        // Раздел 5 и индикатор УК-7.1 убраны вместе со строкой; в оставшихся строках — только существующее. Ссылки на задания не тронуты.
        var sections = fos["passport"]!["sections"]!.AsArray();
        Assert.Equal(1, Assert.Single(sections)!["section_number"]!.GetValue<int>());
        Assert.Equal(["УК-1"], sections[0]!["competence_codes"]!.AsArray().Select(c => (string)c!));
        Assert.Equal("Тест", (string)sections[0]!["tasks"]![0]!["group"]!);
        var line = Assert.Single(fos["passport"]!["indicators"]!.AsArray())!;
        Assert.Equal("УК-1.1", (string)line["indicator_code"]!);
        Assert.Equal([1], line["rpd_section_numbers"]!.AsArray().Select(n => (int)n!));
        Assert.Equal("Тест", (string)line["tasks"]![0]!["group"]!);

        Assert.Equal(
            new[]
            {
                "Паспорт → разделы: раздел 5 не перенесён — его нет в РПД",
                "Паспорт → раздел 1: компетенции УК-7 убраны — их нет в РПД",
                "Паспорт → индикатор УК-1.1: разделы РПД 4, 7 убраны — их нет в РПД",
                "Паспорт → индикаторы: строка УК-7.1 не перенесена — индикатора нет в РПД"
            }.Order(),
            notes.Order());
    }

    [Fact]
    public void A_fos_without_bank_means_and_passport_gives_nothing_to_adapt()
    {
        var fos = Fos("{}");
        Assert.Empty(FosBasing.Adapt(fos));
    }
}
