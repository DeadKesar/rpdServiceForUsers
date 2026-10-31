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

/// <summary>
/// Этап 5.2, B1: состав разделов следует за трудоёмкостью документа, виды аттестации исправляет методист,
/// принимается только документ без ошибок, исправление после приёма, ломающее документ, возвращает его автору.
/// Дисциплина — «Математическое и компьютерное моделирование» (Б1.О.10): 1 семестр, лабораторные 16 ч, экзамен.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class PlanCorrectionApiTests(DatabaseFixture app)
{
    [Fact]
    public async Task Course_project_added_under_review_shows_section_and_blocks_acceptance()
    {
        var (methodist, created, version) = await UnderReviewAsync();
        Assert.False(Section(created, "course_work").Visible);

        var added = await PatchHoursAsync(methodist, created, version, s => s["control_types"] = new JsonArray("exam", "course_project"));
        // Раздел появился пустым, вид курсового проектирования встал по видам аттестации; статус не изменился.
        Assert.Equal(DocumentStatuses.UnderReview, added.Status);
        var section = added.Sections!.Single(s => s.Key == "course_work");
        Assert.True(section.Visible);
        Assert.Equal(SectionFill.Incomplete, section.Fill);
        var details = await DetailsAsync(methodist, created);
        Assert.Equal("course_project", details.Payload.GetProperty("course_work").GetProperty("kind").GetString());
        Assert.DoesNotContain("/course_work", details.Metadata.HiddenPaths);
        // Вид — производное поле: в истории есть, но своего предупреждения и значения по плану для пометки нет.
        Assert.Contains(details.PlanCorrections!, c => c.Path == "/course_work/kind");
        Assert.DoesNotContain(details.Metadata.PlanValues.Keys, p => p.StartsWith("/course_work"));
        Assert.Empty(added.Sections!.Single(s => s.Key == "course_work").Warnings);

        var accept = await methodist.PostAsJsonAsync(Url(created, "accept"), new DocumentVersionRequest(added.Version));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, accept.StatusCode);
        var problem = (await accept.Content.ReadFromJsonAsync<ProblemDetails>())!;
        Assert.Equal("Документ не прошёл проверку", problem.Title);
        Assert.Contains(Errors(problem), e => e.GetProperty("path").GetString()!.StartsWith("/course_work"));

        // Курсовой проект убран — раздел снова скрыт и не проверяется: документ принимается.
        var removed = await PatchHoursAsync(methodist, created, added.Version, s => s["control_types"] = new JsonArray("exam"));
        Assert.Equal(SectionFill.NotApplicable, removed.Sections!.Single(s => s.Key == "course_work").Fill);
        var accepted = await methodist.PostAsJsonAsync(Url(created, "accept"), new DocumentVersionRequest(removed.Version));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task Lab_hours_zero_hides_lab_sections_and_back()
    {
        var (methodist, created, version) = await UnderReviewAsync();

        var zero = await PatchHoursAsync(methodist, created, version, s => s["lab_hours"] = 0);
        Assert.False(zero.Sections!.Single(s => s.Key == "lab_works").Visible);
        // Ответ сохранения несёт скрытые места: редактор обновляет форму без перезагрузки.
        Assert.Contains("/lab_works", zero.HiddenPaths!);
        // Контактная работа уменьшилась на те же 16 ч, ответ несёт содержимое с ней; сумма строк не сходится с з. е. — предупреждение.
        var zeroHours = zero.Payload!.Value.GetProperty("hours_distribution").GetProperty("semesters")[0];
        Assert.Equal(32, zeroHours.GetProperty("contact_hours").GetInt32());
        Assert.Contains(zero.Sections!.Single(s => s.Key == "hours_distribution").Warnings,
            w => w.Path == "/hours_distribution/semesters/0" && w.Message.Contains("контактная работа 32"));
        var hidden = (await DetailsAsync(methodist, created)).Metadata.HiddenPaths;
        Assert.Contains("/lab_works", hidden);
        Assert.Contains("/literature/guidelines/lab_works", hidden);
        // Раздел, скрытый исправлением, остаётся в документе.
        Assert.True((await DetailsAsync(methodist, created)).Payload.TryGetProperty("lab_works", out _));

        var back = await PatchHoursAsync(methodist, created, zero.Version, s => s["lab_hours"] = 16);
        Assert.Equal(SectionFill.Complete, back.Sections!.Single(s => s.Key == "lab_works").Fill);
        Assert.DoesNotContain("/lab_works", (await DetailsAsync(methodist, created)).Metadata.HiddenPaths);
        var restored = (await DetailsAsync(methodist, created)).Payload.GetProperty("hours_distribution").GetProperty("semesters")[0];
        Assert.Equal(48, restored.GetProperty("contact_hours").GetInt32());
    }

    [Fact]
    public async Task Correction_after_acceptance_without_errors_keeps_document_accepted()
    {
        var (methodist, created, version) = await AcceptedAsync();

        var saved = await PatchHoursAsync(methodist, created, version, s => s["control_types"] = new JsonArray("graded_credit"));

        Assert.Equal(DocumentStatuses.Accepted, saved.Status);
        Assert.Contains(saved.Sections!.Single(s => s.Key == "hours_distribution").Warnings,
            w => (w.Path, w.Message) == ("/hours_distribution/semesters/0/control_types", $"{PlanFields.DiffersFromPlan}: в плане Экзамен"));
    }

    [Fact]
    public async Task Correction_after_acceptance_that_breaks_document_needs_confirmation_and_returns_it()
    {
        var (methodist, created, version) = await AcceptedAsync();
        var hours = Hours(await DetailsAsync(methodist, created), s => s["control_types"] = new JsonArray("exam", "course_project"));

        // Без подтверждения — 409 с причинами, документ не изменился.
        var refused = await methodist.PatchAsJsonAsync(Url(created, "sections/hours_distribution"), Request(version, hours));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = (await refused.Content.ReadFromJsonAsync<ProblemDetails>())!;
        Assert.Contains(Errors(problem), e => e.GetProperty("path").GetString()!.StartsWith("/course_work"));
        var unchanged = await DetailsAsync(methodist, created);
        Assert.Equal((DocumentStatuses.Accepted, version), (unchanged.Document.Status, unchanged.Document.Version));
        Assert.False(unchanged.Payload.TryGetProperty("course_work", out _));

        // С подтверждением — «На доработке», замечание исправившего к 3.5, запись в истории.
        var confirmed = await methodist.PatchAsJsonAsync(Url(created, "sections/hours_distribution"), Request(version, hours, confirm: true));
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal(DocumentStatuses.Returned, (await confirmed.Content.ReadFromJsonAsync<SectionSaved>())!.Status);
        await app.WithDbAsync(async db =>
        {
            var reviewerId = await db.Documents.Where(d => d.Id == created.Document.Id)
                .Join(db.DocumentStatusHistory, d => d.Id, h => h.DocumentId, (d, h) => h)
                .Where(h => h.ToStatus == DocumentStatuses.Accepted).Select(h => h.ChangedBy).SingleAsync();
            var comments = await db.DocumentComments.Where(c => c.DocumentId == created.Document.Id).ToListAsync();
            var comment = Assert.Single(comments);
            Assert.StartsWith("/course_work", comment.FieldPath);
            Assert.Equal(reviewerId, comment.AuthorId);
            Assert.False(comment.IsResolved);
            var last = await db.DocumentStatusHistory.Where(h => h.DocumentId == created.Document.Id)
                .OrderByDescending(h => h.ChangedAt).FirstAsync();
            Assert.Equal((DocumentStatuses.Accepted, DocumentStatuses.Returned, DocumentEditing.ReturnedAfterPlanCorrection),
                (last.FromStatus, last.ToStatus, last.Comment));
        });
    }

    [Fact]
    public async Task Zeroed_labs_after_acceptance_break_topic_hours_and_return_with_confirmation()
    {
        var (methodist, created, version) = await AcceptedAsync();
        var hours = Hours(await DetailsAsync(methodist, created), s => s["lab_hours"] = 0);

        var refused = await methodist.PatchAsJsonAsync(Url(created, "sections/hours_distribution"), Request(version, hours));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains(Errors((await refused.Content.ReadFromJsonAsync<ProblemDetails>())!),
            e => e.GetProperty("path").GetString()!.StartsWith("/thematic_plan"));

        var confirmed = await methodist.PatchAsJsonAsync(Url(created, "sections/hours_distribution"), Request(version, hours, confirm: true));
        var saved = (await confirmed.Content.ReadFromJsonAsync<SectionSaved>())!;
        Assert.Equal(DocumentStatuses.Returned, saved.Status);
        Assert.False(saved.Sections!.Single(s => s.Key == "lab_works").Visible);
    }

    [Fact]
    public async Task Author_cannot_change_attestation_types()
    {
        var (author, _) = await DocumentData.UserAsync(app);
        var created = await DocumentData.CreateAsync(app, author, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.10");
        var hours = Hours(created, s => s["control_types"] = new JsonArray("exam", "course_project"));

        var response = await author.PatchAsJsonAsync(Url(created, "sections/hours_distribution"), Request(created.Document.Version, hours));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    private async Task<(HttpClient Methodist, DocumentDetails Created, int Version)> UnderReviewAsync()
    {
        var (author, _) = await DocumentData.UserAsync(app);
        var (methodist, _) = await DocumentData.UserAsync(app, AppRoles.Methodist);
        var created = await DocumentData.CreateAsync(app, author, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.10");
        var version = await DocumentData.FillForSubmissionAsync(author, created);
        version = await StatusAsync(author.PostAsJsonAsync(Url(created, "submit"), new DocumentVersionRequest(version)));
        return (methodist, (await DetailsAsync(methodist, created)), version);
    }

    private async Task<(HttpClient Methodist, DocumentDetails Created, int Version)> AcceptedAsync()
    {
        var (methodist, created, version) = await UnderReviewAsync();
        version = await StatusAsync(methodist.PostAsJsonAsync(Url(created, "accept"), new DocumentVersionRequest(version)));
        return (methodist, created, version);
    }

    private static async Task<SectionSaved> PatchHoursAsync(HttpClient client, DocumentDetails document, int version, Action<JsonObject> change) =>
        await DocumentData.PatchAsync(client, document.Document.Id, "hours_distribution", version,
            Hours(await DetailsAsync(client, document), change));

    // Трудоёмкость документа с изменением единственного семестра.
    private static JsonNode Hours(DocumentDetails document, Action<JsonObject> change)
    {
        var hours = JsonNode.Parse(document.Payload.GetProperty("hours_distribution").GetRawText())!;
        change(hours["semesters"]![0]!.AsObject());
        return hours;
    }

    private static async Task<DocumentDetails> DetailsAsync(HttpClient client, DocumentDetails document) =>
        (await client.GetFromJsonAsync<DocumentDetails>(Url(document, "")))!;

    private static SectionState Section(DocumentDetails document, string key) => document.Metadata.Sections.Single(s => s.Key == key);

    private static SectionUpdateRequest Request(int version, JsonNode value, bool confirm = false) =>
        new(version, JsonDocument.Parse(value.ToJsonString()).RootElement, confirm);

    private static string Url(DocumentDetails document, string action) =>
        $"/api/documents/{document.Document.Id}" + (action.Length > 0 ? $"/{action}" : "");

    private static IEnumerable<JsonElement> Errors(ProblemDetails problem) =>
        ((JsonElement)problem.Extensions["errors"]!).EnumerateArray();

    private static async Task<int> StatusAsync(Task<HttpResponseMessage> request)
    {
        var response = await request;
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<DocumentReviewed>())!.Version;
    }
}
