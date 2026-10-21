using Corvus.Text.Json.Validator;
using RpdFos.Core.DocumentTypes;
using Xunit;

namespace RpdFos.Api.Tests.DocumentTypes;

/// <summary>Схемы типов документов (B1): корректность по JSON Schema 2020-12 и ссылки x-расширений.</summary>
public class DocumentTypeSchemaTests
{
    private static readonly DocumentTypeRegistry Registry = new();

    public static TheoryData<string> Types => [DocumentTypeRegistry.Rpd, DocumentTypeRegistry.Fos];

    // Метасхема 2020-12 — встроенная в валидатор, без сети. Форматы в метасхеме — аннотации (словарь
    // format-annotation): проверка format "regex" у Corvus 5.7.9 отвергает «\.», хотя для ECMA-262 это верно.
    private static readonly JsonSchema MetaSchema = JsonSchema.FromUri("https://json-schema.org/draft/2020-12/schema",
        new JsonSchema.Options(allowFileSystemAndHttpResolution: false, alwaysAssertFormat: false));

    [Theory]
    [MemberData(nameof(Types))]
    public void Schema_is_valid_json_schema_2020_12(string type)
    {
        // x-расширения — неизвестные ключевые слова: стандарт 2020-12 их допускает.
        Assert.True(MetaSchema.Validate(Registry.Get(type).SchemaJson));
        // Проверка метасхемой действует: схема с ошибкой не проходит.
        Assert.False(MetaSchema.Validate("""{ "type": "strin", "minLength": -1 }"""));
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
        // Каталог из 12 правил (8 + 2 по сверке + 2 этапа 5) целиком привязан к полям схем.
        var used = Registry.All.SelectMany(t => SchemaInspector.Extensions(t.Schema))
            .Where(e => e.Keyword == "x-validation-rule").Select(e => e.Value).ToHashSet();

        Assert.Equal(ValidationRules.Names.Order(), used.Order());
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void Approval_sheet_holds_only_author_input(string type)
    {
        // Подписантов определяют правила листа согласования шаблона, отметки согласования — document_signatures:
        // в содержимом только составители и протокол заседания кафедры (ФОС — по $ref на схему РПД).
        var sheet = SchemaInspector.SubschemaAt(Registry.Get(type).Schema, "/approval_sheet", Registry.FindSchema)!.Value;

        Assert.Equal(["department_protocol", "developers"],
            sheet.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order());
        var protocol = SchemaInspector.SubschemaAt(Registry.Get(type).Schema, "/approval_sheet/department_protocol", Registry.FindSchema)!.Value;
        Assert.Equal("approval", protocol.GetProperty("x-required-for-status").GetString());
    }
}
