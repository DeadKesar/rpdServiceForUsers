using System.Text.Json;
using Json.Schema;
using RpdFos.Core.DocumentTypes;
using Xunit;

namespace RpdFos.Api.Tests.DocumentTypes;

/// <summary>Схемы типов документов (B1): корректность по JSON Schema 2020-12 и ссылки x-расширений.</summary>
public class DocumentTypeSchemaTests
{
    private static readonly DocumentTypeRegistry Registry = new();

    public static TheoryData<string> Types => [DocumentTypeRegistry.Rpd, DocumentTypeRegistry.Fos];

    [Theory]
    [MemberData(nameof(Types))]
    public void Schema_is_valid_json_schema_2020_12(string type)
    {
        // x-расширения — неизвестные ключевые слова: стандарт 2020-12 их допускает.
        var result = MetaSchemas.Draft202012.Evaluate(Registry.Get(type).Schema,
            new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(result.IsValid, string.Join("; ",
            (result.Details ?? []).Where(d => d.Errors is not null).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))));
    }

    [Fact]
    public void Fos_references_to_rpd_resolve()
    {
        // ФОС берёт титул и лист согласования из схемы РПД по $id.
        var options = new BuildOptions { SchemaRegistry = new SchemaRegistry() };
        options.SchemaRegistry.Register(JsonSchema.FromText(Registry.Get(DocumentTypeRegistry.Rpd).SchemaJson, options));
        var fos = JsonSchema.FromText(Registry.Get(DocumentTypeRegistry.Fos).SchemaJson, options);

        var instance = JsonDocument.Parse("""{ "title_page": { "discipline_name": "Дискретная математика" } }""").RootElement;
        var result = fos.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List });

        // Ошибки — про обязательные поля титула из схемы РПД: значит, ссылка разрешилась.
        Assert.False(result.IsValid);
        Assert.Contains(result.Details ?? [], d => d.InstanceLocation.ToString() == "/title_page" && d.Errors?.ContainsKey("required") == true);
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void Every_source_is_known(string type)
    {
        var unknown = SchemaInspector.Extensions(Registry.Get(type).Schema)
            .Where(e => e.Keyword is "x-source" or "x-visible-if" && !DocumentSources.IsKnown(e.Value))
            .Select(e => $"{e.Location}: {e.Keyword} = {e.Value}");

        Assert.Empty(unknown);
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void Every_validation_rule_is_in_catalog(string type)
    {
        var extensions = SchemaInspector.Extensions(Registry.Get(type).Schema);
        var unknown = extensions.Where(e => e.Keyword == "x-validation-rule" && !ValidationRules.Names.Contains(e.Value))
            .Select(e => $"{e.Location}: {e.Value}");

        Assert.Empty(unknown);
        Assert.Contains(extensions, e => e.Keyword == "x-validation-rule"); // правила в схеме есть
    }

    [Fact]
    public void Rpd_and_fos_use_every_rule_of_the_catalog()
    {
        // Каталог из 10 правил (8 + 2 по сверке) целиком привязан к полям схем.
        var used = Registry.All.SelectMany(t => SchemaInspector.Extensions(t.Schema))
            .Where(e => e.Keyword == "x-validation-rule").Select(e => e.Value).ToHashSet();

        Assert.Equal(ValidationRules.Names.Order(), used.Order());
    }
}
