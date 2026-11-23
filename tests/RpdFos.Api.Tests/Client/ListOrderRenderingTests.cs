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
/// Кнопки «выше» и «ниже» в виджетах списков (этап 7.1): первая отрисовка виджетов на настоящей схеме РПД. Нажатия и
/// перерисовка после них — в браузере: компонентных тестов с событиями в проекте нет; здесь — что кнопки появляются там, где
/// схема нового шаблона разрешает перестановку, и не появляются у остальных списков и в закрытой для правки форме.
/// </summary>
public sealed class ListOrderRenderingTests
{
    private static readonly JsonElement Schema = new DocumentTypeRegistry().Get(DocumentTypeRegistry.Rpd).Schema;

    private static async Task<string> RenderAsync<T>(SchemaContext context, JsonElement field, JsonNode parent, string key, string path)
        where T : IComponent
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(FieldBase.Context)] = context, [nameof(FieldBase.Schema)] = field, [nameof(FieldBase.Parent)] = parent,
            [nameof(FieldBase.Key)] = key, [nameof(FieldBase.Path)] = path
        });
        // Рендерер кодирует кириллицу в атрибутах числовыми ссылками — для поиска по подписям кнопок их нужно развернуть.
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<T>(parameters)).ToHtmlString());
        return System.Net.WebUtility.HtmlDecode(html);
    }

    private static SchemaContext Context(JsonObject payload, bool locked = false) =>
        new(Schema, payload, new DocumentMetadata([], [], [], [], null), () => { }, () => { }) { Locked = locked };

    private static JsonElement Field(params string[] path)
    {
        var node = Schema;
        foreach (var name in path) node = node.GetProperty("properties").GetProperty(name);
        return node;
    }

    private static int Count(string html, string part) => html.Split(part).Length - 1;

    private static JsonObject Literature(string list, int count) => new()
    {
        ["literature"] = new JsonObject
        {
            [list] = new JsonArray(Enumerable.Range(1, count)
                .Select(i => list is "software" or "periodicals"
                    ? (JsonNode)JsonValue.Create($"Запись {i}")!
                    : new JsonObject { ["citation"] = $"Библиографическая запись номер {i} по порядку" }).ToArray())
        }
    };

    [Fact]
    public async Task String_list_has_up_on_all_but_the_first_and_down_on_all_but_the_last()
    {
        var payload = Literature("software", 3);

        var html = await RenderAsync<StringListWidget>(Context(payload), Field("literature", "software"), payload["literature"]!, "software", "/literature/software");

        Assert.Equal((2, 2), (Count(html, "title=\"Выше\""), Count(html, "title=\"Ниже\"")));
        // На месте недостающей кнопки — пустое место той же ширины, а не кнопка: строки остаются ровными.
        Assert.Equal(2, Count(html, "btn btn-outline-secondary invisible"));
        Assert.Equal(3, Count(html, "title=\"Удалить\""));
    }

    [Fact]
    public async Task Single_record_has_neither_button()
    {
        var payload = Literature("periodicals", 1);

        var html = await RenderAsync<StringListWidget>(Context(payload), Field("literature", "periodicals"), payload["literature"]!, "periodicals", "/literature/periodicals");

        Assert.Equal((0, 0), (Count(html, "title=\"Выше\""), Count(html, "title=\"Ниже\"")));
    }

    [Fact]
    public async Task Table_of_literature_has_the_same_buttons()
    {
        var payload = Literature("main", 3);

        var html = await RenderAsync<ObjectTableWidget>(Context(payload), Field("literature", "main"), payload["literature"]!, "main", "/literature/main");

        Assert.Equal((2, 2), (Count(html, "title=\"Выше\""), Count(html, "title=\"Ниже\"")));
        Assert.Equal(3, Count(html, "title=\"Удалить строку\""));
    }

    [Fact]
    public async Task List_without_the_mark_keeps_its_form()
    {
        // Схема прежнего шаблона пометки не имеет: форма та же, что была.
        var old = JsonNode.Parse(Field("literature", "software").GetRawText())!.AsObject();
        old.Remove("x-reorderable");
        var payload = Literature("software", 3);

        var html = await RenderAsync<StringListWidget>(Context(payload), JsonDocument.Parse(old.ToJsonString()).RootElement,
            payload["literature"]!, "software", "/literature/software");

        Assert.Equal(0, Count(html, "Выше") + Count(html, "Ниже"));
        Assert.Equal(3, Count(html, "title=\"Удалить\""));
    }

    [Fact]
    public async Task Class_table_is_not_reorderable()
    {
        // Занятия нумерует автор: порядок остаётся порядком добавления.
        var payload = new JsonObject
        {
            ["lab_works"] = new JsonArray(new JsonObject { ["number"] = "1", ["hours"] = 4 }, new JsonObject { ["number"] = "2", ["hours"] = 4 })
        };

        var html = await RenderAsync<ObjectTableWidget>(Context(payload), Field("lab_works"), payload, "lab_works", "/lab_works");

        Assert.Equal(0, Count(html, "Выше") + Count(html, "Ниже"));
    }

    [Fact]
    public async Task Locked_form_has_no_buttons()
    {
        var payload = Literature("software", 3);

        var html = await RenderAsync<StringListWidget>(Context(payload, locked: true), Field("literature", "software"),
            payload["literature"]!, "software", "/literature/software");

        Assert.Equal(0, Count(html, "Выше") + Count(html, "Ниже") + Count(html, "Удалить"));
    }
}
