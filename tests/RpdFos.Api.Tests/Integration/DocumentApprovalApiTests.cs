using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Features.Approvals;
using RpdFos.Api.Features.Documents;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Электронное согласование (этап 6, B1–B3; UC-07 – UC-09): старт при приёме, ручное назначение подписанта, отметки
/// в любом порядке, возврат методисту и новый круг. Подписанты — обезличенные записи файла должностей, импортированные
/// с планом 09.04.04: заведующий кафедрой дисциплины и начальник УМУ.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class DocumentApprovalApiTests(DatabaseFixture app)
{
    [Fact]
    public async Task Acceptance_starts_approval_and_marks_in_any_order_approve_the_document()
    {
        var (author, methodist, created, version) = await UnderReviewAsync("Б1.О.10");
        // Заведующий кафедрой — и преподаватель, и согласующий: у учётной записи две роли.
        var (head, headRecord) = await DocumentData.SignatoryAsync(app, created.Document.DisciplineId, SignatoryRoles.DepartmentHead,
            $"{AppRoles.Teacher},{AppRoles.Approver}");
        var (umu, umuRecord) = await DocumentData.SignatoryAsync(app, created.Document.DisciplineId, SignatoryRoles.UmuHead);

        // Приём сам запускает согласование: оба подписанта найдены правилами листа согласования.
        await ExpectAsync(methodist.PostAsJsonAsync(Url(created, "accept"), new DocumentVersionRequest(version)), DocumentStatuses.Approval);

        var forMethodist = await DetailsAsync(methodist, created);
        var panel = forMethodist.Approval!;
        Assert.Equal([(SignatoryRoles.DepartmentHead, headRecord.Id, SignatureStatuses.Pending), (SignatoryRoles.UmuHead, umuRecord.Id, SignatureStatuses.Pending)],
            panel.Seats.Select(s => (s.Role, s.SignatoryId!.Value, s.Status)));
        Assert.All(panel.Seats, s => Assert.True(s.CanSign, s.Role));
        Assert.Equal((true, false, null), (panel.CanAssign, panel.CanStart, panel.Returned));
        // На согласовании исправлений нет — и полей из плана тоже; печать методисту доступна.
        Assert.Equal((false, false, false, true),
            (forMethodist.Editable, forMethodist.Actions.Review, forMethodist.Actions.CorrectPlan, forMethodist.Actions.Print));
        var title = JsonNode.Parse(forMethodist.Payload.GetProperty("title_page").GetRawText())!;
        Assert.Equal(HttpStatusCode.Conflict,
            (await methodist.PatchAsJsonAsync(Url(created, "sections/title_page"), Section(forMethodist.Document.Version, title))).StatusCode);

        // Очередь согласующего (S-31): документ ждёт отметки обоих.
        Assert.Contains(await QueueAsync(head), q => q.Id == created.Document.Id);
        var queued = Assert.Single(await QueueAsync(umu), q => q.Id == created.Document.Id);
        Assert.Equal((created.Document.DisciplineCode, created.Document.Type), (queued.DisciplineCode, queued.Type));
        Assert.NotNull(queued.SentAt);

        // Отметки — в любом порядке: первым — начальник УМУ, документ остаётся на согласовании.
        await ExpectAsync(umu.PostAsync(Url(created, "approval/approve"), null), DocumentStatuses.Approval);
        Assert.DoesNotContain(await QueueAsync(umu), q => q.Id == created.Document.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await umu.PostAsync(Url(created, "approval/approve"), null)).StatusCode);

        // Согласующий видит документ только для чтения, с замечаниями методиста, панелью согласования и печатью.
        var forHead = await DetailsAsync(head, created);
        Assert.Equal((false, true, true), (forHead.Editable, forHead.Actions.Approve, forHead.Actions.Print));
        Assert.Equal([false, true], forHead.Approval!.Seats.Select(s => s.Status == SignatureStatuses.Approved));
        Assert.Single(forHead.Approval.Seats, s => s.Mine);
        Assert.Equal(HttpStatusCode.OK, (await head.GetAsync(Url(created, "comments"))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await head.PostAsync(Url(created, "print/pdf"), null)).StatusCode);
        Assert.False((await DetailsAsync(umu, created)).Actions.Approve);

        // Последний согласовал — документ «Согласован».
        await ExpectAsync(head.PostAsync(Url(created, "approval/approve"), null), DocumentStatuses.Approved);
        Assert.DoesNotContain(await QueueAsync(head), q => q.Id == created.Document.Id);

        // Правки закрыты для всех, печать доступна участникам.
        var approved = await DetailsAsync(methodist, created);
        Assert.Equal((false, false, true), (approved.Editable, approved.Actions.CorrectPlan, approved.Actions.Print));
        foreach (var editor in new[] { methodist, author })
            Assert.Equal(HttpStatusCode.Conflict,
                (await editor.PatchAsJsonAsync(Url(created, "sections/title_page"), Section(approved.Document.Version, title))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await head.PostAsync(Url(created, "approval/approve"), null)).StatusCode);
        foreach (var participant in new[] { author, methodist, head, umu })
            Assert.Equal(HttpStatusCode.Accepted, (await participant.PostAsync(Url(created, "print/docx"), null)).StatusCode);

        await app.WithDbAsync(async db =>
        {
            var document = await db.Documents.SingleAsync(d => d.Id == created.Document.Id);
            Assert.NotNull(document.ApprovedAt);
            var history = await db.DocumentStatusHistory.Where(h => h.DocumentId == document.Id).OrderBy(h => h.ChangedAt).ToListAsync();
            // Приём и старт — два перехода одного сохранения, в порядке событий; согласовал — последний отметивший.
            Assert.Equal(
                [
                    (DocumentStatuses.UnderReview, DocumentStatuses.Accepted), (DocumentStatuses.Accepted, DocumentStatuses.Approval),
                    (DocumentStatuses.Approval, DocumentStatuses.Approved)
                ],
                history.TakeLast(3).Select(h => (h.FromStatus!, h.ToStatus)));
            Assert.Equal(headRecord.Id, history[^1].ChangedBy);
            var rows = await db.DocumentSignatures.Where(s => s.DocumentId == document.Id).ToListAsync();
            Assert.All(rows, r => Assert.Equal((SignatureStatuses.Approved, true), (r.Status, r.ApprovedAt is not null)));
            var events = await db.AuditLog.Where(a => a.EntityId == document.Id).OrderBy(a => a.OccurredAt).Select(a => a.EventType).ToListAsync();
            Assert.Equal([AuditEvents.ApprovalStarted, AuditEvents.SignatureApproved, AuditEvents.SignatureApproved], events);
            Assert.Contains(VersionTriggers.Approved,
                await db.DocumentVersions.Where(v => v.DocumentId == document.Id).Select(v => v.TriggerEvent).ToListAsync());
        });
    }

    [Fact]
    public async Task Approver_without_a_seat_does_not_see_the_document_and_cannot_mark_it()
    {
        var (_, methodist, created, version) = await UnderReviewAsync("Б1.О.12");
        // Утверждающий без назначения: роль есть, строки согласования в документе нет.
        var (stranger, _) = await DocumentData.UserAsync(app, AppRoles.Approver);
        var (teacher, _) = await DocumentData.UserAsync(app);
        await ExpectAsync(methodist.PostAsJsonAsync(Url(created, "accept"), new DocumentVersionRequest(version)), DocumentStatuses.Approval);

        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync(Url(created, ""))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync(Url(created, "comments"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.PostAsync(Url(created, "print/docx"), null)).StatusCode);
        Assert.DoesNotContain(await QueueAsync(stranger), q => q.Id == created.Document.Id);

        // Чужая строка — 403: отметку ставит только назначенный в ней.
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.PostAsync(Url(created, "approval/approve"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await stranger.PostAsJsonAsync(Url(created, "approval/reject"), new RejectRequest("Не согласен"))).StatusCode);
        // Без роли «Утверждающий» — отказ политики: и методисту, и преподавателю.
        foreach (var client in new[] { methodist, teacher })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(Url(created, "approval/approve"), null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/approvals")).StatusCode);
        }
        await app.WithDbAsync(async db => Assert.All(
            await db.DocumentSignatures.Where(s => s.DocumentId == created.Document.Id).ToListAsync(),
            r => Assert.Equal(SignatureStatuses.Pending, r.Status)));
    }

    [Fact]
    public async Task Missing_signatory_keeps_document_accepted_until_assigned_and_started()
    {
        var (_, methodist, created, version) = await UnderReviewAsync("Б1.О.12", DocumentData.SeatsWithMissingSignatory);
        var (admin, adminRecord) = await DocumentData.UserAsync(app, AppRoles.Admin);
        var (teacher, _) = await DocumentData.UserAsync(app);
        var (chair, chairRecord) = await DocumentData.UserAsync(app, AppRoles.Approver);

        // Председатель УМК правилом не найден — документ «Принят», методист видит, кого назначить.
        version = await ExpectAsync(methodist.PostAsJsonAsync(Url(created, "accept"), new DocumentVersionRequest(version)), DocumentStatuses.Accepted);
        var panel = (await DetailsAsync(methodist, created)).Approval!;
        Assert.Equal([true, true, false], panel.Seats.Select(s => s.SignatoryId is not null));
        Assert.Equal((SignatoryRoles.UmkChair, SignatureStatuses.Pending), (panel.Seats[2].Role, panel.Seats[2].Status));
        Assert.Equal("Председатель учебно-методической комиссии по направлению подготовки", panel.Seats[2].Title);
        Assert.Equal((true, false), (panel.CanAssign, panel.CanStart));
        var refused = await methodist.PostAsJsonAsync(Url(created, "approval/start"), new DocumentVersionRequest(version));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("Не все согласующие назначены", (await refused.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);

        // Назначают методист и администратор (FR-REV-13): активную запись справочника на существующее место.
        string Seat(string role) => Url(created, $"approval/seats/{role}/assign");
        Assert.Equal(HttpStatusCode.Forbidden, (await teacher.PostAsJsonAsync(Seat(SignatoryRoles.UmkChair), new AssignRequest(chairRecord.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync(Seat(SignatoryRoles.Rector), new AssignRequest(chairRecord.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await admin.PostAsJsonAsync(Seat(SignatoryRoles.UmkChair), new AssignRequest(Guid.NewGuid()))).StatusCode);
        var assigned = await admin.PostAsJsonAsync(Seat(SignatoryRoles.UmkChair), new AssignRequest(chairRecord.Id));
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        var afterAssign = (await assigned.Content.ReadFromJsonAsync<ApprovalChanged>())!;
        Assert.Equal(DocumentStatuses.Accepted, afterAssign.Status);

        // «Запустить согласование» — методист, с версией документа, которую он видит. У назначенного нет учётной
        // записи с ролью «Утверждающий» (запись справочника связана только заголовком теста) — панель это показывает.
        var ready = (await DetailsAsync(methodist, created)).Approval!;
        Assert.Equal((true, chairRecord.Id, false), (ready.CanStart, ready.Seats[2].SignatoryId, ready.Seats[2].CanSign));
        Assert.False((await DetailsAsync(admin, created)).Approval!.CanStart);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await admin.PostAsJsonAsync(Url(created, "approval/start"), new DocumentVersionRequest(afterAssign.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await methodist.PostAsJsonAsync(Url(created, "approval/start"), new DocumentVersionRequest(version))).StatusCode);
        await ExpectAsync(methodist.PostAsJsonAsync(Url(created, "approval/start"), new DocumentVersionRequest(afterAssign.Version)),
            DocumentStatuses.Approval);
        Assert.Contains(await QueueAsync(chair), q => q.Id == created.Document.Id);

        // Замена отсутствующего согласующего — пока его отметки ждут: прежний теряет доступ, новый получает.
        var (umu, _) = await DocumentData.SignatoryAsync(app, created.Document.DisciplineId, SignatoryRoles.UmuHead);
        var (deputy, deputyRecord) = await DocumentData.UserAsync(app, AppRoles.Approver);
        Assert.Equal(HttpStatusCode.OK, (await umu.GetAsync(Url(created, ""))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await methodist.PostAsJsonAsync(Seat(SignatoryRoles.UmuHead), new AssignRequest(deputyRecord.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await umu.GetAsync(Url(created, ""))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await umu.PostAsync(Url(created, "approval/approve"), null)).StatusCode);
        await ExpectAsync(deputy.PostAsync(Url(created, "approval/approve"), null), DocumentStatuses.Approval);
        // Отметка уже стоит — подписанта не заменить.
        Assert.Equal(HttpStatusCode.Conflict,
            (await methodist.PostAsJsonAsync(Seat(SignatoryRoles.UmuHead), new AssignRequest(chairRecord.Id))).StatusCode);

        await app.WithDbAsync(async db =>
        {
            var history = await db.DocumentStatusHistory.Where(h => h.DocumentId == created.Document.Id).OrderBy(h => h.ChangedAt).ToListAsync();
            Assert.Equal([(DocumentStatuses.UnderReview, DocumentStatuses.Accepted), (DocumentStatuses.Accepted, DocumentStatuses.Approval)],
                history.TakeLast(2).Select(h => (h.FromStatus!, h.ToStatus)));
            // Назначение и замена — в журнале аудита: кто, место, было, стало.
            var changes = await db.AuditLog.Where(a => a.EntityId == created.Document.Id && a.EventType == AuditEvents.SignatoryAssigned)
                .OrderBy(a => a.OccurredAt).ToListAsync();
            Assert.Equal([adminRecord.Id, (await db.Documents.Where(d => d.Id == created.Document.Id)
                    .Join(db.DocumentStatusHistory, d => d.Id, h => h.DocumentId, (d, h) => h)
                    .Where(h => h.ToStatus == DocumentStatuses.Accepted).Select(h => h.ChangedBy).SingleAsync())],
                changes.Select(c => c.UserId!.Value));
            var first = JsonDocument.Parse(changes[0].Payload).RootElement;
            Assert.Equal((SignatoryRoles.UmkChair, JsonValueKind.Null, chairRecord.Id),
                (first.GetProperty("role").GetString(), first.GetProperty("before").ValueKind, first.GetProperty("after").GetGuid()));
            Assert.Equal(deputyRecord.Id, JsonDocument.Parse(changes[1].Payload).RootElement.GetProperty("after").GetGuid());
        });
    }

    [Fact]
    public async Task Return_from_approval_goes_to_methodist_and_next_acceptance_starts_a_new_round()
    {
        var (author, methodist, created, version) = await UnderReviewAsync("Б1.О.13");
        var (head, _) = await DocumentData.SignatoryAsync(app, created.Document.DisciplineId, SignatoryRoles.DepartmentHead);
        var (umu, umuRecord) = await DocumentData.SignatoryAsync(app, created.Document.DisciplineId, SignatoryRoles.UmuHead);
        await ExpectAsync(methodist.PostAsJsonAsync(Url(created, "accept"), new DocumentVersionRequest(version)), DocumentStatuses.Approval);
        await ExpectAsync(head.PostAsync(Url(created, "approval/approve"), null), DocumentStatuses.Approval);

        // Возврат без комментария — 422; с комментарием — документ у методиста, «На проверке».
        var empty = await umu.PostAsJsonAsync(Url(created, "approval/reject"), new RejectRequest("  "));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        version = await ExpectAsync(umu.PostAsJsonAsync(Url(created, "approval/reject"), new RejectRequest(" Не указана литература по разделу 2 ")),
            DocumentStatuses.UnderReview);

        // Методист видит, кто, когда и почему вернул; строки прежнего круга — до нового приёма.
        var returned = await DetailsAsync(methodist, created);
        Assert.Equal((true, true), (returned.Editable, returned.Actions.Review));
        var back = returned.Approval!.Returned!;
        Assert.Equal((umuRecord.ShortName, "Не указана литература по разделу 2"), (back.By, back.Comment));
        Assert.Equal([SignatureStatuses.Approved, SignatureStatuses.Rejected], returned.Approval.Seats.Select(s => s.Status));
        Assert.Equal(("Не указана литература по разделу 2", back.At), (returned.Approval.Seats[1].Comment, returned.Approval.Seats[1].MarkedAt!.Value));
        Assert.Equal((false, false), (returned.Approval.CanAssign, returned.Approval.CanStart));

        // Автору плашка не нужна; его сохранение из открытой вкладки — обычный отказ, а не «взят методистом».
        var forAuthor = await DetailsAsync(author, created);
        Assert.Null(forAuthor.Approval);
        var stale = await author.PatchAsJsonAsync(Url(created, "sections/title_page"),
            Section(version, JsonNode.Parse(forAuthor.Payload.GetProperty("title_page").GetRawText())!));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("Действие недоступно в этом статусе", (await stale.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);

        // Круг окончен: у согласующих документ из очереди ушёл, отметить его нельзя.
        Assert.DoesNotContain(await QueueAsync(head), q => q.Id == created.Document.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await head.PostAsync(Url(created, "approval/approve"), null)).StatusCode);

        // Повторный приём — новый круг: все строки снова ждут отметки, и согласовавший в прежнем круге — тоже.
        await ExpectAsync(methodist.PostAsJsonAsync(Url(created, "accept"), new DocumentVersionRequest(version)), DocumentStatuses.Approval);
        var again = (await DetailsAsync(methodist, created)).Approval!;
        Assert.Null(again.Returned);
        Assert.All(again.Seats, s => Assert.Equal((SignatureStatuses.Pending, null, null), (s.Status, s.MarkedAt, s.Comment)));
        Assert.Contains(await QueueAsync(head), q => q.Id == created.Document.Id);
        await ExpectAsync(umu.PostAsync(Url(created, "approval/approve"), null), DocumentStatuses.Approval);
        await ExpectAsync(head.PostAsync(Url(created, "approval/approve"), null), DocumentStatuses.Approved);

        await app.WithDbAsync(async db =>
        {
            var history = await db.DocumentStatusHistory.Where(h => h.DocumentId == created.Document.Id).OrderBy(h => h.ChangedAt).ToListAsync();
            var back = history.Single(h => h.FromStatus == DocumentStatuses.Approval && h.ToStatus == DocumentStatuses.UnderReview);
            Assert.Equal((umuRecord.Id, "Не указана литература по разделу 2"), (back.ChangedBy, back.Comment));
            // Отметки прежнего круга остались в журнале аудита, хотя строки сброшены.
            var events = await db.AuditLog.Where(a => a.EntityId == created.Document.Id).OrderBy(a => a.OccurredAt).ToListAsync();
            Assert.Equal(
                [
                    AuditEvents.ApprovalStarted, AuditEvents.SignatureApproved, AuditEvents.SignatureRejected,
                    AuditEvents.ApprovalStarted, AuditEvents.SignatureApproved, AuditEvents.SignatureApproved
                ],
                events.Select(e => e.EventType));
            var rejected = JsonDocument.Parse(events[2].Payload).RootElement;
            Assert.Equal((SignatoryRoles.UmuHead, "Не указана литература по разделу 2", umuRecord.Id),
                (rejected.GetProperty("role").GetString(), rejected.GetProperty("comment").GetString(), events[2].UserId));
            Assert.Equal(2, await db.DocumentSignatures.CountAsync(s => s.DocumentId == created.Document.Id));
        });
    }

    [Fact]
    public async Task Document_accepted_before_approval_existed_is_started_from_the_panel()
    {
        var (_, methodist, created, version) = await UnderReviewAsync("Б1.О.08");
        // Документ этапов 5–5.4: «Принят», строк согласования нет.
        await app.WithDbAsync(async db =>
        {
            var document = await db.Documents.SingleAsync(d => d.Id == created.Document.Id);
            document.Status = DocumentStatuses.Accepted;
            await db.SaveChangesAsync();
            version = document.Version;
        });

        var panel = (await DetailsAsync(methodist, created)).Approval!;
        Assert.Equal((0, true, false), (panel.Seats.Count, panel.CanStart, panel.CanAssign));

        // Запуск сам определяет подписантов — как приём.
        await ExpectAsync(methodist.PostAsJsonAsync(Url(created, "approval/start"), new DocumentVersionRequest(version)), DocumentStatuses.Approval);
        Assert.Equal(2, (await DetailsAsync(methodist, created)).Approval!.Seats.Count);
    }

    [Fact]
    public async Task Template_without_electronic_seats_makes_accepted_document_approved()
    {
        // Отмечать некому — согласие «всех» уже есть: документ не должен зависнуть на согласовании.
        var (_, methodist, created, version) = await UnderReviewAsync("Б1.О.10", []);

        await ExpectAsync(methodist.PostAsJsonAsync(Url(created, "accept"), new DocumentVersionRequest(version)), DocumentStatuses.Approved);

        await app.WithDbAsync(async db => Assert.NotNull((await db.Documents.SingleAsync(d => d.Id == created.Document.Id)).ApprovedAt));
    }

    private async Task<(HttpClient Author, HttpClient Methodist, DocumentDetails Created, int Version)> UnderReviewAsync(
        string code, IReadOnlyCollection<string>? electronicSeats = null)
    {
        var (author, _) = await DocumentData.UserAsync(app);
        var (methodist, _) = await DocumentData.UserAsync(app, AppRoles.Methodist);
        var template = await DocumentData.PublishedTemplateAsync(app, electronicSeats);
        var created = await DocumentData.CreateAsync(app, author, template.Id, code);
        var version = await DocumentData.FillForSubmissionAsync(author, created);
        version = await ExpectAsync(author.PostAsJsonAsync(Url(created, "submit"), new DocumentVersionRequest(version)), DocumentStatuses.UnderReview);
        return (author, methodist, created, version);
    }

    private static async Task<List<ApprovalQueueItem>> QueueAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<ApprovalQueueItem>>("/api/approvals"))!;

    private static async Task<DocumentDetails> DetailsAsync(HttpClient client, DocumentDetails document) =>
        (await client.GetFromJsonAsync<DocumentDetails>(Url(document, "")))!;

    private static string Url(DocumentDetails document, string action) =>
        $"/api/documents/{document.Document.Id}" + (action.Length > 0 ? $"/{action}" : "");

    private static SectionUpdateRequest Section(int version, JsonNode value) =>
        new(version, JsonDocument.Parse(value.ToJsonString()).RootElement);

    // Ответ на смену статуса или действие согласования: ожидаемый статус; возвращает новую версию.
    private static async Task<int> ExpectAsync(Task<HttpResponseMessage> request, string status)
    {
        var response = await request;
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        var changed = (await response.Content.ReadFromJsonAsync<StatusAnswer>())!;
        Assert.Equal(status, changed.Status);
        return changed.Version;
    }

    private sealed record StatusAnswer(int Version, string Status);
}
