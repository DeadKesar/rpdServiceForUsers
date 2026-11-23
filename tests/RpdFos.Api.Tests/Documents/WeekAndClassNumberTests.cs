using System.Text.Json;
using System.Text.Json.Nodes;
using RpdFos.Core.Documents;
using RpdFos.Core.DocumentTypes;
using Xunit;

namespace RpdFos.Api.Tests.Documents;

/// <summary>
/// Неделя семестра у темы тематического плана и № занятия текстом (этап 7.1): схема нового шаблона принимает номер, диапазон
/// или перечень и отвергает остальное; сверка часов таблиц занятий от вида номера не зависит. Схемы прежних шаблонов этих
/// полей не получают — их снимки не менялись.
/// </summary>
public class WeekAndClassNumberTests
{
    private static readonly DocumentTypeRegistry Registry = new();
    private static readonly DocumentValidator Validator = new(Registry);
    private static readonly string Rpd = Registry.Get(DocumentTypeRegistry.Rpd).SchemaJson;

    private const string Topic = "Основы моделирования систем";

    private static IReadOnlyList<DocumentValidationError> Week(string? week, ValidationMode mode = ValidationMode.Submit)
    {
        var field = week is null ? "" : $", \"week\": \"{week}\"";
        var payload = $$"""{ "thematic_plan": [ { "section_number": 1, "semester": 1, "topic_title": "{{Topic}}", "hours": { "lectures": 4 }{{field}} } ] }""";
        return Validator.Validate(Rpd, JsonDocument.Parse(payload).RootElement, mode).Where(e => e.Path.StartsWith("/thematic_plan/0/week")).ToList();
    }

    private static IReadOnlyList<DocumentValidationError> Class(string table, string? number, ValidationMode mode = ValidationMode.Submit)
    {
        var field = number is null ? "" : $"\"number\": \"{number}\", ";
        var payload = $$"""{ "{{table}}": [ { {{field}}"title": "Исследование модели", "hours": 4 } ] }""";
        return Validator.Validate(Rpd, JsonDocument.Parse(payload).RootElement, mode).Where(e => e.Path.StartsWith($"/{table}/0/number")).ToList();
    }

    [Theory]
    [InlineData("3")]
    [InlineData("12")]
    [InlineData("1–4")]
    [InlineData("1-4")]
    [InlineData("5, 6")]
    [InlineData("5,6")]
    [InlineData("1–4, 7, 9–10")]
    [InlineData("1 – 4")]
    public void Week_takes_a_number_a_range_or_a_list(string week) => Assert.Empty(Week(week));

    [Fact]
    public void Week_is_optional()
    {
        // Без недели и с пустой строкой (клиент пустое поле не присылает, но схема его допускает) — замечаний нет.
        Assert.Empty(Week(null));
        Assert.Empty(Week(""));
    }

    [Theory]
    [InlineData("первая")]
    [InlineData("0")]
    [InlineData("100")]
    [InlineData("3.")]
    [InlineData("1–")]
    [InlineData("–4")]
    [InlineData("1 2")]
    [InlineData("3; 4")]
    [InlineData("1–4–6")]
    [InlineData("5,")]
    public void Wrong_week_is_a_field_error(string week)
    {
        var error = Assert.Single(Week(week));

        Assert.Equal("/thematic_plan/0/week", error.Path);
        Assert.StartsWith("Неверный формат. Номер недели", error.Message);
        // Ошибка формата блокирует и сохранение черновика, как у адреса литературы.
        Assert.Single(Week(week, ValidationMode.Draft));
    }

    [Theory]
    [InlineData("practical_classes")]
    [InlineData("lab_works")]
    public void Class_number_is_text_with_a_number_a_range_or_a_list(string table)
    {
        foreach (var number in new[] { "3", "1–2", "1-2", "4, 5", "4,5", "1–2, 4", "10" })
            Assert.Empty(Class(table, number));
    }

    [Theory]
    [InlineData("practical_classes", "первое")]
    [InlineData("practical_classes", "0")]
    [InlineData("practical_classes", "1.")]
    [InlineData("practical_classes", "2–")]
    [InlineData("lab_works", "первая")]
    [InlineData("lab_works", "1 2")]
    [InlineData("lab_works", "4;5")]
    public void Wrong_class_number_is_a_field_error(string table, string number)
    {
        var error = Assert.Single(Class(table, number));

        Assert.Equal($"/{table}/0/number", error.Path);
        Assert.StartsWith("Неверный формат. Номер занятия", error.Message);
        Assert.Single(Class(table, number, ValidationMode.Draft));
    }

    [Fact]
    public void Class_number_stays_required()
    {
        // Пустой номер не пропускается: как и прежде, у занятия он обязателен.
        var error = Assert.Single(Class("lab_works", null));

        Assert.Equal("/lab_works/0/number", error.Path);
        Assert.Equal("Обязательное поле не заполнено", error.Message);
        Assert.Single(Class("lab_works", ""));
    }

    [Fact]
    public void Integer_class_number_of_a_former_document_is_not_text()
    {
        // Число вместо текста новая схема отвергает: документы прежних шаблонов проверяются по своим снимкам, где номер — число.
        var payload = """{ "lab_works": [ { "number": 1, "title": "Исследование модели", "hours": 4 } ] }""";

        var errors = Validator.Validate(Rpd, JsonDocument.Parse(payload).RootElement, ValidationMode.Submit);

        Assert.Contains(errors, e => e.Path == "/lab_works/0/number" && e.Message == "Ожидается текст");
    }

    [Fact]
    public void Former_schema_snapshot_keeps_an_integer_number_and_has_no_week()
    {
        // Снимок схемы прежнего шаблона: номер — число, недели у темы нет. Проверка по нему принимает прежнее содержимое.
        var former = JsonNode.Parse(Rpd)!;
        former["$defs"]!["class_item"]!["properties"]!["number"] = JsonNode.Parse("""{ "type": "integer", "title": "№", "minimum": 1 }""");
        former["properties"]!["thematic_plan"]!["items"]!["properties"]!.AsObject().Remove("week");
        var payload = """
            { "lab_works": [ { "number": 1, "title": "Исследование модели", "hours": 4 } ],
              "thematic_plan": [ { "section_number": 1, "semester": 1, "topic_title": "Основы моделирования систем", "hours": { "lectures": 4 } } ] }
            """;

        var errors = Validator.Validate(former.ToJsonString(), JsonDocument.Parse(payload).RootElement, ValidationMode.Submit);

        Assert.DoesNotContain(errors, e => e.Path.StartsWith("/lab_works/0/number") || e.Path.StartsWith("/thematic_plan/0/week"));
    }

    [Fact]
    public void Schema_of_the_bundled_type_has_week_and_text_class_number()
    {
        var schema = JsonNode.Parse(Rpd)!;

        Assert.Equal("string", (string?)schema["properties"]!["thematic_plan"]!["items"]!["properties"]!["week"]!["type"]);
        Assert.Equal("Неделя семестра", (string?)schema["properties"]!["thematic_plan"]!["items"]!["properties"]!["week"]!["title"]);
        Assert.Equal("string", (string?)schema["$defs"]!["class_item"]!["properties"]!["number"]!["type"]);
        // Неделя необязательна, номер занятия обязателен.
        Assert.DoesNotContain("week", schema["properties"]!["thematic_plan"]!["items"]!["required"]!.AsArray().Select(n => (string?)n));
        Assert.Contains("number", schema["$defs"]!["class_item"]!["required"]!.AsArray().Select(n => (string?)n));
    }
}
