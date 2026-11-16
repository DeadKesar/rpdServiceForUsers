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
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/approvals/marked")).StatusCode);
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

    /// <summary>
    /// Замечание автора после показа C2: строка согласующего, не успевшего отметиться до возврата, в таблице остаётся
    /// «ожидает» до нового приёма, и методист читал её как живую. В панели такая строка подписана явно — «прерван»;
    /// новый круг возвращает ей «ожидает».
    /// </summary>
    [Fact]
    public async Task Return_from_approval_labels_the_rows_that_did_not_mark_as_interrupted()
    {
        var (_, methodist, created, version) = await UnderReviewAsync("Б1.О.12");
        var (head, _) = await DocumentData.SignatoryAsync(app, created.Document.DisciplineId, SignatoryRoles.DepartmentHead);
        var (umu, _) = await DocumentData.SignatoryAsync(app, created.Document.DisciplineId, SignatoryRoles.UmuHead);
        await ExpectAsync(methodist.PostAsJsonAsync(Url(created, "accept"), new DocumentVersionRequest(version)), DocumentStatuses.Approval);

        // Начальник УМУ возвращает документ, заведующий кафедрой отметиться не успел.
        version = await ExpectAsync(umu.PostAsJsonAsync(Url(created, "approval/reject"), new RejectRequest("Нет литературы")),
            DocumentStatuses.UnderReview);

        var returned = (await DetailsAsync(methodist, created)).Approval!;
        Assert.Equal([(SignatoryRoles.DepartmentHead, SignatureStatuses.Interrupted, "прерван"),
                (SignatoryRoles.UmuHead, SignatureStatuses.Rejected, "возвращено")],
            returned.Seats.Select(s => (s.Role, s.Status, s.StatusTitle)));
        // В таблице строка не менялась: прежний круг остаётся до нового приёма (решение B1).
        await app.WithDbAsync(async db => Assert.Equal([SignatureStatuses.Pending, SignatureStatuses.Rejected],
            await db.DocumentSignatures.Where(s => s.DocumentId == created.Document.Id).OrderBy(s => s.Role)
                .Select(s => s.Status).ToListAsync()));

        // Новый круг: обе строки снова «ожидает».
        await ExpectAsync(methodist.PostAsJsonAsync(Url(created, "accept"), new DocumentVersionRequest(version)), DocumentStatuses.Approval);
        Assert.All((await DetailsAsync(methodist, created)).Approval!.Seats,
            s => Assert.Equal((SignatureStatuses.Pending, "ожидает"), (s.Status, s.StatusTitle)));
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

    /// <summary>
    /// Решение автора 15.11.2026 (этап 6.1, C2): заведующий кафедрой согласует собственный РПД сам. Одна запись
    /// справочника — и автор документа, и подписант места; у учётной записи две роли — преподаватель и утверждающий.
    /// Ни приём, ни очередь, ни отметка не требуют, чтобы согласующий был не автором; своя отметка стоит в любой
    /// очерёдности — первой или последней.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Author_who_is_also_the_signatory_marks_their_own_document(bool authorMarksFirst)
    {
        var disciplineId = await DocumentData.DisciplineAsync(app, "Б1.О.13");
        var (head, headRecord) = await DocumentData.SignatoryAsync(app, disciplineId, SignatoryRoles.DepartmentHead,
            $"{AppRoles.Teacher},{AppRoles.Approver}");
        var (umu, umuRecord) = await DocumentData.SignatoryAsync(app, disciplineId, SignatoryRoles.UmuHead);
        var (methodist, _) = await DocumentData.UserAsync(app, AppRoles.Methodist);
        var template = await DocumentData.PublishedTemplateAsync(app);

        var created = await DocumentData.CreateAsync(app, head, template.Id, "Б1.О.13");
        Assert.Equal(headRecord.Id, created.Document.AuthorId);
        var version = await DocumentData.FillForSubmissionAsync(head, created);
        version = await ExpectAsync(head.PostAsJsonAsync(Url(created, "submit"), new DocumentVersionRequest(version)), DocumentStatuses.UnderReview);
        await ExpectAsync(methodist.PostAsJsonAsync(Url(created, "accept"), new DocumentVersionRequest(version)), DocumentStatuses.Approval);

        // Документ в очереди автора как в очереди любого согласующего; правок на согласовании нет, отметка и печать есть.
        var queued = Assert.Single(await QueueAsync(head), q => q.Id == created.Document.Id);
        Assert.Equal(headRecord.ShortName, queued.AuthorName);
        var forHead = await DetailsAsync(head, created);
        Assert.Equal((false, true, true), (forHead.Editable, forHead.Actions.Approve, forHead.Actions.Print));
        Assert.Equal([SignatoryRoles.DepartmentHead], forHead.Approval!.Seats.Where(s => s.Mine).Select(s => s.Role));
        Assert.Null(forHead.Approval.Returned);

        // Первая отметка — документ остаётся на согласовании, из очереди ушёл тот, кто отметил.
        var (first, last, lastRecord) = authorMarksFirst ? (head, umu, umuRecord) : (umu, head, headRecord);
        await ExpectAsync(first.PostAsync(Url(created, "approval/approve"), null), DocumentStatuses.Approval);
        Assert.DoesNotContain(await QueueAsync(first), q => q.Id == created.Document.Id);
        Assert.Contains(await QueueAsync(last), q => q.Id == created.Document.Id);

        // Последняя — «Согласован», в том числе когда последним отмечает сам автор.
        await ExpectAsync(last.PostAsync(Url(created, "approval/approve"), null), DocumentStatuses.Approved);
        Assert.DoesNotContain(await QueueAsync(last), q => q.Id == created.Document.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await head.PostAsync(Url(created, "approval/approve"), null)).StatusCode);

        // Автору правки закрыты, печать доступна — как и остальным участникам.
        var approved = await DetailsAsync(head, created);
        Assert.Equal((false, false, true), (approved.Editable, approved.Actions.Approve, approved.Actions.Print));
        Assert.All(approved.Approval!.Seats, s => Assert.Equal(SignatureStatuses.Approved, s.Status));
        var title = JsonNode.Parse(approved.Payload.GetProperty("title_page").GetRawText())!;
        Assert.Equal(HttpStatusCode.Conflict,
            (await head.PatchAsJsonAsync(Url(created, "sections/title_page"), Section(approved.Document.Version, title))).StatusCode);
        foreach (var participant in new[] { head, methodist, umu })
            Assert.Equal(HttpStatusCode.Accepted, (await participant.PostAsync(Url(created, "print/docx"), null)).StatusCode);

        await app.WithDbAsync(async db =>
        {
            Assert.NotNull((await db.Documents.SingleAsync(d => d.Id == created.Document.Id)).ApprovedAt);
            var history = await db.DocumentStatusHistory.Where(h => h.DocumentId == created.Document.Id).OrderBy(h => h.ChangedAt).ToListAsync();
            // «Согласован» ставит последний отметивший: заведующий — автор документа или начальник УМУ.
            Assert.Equal((DocumentStatuses.Approval, DocumentStatuses.Approved, lastRecord.Id), (history[^1].FromStatus!, history[^1].ToStatus, history[^1].ChangedBy));
            var rows = await db.DocumentSignatures.Where(s => s.DocumentId == created.Document.Id).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Equal((SignatureStatuses.Approved, true), (r.Status, r.ApprovedAt is not null)));
        });
    }

    /// <summary>
    /// «Отмеченные мной» (этап 6.1, C4; FR-REV-05, S-31): документы, где в текущем круге стоит отметка пользователя —
    /// согласие или возврат; чужих нет. Возврат оставляет отметки круга, новый приём сбрасывает их: документ снова в
    /// очереди и из списка отмеченных ушёл.
    /// </summary>
    [Fact]
    public async Task Marked_list_holds_my_marks_of_the_current_round_and_a_new_round_returns_the_document_to_the_queue()
    {
        var (_, methodist, created, version) = await UnderReviewAsync("Б1.О.13");
        var (head, headRecord) = await DocumentData.SignatoryAsync(app, created.Document.DisciplineId, SignatoryRoles.DepartmentHead);
        var (umu, _) = await DocumentData.SignatoryAsync(app, created.Document.DisciplineId, SignatoryRoles.UmuHead);
        var (stranger, _) = await DocumentData.UserAsync(app, AppRoles.Approver);
        var id = created.Document.Id;
        var authorName = "";
        await app.WithDbAsync(async db => authorName = (await db.Employees.SingleAsync(e => e.Id == created.Document.AuthorId)).ShortName);

        // Круг идёт, отметок нет: ни у кого из них документа в списке отмеченных нет.
        version = await ExpectAsync(methodist.PostAsJsonAsync(Url(created, "accept"), new DocumentVersionRequest(version)), DocumentStatuses.Approval);
        foreach (var client in new[] { head, umu, stranger })
            Assert.DoesNotContain(await MarkedAsync(client), m => m.Id == id);

        // Согласие заведующего: документ ушёл из его очереди и стоит в списке — с отметкой и её временем.
        await ExpectAsync(head.PostAsync(Url(created, "approval/approve"), null), DocumentStatuses.Approval);
        Assert.DoesNotContain(await QueueAsync(head), q => q.Id == id);
        var approved = Assert.Single(await MarkedAsync(head), m => m.Id == id);
        Assert.Equal((created.Document.DisciplineCode, created.Document.DisciplineName, created.Document.Type, authorName),
            (approved.DisciplineCode, approved.DisciplineName, approved.Type, approved.AuthorName));
        Assert.Equal((DocumentStatuses.Approval, DocumentStatuses.Title(DocumentStatuses.Approval), SignatureStatuses.Approved, "согласовано"),
            (approved.Status, approved.StatusTitle, approved.Mark, approved.MarkTitle));
        await app.WithDbAsync(async db => Assert.Equal(
            (await db.DocumentSignatures.SingleAsync(s => s.DocumentId == id && s.ResolvedUserId == headRecord.Id)).ApprovedAt,
            approved.MarkedAt));
        // Начальник УМУ ещё не отметился, посторонний согласующий документа не видит.
        foreach (var client in new[] { umu, stranger })
            Assert.DoesNotContain(await MarkedAsync(client), m => m.Id == id);

        // Возврат начальника УМУ: отметка «возвращено», её время — из истории статусов; документ у методиста.
        version = await ExpectAsync(umu.PostAsJsonAsync(Url(created, "approval/reject"), new RejectRequest("Нет литературы")),
            DocumentStatuses.UnderReview);
        var rejected = Assert.Single(await MarkedAsync(umu), m => m.Id == id);
        Assert.Equal((DocumentStatuses.UnderReview, SignatureStatuses.Rejected, "возвращено", await ReturnTimeAsync(id)),
            (rejected.Status, rejected.Mark, rejected.MarkTitle, rejected.MarkedAt));
        // Согласие заведующего принадлежит тому же кругу и остаётся, пока не начат новый; ждать отметок у обоих нечего.
        var kept = Assert.Single(await MarkedAsync(head), m => m.Id == id);
        Assert.Equal((DocumentStatuses.UnderReview, SignatureStatuses.Approved, approved.MarkedAt), (kept.Status, kept.Mark, kept.MarkedAt));
        foreach (var client in new[] { head, umu })
            Assert.DoesNotContain(await QueueAsync(client), q => q.Id == id);

        // Новый приём — новый круг: отметки сброшены, документ снова в очереди обоих и из списков отмеченных ушёл.
        await ExpectAsync(methodist.PostAsJsonAsync(Url(created, "accept"), new DocumentVersionRequest(version)), DocumentStatuses.Approval);
        foreach (var client in new[] { head, umu })
        {
            Assert.Contains(await QueueAsync(client), q => q.Id == id);
            Assert.DoesNotContain(await MarkedAsync(client), m => m.Id == id);
        }

        // Оба согласовали: документ «Согласован», у обоих в списке — «согласовано».
        await ExpectAsync(umu.PostAsync(Url(created, "approval/approve"), null), DocumentStatuses.Approval);
        await ExpectAsync(head.PostAsync(Url(created, "approval/approve"), null), DocumentStatuses.Approved);
        foreach (var client in new[] { head, umu })
        {
            var done = Assert.Single(await MarkedAsync(client), m => m.Id == id);
            Assert.Equal((DocumentStatuses.Approved, SignatureStatuses.Approved), (done.Status, done.Mark));
        }
    }

    /// <summary>
    /// В «Отмеченных мной» сверху — последние отметки, а документ, где у человека два места, стоит один раз: его
    /// отметки в документе ставятся вместе.
    /// </summary>
    [Fact]
    public async Task Marked_list_puts_the_latest_mark_first_and_lists_a_document_once()
    {
        // Два документа одной дисциплины в разных шаблонах: заведующий кафедрой и начальник УМУ у них общие.
        var (_, methodist, first, firstVersion) = await UnderReviewAsync("Б1.О.13");
        var (_, _, second, secondVersion) = await UnderReviewAsync("Б1.О.13");
        var (head, headRecord) = await DocumentData.SignatoryAsync(app, first.Document.DisciplineId, SignatoryRoles.DepartmentHead);
        await ExpectAsync(methodist.PostAsJsonAsync(Url(first, "accept"), new DocumentVersionRequest(firstVersion)), DocumentStatuses.Approval);
        await ExpectAsync(methodist.PostAsJsonAsync(Url(second, "accept"), new DocumentVersionRequest(secondVersion)), DocumentStatuses.Approval);

        // Во втором документе заведующий занимает и место начальника УМУ (ручное назначение, FR-REV-13).
        var assigned = await methodist.PostAsJsonAsync(
            Url(second, $"approval/seats/{SignatoryRoles.UmuHead}/assign"), new AssignRequest(headRecord.Id));
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);

        // Сначала он отмечает второй документ — обе его строки сразу, потом первый: последний в списке — сверху.
        await ExpectAsync(head.PostAsync(Url(second, "approval/approve"), null), DocumentStatuses.Approved);
        await ExpectAsync(head.PostAsync(Url(first, "approval/approve"), null), DocumentStatuses.Approval);

        var marked = (await MarkedAsync(head)).Where(m => m.Id == first.Document.Id || m.Id == second.Document.Id).ToList();
        Assert.Equal([first.Document.Id, second.Document.Id], marked.Select(m => m.Id));
        Assert.True(marked[0].MarkedAt > marked[1].MarkedAt);
        Assert.Equal([DocumentStatuses.Approval, DocumentStatuses.Approved], marked.Select(m => m.Status));
        await app.WithDbAsync(async db => Assert.Equal(2,
            await db.DocumentSignatures.CountAsync(s => s.DocumentId == second.Document.Id && s.ResolvedUserId == headRecord.Id)));
    }

    /// <summary>
    /// Пометка «с согласования» в очереди «Проверка» (этап 6.1, C4; FR-REV-01, S-30): у документа на проверке последний
    /// переход в этот статус пришёл с согласования — в ответе списка кто и когда вернул. У отправленного автором её нет;
    /// возврат автору и новая отправка её снимают. Пометку получают методист и администратор, автор — нет.
    /// </summary>
    [Fact]
    public async Task Review_queue_marks_the_documents_returned_from_approval()
    {
        var (author, methodist, returned, version) = await UnderReviewAsync("Б1.О.13");
        var (_, _, sent, _) = await UnderReviewAsync("Б1.О.12");
        var (head, headRecord) = await DocumentData.SignatoryAsync(app, returned.Document.DisciplineId, SignatoryRoles.DepartmentHead);
        var (umu, umuRecord) = await DocumentData.SignatoryAsync(app, returned.Document.DisciplineId, SignatoryRoles.UmuHead);
        var (admin, _) = await DocumentData.UserAsync(app, AppRoles.Admin);
        var (id, sentId) = (returned.Document.Id, sent.Document.Id);

        // Оба документа отправил автор — помечать нечего.
        var queue = (await ReviewQueueAsync(methodist)).Where(d => d.Id == id || d.Id == sentId).ToList();
        Assert.Equal(2, queue.Count);
        Assert.All(queue, d => Assert.Null(d.FromApproval));

        // Начальник УМУ вернул документ с согласования: на согласовании в очереди его не было, теперь он с пометкой.
        await ExpectAsync(methodist.PostAsJsonAsync(Url(returned, "accept"), new DocumentVersionRequest(version)), DocumentStatuses.Approval);
        Assert.DoesNotContain(await ReviewQueueAsync(methodist), d => d.Id == id);
        version = await ExpectAsync(umu.PostAsJsonAsync(Url(returned, "approval/reject"), new RejectRequest("Нет литературы")),
            DocumentStatuses.UnderReview);
        var firstReturn = Assert.Single(await ReviewQueueAsync(methodist), d => d.Id == id).FromApproval!;
        Assert.Equal((umuRecord.ShortName, await ReturnTimeAsync(id)), (firstReturn.By, firstReturn.At));
        // Отправленный автором — без пометки; администратору пометка видна так же, автору — нет.
        Assert.Null(Assert.Single(await ReviewQueueAsync(methodist), d => d.Id == sentId).FromApproval);
        Assert.Equal(firstReturn, Assert.Single(await ReviewQueueAsync(admin), d => d.Id == id).FromApproval);
        Assert.Null(Assert.Single(await ReviewQueueAsync(author), d => d.Id == id).FromApproval);

        // Второй круг и второй возврат — пометка про последний: кто вернул теперь, а не в первый раз.
        await ExpectAsync(methodist.PostAsJsonAsync(Url(returned, "accept"), new DocumentVersionRequest(version)), DocumentStatuses.Approval);
        version = await ExpectAsync(head.PostAsJsonAsync(Url(returned, "approval/reject"), new RejectRequest("Не согласен с темой")),
            DocumentStatuses.UnderReview);
        var secondReturn = Assert.Single(await ReviewQueueAsync(methodist), d => d.Id == id).FromApproval!;
        Assert.Equal((headRecord.ShortName, await ReturnTimeAsync(id)), (secondReturn.By, secondReturn.At));
        Assert.True(secondReturn.At > firstReturn.At);

        // Методист вернул документ автору, тот исправил и отправил снова: последний переход — уже не с согласования.
        var remark = await methodist.PostAsJsonAsync(Url(returned, "comments"), new CommentRequest("/thematic_plan/0/hours", "Разбейте часы по темам"));
        Assert.Equal(HttpStatusCode.Created, remark.StatusCode);
        var comment = (await remark.Content.ReadFromJsonAsync<CommentItem>())!;
        version = await ExpectAsync(methodist.PostAsJsonAsync(Url(returned, "return"), new ReturnRequest(version, null)), DocumentStatuses.Returned);
        Assert.DoesNotContain(await ReviewQueueAsync(methodist), d => d.Id == id);
        Assert.True((await author.PostAsync(Url(returned, $"comments/{comment.Id}/resolve"), null)).IsSuccessStatusCode);
        await ExpectAsync(author.PostAsJsonAsync(Url(returned, "submit"), new DocumentVersionRequest(version)), DocumentStatuses.UnderReview);
        Assert.Null(Assert.Single(await ReviewQueueAsync(methodist), d => d.Id == id).FromApproval);
    }

    /// <summary>
    /// Этап 7, A2: согласующий со строкой оставляет замечания к полям, пока документ на согласовании и его отметка ждёт;
    /// видят их все, кто видит документ. Согласовать при своём неснятом замечании нельзя, а снять его может только он сам.
    /// </summary>
    [Fact]
    public async Task Approver_comments_fields_while_awaiting_mark_and_approves_only_without_own_open_comments()
    {
        var (author, methodist, created, version) = await UnderReviewAsync("Б1.О.10");
        var (head, _) = await DocumentData.SignatoryAsync(app, created.Document.DisciplineId, SignatoryRoles.DepartmentHead);
        var (umu, umuRecord) = await DocumentData.SignatoryAsync(app, created.Document.DisciplineId, SignatoryRoles.UmuHead);
        var (stranger, _) = await DocumentData.UserAsync(app, AppRoles.Approver);
        await ExpectAsync(methodist.PostAsJsonAsync(Url(created, "accept"), new DocumentVersionRequest(version)), DocumentStatuses.Approval);

        // Утверждающий без строки — 403; методист на согласовании замечаний не оставляет — 409; автору не даёт политика.
        Assert.Equal(HttpStatusCode.Forbidden, (await CommentAsync(stranger, created, "/thematic_plan", "Не согласен")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await CommentAsync(methodist, created, "/thematic_plan", "Уточните темы")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CommentAsync(author, created, "/thematic_plan", "Своё")).StatusCode);

        var forUmu = (await DetailsAsync(umu, created)).Actions;
        Assert.Equal((true, true, true, false), (forUmu.Comment, forUmu.ResolveOwnComments, forUmu.Approve, forUmu.ResolveApprovalComments));
        var remark = await CreatedCommentAsync(umu, created, "/thematic_plan", "Темы раздела 2 не соответствуют плану");
        Assert.Equal((umuRecord.ShortName, true, false), (remark.AuthorName, remark.FromApproval, remark.IsResolved));
        foreach (var client in new[] { author, methodist, head, umu })
            Assert.Contains(await CommentsAsync(client, created), c => c.Id == remark.Id && c.FromApproval);

        // Своё неснятое замечание — согласие не проходит.
        var refused = await umu.PostAsync(Url(created, "approval/approve"), null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("Снимите свои замечания или верните документ.", (await refused.Content.ReadFromJsonAsync<ProblemDetails>())!.Detail);

        // Чужое замечание другой согласующий не снимает (403), методист и автор на согласовании — тоже (409).
        Assert.Equal(HttpStatusCode.Forbidden, (await ResolveAsync(head, created, remark)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ResolveAsync(methodist, created, remark)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ResolveAsync(author, created, remark)).StatusCode);

        // Снял своё — согласие проходит; после своей отметки замечаний уже не оставить.
        var resolved = (await (await ResolveAsync(umu, created, remark)).Content.ReadFromJsonAsync<CommentItem>())!;
        Assert.Equal((true, umuRecord.ShortName), (resolved.IsResolved, resolved.ResolvedByName));
        await ExpectAsync(umu.PostAsync(Url(created, "approval/approve"), null), DocumentStatuses.Approval);
        var late = await CommentAsync(umu, created, "/thematic_plan", "Ещё одно");
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal("Отметка уже поставлена", (await late.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
        forUmu = (await DetailsAsync(umu, created)).Actions;
        Assert.Equal((false, false, false), (forUmu.Comment, forUmu.ResolveOwnComments, forUmu.Approve));

        // Согласованный документ — вне согласования: строка есть, а замечание — 409.
        await ExpectAsync(head.PostAsync(Url(created, "approval/approve"), null), DocumentStatuses.Approved);
        Assert.Equal(HttpStatusCode.Conflict, (await CommentAsync(head, created, "/thematic_plan", "Поздно")).StatusCode);
    }

    /// <summary>
    /// Этап 7, A2: после возврата с согласования замечания согласующего снимает методист на проверке — как свои, —
    /// или, вернув документ, автор на доработке. Сам согласующий их уже не снимает; приём — без неснятых замечаний.
    /// </summary>
    [Fact]
    public async Task After_return_from_approval_methodist_or_author_resolves_the_approver_comments()
    {
        var (author, methodist, created, version) = await UnderReviewAsync("Б1.О.13");
        var (umu, _) = await DocumentData.SignatoryAsync(app, created.Document.DisciplineId, SignatoryRoles.UmuHead);
        var (head, _) = await DocumentData.SignatoryAsync(app, created.Document.DisciplineId, SignatoryRoles.DepartmentHead);
        var (otherMethodist, _) = await DocumentData.UserAsync(app, AppRoles.Methodist);
        await ExpectAsync(methodist.PostAsJsonAsync(Url(created, "accept"), new DocumentVersionRequest(version)), DocumentStatuses.Approval);

        var first = await CreatedCommentAsync(umu, created, "/thematic_plan", "Темы раздела 2 не соответствуют плану");
        var second = await CreatedCommentAsync(umu, created, "/goals_and_objectives", "Цели сформулированы слишком общо");
        // Общий комментарий возврата по-прежнему обязателен.
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await umu.PostAsJsonAsync(Url(created, "approval/reject"), new RejectRequest(" "))).StatusCode);
        version = await ExpectAsync(umu.PostAsJsonAsync(Url(created, "approval/reject"), new RejectRequest("Замечания у полей")),
            DocumentStatuses.UnderReview);

        // Вне согласования строка согласования замечаний не даёт: ни оставить новое, ни снять своё.
        Assert.Equal(HttpStatusCode.Conflict, (await CommentAsync(umu, created, "/thematic_plan", "Ещё")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await CommentAsync(head, created, "/thematic_plan", "И ещё")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ResolveAsync(umu, created, first)).StatusCode);

        // Методист на проверке снимает замечание согласующего — любой методист, это не чужое замечание методиста.
        Assert.True((await DetailsAsync(otherMethodist, created)).Actions.ResolveApprovalComments);
        var resolved = (await (await ResolveAsync(otherMethodist, created, first)).Content.ReadFromJsonAsync<CommentItem>())!;
        Assert.Equal((true, true), (resolved.IsResolved, resolved.FromApproval));
        // Своё замечание методиста — не согласования: его другой методист не снимает.
        var own = await CreatedCommentAsync(methodist, created, "/title_page", "Проверьте год утверждения");
        Assert.False(own.FromApproval);
        Assert.Equal(HttpStatusCode.Forbidden, (await ResolveAsync(otherMethodist, created, own)).StatusCode);

        // Неснятое замечание согласующего держит приём; методист возвращает документ автору.
        Assert.Equal(HttpStatusCode.Conflict,
            (await methodist.PostAsJsonAsync(Url(created, "accept"), new DocumentVersionRequest(version))).StatusCode);
        version = await ExpectAsync(methodist.PostAsJsonAsync(Url(created, "return"), new ReturnRequest(version, null)),
            DocumentStatuses.Returned);

        // Автор на доработке снимает и замечание согласующего, и методиста; новый приём начинает новый круг.
        foreach (var comment in new[] { second, own })
            Assert.Equal(HttpStatusCode.OK, (await ResolveAsync(author, created, comment)).StatusCode);
        version = await ExpectAsync(author.PostAsJsonAsync(Url(created, "submit"), new DocumentVersionRequest(version)),
            DocumentStatuses.UnderReview);
        await ExpectAsync(methodist.PostAsJsonAsync(Url(created, "accept"), new DocumentVersionRequest(version)), DocumentStatuses.Approval);
        // Замечания прежнего круга сняты — согласовать они не мешают.
        await ExpectAsync(umu.PostAsync(Url(created, "approval/approve"), null), DocumentStatuses.Approval);
    }

    private static Task<HttpResponseMessage> CommentAsync(HttpClient client, DocumentDetails document, string path, string text) =>
        client.PostAsJsonAsync(Url(document, "comments"), new CommentRequest(path, text));

    private static async Task<CommentItem> CreatedCommentAsync(HttpClient client, DocumentDetails document, string path, string text)
    {
        var response = await CommentAsync(client, document, path, text);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<CommentItem>())!;
    }

    private static Task<HttpResponseMessage> ResolveAsync(HttpClient client, DocumentDetails document, CommentItem comment) =>
        client.PostAsync(Url(document, $"comments/{comment.Id}/resolve"), null);

    private static async Task<List<CommentItem>> CommentsAsync(HttpClient client, DocumentDetails document) =>
        (await client.GetFromJsonAsync<List<CommentItem>>(Url(document, "comments")))!;

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

    private static async Task<List<MarkedApprovalItem>> MarkedAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<MarkedApprovalItem>>("/api/approvals/marked"))!;

    // Очередь «Проверка»: документы на проверке; методисту и администратору — со всеми авторами, автору — только его.
    private static async Task<List<DocumentListItem>> ReviewQueueAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<DocumentListItem>>("/api/documents?status=under_review"))!;

    // Время последнего возврата с согласования — по истории статусов, как его берёт и сервер.
    private async Task<DateTimeOffset> ReturnTimeAsync(Guid documentId)
    {
        var at = default(DateTimeOffset);
        await app.WithDbAsync(async db => at = await db.DocumentStatusHistory
            .Where(h => h.DocumentId == documentId && h.FromStatus == DocumentStatuses.Approval && h.ToStatus == DocumentStatuses.UnderReview)
            .MaxAsync(h => h.ChangedAt));
        return at;
    }

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
