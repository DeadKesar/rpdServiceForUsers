using System.Text.Json.Nodes;
using RpdFos.Core.Documents;
using Xunit;

namespace RpdFos.Api.Tests.Documents;

/// <summary>
/// Банк заданий ФОС (этап 7, B5): номер нового задания следующий за наибольшим, удаление оставляет пропуск, «Перенумеровать»
/// делает номера группы сплошными и переписывает ссылки паспорта на её задания. Правила — чистые функции над содержимым.
/// </summary>
public class FosBankTests
{
    private static JsonObject Payload(string numbers, string sections = "[]", string indicators = "[]") => JsonNode.Parse($$"""
        {
          "task_bank": [
            { "group": "Вопросы", "kind": "questions", "control_form": "intermediate", "tasks": {{Tasks(numbers)}} },
            { "group": "Тест 1", "kind": "tests", "section_number": 1, "control_form": "current", "tasks": {{Tasks("1, 3")}} }
          ],
          "passport": { "sections": {{sections}}, "indicators": {{indicators}} }
        }
        """)!.AsObject();

    private static string Tasks(string numbers) =>
        "[" + string.Join(",", numbers.Split(',', StringSplitOptions.TrimEntries).Select(n => $$"""{ "number": {{n}}, "text": "<p>Задание {{n}}</p>" }""")) + "]";

    private static string Section(string refs) =>
        $$"""[ { "section_number": 1, "competence_codes": ["УК-1"], "tasks": {{refs}} } ]""";

    private static int[] NumbersOf(JsonObject payload, string group) =>
        payload["task_bank"]!.AsArray().Single(b => (string)b!["group"]! == group)!["tasks"]!.AsArray()
            .Select(t => (int)t!["number"]!).ToArray();

