using System.Text.Json;
using RpdFos.Core.DocumentTypes;
using Xunit;

namespace RpdFos.Api.Tests.DocumentTypes;

/// <summary>
/// Схема для клиента (этап 7, B7): ссылки на схему другого типа документа заменены её содержимым. Клиент разбирает только
/// локальные «#/…», и без подстановки титульный лист и лист согласования ФОС показывались одним полем с сырым JSON.
/// </summary>
public class SchemaBundleTests
{
    private static readonly DocumentTypeRegistry Registry = new();

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // Чужая схема: у неё свои $defs, на которые ссылается подставляемый кусок.
    private static readonly JsonElement Other = Parse("""
        {
          "$id": "urn:other",
          "properties": {
            "part": { "type": "object", "title": "Часть", "properties": { "code": { "$ref": "#/$defs/code" } } },
            "loop": { "$ref": "#/$defs/loop" }
          },
          "$defs": {
            "code": { "type": "string", "title": "Код" },
            "loop": { "$ref": "#/$defs/loop" }
          }
        }
        """);

    private static JsonElement? Resolve(string id) => id == "urn:other" ? Other : null;

    private static JsonElement Bundle(string schema) => SchemaBundle.ForClient(schema, Resolve);

    [Fact]
    public void A_reference_to_another_schema_becomes_the_place_it_points_to_with_its_own_defs_resolved()
    {
        var bundled = Bundle("""{ "properties": { "x": { "$ref": "urn:other#/properties/part" } } }""");

        var part = bundled.GetProperty("properties").GetProperty("x");
        Assert.Equal("object", part.GetProperty("type").GetString());
        // «#/$defs/code» вёл в $defs чужой схемы: в основной такого определения нет, поэтому оно подставлено тоже.
        var code = part.GetProperty("properties").GetProperty("code");
        Assert.False(code.TryGetProperty("$ref", out _));
        Assert.Equal("Код", code.GetProperty("title").GetString());
    }

    [Fact]
    public void Local_references_of_the_main_schema_stay_and_what_is_written_next_to_a_reference_wins()
    {
        var bundled = Bundle("""
            {
              "properties": {
                "x": { "$ref": "urn:other#/properties/part", "title": "Свой заголовок" },
                "y": { "$ref": "#/$defs/mine" }
              },
              "$defs": { "mine": { "type": "integer" } }
            }
            """);

        var properties = bundled.GetProperty("properties");
        Assert.Equal("Свой заголовок", properties.GetProperty("x").GetProperty("title").GetString());
        Assert.Equal("#/$defs/mine", properties.GetProperty("y").GetProperty("$ref").GetString());
        Assert.Equal("integer", bundled.GetProperty("$defs").GetProperty("mine").GetProperty("type").GetString());
    }

    [Fact]
    public void A_reference_that_cannot_be_followed_is_left_as_it_is()
    {
        var bundled = Bundle("""
            { "properties": { "unknown": { "$ref": "urn:nowhere#/properties/part" }, "missing": { "$ref": "urn:other#/properties/none" } } }
            """);

        var properties = bundled.GetProperty("properties");
        Assert.Equal("urn:nowhere#/properties/part", properties.GetProperty("unknown").GetProperty("$ref").GetString());
        Assert.Equal("urn:other#/properties/none", properties.GetProperty("missing").GetProperty("$ref").GetString());
    }

    [Fact]
    public void References_that_loop_do_not_hang()
    {
        // «loop» ссылается сам на себя: подстановка останавливается на предельной глубине, а не уходит в бесконечность.
        var bundled = Bundle("""{ "properties": { "x": { "$ref": "urn:other#/properties/loop" } } }""");

        Assert.True(bundled.GetProperty("properties").TryGetProperty("x", out _));
    }

    [Fact]
    public void The_fos_title_page_and_approval_sheet_arrive_as_the_sections_of_the_rpd_schema()
    {
        var fos = Registry.Get(DocumentTypeRegistry.Fos);
        var rpd = Registry.Get(DocumentTypeRegistry.Rpd);

        var bundled = SchemaBundle.ForClient(fos.SchemaJson, Registry.FindSchema).GetProperty("properties");

        Assert.Equal(Names(rpd.Schema.GetProperty("properties").GetProperty("title_page")), Names(bundled.GetProperty("title_page")));
        var developers = bundled.GetProperty("approval_sheet").GetProperty("properties").GetProperty("developers");
        Assert.Equal("developer_picker", developers.GetProperty("x-ui-widget").GetString());
        // Остальное в схеме ФОС не тронуто: свои разделы и локальные ссылки на месте.
        Assert.Equal("fos_passport", bundled.GetProperty("passport").GetProperty("x-ui-widget").GetString());
        Assert.Empty(ForeignReferences(SchemaBundle.ForClient(fos.SchemaJson, Registry.FindSchema)));
        Assert.NotEmpty(ForeignReferences(fos.Schema));
    }

    [Fact]
    public void The_rpd_schema_has_nothing_to_replace()
    {
        var rpd = Registry.Get(DocumentTypeRegistry.Rpd);

        Assert.True(JsonElement.DeepEquals(SchemaBundle.ForClient(rpd.SchemaJson, Registry.FindSchema), rpd.Schema));
    }

    private static string[] Names(JsonElement section) =>
        section.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order().ToArray();

    /// <summary><c>$ref</c>, ведущие не в «#/…»: клиент по ним ничего не покажет.</summary>
    private static List<string> ForeignReferences(JsonElement schema)
    {
        var found = new List<string>();
        Walk(schema);
        return found;

        void Walk(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Array)
                foreach (var item in node.EnumerateArray()) Walk(item);
            else if (node.ValueKind == JsonValueKind.Object)
                foreach (var property in node.EnumerateObject())
                    if (property.Name == "$ref" && property.Value.ValueKind == JsonValueKind.String &&
                        !property.Value.GetString()!.StartsWith('#'))
                        found.Add(property.Value.GetString()!);
                    else
                        Walk(property.Value);
        }
    }
}
