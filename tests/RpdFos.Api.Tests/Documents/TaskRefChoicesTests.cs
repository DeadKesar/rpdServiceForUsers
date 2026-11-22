using System.Text.Json.Nodes;
using RpdFos.Web.Forms.Widgets;
using Xunit;

namespace RpdFos.Api.Tests.Documents;

/// <summary>
/// Выбор заданий банка для ссылки паспорта ФОС (этап 7, B6): новая ссылка собирается только на то, что есть в банке;
/// сохранённая ссылка на исчезнувшее задание называется, а не пропадает.
/// </summary>
public class TaskRefChoicesTests
{
    // «Вопросы к зачёту»: номера 1, 3, 4, 7 (с пропусками), «Тест 1»: 1, 2, «Пустая» — без заданий.
    private static readonly JsonNode Bank = JsonNode.Parse("""
        [
          { "group": "Вопросы к зачёту", "kind": "questions", "control_form": "intermediate",
            "tasks": [ { "number": 1, "text": "" }, { "number": 3, "text": "" }, { "number": 4, "text": "" }, { "number": 7, "text": "" } ] },
          { "group": "Тест 1", "kind": "tests", "control_form": "current", "tasks": [ { "number": 1, "text": "" }, { "number": 2, "text": "" } ] },
          { "group": "Пустая", "kind": "tasks", "control_form": "current", "tasks": [] }
        ]
        """)!;

    [Fact]
    public void Groups_and_numbers_come_from_the_bank()
    {
        Assert.Equal(["Вопросы к зачёту", "Тест 1", "Пустая"], TaskRefChoices.Groups(Bank));
        Assert.Equal([1, 3, 4, 7], TaskRefChoices.Numbers(Bank, "Вопросы к зачёту"));
        Assert.Empty(TaskRefChoices.Numbers(Bank, "Пустая"));
        Assert.Empty(TaskRefChoices.Numbers(Bank, "Нет такой"));
        Assert.Empty(TaskRefChoices.Groups(null));
    }

    [Fact]
    public void A_reference_is_built_for_a_whole_group_one_task_and_a_range()
    {
        var whole = TaskRefChoices.Build(Bank, "Тест 1", null, null, out var error);
        Assert.Null(error);
        Assert.Equal("""{"group":"Тест 1"}""", whole!.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

        var one = TaskRefChoices.Build(Bank, "Вопросы к зачёту", 3, null, out _)!;
        Assert.Equal((3, 3), ((int)one["from"]!, (int)one["to"]!));

        // Диапазон может пересекать пропуск в нумерации: концы — существующие задания.
        var range = TaskRefChoices.Build(Bank, "Вопросы к зачёту", 1, 7, out _)!;
        Assert.Equal((1, 7), ((int)range["from"]!, (int)range["to"]!));
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("Нет такой", null, null)]
    [InlineData("Пустая", null, null)]
    [InlineData("Вопросы к зачёту", 2, null)]
    [InlineData("Вопросы к зачёту", 1, 5)]
    [InlineData("Вопросы к зачёту", 99, 99)]
    [InlineData("Вопросы к зачёту", null, 3)]
    [InlineData("Вопросы к зачёту", 4, 3)]
    public void A_reference_to_a_missing_group_or_task_is_not_built(string? group, int? from, int? to)
    {
        var reference = TaskRefChoices.Build(Bank, group, from, to, out var error);

        Assert.Null(reference);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void A_saved_reference_is_checked_against_the_bank()
    {
        Assert.Null(TaskRefChoices.Problem(Bank, JsonNode.Parse("""{ "group": "Вопросы к зачёту", "from": 3, "to": 4 }""")));
        Assert.Null(TaskRefChoices.Problem(Bank, JsonNode.Parse("""{ "group": "Тест 1" }""")));
        Assert.Contains("Нет такой", TaskRefChoices.Problem(Bank, JsonNode.Parse("""{ "group": "Нет такой" }"""))!);
        Assert.Contains("№ 5", TaskRefChoices.Problem(Bank, JsonNode.Parse("""{ "group": "Вопросы к зачёту", "from": 3, "to": 5 }"""))!);
        Assert.Contains("нет заданий", TaskRefChoices.Problem(Bank, JsonNode.Parse("""{ "group": "Пустая" }"""))!);
        Assert.NotNull(TaskRefChoices.Problem(Bank, JsonNode.Parse("""{ "from": 1 }""")));
    }

    [Fact]
    public void References_are_described_and_duplicates_found()
    {
        Assert.Equal("Тест 1 (все)", TaskRefChoices.Describe(JsonNode.Parse("""{ "group": "Тест 1" }""")));
        Assert.Equal("Тест 1, № 2", TaskRefChoices.Describe(JsonNode.Parse("""{ "group": "Тест 1", "from": 2, "to": 2 }""")));
        Assert.Equal("Вопросы к зачёту, № 1–7", TaskRefChoices.Describe(JsonNode.Parse("""{ "group": "Вопросы к зачёту", "from": 1, "to": 7 }""")));

        var list = new JsonArray(JsonNode.Parse("""{ "group": "Тест 1", "from": 2, "to": 2 }"""), JsonNode.Parse("""{ "group": "Тест 1" }"""));
        Assert.True(TaskRefChoices.Contains(list, JsonNode.Parse("""{ "group": "Тест 1", "from": 2 }""")!.AsObject()));
        Assert.True(TaskRefChoices.Contains(list, JsonNode.Parse("""{ "group": "Тест 1" }""")!.AsObject()));
        Assert.False(TaskRefChoices.Contains(list, JsonNode.Parse("""{ "group": "Тест 1", "from": 1, "to": 2 }""")!.AsObject()));
    }
}
