using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Features.Templates;
using RpdFos.Core.Data.Entities;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>Срез «Шаблоны» с настоящей базой: механика версий, конфликт версий, роли.</summary>
[Collection(DatabaseCollection.Name)]
public class TemplatesApiTests(DatabaseFixture app)
{
    [Fact]
    public async Task Publish_increments_version()
    {
        var client = app.CreateClientFor(AppRoles.Methodist);
        var created = await CreateTemplateAsync(client);
        Assert.Equal(1, created.Version);

        var response = await client.PostAsJsonAsync($"/api/templates/{created.Id}/publish",
            new PublishTemplateRequest(created.Version));

        response.EnsureSuccessStatusCode();
        var published = await response.Content.ReadFromJsonAsync<TemplateListItem>();
        Assert.Equal(TemplateStatuses.Published, published!.Status);
        Assert.Equal(2, published.Version);

        // Версию увеличил перехватчик сохранения — в базе то же, что в ответе.
        await app.WithDbAsync(async db =>
            Assert.Equal(2, (await db.DocumentTemplates.SingleAsync(t => t.Id == created.Id)).Version));
    }

    [Fact]
    public async Task Publish_with_stale_version_returns_409()
    {
        var client = app.CreateClientFor(AppRoles.Methodist);
        var created = await CreateTemplateAsync(client);

        // Другая сессия изменила шаблон после того, как клиент его прочитал: версия 1 -> 2.
        await app.WithDbAsync(async db =>
        {
            var t = await db.DocumentTemplates.SingleAsync(x => x.Id == created.Id);
            t.Name += " (изменён)";
            await db.SaveChangesAsync();
        });

        var response = await client.PostAsJsonAsync($"/api/templates/{created.Id}/publish",
            new PublishTemplateRequest(created.Version));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("Конфликт версий", problem!.Title);

        // Публикация не прошла: статус и версия — от другой сессии.
        await app.WithDbAsync(async db =>
        {
            var t = await db.DocumentTemplates.SingleAsync(x => x.Id == created.Id);
            Assert.Equal(TemplateStatuses.Empty, t.Status);
            Assert.Equal(2, t.Version);
        });
    }

    [Theory]
    [InlineData(AppRoles.Teacher, HttpStatusCode.Forbidden)]
    [InlineData(AppRoles.Methodist, HttpStatusCode.Created)]
    public async Task Create_template_is_allowed_by_role(string role, HttpStatusCode expected)
    {
        var response = await app.CreateClientFor(role).PostAsJsonAsync("/api/templates",
            new CreateTemplateRequest(TemplateTypes.Rpd, $"Шаблон ({role})"));

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_request_returns_401()
    {
        var response = await app.CreateClientFor(role: null).GetAsync("/api/templates");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<TemplateListItem> CreateTemplateAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/templates",
            new CreateTemplateRequest(TemplateTypes.Rpd, "Тестовый шаблон"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TemplateListItem>())!;
    }
}
