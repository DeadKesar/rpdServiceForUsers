using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Features.Plans;
using RpdFos.Api.Features.Templates;
using RpdFos.Api.Tests.DocumentTypes;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.DocumentTypes;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>Срез «Шаблоны» с настоящей базой: жизненный цикл, проверка перед публикацией, редакции, версии, роли.</summary>
[Collection(DatabaseCollection.Name)]
public class TemplatesApiTests(DatabaseFixture app)
{
    [Theory]
    [InlineData(DocumentTypeRegistry.Rpd, DocxBookmarksTests.Rpd, 57)]
    [InlineData(DocumentTypeRegistry.Fos, DocxBookmarksTests.Fos, 24)]
    public async Task Methodist_creates_uploads_print_template_and_publishes(string type, string file, int bookmarks)
    {
        var (client, plan) = await MethodistWithPlanAsync();

        var created = await CreateAsync(client, type, plan.Id);
        Assert.Equal((TemplateStatuses.Empty, 1, created.Template.Id), (created.Template.Status, created.Template.Revision, created.Template.LineageId));
        Assert.Equal(plan.Id, created.Template.Plan!.Id);
        Assert.Contains("Не загружен DOCX-шаблон оформления.", created.Errors);

        var draft = await UploadAsync(client, created, DocxBookmarksTests.Template(file), file);
        Assert.Equal((TemplateStatuses.Draft, file), (draft.Template.Status, draft.PrintTemplateName));
        Assert.Equal(bookmarks, draft.Bookmarks.Count);
        Assert.All(draft.Bookmarks, b => Assert.NotNull(b.Target));
        Assert.Empty(draft.Errors);
        Assert.True(draft.CanPublish);
        // Категория подписанта — с русским названием, код остаётся для подсказки.
        var head = Assert.Single(draft.Signatures, s => s.Role == SignatoryRoles.DepartmentHead);
        Assert.Equal("Заведующий кафедрой", head.RoleTitle);

        var saved = await PostAsync(client, $"/api/templates/{draft.Template.Id}/save", draft.Template.Version);
        Assert.Equal(TemplateStatuses.SavedDraft, saved.Template.Status);

        var published = await PostAsync(client, $"/api/templates/{saved.Template.Id}/publish", saved.Template.Version);
        Assert.Equal(TemplateStatuses.Published, published.Template.Status);
        Assert.NotNull(published.Template.PublishedAt);

        var list = await client.GetFromJsonAsync<List<TemplateItem>>($"/api/templates?type={type}&status=published&planId={plan.Id}");
        Assert.Equal(published.Template.Id, Assert.Single(list!).Id);
    }

