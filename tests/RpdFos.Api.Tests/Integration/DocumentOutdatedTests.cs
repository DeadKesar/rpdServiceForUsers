using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Features.Documents;
using RpdFos.Api.Features.Templates;
using RpdFos.Core.Data.Entities;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>Признак «устарел» (FR-DOC-12): публикация новой редакции шаблона помечает документы прежних редакций.</summary>
[Collection(DatabaseCollection.Name)]
public class DocumentOutdatedTests(DatabaseFixture app)
{
    [Fact]
    public async Task New_revision_marks_documents_of_previous_revisions_outdated()
    {
        var (teacher, _) = await DocumentData.UserAsync(app);
        var (methodist, _) = await DocumentData.UserAsync(app, AppRoles.Methodist);
        var template = await DocumentData.PublishedTemplateAsync(app);
        var kept = await DocumentData.CreateAsync(app, teacher, template.Id, "Б1.О.09");
        var deleted = await DocumentData.CreateAsync(app, teacher, template.Id, "Б1.О.06");
        Assert.Equal(HttpStatusCode.NoContent, (await teacher.DeleteAsync($"/api/documents/{deleted.Document.Id}?version=1")).StatusCode);

        var revision = (await (await methodist.PostAsJsonAsync($"/api/templates/{template.Id}/revisions",
            new TemplateVersionRequest(template.Version))).Content.ReadFromJsonAsync<TemplateDetails>())!;

        // Пока новая редакция не опубликована, документ не устарел.
        Assert.False((await teacher.GetFromJsonAsync<DocumentDetails>($"/api/documents/{kept.Document.Id}"))!.Document.IsOutdated);

        var published = await methodist.PostAsJsonAsync($"/api/templates/{revision.Template.Id}/publish",
            new TemplateVersionRequest(revision.Template.Version));
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);

        var document = (await teacher.GetFromJsonAsync<DocumentDetails>($"/api/documents/{kept.Document.Id}"))!.Document;
        Assert.True(document.IsOutdated);
        // Признак не меняет версию: правка в открытом редакторе не упадёт с конфликтом.
        Assert.Equal(kept.Document.Version, document.Version);
        Assert.Contains((await teacher.GetFromJsonAsync<List<DocumentListItem>>("/api/documents"))!, d => d.Id == kept.Document.Id && d.IsOutdated);
        await app.WithDbAsync(async db =>
            Assert.False((await db.Documents.IgnoreQueryFilters().SingleAsync(d => d.Id == deleted.Document.Id)).IsOutdated));

        // Документ по новой редакции — не устаревший.
        var fresh = await DocumentData.CreateAsync(app, teacher, revision.Template.Id, "Б1.О.05");
        Assert.False(fresh.Document.IsOutdated);
    }
}
