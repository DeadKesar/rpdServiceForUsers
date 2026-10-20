using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Features.Documents;
using RpdFos.Core.Data.Entities;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>Сценарии документов на реальном плане 09.04.04: авторы, опубликованные шаблоны, дисциплины, создание.</summary>
public static class DocumentData
{
    /// <summary>Пользователь с ролью и своей записью справочника.</summary>
    public static async Task<(HttpClient Client, Employee Employee)> UserAsync(DatabaseFixture app, string role = AppRoles.Teacher)
    {
        Employee? employee = null;
        await app.WithDbAsync(async db => employee = await ImportFiles.AddMethodistAsync(db));
        return (app.CreateClientFor(role, employee!.IdentitySubject), employee);
    }

    public static async Task<DocumentTemplate> PublishedTemplateAsync(DatabaseFixture app)
    {
        var planId = await app.RealPlanIdAsync();
        DocumentTemplate? template = null;
        await app.WithDbAsync(async db => template = await TemplateData.AddPublishedAsync(db, planId));
        return template!;
    }

    public static async Task<Guid> DisciplineAsync(DatabaseFixture app, string code)
    {
        var planId = await app.RealPlanIdAsync();
        var id = Guid.Empty;
        await app.WithDbAsync(async db =>
            id = await db.Disciplines.Where(d => d.CurriculumPlanId == planId && d.Code == code).Select(d => d.Id).SingleAsync());
        return id;
    }

    public static async Task<DocumentDetails> CreateAsync(DatabaseFixture app, HttpClient client, Guid templateId, string code)
    {
        var response = await client.PostAsJsonAsync("/api/documents",
            new CreateDocumentRequest(templateId, await DisciplineAsync(app, code)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DocumentDetails>())!;
    }
}
