using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Features.Approvals;
using RpdFos.Api.Features.Documents;
using RpdFos.Api.Features.Notifications;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using RpdFos.Core.DocumentTypes;
using Xunit;
using static RpdFos.Api.Tests.Integration.DocumentCircles;
using S = RpdFos.Core.Documents.DocumentStatuses;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// ФОС в цикле (этап 7, B10; решения 3, 4, 8): проверка и согласование — тем же механизмом, что у РПД; «Создать на основе» для
/// ФОС переносит банк, билеты, средства, текущий контроль и шкалу, а ссылки приводит к РПД цели; «устарел» и архив РПД
/// распространяются на её согласованный ФОС, а РПД с незавершённым ФОС в архив не уходит. Круг строит
/// <see cref="DocumentCircles.ApprovedAsync"/> для РПД: те же автор, методист и согласующие ведут и ФОС — подписанты листа
/// согласования ФОС находятся теми же правилами по дисциплине.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class FosCycleApiTests(DatabaseFixture fixture) : AssignmentTestBase(fixture)
{
    [Fact]
    public async Task A_fos_goes_the_whole_circle_review_remark_return_acceptance_marks_and_approved()
    {
        var c = await DocumentCircles.ApprovedAsync(App, "Б1.О.13");
        var (fos, version) = await FilledFosAsync(c, c.Created);

        // Отправка автором: ушёл на проверку, у методиста — в очереди «Проверка» среди других документов, с типом ФОС.
        version = await ExpectAsync(c.Author.PostAsJsonAsync(Url(fos, "submit"), new DocumentVersionRequest(version)), S.UnderReview);
        var queued = (await c.Methodist.GetFromJsonAsync<List<DocumentListItem>>("/api/documents?status=under_review"))!;
        Assert.Equal("fos", Assert.Single(queued, q => q.Id == fos.Document.Id).Type);

        // Замечание к полю банка — возврат на доработку; автор правит и отправляет снова.
        var remark = await c.Methodist.PostAsJsonAsync(Url(fos, "comments"), new CommentRequest("/task_bank/0/tasks/0/text", "Уточните формулировку"));
        Assert.Equal(HttpStatusCode.Created, remark.StatusCode);
        version = await ExpectAsync(c.Methodist.PostAsJsonAsync(Url(fos, "return"), new ReturnRequest(version, null)), S.Returned);
        Assert.Contains(await DocumentCircles.FeedAsync(c.Author, fos, NotificationEvents.ReturnedForRework), n => n.Text.Contains(fos.Document.Title));

        var comment = (await c.Author.GetFromJsonAsync<List<CommentItem>>(Url(fos, "comments")))!.Single();
        Assert.Equal(HttpStatusCode.OK, (await c.Author.PostAsync(Url(fos, $"comments/{comment.Id}/resolve"), null)).StatusCode);
        version = await ExpectAsync(c.Author.PostAsJsonAsync(Url(fos, "submit"), new DocumentVersionRequest(version)), S.UnderReview);

        // Приём запускает круг по листу согласования ФОС: два электронных места — заведующий кафедрой и начальник УМУ.
        version = await ExpectAsync(c.Methodist.PostAsJsonAsync(Url(fos, "accept"), new DocumentVersionRequest(version)), S.Approval);
        var circle = (await DetailsAsync(c.Methodist, fos.Document.Id)).Approval!;
        Assert.Equal([SignatoryRoles.DepartmentHead, SignatoryRoles.UmuHead], circle.Seats.Select(s => s.Role));
        Assert.Equal([c.HeadRecord.Id, c.UmuRecord.Id], circle.Seats.Select(s => s.SignatoryId!.Value));

        // Согласующий видит ФОС в своей очереди; замечание к полю — несогласие: «Согласовать» отказывает, пока оно не снято.
        var queue = (await c.Head.GetFromJsonAsync<List<ApprovalQueueItem>>("/api/approvals"))!;
        Assert.Contains(queue, q => q.Id == fos.Document.Id);
        Assert.Equal(HttpStatusCode.Created,
            (await c.Head.PostAsJsonAsync(Url(fos, "comments"), new CommentRequest("/current_control/forms", "Дополните формы"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.Head.PostAsync(Url(fos, "approval/approve"), null)).StatusCode);
        var open = (await c.Head.GetFromJsonAsync<List<CommentItem>>(Url(fos, "comments")))!.Single(x => !x.IsResolved);
        Assert.Equal(HttpStatusCode.OK, (await c.Head.PostAsync(Url(fos, $"comments/{open.Id}/resolve"), null)).StatusCode);

        // Отметки обоих мест: «Согласован», время согласования, снимок версии и журнал; автор получает уведомление.
        await ExpectAsync(c.Head.PostAsync(Url(fos, "approval/approve"), null), S.Approval);
        Assert.Contains(await DocumentCircles.FeedAsync(c.Umu, fos, NotificationEvents.AwaitingMark), n => n.Text.Contains(fos.Document.Title));
        await ExpectAsync(c.Umu.PostAsync(Url(fos, "approval/approve"), null), S.Approved);
        Assert.Contains(await DocumentCircles.FeedAsync(c.Author, fos, NotificationEvents.Approved), n => n.Text.Contains(fos.Document.Title));
        await App.WithDbAsync(async db =>
        {
            var document = await db.Documents.SingleAsync(d => d.Id == fos.Document.Id);
            Assert.NotNull(document.ApprovedAt);
            Assert.True(await db.DocumentVersions.AnyAsync(v => v.DocumentId == document.Id && v.TriggerEvent == VersionTriggers.Approved));
            var marks = await db.AuditLog.Where(a => a.EntityId == document.Id && a.EventType == AuditEvents.SignatureApproved).CountAsync();
            Assert.Equal(2, marks);
        });
    }

    [Fact]
    public async Task The_next_fos_is_created_on_the_approved_one_for_the_new_rpd_and_the_old_pair_becomes_outdated()
    {
        var c = await DocumentCircles.ApprovedAsync(App, "Б1.О.05");
        var (oldRpd, templateId, disciplineId) = (c.Created.Document.Id, c.Created.Document.TemplateId, c.Created.Document.DisciplineId);
        // Рисунок в задании банка: при переносе «на основе» файл остаётся тем же, новому ФОС добавляется связь (B4а).
        var draft = await CreatedFosAsync(c.Author, c.Created);
        var image = await DocumentImagesApiTests.UploadedAsync(c.Author, draft.Document.Id, Documents.TestImages.Png(seed: 91));
        var figure = DocumentImagesApiTests.Tag(image.Id, "width=\"90\" alt=\"Схема\"");
        var oldVersion = await ApproveFosAsync(c, draft, await FillFosAsync(c.Author, draft, figure));
        var oldFos = await DetailsAsync(c.Author, draft.Document.Id);
        Assert.Equal(S.Approved, oldFos.Document.Status);
        Assert.True(oldFos.Actions.CreateBasedOn, "автору согласованного ФОС кнопка «Создать на основе» положена");

        // Согласованный ФОС занимает дисциплину: без основы — 409; основа другого типа (РПД для ФОС и ФОС для РПД) — 422.
        var blocked = await RefusedAsync(c.Author, HttpStatusCode.Conflict, oldFos.Document.TemplateId, disciplineId, oldRpd, null);
        Assert.Equal(oldFos.Document.Id.ToString(), blocked.Extensions["documentId"]!.ToString());
        Assert.Equal("Основа другого типа", (await RefusedAsync(c.Author, HttpStatusCode.UnprocessableEntity,
            oldFos.Document.TemplateId, disciplineId, oldRpd, oldRpd)).Title);
        Assert.Equal("Основа другого типа", (await RefusedAsync(c.Author, HttpStatusCode.UnprocessableEntity,
            templateId, disciplineId, null, oldFos.Document.Id)).Title);
        var option = (await c.Author.GetFromJsonAsync<List<DisciplineOption>>($"/api/documents/disciplines?templateId={oldFos.Document.TemplateId}"))!
            .Single(o => o.Id == disciplineId);
        Assert.Equal((oldFos.Document.Id, S.Approved), (option.DocumentId, option.DocumentStatus));

        // Новая РПД на основе прежней согласуется: прежняя устарела, а с ней — и её согласованный ФОС.
        var created = await CreatedRpdAsync(c.Author, templateId, disciplineId, oldRpd);
        await ApproveRpdAsync(c, created);
        var newRpd = await DetailsAsync(c.Methodist, created.Document.Id);
        Assert.Equal((S.Approved, false), (newRpd.Document.Status, newRpd.Document.IsOutdated));
        var outdated = await DetailsAsync(c.Methodist, oldFos.Document.Id);
        Assert.Equal((S.Approved, true, oldVersion), (outdated.Document.Status, outdated.Document.IsOutdated, outdated.Document.Version));
        Assert.True((await DetailsAsync(c.Methodist, oldRpd)).Document.IsOutdated);
        // Автор прежней РПД и ФОС — один человек, документов два: событие на каждый, по одному; у ФОС преемником названа новая РПД.
        var told = await OutdatedFeedAsync(c.Author);
        Assert.Equal(new[] { oldRpd, oldFos.Document.Id }.Order(), told.Select(n => n.DocumentId!.Value).Order());
        Assert.All(told, n => Assert.Contains(newRpd.Document.Title, n.Text));
        Assert.Contains("Согласована новая РПД", told.Single(n => n.DocumentId == oldFos.Document.Id).Text);
        Assert.Contains("Согласован новый документ", told.Single(n => n.DocumentId == oldRpd).Text);
        await App.WithDbAsync(async db =>
        {
            var audit = await db.AuditLog.SingleAsync(a => a.EntityId == oldFos.Document.Id && a.EventType == AuditEvents.DocumentOutdated);
            var logged = JsonDocument.Parse(audit.Payload).RootElement;
            Assert.Equal((newRpd.Document.Id.ToString(), oldRpd.ToString()),
                (logged.GetProperty("successor").GetString(), logged.GetProperty("rpd").GetString()));
        });

        // ФОС на основе прежнего — для новой РПД: банк, билеты, средства, контроль, шкала и паспорт те же, наследуемое — из новой.
        var next = await CreatedFosAsync(c.Author, newRpd, basedOn: oldFos.Document.Id, templateId: oldFos.Document.TemplateId);
        Assert.Equal((S.Draft, c.AuthorRecord.Id, oldFos.Document.Id), (next.Document.Status, next.Document.AuthorId, next.BasedOn!.Id));
        var (before, after) = (JsonNode.Parse(oldFos.Payload.GetRawText())!, JsonNode.Parse(next.Payload.GetRawText())!);
        foreach (var section in new[] { "task_bank", "exam_tickets", "evaluation_means", "current_control", "evaluation_scale", "passport" })
            Assert.True(JsonNode.DeepEquals(before[section], after[section]), section);
        Assert.Equal(newRpd.Document.Id.ToString(), (string)after["linked_rpd"]!["rpd_document_id"]!);
        Assert.Equal(newRpd.Document.Version, (int)after["linked_rpd"]!["rpd_version"]!);
        Assert.Empty(next.BasedOn.NotTransferred);
        Assert.False((await DetailsAsync(c.Author, next.Document.Id)).LinkedRpd!.Changed);
        await App.WithDbAsync(async db =>
        {
            Assert.Equal(new[] { draft.Document.Id, next.Document.Id }.Order(),
                (await db.DocumentImages.Where(i => i.FileId == image.Id).Select(i => i.DocumentId).ToListAsync()).Order());
            Assert.Equal(1, await db.UploadedFiles.CountAsync(f => f.Id == image.Id));
            Assert.True(await db.AuditLog.AnyAsync(a => a.EntityId == next.Document.Id && a.EventType == AuditEvents.DocumentBasedOn));
        });

        // Новый ФОС согласуется — прежний уже устарел, статус и версия его прежние.
        await ApproveFosAsync(c, next, await PositionAsync(c.Author, next.Document.Id, next.Document.Version));
        var still = await DetailsAsync(c.Methodist, oldFos.Document.Id);
        Assert.Equal((S.Approved, true, oldVersion), (still.Document.Status, still.Document.IsOutdated, still.Document.Version));
        Assert.False((await DetailsAsync(c.Methodist, next.Document.Id)).Document.IsOutdated);
    }

    [Fact]
    public async Task An_unfinished_fos_keeps_its_rpd_out_of_the_archive_and_an_approved_one_goes_with_it()
    {
        var c = await DocumentCircles.ApprovedAsync(App, "Б1.О.13");
        var rpd = await DetailsAsync(c.Methodist, c.Created.Document.Id);
        var draft = await CreatedFosAsync(c.Author, c.Created);

        // ФОС-черновик удерживает РПД: 409 с названием ФОС и ссылкой на него; РПД остаётся согласованной.
        var refused = await c.Methodist.PostAsJsonAsync(Url(rpd, "archive"), new DocumentVersionRequest(rpd.Document.Version));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = (await refused.Content.ReadFromJsonAsync<ProblemDetails>())!;
        Assert.Equal("Есть незавершённый ФОС", problem.Title);
        Assert.Contains(draft.Document.Title, problem.Detail);
        Assert.Equal(draft.Document.Id.ToString(), problem.Extensions["documentId"]!.ToString());
        var held = await DetailsAsync(c.Methodist, rpd.Document.Id);
        Assert.Equal((S.Approved, rpd.Document.Version), (held.Document.Status, held.Document.Version));

        // ФОС доведён до согласования — архив РПД уводит в архив и его: два перехода, записи аудита, уведомление автору.
        var version = await ApproveFosAsync(c, draft, await FillFosAsync(c.Author, draft));
        await ExpectAsync(c.Methodist.PostAsJsonAsync(Url(rpd, "archive"), new DocumentVersionRequest(rpd.Document.Version)), S.Archived);
        var fos = await DetailsAsync(c.Methodist, draft.Document.Id);
        Assert.Equal((S.Archived, version + 1), (fos.Document.Status, fos.Document.Version));
        Assert.Equal(S.Archived, (await DetailsAsync(c.Methodist, rpd.Document.Id)).Document.Status);
        await App.WithDbAsync(async db =>
        {
            var move = await db.DocumentStatusHistory.SingleAsync(h => h.DocumentId == fos.Document.Id && h.ToStatus == S.Archived);
            Assert.Equal((S.Approved, c.MethodistRecord.Id), (move.FromStatus, move.ChangedBy));
            Assert.True(await db.AuditLog.AnyAsync(a => a.EntityId == fos.Document.Id && a.EventType == AuditEvents.DocumentArchived));
            Assert.True(await db.DocumentVersions.AnyAsync(v => v.DocumentId == fos.Document.Id && v.TriggerEvent == VersionTriggers.StatusChange));
        });
        Assert.Contains(await DocumentCircles.FeedAsync(c.Author, fos, NotificationEvents.Archived), n => n.Text.Contains(fos.Document.Title));
    }

    [Fact]
    public async Task An_unfinished_fos_does_not_become_outdated_with_its_rpd_and_a_deleted_one_does_not_hold_the_archive()
    {
        var c = await DocumentCircles.ApprovedAsync(App, "Б1.О.12");
        var (oldRpd, templateId, disciplineId) = (c.Created.Document.Id, c.Created.Document.TemplateId, c.Created.Document.DisciplineId);
        var draft = await CreatedFosAsync(c.Author, c.Created);

        // Преемник РПД согласован — ФОС-черновик остаётся как есть: он не согласован, и признак ему не нужен.
        await ApproveRpdAsync(c, await CreatedRpdAsync(c.Author, templateId, disciplineId, oldRpd));
        var kept = await DetailsAsync(c.Methodist, draft.Document.Id);
        Assert.Equal((S.Draft, false), (kept.Document.Status, kept.Document.IsOutdated));
        Assert.True((await DetailsAsync(c.Methodist, oldRpd)).Document.IsOutdated);
        Assert.Equal(oldRpd, Assert.Single(await OutdatedFeedAsync(c.Author)).DocumentId); // у ФОС без согласования «устарел» нет, события нет

        // Удалённый ФОС РПД не удерживает: архив проходит.
        var deleted = await c.Author.DeleteAsync($"/api/documents/{draft.Document.Id}?version={kept.Document.Version}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var rpd = await DetailsAsync(c.Methodist, oldRpd);
        await ExpectAsync(c.Methodist.PostAsJsonAsync(Url(rpd, "archive"), new DocumentVersionRequest(rpd.Document.Version)), S.Archived);
    }

    // ───── вспомогательное ─────

    /// <summary>Согласие всех согласующих круга: отправка автором, приём методистом, отметки заведующего и начальника УМУ.</summary>
    private static async Task<int> ApproveFosAsync(DocumentCircle c, DocumentDetails fos, int version)
    {
        version = await ExpectAsync(c.Author.PostAsJsonAsync(Url(fos, "submit"), new DocumentVersionRequest(version)), S.UnderReview);
        await ExpectAsync(c.Methodist.PostAsJsonAsync(Url(fos, "accept"), new DocumentVersionRequest(version)), S.Approval);
        await ExpectAsync(c.Head.PostAsync(Url(fos, "approval/approve"), null), S.Approval);
        return await ExpectAsync(c.Umu.PostAsync(Url(fos, "approval/approve"), null), S.Approved);
    }

    /// <summary>То же для РПД, созданной на основе прежней: сначала заполнение.</summary>
    private static async Task ApproveRpdAsync(DocumentCircle c, DocumentDetails created)
    {
        var version = await DocumentData.FillForSubmissionAsync(c.Author, created);
        version = await ExpectAsync(c.Author.PostAsJsonAsync(Url(created, "submit"), new DocumentVersionRequest(version)), S.UnderReview);
        await ExpectAsync(c.Methodist.PostAsJsonAsync(Url(created, "accept"), new DocumentVersionRequest(version)), S.Approval);
        await ExpectAsync(c.Head.PostAsync(Url(created, "approval/approve"), null), S.Approval);
        await ExpectAsync(c.Umu.PostAsync(Url(created, "approval/approve"), null), S.Approved);
    }

    private static async Task<DocumentDetails> CreatedRpdAsync(HttpClient client, Guid templateId, Guid disciplineId, Guid basedOn)
    {
        var response = await client.PostAsJsonAsync("/api/documents", new CreateDocumentRequest(templateId, disciplineId, basedOn));
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<DocumentDetails>())!;
    }

    private static async Task<ProblemDetails> RefusedAsync(
        HttpClient client, HttpStatusCode status, Guid templateId, Guid disciplineId, Guid? rpdId, Guid? basedOn)
    {
        var response = await client.PostAsJsonAsync("/api/documents", new CreateDocumentRequest(templateId, disciplineId, basedOn, rpdId));
        Assert.True(response.StatusCode == status, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ProblemDetails>())!;
    }

    private static string Url(DocumentDetails document, string action) => DocumentCircles.Url(document, action);

    private static async Task<DocumentDetails> DetailsAsync(HttpClient client, Guid id) => await DocumentCircles.DetailsAsync(client, id);

    private async Task<DocumentTemplate> FosTemplateAsync(Guid? planId = null)
    {
        var plan = planId ?? await App.RealPlanIdAsync();
        DocumentTemplate? template = null;
        await App.WithDbAsync(async db => template = await TemplateData.AddPublishedAsync(db, plan, DocumentTypeRegistry.Fos));
        return template!;
    }

    private async Task<DocumentDetails> CreatedFosAsync(HttpClient client, DocumentDetails rpd, Guid? basedOn = null, Guid? templateId = null)
    {
        var response = await client.PostAsJsonAsync("/api/documents", new CreateDocumentRequest(
            templateId ?? (await FosTemplateAsync()).Id, rpd.Document.DisciplineId, basedOn, rpd.Document.Id));
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<DocumentDetails>())!;
    }

    /// <summary>
    /// ФОС по РПД круга, заполненный так, чтобы пройти полную проверку: банк заданий (вопросы промежуточной аттестации и тест),
    /// билеты, паспорт со ссылками на задания, оценочное средство на все индикаторы РПД, текущий контроль и шкала.
    /// </summary>
    private async Task<(DocumentDetails Fos, int Version)> FilledFosAsync(DocumentCircle c, DocumentDetails rpd)
    {
        var fos = await CreatedFosAsync(c.Author, rpd);
        return (fos, await FillFosAsync(c.Author, fos));
    }

    private static async Task<int> FillFosAsync(HttpClient author, DocumentDetails fos, string? figure = null)
    {
        var payload = JsonNode.Parse(fos.Payload.GetRawText())!;
        var codes = payload["competence_indicators"]!.AsArray().Select(i => (string)i!["code"]!).ToList();
        Assert.NotEmpty(codes);
        var first = (int)payload["linked_rpd"]!["semesters"]![0]!["number"]!;
        var firstSection = (int)payload["linked_rpd"]!["sections"]![0]!["number"]!;
        string Competence(string indicator) => indicator[..indicator.IndexOf('.')];
        var version = fos.Document.Version;

        async Task<int> Save(string section, JsonNode value) => version = (await DocumentData.PatchAsync(author, fos.Document.Id, section, version, value)).Version;

        await Save("task_bank", new JsonArray(
            new JsonObject
            {
                ["group"] = "Вопросы к экзамену", ["kind"] = "questions", ["control_form"] = "intermediate",
                ["semesters"] = new JsonArray(first),
                ["tasks"] = new JsonArray(
                    new JsonObject { ["number"] = 1, ["text"] = $"<p>Что такое модель системы? {figure}</p>".Replace(" </p>", "</p>"), ["indicator_codes"] = new JsonArray(codes[0]) },
                    new JsonObject { ["number"] = 2, ["text"] = "<p>Чем отличаются типы моделей?</p>", ["indicator_codes"] = new JsonArray(codes.Select(x => (JsonNode)x).ToArray()) })
            },
            new JsonObject
            {
                ["group"] = "Тест 1", ["kind"] = "tests", ["section_number"] = firstSection, ["control_form"] = "current",
                ["tasks"] = new JsonArray(new JsonObject { ["number"] = 1, ["text"] = "<p>Вопрос теста</p>", ["expected_answer"] = "а", ["max_score"] = 2 })
            }));
        await Save("exam_tickets", new JsonArray(new JsonObject
        {
            ["number"] = 1, ["questions"] = new JsonArray("<p>Что такое модель системы?</p>", "<p>Чем отличаются типы моделей?</p>")
        }));
        await Save("evaluation_means", new JsonArray(new JsonObject
        {
            ["means_type"] = "exam_questions", ["title"] = "Вопросы к экзамену",
            ["indicator_codes"] = new JsonArray(codes.Select(x => (JsonNode)x).ToArray())
        }));
        await Save("current_control", new JsonObject { ["forms"] = new JsonArray("Контрольная работа") });
        await Save("evaluation_scale", new JsonObject { ["type"] = "five_point" });
        await Save("passport", new JsonObject
        {
            ["sections"] = new JsonArray(new JsonObject
            {
                ["section_number"] = firstSection, ["competence_codes"] = new JsonArray(codes.Select(Competence).Distinct().Select(x => (JsonNode)x).ToArray()),
                ["tasks"] = new JsonArray(new JsonObject { ["group"] = "Тест 1" })
            }),
            ["indicators"] = new JsonArray(codes.Select(code => (JsonNode)new JsonObject
            {
                ["competence_code"] = Competence(code), ["indicator_code"] = code,
                ["tasks"] = new JsonArray(new JsonObject { ["group"] = "Вопросы к экзамену", ["from"] = 1, ["to"] = 2 })
            }).ToArray())
        });
        return await PositionAsync(author, fos.Document.Id, version);
    }

    // Должность составителя в справочнике тестов нет — автор вводит её сам (как у РПД); при переносе «на основе» составитель — один автор.
    private static async Task<int> PositionAsync(HttpClient author, Guid fosId, int version)
    {
        var approval = JsonNode.Parse((await DetailsAsync(author, fosId)).Payload.GetProperty("approval_sheet").GetRawText())!;
        foreach (var developer in approval["developers"]!.AsArray().OfType<JsonObject>()) developer["position"] ??= "доцент";
        return (await DocumentData.PatchAsync(author, fosId, "approval_sheet", version, approval)).Version;
    }
}