    // Кириллица без экранирования — так читается ожидаемая строка.
    private static string RefsOf(JsonObject payload) => payload["passport"]!["sections"]![0]!["tasks"]!.ToJsonString(
        new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    [Theory]
    [InlineData("1, 2, 3", 4)]
    [InlineData("1, 3, 7", 8)]   // пропуски остаются: следующий — за наибольшим, а не в «дыру»
    [InlineData("5", 6)]
    [InlineData("", 1)]
    public void A_new_task_gets_the_number_after_the_largest(string numbers, int expected)
    {
        var tasks = new JsonArray(numbers.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(n => (JsonNode?)new JsonObject { ["number"] = int.Parse(n) }).ToArray());

        Assert.Equal(expected, FosBank.NextNumber(tasks));
        Assert.Equal(1, FosBank.NextNumber(null));
    }

    [Fact]
    public void Deleting_a_task_leaves_a_gap_and_the_next_number_does_not_reuse_it()
    {
        var payload = Payload("1, 2, 3, 4");
        var tasks = payload["task_bank"]![0]!["tasks"]!.AsArray();
        tasks.RemoveAt(1); // удалено задание № 2

        Assert.Equal(new[] { 1, 3, 4 }, NumbersOf(payload, "Вопросы"));
        Assert.Equal(5, FosBank.NextNumber(tasks));
    }

    [Fact]
    public void Renumbering_makes_the_group_continuous_and_leaves_other_groups_alone()
    {
        var payload = Payload("1, 3, 4, 7");

        var result = FosBank.Renumber(payload, "Вопросы");

        Assert.Null(result.Problem);
        Assert.Equal(new[] { 1, 2, 3, 4 }, NumbersOf(payload, "Вопросы"));
        Assert.Equal(new[] { 1, 3 }, NumbersOf(payload, "Тест 1"));
        // Задания остались при своих текстах: поменялся только номер.
        Assert.Equal("<p>Задание 7</p>", (string)payload["task_bank"]![0]!["tasks"]![3]!["text"]!);
        Assert.Equal("Задания группы «Вопросы» перенумерованы: 1–4.", result.Notice);
    }

    [Fact]
    public void Tasks_go_in_the_order_of_their_old_numbers()
    {
        var payload = Payload("4, 1, 9");

        FosBank.Renumber(payload, "Вопросы");

        var tasks = payload["task_bank"]![0]!["tasks"]!.AsArray();
        Assert.Equal(new[] { "Задание 1", "Задание 4", "Задание 9" }, tasks.Select(t => ((string)t!["text"]!)[3..^4]).ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, tasks.Select(t => (int)t!["number"]!).ToArray());
    }

    [Fact]
    public void Passport_references_follow_the_tasks_they_pointed_at()
    {
        // Были 1, 3, 4, 7; станут 1, 2, 3, 4. Ссылки: 3–4 → 2–3, одно задание 7 → 4, 1–7 → 1–4, вся группа и чужая группа — как были.
        var payload = Payload("1, 3, 4, 7", Section("""
            [ { "group": "Вопросы", "from": 3, "to": 4 }, { "group": "Вопросы", "from": 7, "to": 7 },
              { "group": "Вопросы", "from": 1, "to": 7 }, { "group": "Вопросы" }, { "group": "Тест 1", "from": 3, "to": 3 } ]
            """), indicators: """
            [ { "competence_code": "УК-1", "indicator_code": "УК-1.1", "tasks": [ { "group": "Вопросы", "from": 4, "to": 4 } ] } ]
            """);

        var result = FosBank.Renumber(payload, "Вопросы");

        Assert.Equal(
            """[{"group":"Вопросы","from":2,"to":3},{"group":"Вопросы","from":4,"to":4},{"group":"Вопросы","from":1,"to":4},{"group":"Вопросы"},{"group":"Тест 1","from":3,"to":3}]""",
            RefsOf(payload));
        Assert.Equal(3, (int)payload["passport"]!["indicators"]![0]!["tasks"]![0]!["from"]!); // старый № 4 стал № 3
        Assert.Equal(4, result.Rewritten); // 3–4, 7, 1–7 и ссылка индикатора; ссылка на всю группу и на чужую не считаются
    }

    [Fact]
    public void A_gap_in_the_old_numbering_does_not_pull_other_tasks_into_a_range()
    {
        // Были 1, 2, 3, 6, 7. Диапазон 6–7 станет 4–5; диапазон 4–5 (пропуск) не указывал ни на одно задание и после
        // перенумерации указал бы на чужие 4–5, поэтому он удаляется, а не остаётся.
        var payload = Payload("1, 2, 3, 6, 7", Section("""
            [ { "group": "Вопросы", "from": 6, "to": 7 }, { "group": "Вопросы", "from": 4, "to": 5 } ]
            """));

        var result = FosBank.Renumber(payload, "Вопросы");

        Assert.Equal("""[{"group":"Вопросы","from":4,"to":5}]""", RefsOf(payload));
        Assert.Equal(new[] { "4–5" }, result.Dropped);
        Assert.Contains("Удалены ссылки паспорта на несуществующие задания: 4–5.", result.Notice);
    }

    [Fact]
    public void An_open_ended_range_is_remapped_by_the_end_that_is_there()
    {
        var payload = Payload("2, 3, 5", Section("""[ { "group": "Вопросы", "from": 3 }, { "group": "Вопросы", "to": 3 } ]"""));

        FosBank.Renumber(payload, "Вопросы");

        Assert.Equal("""[{"group":"Вопросы","from":2},{"group":"Вопросы","to":2}]""", RefsOf(payload));
    }

    [Fact]
    public void Continuous_numbering_unknown_and_repeated_groups_are_refused_without_changes()
    {
        var continuous = Payload("1, 2, 3");
        var before = continuous.ToJsonString();
        Assert.Equal(BankRenumberProblem.AlreadyContinuous, FosBank.Renumber(continuous, "Вопросы").Problem);
        Assert.Equal(BankRenumberProblem.NoSuchGroup, FosBank.Renumber(continuous, "Нет такой").Problem);
        Assert.Equal(BankRenumberProblem.NoSuchGroup, FosBank.Renumber(continuous, "").Problem);
        Assert.Equal(before, continuous.ToJsonString());

        var repeated = Payload("1, 4");
        repeated["task_bank"]!.AsArray()[1]!["group"] = "Вопросы";
        var repeatedBefore = repeated.ToJsonString();
        Assert.Equal(BankRenumberProblem.DuplicateGroup, FosBank.Renumber(repeated, "Вопросы").Problem);
        Assert.Equal(repeatedBefore, repeated.ToJsonString());
    }

    [Fact]
    public void A_group_without_a_passport_is_renumbered()
    {
        var payload = JsonNode.Parse("""
            { "task_bank": [ { "group": "Темы", "kind": "tasks", "control_form": "current", "tasks": [ { "number": 2, "text": "а" } ] } ] }
            """)!.AsObject();

        Assert.Null(FosBank.Renumber(payload, "Темы").Problem);
        Assert.Equal(new[] { 1 }, NumbersOf(payload, "Темы"));
    }
}