    [Fact]
    public async Task Publish_lists_unmapped_bookmarks_with_422()
    {
        // В шаблон РПД загружено оформление ФОС: закладок ФОС нет в сопоставлении РПД.
        var (client, plan) = await MethodistWithPlanAsync();
        var created = await CreateAsync(client, DocumentTypeRegistry.Rpd, plan.Id);
        var draft = await UploadAsync(client, created, DocxBookmarksTests.Template(DocxBookmarksTests.Fos), DocxBookmarksTests.Fos);
        Assert.Contains(draft.Bookmarks, b => b.BaseName == "AutofillCurs" && b.Target is null);

        var response = await client.PostAsJsonAsync($"/api/templates/{draft.Template.Id}/publish",
            new TemplateVersionRequest(draft.Template.Version));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Extensions["errors"];
        Assert.Contains("Закладка AutofillCurs не сопоставлена",
            ((JsonElement)errors!).EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(TemplateStatuses.Draft, (await GetAsync(client, draft.Template.Id)).Template.Status);
    }

    [Fact]
    public async Task Template_without_print_template_cannot_be_published()
    {
        var (client, plan) = await MethodistWithPlanAsync();
        var created = await CreateAsync(client, DocumentTypeRegistry.Rpd, plan.Id);

        var response = await client.PostAsJsonAsync($"/api/templates/{created.Template.Id}/publish",
            new TemplateVersionRequest(created.Template.Version));

        // Из «Пустого» публиковать нельзя (правило этапа 0 обновлено); причина видна и в проверке.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(["Не загружен DOCX-шаблон оформления."], (await GetAsync(client, created.Template.Id)).Errors);
    }

    [Theory]
    [InlineData("plan.xlsx", "PK")]
    [InlineData("broken.docx", "не документ Word")]
    public async Task Upload_rejects_non_docx_files(string fileName, string content)
    {
        var (client, plan) = await MethodistWithPlanAsync();
        var created = await CreateAsync(client, DocumentTypeRegistry.Rpd, plan.Id);

        var response = await client.PostAsync($"/api/templates/{created.Template.Id}/print-template",
            Form(System.Text.Encoding.UTF8.GetBytes(content), fileName, created.Template.Version));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(TemplateStatuses.Empty, (await GetAsync(client, created.Template.Id)).Template.Status);
    }

    [Fact]
    public async Task New_revision_archives_previous_when_published()
    {
        var (client, plan) = await MethodistWithPlanAsync();
        var first = await PublishedAsync(client, plan.Id);

        var response = await client.PostAsJsonAsync($"/api/templates/{first.Template.Id}/revisions",
            new TemplateVersionRequest(first.Template.Version));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var second = (await response.Content.ReadFromJsonAsync<TemplateDetails>())!;
        Assert.Equal((first.Template.LineageId, 2, TemplateStatuses.Draft), (second.Template.LineageId, second.Template.Revision, second.Template.Status));
        Assert.Equal((first.PrintTemplateName, first.Bookmarks.Count), (second.PrintTemplateName, second.Bookmarks.Count));
        Assert.Empty(second.Errors);

        // Прежняя редакция опубликована до публикации новой; вторую новую редакцию параллельно не создать.
        Assert.Equal(TemplateStatuses.Published, (await GetAsync(client, first.Template.Id)).Template.Status);
        var again = await client.PostAsJsonAsync($"/api/templates/{first.Template.Id}/revisions",
            new TemplateVersionRequest(first.Template.Version));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        var published = await PostAsync(client, $"/api/templates/{second.Template.Id}/publish", second.Template.Version);

        Assert.Equal(TemplateStatuses.Published, published.Template.Status);
        Assert.Equal(TemplateStatuses.Archived, (await GetAsync(client, first.Template.Id)).Template.Status);
    }

    [Fact]
    public async Task Revision_number_skips_deleted_revisions()
    {
        var (client, plan) = await MethodistWithPlanAsync();
        var first = await PublishedAsync(client, plan.Id);
        var url = $"/api/templates/{first.Template.Id}/revisions";

        var second = (await (await client.PostAsJsonAsync(url, new TemplateVersionRequest(first.Template.Version)))
            .Content.ReadFromJsonAsync<TemplateDetails>())!;
        var deleted = await client.DeleteAsync($"/api/templates/{second.Template.Id}?version={second.Template.Version}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/templates/{second.Template.Id}")).StatusCode);

        var third = (await (await client.PostAsJsonAsync(url, new TemplateVersionRequest(first.Template.Version)))
            .Content.ReadFromJsonAsync<TemplateDetails>())!;

