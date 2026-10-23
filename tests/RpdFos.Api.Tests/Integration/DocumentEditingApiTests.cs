using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Features.Documents;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>Редактирование и сохранение РПД (B4): разделы, версии, поля из плана, санитизация, доступ, удаление.</summary>
[Collection(DatabaseCollection.Name)]
public class DocumentEditingApiTests(DatabaseFixture app)
{
    private const string Goals =
        "<p>Сформировать у обучающихся <strong>умения</strong> строить математические и компьютерные модели систем.</p>";

    [Fact]
    public async Task Section_is_saved_and_version_grows()
    {
        var (client, _) = await DocumentData.UserAsync(app);
        var created = await DocumentData.CreateAsync(app, client, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.10");

        var first = await PatchAsync(client, created, "goals_and_objectives", 1, GoalsSection());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var saved = (await first.Content.ReadFromJsonAsync<SectionSaved>())!;
        Assert.Equal((2, DocumentStatuses.Draft, SectionFill.Complete), (saved.Version, saved.Status, saved.Section.Fill));

        // Второе автосохранение перезаписывает рабочий снимок, а не добавляет новый.
        var second = (await (await PatchAsync(client, created, "goals_and_objectives", 2, GoalsSection("Ещё одна задача дисциплины")))
            .Content.ReadFromJsonAsync<SectionSaved>())!;
        Assert.Equal(3, second.Version);
        await app.WithDbAsync(async db =>
        {
            var version = Assert.Single(await db.DocumentVersions.Where(v => v.DocumentId == created.Document.Id).ToListAsync());
            Assert.Equal((1, VersionTriggers.AutoSave), (version.VersionNumber, version.TriggerEvent));
            var payload = JsonNode.Parse((await db.Documents.SingleAsync(d => d.Id == created.Document.Id)).Payload)!;
            Assert.Equal("Ещё одна задача дисциплины", payload["goals_and_objectives"]!["objectives"]![1]!.GetValue<string>());
        });
    }

    [Fact]
    public async Task Stale_version_is_409_and_payload_is_kept()
    {
        var (client, _) = await DocumentData.UserAsync(app);
        var created = await DocumentData.CreateAsync(app, client, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.11");
        await PatchAsync(client, created, "goals_and_objectives", 1, GoalsSection());

        // Вторая вкладка видела версию 1.
        var stale = await PatchAsync(client, created, "goals_and_objectives", 1, GoalsSection("Задача из другой вкладки"));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("Конфликт версий", (await stale.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
        await app.WithDbAsync(async db => Assert.DoesNotContain("другой вкладки",
            (await db.Documents.SingleAsync(d => d.Id == created.Document.Id)).Payload));
    }

    [Fact]
    public async Task Plan_fields_cannot_be_changed()
    {
        var (client, _) = await DocumentData.UserAsync(app);
        var created = await DocumentData.CreateAsync(app, client, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.12");
        var title = JsonNode.Parse(created.Payload.GetProperty("title_page").GetRawText())!;

        // Учебный год — можно: он не из плана.
        title["academic_year"] = "2024/2025";
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(client, created, "title_page", 1, title)).StatusCode);

        title["discipline_name"] = "Другое название";
        var renamed = await PatchAsync(client, created, "title_page", 2, title);
        var hours = JsonNode.Parse(created.Payload.GetProperty("hours_distribution").GetRawText())!;
        hours["semesters"]![0]!["lectures"] = 100;
        var moreHours = await PatchAsync(client, created, "hours_distribution", 2, hours);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, renamed.StatusCode);
        Assert.Equal(["/title_page/discipline_name"], await ErrorPathsAsync(renamed));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, moreHours.StatusCode);
        Assert.Equal(["/hours_distribution/semesters"], await ErrorPathsAsync(moreHours));
    }

    [Fact]
    public async Task Rich_text_is_sanitized_and_invalid_values_are_422()
    {
        var (client, _) = await DocumentData.UserAsync(app);
        var created = await DocumentData.CreateAsync(app, client, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.13");
        var place = JsonNode.Parse(created.Payload.GetProperty("place_in_structure").GetRawText())!;
        place["prior_outcomes"] = new JsonObject { ["knows"] = "<p onclick=\"steal()\">Знания <em>математики</em></p><script>alert(1)</script>" };

        var response = await PatchAsync(client, created, "place_in_structure", 1, place);
        var invalid = await PatchAsync(client, created, "goals_and_objectives", 2, new JsonObject { ["goals"] = 123 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await app.WithDbAsync(async db =>
        {
            var payload = JsonNode.Parse((await db.Documents.SingleAsync(d => d.Id == created.Document.Id)).Payload)!;
            Assert.Equal("<p>Знания <em>математики</em></p>", payload["place_in_structure"]!["prior_outcomes"]!["knows"]!.GetValue<string>());
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal(["/goals_and_objectives/goals"], await ErrorPathsAsync(invalid));
    }

    [Fact]
    public async Task Hidden_and_unknown_sections_are_refused()
    {
        var (client, _) = await DocumentData.UserAsync(app);
        // Лабораторных у «Методологии научных исследований» в плане нет.
        var created = await DocumentData.CreateAsync(app, client, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.01");

        var hidden = await PatchAsync(client, created, "lab_works", 1, new JsonArray());
        var unknown = await PatchAsync(client, created, "no_such_section", 1, new JsonObject());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, hidden.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Save_makes_saved_draft_with_snapshot_and_edit_returns_draft()
    {
        var (client, author) = await DocumentData.UserAsync(app);
        var created = await DocumentData.CreateAsync(app, client, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.02");
        await PatchAsync(client, created, "goals_and_objectives", 1, GoalsSection());

        var saved = (await (await client.PostAsJsonAsync($"/api/documents/{created.Document.Id}/save", new DocumentVersionRequest(2)))
            .Content.ReadFromJsonAsync<DocumentSaved>())!;
        Assert.Equal((DocumentStatuses.SavedDraft, 3), (saved.Status, saved.Version));

        // Правка после «Сохранить» возвращает черновик.
        var edited = (await (await PatchAsync(client, created, "goals_and_objectives", 3, GoalsSection("Новая задача дисциплины")))
            .Content.ReadFromJsonAsync<SectionSaved>())!;
        Assert.Equal(DocumentStatuses.Draft, edited.Status);
        await client.PostAsJsonAsync($"/api/documents/{created.Document.Id}/save", new DocumentVersionRequest(edited.Version));

        await app.WithDbAsync(async db =>
        {
            // Снимки: рабочий (автосохранение), ручное сохранение, рабочий после правки, ручное сохранение.
            var versions = await db.DocumentVersions.Where(v => v.DocumentId == created.Document.Id).OrderBy(v => v.VersionNumber).ToListAsync();
            Assert.Equal([VersionTriggers.AutoSave, VersionTriggers.ManualSave, VersionTriggers.AutoSave, VersionTriggers.ManualSave],
                versions.Select(v => v.TriggerEvent));
            Assert.Contains("Новая задача дисциплины", versions[^1].Snapshot);
            Assert.Contains("\"semester\": 3", versions[^1].HoursSnapshot.Replace("\"semester\":3", "\"semester\": 3"));
            var history = await db.DocumentStatusHistory.Where(h => h.DocumentId == created.Document.Id).OrderBy(h => h.ChangedAt).ToListAsync();
            Assert.Equal([(null, DocumentStatuses.Draft), (DocumentStatuses.Draft, DocumentStatuses.SavedDraft),
                    (DocumentStatuses.SavedDraft, DocumentStatuses.Draft), (DocumentStatuses.Draft, DocumentStatuses.SavedDraft)],
                history.Select(h => (h.FromStatus, h.ToStatus)));
            Assert.All(history, h => Assert.Equal(author.Id, h.ChangedBy));
        });
    }

    [Fact]
    public async Task Teacher_sees_and_changes_only_own_documents()
    {
        var (owner, _) = await DocumentData.UserAsync(app);
        var (other, _) = await DocumentData.UserAsync(app);
        var (methodist, _) = await DocumentData.UserAsync(app, AppRoles.Methodist);
        var created = await DocumentData.CreateAsync(app, owner, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.04");
        var url = $"/api/documents/{created.Document.Id}";

        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PatchAsync(other, created, "goals_and_objectives", 1, GoalsSection())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await methodist.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(url)).StatusCode);

        // Список: преподаватель — только свои (FR-DOC-08), методист — все.
        Assert.Equal([created.Document.Id], (await owner.GetFromJsonAsync<List<DocumentListItem>>("/api/documents"))!.Select(d => d.Id));
        Assert.Empty((await other.GetFromJsonAsync<List<DocumentListItem>>("/api/documents"))!);
        var all = (await methodist.GetFromJsonAsync<List<DocumentListItem>>($"/api/documents?status=draft&planId={await app.RealPlanIdAsync()}"))!;
        var item = Assert.Single(all, d => d.Id == created.Document.Id);
        Assert.Equal(("Б1.О.04", "Черновик", 2023), (item.DisciplineCode, item.StatusTitle, item.Plan!.Year));
    }

    [Fact]
    public async Task Author_deletes_draft_softly()
    {
        var (client, _) = await DocumentData.UserAsync(app);
        var created = await DocumentData.CreateAsync(app, client, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.07");
        var url = $"/api/documents/{created.Document.Id}";

        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"{url}?version=7")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{url}?version=1")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url)).StatusCode);
        await app.WithDbAsync(async db =>
        {
            var row = await db.Documents.IgnoreQueryFilters().SingleAsync(d => d.Id == created.Document.Id);
            Assert.Equal((DocumentStatuses.Deleted, true), (row.Status, row.IsDeleted));
        });
    }

    [Fact]
    public async Task Disciplines_for_creation_show_only_real_disciplines_and_existing_documents()
    {
        var (owner, _) = await DocumentData.UserAsync(app);
        var (other, _) = await DocumentData.UserAsync(app);
        var template = await DocumentData.PublishedTemplateAsync(app);
        var created = await DocumentData.CreateAsync(app, owner, template.Id, "Б1.О.08");

        var forOwner = (await owner.GetFromJsonAsync<List<DisciplineOption>>($"/api/documents/disciplines?templateId={template.Id}"))!;
        var forOther = (await other.GetFromJsonAsync<List<DisciplineOption>>($"/api/documents/disciplines?templateId={template.Id}"))!;

        var codes = forOwner.Select(d => d.Code).ToList();
        Assert.Contains("Б1.В.ДВ.01.01", codes);
        Assert.Contains("ФТД.В.01", codes);
        Assert.DoesNotContain("Б1.О.14", codes);     // модуль
        Assert.DoesNotContain("Б1.В.ДВ.01", codes);  // группа элективов
        Assert.DoesNotContain(codes, c => c.StartsWith("Б2") || c.StartsWith("Б3"));
        Assert.Equal((created.Document.Id, true), forOwner.Where(d => d.Code == "Б1.О.08").Select(d => (d.DocumentId, d.CanOpen)).Single());
        Assert.Equal((created.Document.Id, false), forOther.Where(d => d.Code == "Б1.О.08").Select(d => (d.DocumentId, d.CanOpen)).Single());
    }

    [Fact]
    public async Task Future_publication_year_blocks_saving_old_fundamental_does_not()
    {
        var (client, _) = await DocumentData.UserAsync(app);
        var created = await DocumentData.CreateAsync(app, client, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.09");
        var year = DateTime.UtcNow.Year;
        JsonObject Literature(int mainYear, bool fundamental = false) => new()
        {
            ["main"] = new JsonArray(new JsonObject
            {
                ["citation"] = "Колмогоров А. Н. Основания теории вероятностей. — Москва : Наука, 1974. — 120 с.",
                ["year"] = mainYear, ["fundamental"] = fundamental
            }),
            ["additional"] = new JsonArray()
        };

        var future = await PatchAsync(client, created, "literature", 1, Literature(year + 1));
        var tooOld = await PatchAsync(client, created, "literature", 1, Literature(1700));
        var fundamental = await PatchAsync(client, created, "literature", 1, Literature(1974, fundamental: true));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, future.StatusCode);
        Assert.Equal(["/literature/main/0/year"], await ErrorPathsAsync(future));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooOld.StatusCode);
        Assert.Equal(["/literature/main/0/year"], await ErrorPathsAsync(tooOld));
        // Старое фундаментальное издание сохраняется и предупреждения о возрасте не даёт.
        Assert.Equal(HttpStatusCode.OK, fundamental.StatusCode);
        var section = (await fundamental.Content.ReadFromJsonAsync<SectionSaved>())!.Section;
        Assert.DoesNotContain(section.Warnings, w => w.Path.StartsWith("/literature/main"));
    }

    private static JsonObject GoalsSection(string extra = "Освоить методы имитационного моделирования") => new()
    {
        ["goals"] = Goals,
        ["objectives"] = new JsonArray("Изучить виды математических моделей", extra)
    };

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, DocumentDetails document, string section, int version, JsonNode value) =>
        client.PatchAsJsonAsync($"/api/documents/{document.Document.Id}/sections/{section}",
            new SectionUpdateRequest(version, JsonDocument.Parse(value.ToJsonString()).RootElement));

    private static async Task<List<string>> ErrorPathsAsync(HttpResponseMessage response)
    {
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>())!;
        return ((JsonElement)problem.Extensions["errors"]!).EnumerateArray().Select(e => e.GetProperty("path").GetString()!).ToList();
    }
}
