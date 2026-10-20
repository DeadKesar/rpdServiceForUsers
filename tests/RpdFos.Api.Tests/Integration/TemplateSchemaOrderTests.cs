using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RpdFos.Core.Data;
using RpdFos.Core.DocumentTypes;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Снимок схемы в шаблоне хранит порядок ключей (json, а не jsonb): по нему редактор строит разделы и поля.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class TemplateSchemaOrderTests(DatabaseFixture app)
{
    private static readonly JsonElement Rpd = new DocumentTypeRegistry().Get(DocumentTypeRegistry.Rpd).Schema;

    [Fact]
    public async Task Schema_snapshot_keeps_order_of_sections_and_fields()
    {
        Guid id = default;
        await app.WithDbAsync(async db => id = (await TemplateData.AddDraftAsync(db)).Id);

        // Чтение — новым контекстом: значение приходит из базы, а не из отслеживаемой сущности.
        await using var scope = app.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>().DocumentTemplates.AsNoTracking()
            .Where(t => t.Id == id).Select(t => t.Schema).SingleAsync();
        var schema = JsonDocument.Parse(stored).RootElement;

        Assert.Equal(Names(Rpd, "properties"), Names(schema, "properties"));
        Assert.Equal(Names(Rpd.GetProperty("properties").GetProperty("title_page"), "properties"),
            Names(schema.GetProperty("properties").GetProperty("title_page"), "properties"));
        Assert.Equal(Names(Rpd.GetProperty("$defs").GetProperty("class_item"), "properties"),
            Names(schema.GetProperty("$defs").GetProperty("class_item"), "properties"));
    }

    [Fact]
    public async Task Document_sections_follow_schema_order()
    {
        var (client, _) = await DocumentData.UserAsync(app);
        var created = await DocumentData.CreateAsync(app, client, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.В.03");

        Assert.Equal(Names(Rpd, "properties"), created.Metadata.Sections.Select(s => s.Key));
        Assert.Equal(Names(Rpd, "properties"), Names(created.Schema, "properties"));
    }

    private static List<string> Names(JsonElement node, string property) =>
        node.GetProperty(property).EnumerateObject().Select(p => p.Name).ToList();
}
