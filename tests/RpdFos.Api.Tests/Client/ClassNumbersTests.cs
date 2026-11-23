using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RpdFos.Core.DocumentTypes;
using RpdFos.Web.Forms;
using RpdFos.Web.Forms.Widgets;
using RpdFos.Web.Models;
using Xunit;

namespace RpdFos.Api.Tests.Client;

/// <summary>
/// № занятия текстом и неделя семестра в форме РПД (этап 7.1): номер новой строки, поле «Неделя семестра» у тем нового шаблона.
/// Нажатие «Добавить строку» и перерисовка после него — в браузере: компонентных тестов с событиями в проекте нет; здесь —
/// правило номера и первая отрисовка виджетов на настоящей схеме РПД.
/// </summary>
public sealed class ClassNumbersTests
{
    private static readonly JsonElement Schema = new DocumentTypeRegistry().Get(DocumentTypeRegistry.Rpd).Schema;

    private static JsonArray Rows(params JsonNode?[] numbers) =>
        new(numbers.Select(n => (JsonNode)new JsonObject { ["number"] = n, ["hours"] = 2 }).ToArray());

    [Theory]
    [InlineData("1", 2)]
    [InlineData("3", 4)]
    [InlineData("1–2", 3)]
    [InlineData("1-2", 3)]
    [InlineData("4, 5", 6)]
    [InlineData("1–2, 4", 5)]
    [InlineData("9–10", 11)]
    public void Next_number_follows_the_last_number_of_the_previous_row(string previous, int expected) =>
        Assert.Equal(expected, ClassNumbers.Next(Rows(JsonValue.Create(previous))));

    [Fact]
    public void Next_number_goes_by_the_previous_row_not_by_the_largest_or_the_count()
    {
        // «1–2» — одна строка: по числу строк новая получила бы «2», а после «1» и «4, 5» — «3» вместо «6».
        Assert.Equal(3, ClassNumbers.Next(Rows(JsonValue.Create("1–2"))));
        Assert.Equal(6, ClassNumbers.Next(Rows(JsonValue.Create("1"), JsonValue.Create("4, 5"))));
        // Автор расставил номера не по порядку: за последней строкой идёт число после её номера.
        Assert.Equal(3, ClassNumbers.Next(Rows(JsonValue.Create("7"), JsonValue.Create("2"))));
    }

    [Fact]
    public void Next_number_of_a_former_template_with_integer_numbers_is_the_next_integer()
    {
        Assert.Equal(3, ClassNumbers.Next(Rows(JsonValue.Create(1), JsonValue.Create(2))));
        // Строку между ними удалили: следующий номер не повторяет последний.
        Assert.Equal(4, ClassNumbers.Next(Rows(JsonValue.Create(1), JsonValue.Create(3))));
    }

    [Fact]
    public void First_row_gets_one_and_a_row_without_a_number_looks_up_to_the_nearest_numbered()
    {
        Assert.Equal(1, ClassNumbers.Next([]));
        Assert.Equal(3, ClassNumbers.Next(Rows(JsonValue.Create("2"), null)));
        Assert.Equal(4, ClassNumbers.Next(Rows(JsonValue.Create("первая"), null, null)));
        Assert.Equal(3, ClassNumbers.Next(Rows(JsonValue.Create("первое"), JsonValue.Create("2"), JsonValue.Create("позже"))));
    }

    // ───── первая отрисовка виджетов ─────

    private static async Task<string> RenderAsync<T>(JsonElement root, JsonObject payload, JsonElement field, string key)
        where T : IComponent
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var context = new SchemaContext(root, payload, new DocumentMetadata([], [], [], [], null), () => { }, () => { });
        var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(FieldBase.Context)] = context, [nameof(FieldBase.Schema)] = field, [nameof(FieldBase.Parent)] = payload,
            [nameof(FieldBase.Key)] = key, [nameof(FieldBase.Path)] = $"/{key}"
        });
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<T>(parameters)).ToHtmlString());
        return System.Net.WebUtility.HtmlDecode(html);
    }

    private static JsonElement Field(JsonElement root, string name) => root.GetProperty("properties").GetProperty(name);

    // Схема прежнего шаблона: у темы нет недели, № занятия — целое число.
    private static JsonElement Former()
    {
        var former = JsonNode.Parse(Schema.GetRawText())!;
        former["$defs"]!["class_item"]!["properties"]!["number"] = JsonNode.Parse("""{ "type": "integer", "title": "№", "minimum": 1 }""");
        former["properties"]!["thematic_plan"]!["items"]!["properties"]!.AsObject().Remove("week");
        return JsonDocument.Parse(former.ToJsonString()).RootElement;
    }

    private static JsonObject Topics() => new()
    {
        ["thematic_plan"] = new JsonArray(new JsonObject
        {
            ["section_number"] = 1, ["semester"] = 1, ["topic_title"] = "Основы моделирования", ["week"] = "1–4", ["hours"] = new JsonObject()
        })
    };

    [Fact]
    public async Task Topic_of_a_new_template_has_the_week_field_with_the_entered_value()
    {
        var html = await RenderAsync<TopicPlanWidget>(Schema, Topics(), Field(Schema, "thematic_plan"), "thematic_plan");

        Assert.Contains("Неделя семестра", html);
        Assert.Contains("value=\"1–4\"", html);
    }

    [Fact]
    public async Task Topic_of_a_former_template_has_no_week_field()
    {
        var former = Former();
        var payload = Topics();
        ((JsonObject)payload["thematic_plan"]![0]!).Remove("week");

        var html = await RenderAsync<TopicPlanWidget>(former, payload, Field(former, "thematic_plan"), "thematic_plan");

        Assert.DoesNotContain("Неделя семестра", html);
        Assert.Contains("Основы моделирования", html);
    }

    [Fact]
    public async Task Class_table_of_a_new_template_shows_a_range_as_text_and_a_former_one_the_integer()
    {
        var text = new JsonObject { ["lab_works"] = new JsonArray(new JsonObject { ["number"] = "1–2", ["hours"] = 4 }) };
        var integer = new JsonObject { ["lab_works"] = new JsonArray(new JsonObject { ["number"] = 1, ["hours"] = 4 }) };
        var former = Former();

        var newHtml = await RenderAsync<ObjectTableWidget>(Schema, text, Field(Schema, "lab_works"), "lab_works");
        var formerHtml = await RenderAsync<ObjectTableWidget>(former, integer, Field(former, "lab_works"), "lab_works");

        Assert.Contains("type=\"text\"", newHtml);
        Assert.Contains("value=\"1–2\"", newHtml);
        Assert.Contains("type=\"number\"", formerHtml);
    }
}
