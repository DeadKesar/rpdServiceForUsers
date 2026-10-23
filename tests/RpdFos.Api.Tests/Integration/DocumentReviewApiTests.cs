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

/// <summary>Проверка РПД (этап 5, B1, B3): отправка с полной проверкой, замечания, возврат, доработка, приём.</summary>
[Collection(DatabaseCollection.Name)]
public class DocumentReviewApiTests(DatabaseFixture app)
{
    [Fact]
    public async Task Incomplete_document_is_not_submitted()
    {
        var (client, _) = await DocumentData.UserAsync(app);
        var created = await DocumentData.CreateAsync(app, client, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.10");

        var response = await client.PostAsJsonAsync(Url(created, "submit"), new DocumentVersionRequest(created.Document.Version));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var paths = (await ErrorsAsync(response)).Select(e => e.GetProperty("path").GetString()).ToList();
        Assert.Contains("/goals_and_objectives", paths);
        Assert.Contains("/thematic_plan", paths);
        await app.WithDbAsync(async db =>
            Assert.Equal(DocumentStatuses.Draft, (await db.Documents.SingleAsync(d => d.Id == created.Document.Id)).Status));
    }

    [Fact]
    public async Task Hours_mismatch_blocks_submission_and_old_literature_only_warns()
    {
        var (client, _) = await DocumentData.UserAsync(app);
        var created = await DocumentData.CreateAsync(app, client, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.10");
        var version = await DocumentData.FillForSubmissionAsync(client, created);
        var plan = DocumentData.Sums(created.Payload);

        // Лекций по темам на 2 часа меньше плана — ошибка правила, отправка не проходит.
        var topic = new JsonObject
        {
            ["section_number"] = 1, ["topic_title"] = "Основы моделирования систем",
            ["hours"] = new JsonObject
            {
                ["lectures"] = plan.Lectures - 2, ["practice"] = plan.Practice, ["lab_hours"] = plan.Labs, ["self_study"] = plan.SelfStudy
            }
        };
        var saved = await DocumentData.PatchAsync(client, created.Document.Id, "thematic_plan", version, new JsonArray(topic.DeepClone()));
        Assert.Equal(SectionFill.Incomplete, saved.Section.Fill);
        var refused = await client.PostAsJsonAsync(Url(created, "submit"), new DocumentVersionRequest(saved.Version));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var error = Assert.Single(await ErrorsAsync(refused));
        Assert.Equal(("/thematic_plan", IssueLevels.Error), (error.GetProperty("path").GetString(), error.GetProperty("level").GetString()));
        Assert.StartsWith("Лекции: по темам", error.GetProperty("message").GetString());

        // Часы сходятся, литература старая — предупреждение приходит, но документ отправлен.
        topic["hours"]!["lectures"] = plan.Lectures;
        version = (await DocumentData.PatchAsync(client, created.Document.Id, "thematic_plan", saved.Version, new JsonArray(topic.DeepClone()))).Version;
        var literature = new JsonObject
        {
            ["main"] = new JsonArray(new JsonObject { ["citation"] = "Сидоров С. С. Теория систем. — М. : Наука, 1995. — 200 с.", ["year"] = 1995 }),
            ["additional"] = new JsonArray()
        };
        version = (await DocumentData.PatchAsync(client, created.Document.Id, "literature", version, literature)).Version;
        var submitted = await client.PostAsJsonAsync(Url(created, "submit"), new DocumentVersionRequest(version));
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        var reviewed = (await submitted.Content.ReadFromJsonAsync<DocumentReviewed>())!;
        Assert.Equal(DocumentStatuses.UnderReview, reviewed.Status);
        Assert.Equal(("/literature/main/0", IssueLevels.Warning), (Assert.Single(reviewed.Warnings).Path, reviewed.Warnings[0].Level));
    }

    [Fact]
    public async Task Review_cycle_with_comments()
    {
        var (author, _) = await DocumentData.UserAsync(app);
        var (methodist, _) = await DocumentData.UserAsync(app, AppRoles.Methodist);
        var created = await DocumentData.CreateAsync(app, author, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.10");
        var version = await DocumentData.FillForSubmissionAsync(author, created);

        version = await ExpectAsync(author.PostAsJsonAsync(Url(created, "submit"), new DocumentVersionRequest(version)), DocumentStatuses.UnderReview);

        // На проверке документ закрыт для автора.
        var closed = await author.PatchAsJsonAsync(Url(created, "sections/goals_and_objectives"), Section(version, Goals("Правка на проверке")));
        Assert.Equal(HttpStatusCode.Conflict, closed.StatusCode);

        // Без замечаний не вернуть, пустое и чужое место — 422.
        Assert.Equal(HttpStatusCode.Conflict, (await methodist.PostAsJsonAsync(Url(created, "return"), new ReturnRequest(version, null))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await methodist.PostAsJsonAsync(Url(created, "comments"), new CommentRequest("/no_such_section", "Текст"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await methodist.PostAsJsonAsync(Url(created, "comments"), new CommentRequest("/thematic_plan/0/hours", " "))).StatusCode);

        var added = await methodist.PostAsJsonAsync(Url(created, "comments"),
            new CommentRequest("/thematic_plan/0/hours", "Разбейте часы по темам"));
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var comment = (await added.Content.ReadFromJsonAsync<CommentItem>())!;

        // С неразрешённым замечанием не принять — только вернуть.
        Assert.Equal(HttpStatusCode.Conflict, (await methodist.PostAsJsonAsync(Url(created, "accept"), new DocumentVersionRequest(version))).StatusCode);
        version = await ExpectAsync(methodist.PostAsJsonAsync(Url(created, "return"), new ReturnRequest(version, null)), DocumentStatuses.Returned);

        // Доработка: правка и «Сохранить» статус не меняют.
        var edited = await DocumentData.PatchAsync(author, created.Document.Id, "goals_and_objectives", version, Goals("Исправленная задача дисциплины"));
        Assert.Equal(DocumentStatuses.Returned, edited.Status);
        var saved = (await (await author.PostAsJsonAsync(Url(created, "save"), new DocumentVersionRequest(edited.Version)))
            .Content.ReadFromJsonAsync<DocumentSaved>())!;
        Assert.Equal(DocumentStatuses.Returned, saved.Status);

        // Замечание видит автор и отмечает разрешённым — один раз.
        var comments = (await author.GetFromJsonAsync<List<CommentItem>>(Url(created, "comments")))!;
        Assert.Equal(("/thematic_plan/0/hours", false), (Assert.Single(comments).FieldPath, comments[0].IsResolved));
        var resolved = await author.PostAsync(Url(created, $"comments/{comment.Id}/resolve"), null);
        Assert.True((await resolved.Content.ReadFromJsonAsync<CommentItem>())!.IsResolved);
        Assert.Equal(HttpStatusCode.Conflict, (await author.PostAsync(Url(created, $"comments/{comment.Id}/resolve"), null)).StatusCode);

        version = await ExpectAsync(author.PostAsJsonAsync(Url(created, "submit"), new DocumentVersionRequest(saved.Version)), DocumentStatuses.UnderReview);
        await ExpectAsync(methodist.PostAsJsonAsync(Url(created, "accept"), new DocumentVersionRequest(version)), DocumentStatuses.Accepted);

        await app.WithDbAsync(async db =>
        {
            var history = await db.DocumentStatusHistory.Where(h => h.DocumentId == created.Document.Id).OrderBy(h => h.ChangedAt).ToListAsync();
            Assert.Equal(
                [
                    (null, DocumentStatuses.Draft), (DocumentStatuses.Draft, DocumentStatuses.UnderReview),
                    (DocumentStatuses.UnderReview, DocumentStatuses.Returned), (DocumentStatuses.Returned, DocumentStatuses.UnderReview),
                    (DocumentStatuses.UnderReview, DocumentStatuses.Accepted)
                ],
                history.Select(h => (h.FromStatus, h.ToStatus)));
            Assert.Equal(string.Format(DocumentReview.ReturnedWithComments, 1), history[2].Comment);

            // Каждая смена статуса проверки — снимок версии status_change.
            var triggers = await db.DocumentVersions.Where(v => v.DocumentId == created.Document.Id)
                .OrderBy(v => v.VersionNumber).Select(v => v.TriggerEvent).ToListAsync();
            Assert.Equal(4, triggers.Count(t => t == VersionTriggers.StatusChange));
            Assert.Contains(VersionTriggers.ManualSave, triggers);
        });
    }

    [Fact]
    public async Task Methodist_edits_document_under_review_and_the_edit_is_recorded()
    {
        var (author, _) = await DocumentData.UserAsync(app);
        var (methodist, reviewer) = await DocumentData.UserAsync(app, AppRoles.Methodist);
        var created = await DocumentData.CreateAsync(app, author, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.11");
        var version = await DocumentData.FillForSubmissionAsync(author, created);
        version = await ExpectAsync(author.PostAsJsonAsync(Url(created, "submit"), new DocumentVersionRequest(version)), DocumentStatuses.UnderReview);

        var details = (await methodist.GetFromJsonAsync<DocumentDetails>(Url(created, "")))!;
        Assert.True(details.Editable);
        Assert.Equal(new DocumentActions(Submit: false, Review: true, ResolveComments: false, Print: true, CorrectPlan: true), details.Actions);

        var edited = await DocumentData.PatchAsync(methodist, created.Document.Id, "goals_and_objectives", version, Goals("Задача, уточнённая методистом"));

        // Правка методиста статус не меняет и попадает в историю версий с его именем.
        Assert.Equal(DocumentStatuses.UnderReview, edited.Status);
        await app.WithDbAsync(async db =>
        {
            var last = await db.DocumentVersions.Where(v => v.DocumentId == created.Document.Id)
                .OrderByDescending(v => v.VersionNumber).FirstAsync();
            Assert.Equal((reviewer.Id, VersionTriggers.AutoSave), (last.SavedBy, last.TriggerEvent));
        });
    }

    [Fact]
    public async Task Methodist_corrects_plan_hours_with_warning_mark_and_history()
    {
        var (author, _) = await DocumentData.UserAsync(app);
        var (methodist, reviewer) = await DocumentData.UserAsync(app, AppRoles.Methodist);
        var created = await DocumentData.CreateAsync(app, author, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.13");
        var version = await DocumentData.FillForSubmissionAsync(author, created);
        version = await ExpectAsync(author.PostAsJsonAsync(Url(created, "submit"), new DocumentVersionRequest(version)), DocumentStatuses.UnderReview);

        // На проверке методист исправляет лекции 1 семестра: на 2 часа меньше, чем в плане.
        var before = (await methodist.GetFromJsonAsync<DocumentDetails>(Url(created, "")))!;
        var hours = JsonNode.Parse(before.Payload.GetProperty("hours_distribution").GetRawText())!;
        var planLectures = (int)hours["semesters"]![0]!["lectures"]!;
        hours["semesters"]![0]!["lectures"] = planLectures - 2;
        var saved = await DocumentData.PatchAsync(methodist, created.Document.Id, "hours_distribution", version, hours);
        Assert.Equal(DocumentStatuses.UnderReview, saved.Status);

        var after = (await methodist.GetFromJsonAsync<DocumentDetails>(Url(created, "")))!;
        var sections = after.Metadata.Sections.ToDictionary(s => s.Key);
        // Предупреждение «отличается от плана» отправку и приём не блокирует; правило часов сверяет уже с исправленными.
        var warning = Assert.Single(sections["hours_distribution"].Warnings);
        Assert.Equal(("/hours_distribution/semesters/0/lectures", $"{PlanFields.DiffersFromPlan}: в плане {planLectures}"),
            (warning.Path, warning.Message));
        Assert.Contains(sections["thematic_plan"].Missing, e => e.Message == $"Лекции: по темам {planLectures} ч, по распределению часов {planLectures - 2} ч");
        // Значение по плану — для пометки в редакторе; история — кто, было, стало.
        Assert.Equal(planLectures, (int)after.Metadata.PlanValues["/hours_distribution/semesters"]![0]!["lectures"]!);
        var correction = Assert.Single(after.PlanCorrections!);
        Assert.Equal(("/hours_distribution/semesters/0/lectures", planLectures, planLectures - 2, reviewer.ShortName),
            (correction.Path, correction.Before!.Value.GetInt32(), correction.After!.Value.GetInt32(), correction.ChangedBy));
        await app.WithDbAsync(async db =>
        {
            var entry = await db.AuditLog.SingleAsync(a => a.EntityId == created.Document.Id);
            Assert.Equal((AuditEvents.PlanFieldCorrected, reviewer.Id), (entry.EventType, entry.UserId));
        });
    }

    [Fact]
    public async Task After_acceptance_methodist_and_admin_correct_only_plan_fields()
    {
        var (author, _) = await DocumentData.UserAsync(app);
        var (other, _) = await DocumentData.UserAsync(app);
        var (methodist, _) = await DocumentData.UserAsync(app, AppRoles.Methodist);
        var (admin, _) = await DocumentData.UserAsync(app, AppRoles.Admin);
        var created = await DocumentData.CreateAsync(app, author, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.08");
        var version = await DocumentData.FillForSubmissionAsync(author, created);
        version = await ExpectAsync(author.PostAsJsonAsync(Url(created, "submit"), new DocumentVersionRequest(version)), DocumentStatuses.UnderReview);
        version = await ExpectAsync(methodist.PostAsJsonAsync(Url(created, "accept"), new DocumentVersionRequest(version)), DocumentStatuses.Accepted);

        var forMethodist = (await methodist.GetFromJsonAsync<DocumentDetails>(Url(created, "")))!;
        Assert.Equal((false, true), (forMethodist.Editable, forMethodist.Actions.CorrectPlan));
        Assert.False((await author.GetFromJsonAsync<DocumentDetails>(Url(created, "")))!.Actions.CorrectPlan);

        var title = JsonNode.Parse(forMethodist.Payload.GetProperty("title_page").GetRawText())!;
        title["discipline_name"] = "Название, исправленное методистом";
        var corrected = await DocumentData.PatchAsync(methodist, created.Document.Id, "title_page", version, title);
        Assert.Equal(DocumentStatuses.Accepted, corrected.Status);
        title["discipline_name"] = "Название, исправленное администратором";
        version = (await DocumentData.PatchAsync(admin, created.Document.Id, "title_page", corrected.Version, title)).Version;

        // Остальное содержимое после приёма закрыто и для методиста; автору — всё; чужому — 403.
        var goals = await methodist.PatchAsJsonAsync(Url(created, "sections/goals_and_objectives"), Section(version, Goals("Правка после приёма")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, goals.StatusCode);
        Assert.Equal(DocumentEditing.PlanOnlyChanged, Assert.Single(await ErrorsAsync(goals)).GetProperty("message").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await author.PatchAsJsonAsync(Url(created, "sections/title_page"), Section(version, title))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PatchAsJsonAsync(Url(created, "sections/title_page"), Section(version, title))).StatusCode);

        var history = (await admin.GetFromJsonAsync<DocumentDetails>(Url(created, "")))!.PlanCorrections!;
        Assert.Equal(["Название, исправленное методистом", "Название, исправленное администратором"],
            history.Select(c => c.After!.Value.GetString()));
    }

    [Fact]
    public async Task Only_methodist_reviews()
    {
        var (author, _) = await DocumentData.UserAsync(app);
        var (admin, _) = await DocumentData.UserAsync(app, AppRoles.Admin);
        var created = await DocumentData.CreateAsync(app, author, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.12");
        var version = await DocumentData.FillForSubmissionAsync(author, created);
        version = await ExpectAsync(author.PostAsJsonAsync(Url(created, "submit"), new DocumentVersionRequest(version)), DocumentStatuses.UnderReview);

        foreach (var client in new[] { author, admin })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Url(created, "return"), new ReturnRequest(version, null))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Url(created, "accept"), new DocumentVersionRequest(version))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await client.PostAsJsonAsync(Url(created, "comments"), new CommentRequest("/thematic_plan", "Текст"))).StatusCode);
        }
    }

    private static string Url(DocumentDetails document, string action) =>
        $"/api/documents/{document.Document.Id}" + (action.Length > 0 ? $"/{action}" : "");

    private static SectionUpdateRequest Section(int version, JsonNode value) =>
        new(version, JsonDocument.Parse(value.ToJsonString()).RootElement);

    private static JsonObject Goals(string objective) => new()
    {
        ["goals"] = "<p>Сформировать у обучающихся умения строить и исследовать математические модели систем.</p>",
        ["objectives"] = new JsonArray(objective)
    };

    // Ответ на смену статуса: ожидаемый статус; возвращает новую версию.
    private static async Task<int> ExpectAsync(Task<HttpResponseMessage> request, string status)
    {
        var response = await request;
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        var reviewed = (await response.Content.ReadFromJsonAsync<DocumentReviewed>())!;
        Assert.Equal(status, reviewed.Status);
        return reviewed.Version;
    }

    private static async Task<List<JsonElement>> ErrorsAsync(HttpResponseMessage response)
    {
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>())!;
        return ((JsonElement)problem.Extensions["errors"]!).EnumerateArray().ToList();
    }
}