        Assert.Equal(3, third.Template.Revision);
        // Удаление — мягкое: статус и флаг вместе, запись остаётся.
        await app.WithDbAsync(async db =>
        {
            var row = await db.DocumentTemplates.IgnoreQueryFilters().SingleAsync(t => t.Id == second.Template.Id);
            Assert.Equal((TemplateStatuses.Deleted, true), (row.Status, row.IsDeleted));
        });
    }

    [Fact]
    public async Task Archive_and_delete_follow_status_rules()
    {
        var (client, plan) = await MethodistWithPlanAsync();
        var published = await PublishedAsync(client, plan.Id);

        var deletePublished = await client.DeleteAsync($"/api/templates/{published.Template.Id}?version={published.Template.Version}");
        Assert.Equal(HttpStatusCode.Conflict, deletePublished.StatusCode);

        var archived = await PostAsync(client, $"/api/templates/{published.Template.Id}/archive", published.Template.Version);
        Assert.Equal(TemplateStatuses.Archived, archived.Template.Status);

        var again = await client.PostAsJsonAsync($"/api/templates/{published.Template.Id}/archive",
            new TemplateVersionRequest(archived.Template.Version));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var problem = await again.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("Шаблон в статусе «Архивный» нельзя архивировать.", problem!.Detail);
    }

    [Fact]
    public async Task Publish_increments_version()
    {
        var client = app.CreateClientFor(AppRoles.Methodist);
        var created = await AddDraftAsync();
        Assert.Equal(1, created.Version);

        var response = await client.PostAsJsonAsync($"/api/templates/{created.Id}/publish",
            new TemplateVersionRequest(created.Version));

        response.EnsureSuccessStatusCode();
        var published = await response.Content.ReadFromJsonAsync<TemplateDetails>();
        Assert.Equal(TemplateStatuses.Published, published!.Template.Status);
        Assert.Equal(2, published.Template.Version);

        // Версию увеличил перехватчик сохранения — в базе то же, что в ответе.
        await app.WithDbAsync(async db =>
            Assert.Equal(2, (await db.DocumentTemplates.SingleAsync(t => t.Id == created.Id)).Version));
    }

    [Fact]
    public async Task Publish_with_stale_version_returns_409()
    {
        var client = app.CreateClientFor(AppRoles.Methodist);
        var created = await AddDraftAsync();

        // Другая сессия изменила шаблон после того, как клиент его прочитал: версия 1 -> 2.
        await app.WithDbAsync(async db =>
        {
            var t = await db.DocumentTemplates.SingleAsync(x => x.Id == created.Id);
            t.Name += " (изменён)";
            await db.SaveChangesAsync();
        });

        var response = await client.PostAsJsonAsync($"/api/templates/{created.Id}/publish",
            new TemplateVersionRequest(created.Version));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("Конфликт версий", problem!.Title);

        // Публикация не прошла: статус и версия — от другой сессии.
        await app.WithDbAsync(async db =>
        {
            var t = await db.DocumentTemplates.SingleAsync(x => x.Id == created.Id);
            Assert.Equal(TemplateStatuses.Draft, t.Status);
            Assert.Equal(2, t.Version);
        });
    }

    [Fact]
    public async Task Stale_version_is_conflict_for_save_and_revision()
    {
        var (client, plan) = await MethodistWithPlanAsync();
        var created = await CreateAsync(client, DocumentTypeRegistry.Rpd, plan.Id);
        var draft = await UploadAsync(client, created, DocxBookmarksTests.Template(DocxBookmarksTests.Rpd), DocxBookmarksTests.Rpd);

        var save = await client.PostAsJsonAsync($"/api/templates/{draft.Template.Id}/save", new TemplateVersionRequest(created.Template.Version));
        Assert.Equal(HttpStatusCode.Conflict, save.StatusCode);

        var published = await PostAsync(client, $"/api/templates/{draft.Template.Id}/publish", draft.Template.Version);
        var revision = await client.PostAsJsonAsync($"/api/templates/{published.Template.Id}/revisions",
            new TemplateVersionRequest(draft.Template.Version));
        Assert.Equal(HttpStatusCode.Conflict, revision.StatusCode);
        Assert.Equal("Конфликт версий", (await revision.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
    }

    [Fact]
    public async Task Create_validates_type_plan_and_directory_record()
    {
        var (client, plan) = await MethodistWithPlanAsync();

        var unknownType = await client.PostAsJsonAsync("/api/templates", new CreateTemplateRequest("syllabus", "Аннотация", plan.Id));
        var unknownPlan = await client.PostAsJsonAsync("/api/templates", new CreateTemplateRequest(TemplateTypes.Rpd, "РПД", Guid.NewGuid()));
        // Учётная запись без записи справочника: автора шаблона не определить.
        var unlinked = await app.CreateClientFor(AppRoles.Methodist, Guid.NewGuid().ToString())
            .PostAsJsonAsync("/api/templates", new CreateTemplateRequest(TemplateTypes.Rpd, "РПД", plan.Id));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownType.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownPlan.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, unlinked.StatusCode);
    }

    [Fact]
    public async Task Teacher_reads_but_cannot_change_templates()
    {
        var (_, plan) = await MethodistWithPlanAsync();
        var teacher = app.CreateClientFor(AppRoles.Teacher);

        var create = await teacher.PostAsJsonAsync("/api/templates", new CreateTemplateRequest(TemplateTypes.Rpd, "РПД", plan.Id));
        var list = await teacher.GetAsync("/api/templates");

        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }

    [Fact]
    public async Task Plans_are_listed_for_template_creation()
    {
        var (client, plan) = await MethodistWithPlanAsync();

        var plans = await client.GetFromJsonAsync<List<PlanItem>>("/api/curriculum-plans");

        Assert.Contains(plans!, p => p.Id == plan.Id && p.ProfileName == "Тестовый профиль" && p.Year == 2026);
    }

    [Fact]
    public async Task Anonymous_request_returns_401()
    {
        var response = await app.CreateClientFor(role: null).GetAsync("/api/templates");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task New_rpd_template_starts_with_university_defaults()
    {
        var (client, plan) = await MethodistWithPlanAsync();
        var created = await CreateAsync(client, DocumentTypeRegistry.Rpd, plan.Id);

        // Поля для умолчаний — по пометке схемы, в порядке схемы, с подписью «Раздел → Поле».
        Assert.Equal(
            ["/hours_distribution/self_study_kinds", "/literature/internet_resources", "/literature/guidelines", "/literature/software",
             "/material_support"],
            created.DefaultFields.Select(f => f.Pointer));
        Assert.Equal("Учебно-методическое обеспечение дисциплины → Интернет-ресурсы", created.DefaultFields[1].Title);
        // Начальные умолчания — постоянные пункты, убранные из третьей версии шаблона оформления, и стандартные виды СРС.
        var defaults = created.Defaults!.Value;
        Assert.Equal(7, defaults.GetProperty("hours_distribution").GetProperty("self_study_kinds").GetArrayLength());
        Assert.Equal(4, defaults.GetProperty("literature").GetProperty("internet_resources").GetArrayLength());
        Assert.Equal(8, defaults.GetProperty("literature").GetProperty("software").GetArrayLength());
        Assert.Equal(2, defaults.GetProperty("material_support").GetArrayLength());
        // У ФОС начальных умолчаний нет.
        Assert.Null((await CreateAsync(client, DocumentTypeRegistry.Fos, plan.Id)).Defaults);
    }

    [Fact]
    public async Task Methodist_edits_defaults_until_publication()
    {
        var (client, plan) = await MethodistWithPlanAsync();
        var created = await CreateAsync(client, DocumentTypeRegistry.Rpd, plan.Id);
        var draft = await UploadAsync(client, created, DocxBookmarksTests.Template(DocxBookmarksTests.Rpd), DocxBookmarksTests.Rpd);
        var saved = await PostAsync(client, $"/api/templates/{draft.Template.Id}/save", draft.Template.Version);

        // Пустые контейнеры от формы отбрасываются; сохранённый черновик снова становится черновиком.
        var updated = await PutDefaultsAsync(client, saved, """
            { "literature": { "software": [ "Программный пакет LibreOffice" ], "internet_resources": [] }, "hours_distribution": {} }
            """);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var details = (await updated.Content.ReadFromJsonAsync<TemplateDetails>())!;
        Assert.Equal(TemplateStatuses.Draft, details.Template.Status);
        Assert.Equal("""{"literature":{"software":["Программный пакет LibreOffice"]}}""", details.Defaults!.Value.GetRawText());

        // Поле без пометки и неполный пункт — 422 с местами.
        var refused = await PutDefaultsAsync(client, details, """
            { "goals_and_objectives": { "goals": "Цель" }, "literature": { "internet_resources": [ { "title": "Без адреса" } ] } }
            """);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var paths = (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").EnumerateArray()
            .Select(e => e.GetProperty("path").GetString()).ToList();
        Assert.Contains("/goals_and_objectives", paths);
        Assert.Contains(paths, p => p!.StartsWith("/literature/internet_resources/0"));

        // Опубликованный шаблон меняется новой редакцией — 409; редакция копирует умолчания.
        var published = await PostAsync(client, $"/api/templates/{details.Template.Id}/publish", details.Template.Version);
        Assert.Equal(HttpStatusCode.Conflict, (await PutDefaultsAsync(client, published, "{}")).StatusCode);
        var revision = await client.PostAsJsonAsync($"/api/templates/{published.Template.Id}/revisions",
            new TemplateVersionRequest(published.Template.Version));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(details.Defaults!.Value.GetRawText()),
            JsonNode.Parse((await revision.Content.ReadFromJsonAsync<TemplateDetails>())!.Defaults!.Value.GetRawText())));
    }

    private static Task<HttpResponseMessage> PutDefaultsAsync(HttpClient client, TemplateDetails template, string defaults) =>
        client.PutAsJsonAsync($"/api/templates/{template.Template.Id}/defaults",
            new TemplateDefaultsRequest(template.Template.Version, JsonDocument.Parse(defaults).RootElement));

    private async Task<(HttpClient Client, CurriculumPlan Plan)> MethodistWithPlanAsync()
    {
        Employee? methodist = null;
        CurriculumPlan? plan = null;
        await app.WithDbAsync(async db =>
        {
            methodist = await ImportFiles.AddMethodistAsync(db);
            plan = await TemplateData.AddPlanAsync(db);
        });
        return (app.CreateClientFor(AppRoles.Methodist, methodist!.IdentitySubject), plan!);
    }

    private static async Task<TemplateDetails> CreateAsync(HttpClient client, string type, Guid planId)
    {
        var response = await client.PostAsJsonAsync("/api/templates", new CreateTemplateRequest(type, $"Шаблон {type}", planId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TemplateDetails>())!;
    }

    private static async Task<TemplateDetails> UploadAsync(HttpClient client, TemplateDetails template, byte[] content, string fileName)
    {
        var response = await client.PostAsync($"/api/templates/{template.Template.Id}/print-template",
            Form(content, fileName, template.Template.Version));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TemplateDetails>())!;
    }

    private static async Task<TemplateDetails> PostAsync(HttpClient client, string url, int version)
    {
        var response = await client.PostAsJsonAsync(url, new TemplateVersionRequest(version));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TemplateDetails>())!;
    }

    private static async Task<TemplateDetails> GetAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<TemplateDetails>($"/api/templates/{id}"))!;

    private static async Task<TemplateDetails> PublishedAsync(HttpClient client, Guid planId)
    {
        var created = await CreateAsync(client, DocumentTypeRegistry.Rpd, planId);
        var draft = await UploadAsync(client, created, DocxBookmarksTests.Template(DocxBookmarksTests.Rpd), DocxBookmarksTests.Rpd);
        return await PostAsync(client, $"/api/templates/{draft.Template.Id}/publish", draft.Template.Version);
    }

    private static MultipartFormDataContent Form(byte[] content, string fileName, int version)
    {
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(ImportFiles.Docx);
        return new MultipartFormDataContent { { file, "file", fileName }, { new StringContent(version.ToString()), "version" } };
    }

    private async Task<DocumentTemplate> AddDraftAsync()
    {
        DocumentTemplate? template = null;
        await app.WithDbAsync(async db => template = await TemplateData.AddDraftAsync(db));
        return template!;
    }
}
