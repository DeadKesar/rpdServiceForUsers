using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Features.Approvals;
using RpdFos.Api.Features.Documents;
using RpdFos.Api.Features.Notifications;
using RpdFos.Api.Features.Users;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using Xunit;
using S = RpdFos.Core.Documents.DocumentStatuses;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Обратные переходы документа (этап 7, A8; FR-ADM-04, решение 2): методист возвращает согласованный документ на
/// исправление, администратор принудительно меняет статус — только назад. Методисты и администратор — настоящие учётные
/// записи, созданные через страницу администратора: получатели рассылки берутся из ролей в базе. Согласующие — обезличенные
/// записи файла должностей плана 09.04.04: заведующий кафедрой дисциплины и начальник УМУ. Имя у всех созданных здесь
/// учётных записей одно — «Получатель У. О.».
/// </summary>
[Collection(DatabaseCollection.Name)]
public class DocumentReopeningApiTests(DatabaseFixture app)
{
    private const string Reason = "Исправить литературу";

    [Fact]
    public async Task Methodist_returns_an_approved_document_for_correction_and_everyone_involved_is_told()
    {
        var c = await ApprovedAsync("Б1.О.10");
        var url = Url(c.Created, "return-for-correction");

        // Кнопка — методисту; автору, администратору и согласующему её нет.
        Assert.True((await DetailsAsync(c.Methodist, c.Created)).Actions.ReturnForCorrection);
        foreach (var client in new[] { c.Author, c.Admin, c.Head })
            Assert.False((await DetailsAsync(client, c.Created)).Actions.ReturnForCorrection);

        // Не методист — 403; причина обязательна — 422; чужая версия — 409. Документ не менялся.
        foreach (var client in new[] { c.Author, c.Admin, c.Head })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(url, new CorrectionRequest(c.Version, Reason))).StatusCode);
        foreach (var empty in new[] { null, "", "   " })
            Assert.Equal(HttpStatusCode.UnprocessableEntity,
                (await c.Methodist.PostAsJsonAsync(url, new CorrectionRequest(c.Version, empty))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await c.Methodist.PostAsJsonAsync(url, new CorrectionRequest(c.Version - 1, Reason))).StatusCode);
        Assert.Equal(S.Approved, (await DetailsAsync(c.Methodist, c.Created)).Document.Status);

        await ExpectAsync(c.Methodist.PostAsJsonAsync(url, new CorrectionRequest(c.Version, $"  {Reason}  ")), S.UnderReview);

        // Методист и администратор видят плашку «кто, когда, причина»; автор и согласующий — нет.
        var forMethodist = await DetailsAsync(c.Methodist, c.Created);
        var corrected = forMethodist.Approval!.Corrected!;
        Assert.Equal((c.MethodistRecord.ShortName, Reason), (corrected.By, corrected.Comment));
        Assert.Null(forMethodist.Approval.Returned);
        Assert.Equal(corrected, (await DetailsAsync(c.Admin, c.Created)).Approval!.Corrected);
        Assert.Null((await DetailsAsync(c.Author, c.Created)).Approval);
        Assert.Null((await DetailsAsync(c.Head, c.Created)).Approval);

        // Документ снова на проверке у методиста: он правит его и принимает.
        Assert.Equal((true, true), (forMethodist.Editable, forMethodist.Actions.Review));
        Assert.Contains(await ReviewQueueAsync(c.Methodist), d => d.Id == c.Created.Document.Id);

        // Согласующие: документ ушёл из очереди, отметить его нельзя — даже тому, кто уже согласовал.
        foreach (var approver in new[] { c.Head, c.Umu })
        {
            Assert.DoesNotContain(await QueueAsync(approver), q => q.Id == c.Created.Document.Id);
            Assert.Equal(HttpStatusCode.Conflict, (await approver.PostAsync(Url(c.Created, "approval/approve"), null)).StatusCode);
        }

        // Автору, согласующим последнего круга и остальным методистам — по одному уведомлению; совершившему возврат — нет.
        foreach (var client in new[] { c.Author, c.Head, c.Umu, c.Other })
        {
            var item = Assert.Single(await FeedAsync(client, c.Created, NotificationEvents.ReturnedForCorrection));
            Assert.StartsWith($"Возвращён на исправление: {c.Created.Document.Title}. Методист: {c.MethodistRecord.ShortName}", item.Text);
            Assert.EndsWith($"Причина: {Reason}.", item.Text);
        }
        foreach (var client in new[] { c.Methodist, c.Admin })
            Assert.Empty(await FeedAsync(client, c.Created, NotificationEvents.ReturnedForCorrection));
        var payload = await PayloadAsync(Assert.Single(await FeedAsync(c.Author, c.Created, NotificationEvents.ReturnedForCorrection)));
        Assert.Equal((c.MethodistRecord.ShortName, Reason), (payload.GetProperty("by").GetString(), payload.GetProperty("comment").GetString()));

        await app.WithDbAsync(async db =>
        {
            var document = await db.Documents.SingleAsync(d => d.Id == c.Created.Document.Id);
            // Время согласования сброшено; снимок согласованной версии остался, возврат добавил свой.
            Assert.Null(document.ApprovedAt);
            var triggers = await db.DocumentVersions.Where(v => v.DocumentId == document.Id).OrderBy(v => v.VersionNumber)
                .Select(v => v.TriggerEvent).ToListAsync();
            Assert.Equal(1, triggers.Count(t => t == VersionTriggers.Approved));
            Assert.Equal(VersionTriggers.StatusChange, triggers[^1]);

            var change = await db.DocumentStatusHistory.Where(h => h.DocumentId == document.Id).OrderBy(h => h.ChangedAt).LastAsync();
            Assert.Equal((S.Approved, S.UnderReview, c.MethodistRecord.Id, Reason), (change.FromStatus, change.ToStatus, change.ChangedBy, change.Comment));
            Assert.Equal(change.ChangedAt, corrected.At);

            var audit = await db.AuditLog.SingleAsync(a => a.EntityId == document.Id && a.EventType == AuditEvents.ReturnedForCorrection);
            Assert.Equal((c.MethodistRecord.Id, AuditEntities.Document, change.ChangedAt), (audit.UserId, audit.EntityType, audit.OccurredAt));
            var logged = JsonDocument.Parse(audit.Payload).RootElement;
            Assert.Equal((S.Approved, S.UnderReview, Reason),
                (logged.GetProperty("from").GetString(), logged.GetProperty("to").GetString(), logged.GetProperty("comment").GetString()));

            // Строки согласованного круга остаются до нового приёма, как после возврата с согласования.
            Assert.All(await db.DocumentSignatures.Where(s => s.DocumentId == document.Id).ToListAsync(),
                row => Assert.Equal(SignatureStatuses.Approved, row.Status));
        });
    }

    [Fact]
    public async Task After_the_return_the_next_acceptance_starts_a_new_round_and_the_document_is_approved_again()
    {
        var c = await ApprovedAsync("Б1.О.12");
        var firstApprovedAt = (await ApprovedAtAsync(c.Created.Document.Id))!.Value;
        var version = await ExpectAsync(
            c.Methodist.PostAsJsonAsync(Url(c.Created, "return-for-correction"), new CorrectionRequest(c.Version, Reason)), S.UnderReview);

        // Новый приём начинает круг заново: отметки прежнего сброшены, плашки больше нет, согласующие снова в очереди.
        await ExpectAsync(c.Methodist.PostAsJsonAsync(Url(c.Created, "accept"), new DocumentVersionRequest(version)), S.Approval);
        var round = (await DetailsAsync(c.Methodist, c.Created)).Approval!;
        Assert.Null(round.Corrected);
        Assert.Null(round.Returned);
        Assert.All(round.Seats, s => Assert.Equal((SignatureStatuses.Pending, null, null), (s.Status, s.MarkedAt, s.Comment)));
        foreach (var approver in new[] { c.Head, c.Umu })
            Assert.Contains(await QueueAsync(approver), q => q.Id == c.Created.Document.Id);

        // Второй круг кончился возвратом с согласования: плашка — о нём, а не о прошлом возврате на исправление.
        version = await ExpectAsync(
            c.Umu.PostAsJsonAsync(Url(c.Created, "approval/reject"), new RejectRequest("Нет литературы")), S.UnderReview);
        var back = (await DetailsAsync(c.Methodist, c.Created)).Approval!;
        Assert.Null(back.Corrected);
        Assert.Equal(c.UmuRecord.ShortName, back.Returned!.By);

        // Третий круг: отметили все — снова «Согласован», время согласования новое, снимок согласованной версии второй.
        await ExpectAsync(c.Methodist.PostAsJsonAsync(Url(c.Created, "accept"), new DocumentVersionRequest(version)), S.Approval);
        await ExpectAsync(c.Umu.PostAsync(Url(c.Created, "approval/approve"), null), S.Approval);
        await ExpectAsync(c.Head.PostAsync(Url(c.Created, "approval/approve"), null), S.Approved);
        Assert.Null((await DetailsAsync(c.Admin, c.Created)).Approval!.Corrected);

        await app.WithDbAsync(async db =>
        {
            var document = await db.Documents.SingleAsync(d => d.Id == c.Created.Document.Id);
            Assert.True(document.ApprovedAt > firstApprovedAt);
            Assert.Equal(2, await db.DocumentVersions.CountAsync(v => v.DocumentId == document.Id && v.TriggerEvent == VersionTriggers.Approved));

            var history = await db.DocumentStatusHistory.Where(h => h.DocumentId == document.Id).OrderBy(h => h.ChangedAt)
                .Select(h => new { h.FromStatus, h.ToStatus }).ToListAsync();
            Assert.Equal(
                [
                    (S.UnderReview, S.Accepted), (S.Accepted, S.Approval), (S.Approval, S.Approved), (S.Approved, S.UnderReview),
                    (S.UnderReview, S.Accepted), (S.Accepted, S.Approval), (S.Approval, S.UnderReview),
                    (S.UnderReview, S.Accepted), (S.Accepted, S.Approval), (S.Approval, S.Approved)
                ],
                history.TakeLast(10).Select(h => (h.FromStatus!, h.ToStatus)));
            var events = await db.AuditLog.Where(a => a.EntityId == document.Id).OrderBy(a => a.OccurredAt).Select(a => a.EventType).ToListAsync();
            Assert.Equal(
                [
                    AuditEvents.ApprovalStarted, AuditEvents.SignatureApproved, AuditEvents.SignatureApproved,
                    AuditEvents.ReturnedForCorrection,
                    AuditEvents.ApprovalStarted, AuditEvents.SignatureRejected,
                    AuditEvents.ApprovalStarted, AuditEvents.SignatureApproved, AuditEvents.SignatureApproved
                ],
                events);
        });
    }

    /// <summary>
    /// Уточнение планировщика: на дисциплину и линию шаблона — не больше одного незавершённого документа. Возврат и
    /// принудительная смена возвращают согласованный документ в работу, поэтому при другом незавершённом документе — 409;
    /// документ, который и так в работе, число незавершённых не меняет.
    /// </summary>
    [Fact]
    public async Task Return_for_correction_needs_an_approved_document_and_no_other_unfinished_one()
    {
        // Документ на проверке не согласован: возвращать нечего.
        var review = await UnderReviewAsync("Б1.О.12");
        var notApproved = await review.Methodist.PostAsJsonAsync(
            Url(review.Created, "return-for-correction"), new CorrectionRequest(review.Version, Reason));
        Assert.Equal(HttpStatusCode.Conflict, notApproved.StatusCode);
        Assert.Equal("Действие недоступно в этом статусе", (await notApproved.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);

        // У дисциплины согласованного документа уже есть другой документ той же линии шаблона — в работе.
        var c = await ApprovedAsync("Б1.О.13");
        var other = Guid.Empty;
        await app.WithDbAsync(async db =>
        {
            var source = await db.Documents.SingleAsync(d => d.Id == c.Created.Document.Id);
            var second = new Document
            {
                Type = source.Type, TemplateId = source.TemplateId, TemplateRevision = source.TemplateRevision,
                AuthorId = source.AuthorId, DisciplineId = source.DisciplineId, Title = source.Title, Payload = source.Payload
            };
            db.Documents.Add(second);
            await db.SaveChangesAsync();
            other = second.Id;
        });

        var url = Url(c.Created, "return-for-correction");
        var blocked = await c.Methodist.PostAsJsonAsync(url, new CorrectionRequest(c.Version, Reason));
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        var problem = (await blocked.Content.ReadFromJsonAsync<ProblemDetails>())!;
        Assert.Equal("Есть незавершённый документ", problem.Title);
        Assert.Equal(other.ToString(), problem.Extensions["documentId"]?.ToString());
        // То же — принудительная смена: она вернула бы согласованный документ в работу.
        var forced = await c.Admin.PostAsJsonAsync(Url(c.Created, "force-status"), new ForceStatusRequest(c.Version, S.UnderReview, "Проверка"));
        Assert.Equal(HttpStatusCode.Conflict, forced.StatusCode);
        Assert.Equal(S.Approved, (await DetailsAsync(c.Methodist, c.Created)).Document.Status);

        // Но другой документ, который и так в работе, смене статуса не мешает: число незавершённых не меняется.
        var otherVersion = (await c.Admin.GetFromJsonAsync<DocumentDetails>($"/api/documents/{other}"))!.Document.Version;
        await ExpectAsync(c.Admin.PostAsJsonAsync($"/api/documents/{other}/force-status",
            new ForceStatusRequest(otherVersion, S.SavedDraft, "Проверка")), S.SavedDraft);

        // Завершённый документ мешать перестаёт: согласованный, архивный и удалённый не считаются.
        await SetStatusAsync(other, S.Archived);
        await ExpectAsync(c.Methodist.PostAsJsonAsync(url, new CorrectionRequest(c.Version, Reason)), S.UnderReview);
    }

    [Fact]
    public async Task Admin_forces_the_status_back_from_any_status_to_each_allowed_target()
    {
        var (_, created) = await DraftAsync("Б1.О.10");
        var (admin, adminRecord) = await AccountAsync(AppRoles.Admin);
        string[] sources = [S.Draft, S.SavedDraft, S.UnderReview, S.Returned, S.Accepted, S.Approval, S.Approved, S.Archived];
        string[] targets = [S.SavedDraft, S.Returned, S.UnderReview];

        // Каждый целевой статус из каждого исходного, кроме себя самого: состояние выставляется напрямую, а путь к каждому
        // из восьми статусов через интерфейс занял бы десятки запросов — смена зависит только от статуса.
        var expected = new List<(string From, string To, string Comment)>();
        foreach (var source in sources)
            foreach (var target in targets.Where(t => t != source))
            {
                await SetStatusAsync(created.Document.Id, source, approvedAt: DateTimeOffset.UtcNow);
                var version = (await DetailsAsync(admin, created)).Document.Version;
                var comment = $"{source} → {target}";
                await ExpectAsync(admin.PostAsJsonAsync(Url(created, "force-status"), new ForceStatusRequest(version, target, comment)), target);
                // Согласия у документа больше нет, откуда бы он ни пришёл.
                Assert.Null(await ApprovedAtAsync(created.Document.Id));
                expected.Add((source, target, comment));
            }
        Assert.Equal(21, expected.Count);

        await app.WithDbAsync(async db =>
        {
            // Каждый переход — в истории статусов (с комментарием) и в журнале аудита.
            var history = await db.DocumentStatusHistory.Where(h => h.DocumentId == created.Document.Id && h.ChangedBy == adminRecord.Id)
                .OrderBy(h => h.ChangedAt).ToListAsync();
            Assert.Equal(expected, history.Select(h => (h.FromStatus!, h.ToStatus, h.Comment!)).ToList());

            var audit = await db.AuditLog.Where(a => a.EntityId == created.Document.Id && a.EventType == AuditEvents.StatusForced)
                .OrderBy(a => a.OccurredAt).ToListAsync();
            Assert.Equal(expected.Count, audit.Count);
            for (var i = 0; i < audit.Count; i++)
            {
                var logged = JsonDocument.Parse(audit[i].Payload).RootElement;
                Assert.Equal(expected[i],
                    (logged.GetProperty("from").GetString()!, logged.GetProperty("to").GetString()!, logged.GetProperty("comment").GetString()!));
                Assert.Equal((adminRecord.Id, AuditEntities.Document, history[i].ChangedAt), (audit[i].UserId, audit[i].EntityType, audit[i].OccurredAt));
                // Признак прерванного круга — только у смены с согласования.
                Assert.Equal(expected[i].From == S.Approval, logged.TryGetProperty("approval_interrupted", out var interrupted) && interrupted.GetBoolean());
            }
            // Документ пришёл на проверку не отправкой автора — но дата отправки у него есть, по ней он в очереди.
            Assert.NotNull((await db.Documents.SingleAsync(d => d.Id == created.Document.Id)).SubmittedAt);
        });
    }

    [Fact]
    public async Task Forced_status_refuses_forbidden_targets_missing_comments_other_roles_stale_versions_and_deleted_documents()
    {
        var (author, created) = await DraftAsync("Б1.О.12");
        var (admin, _) = await AccountAsync(AppRoles.Admin);
        var (methodist, _) = await AccountAsync(AppRoles.Methodist);
        var (approver, _) = await DocumentData.SignatoryAsync(app, created.Document.DisciplineId, SignatoryRoles.UmuHead);
        var url = Url(created, "force-status");
        await SetStatusAsync(created.Document.Id, S.UnderReview);
        var version = (await DetailsAsync(admin, created)).Document.Version;

        // Список целей — только администратору: все допустимые, кроме нынешнего статуса.
        var targets = (await DetailsAsync(admin, created)).Actions.ForceTargets!;
        Assert.Equal([(S.SavedDraft, "Сохранённый черновик"), (S.Returned, "На доработке")], targets.Select(t => (t.Status, t.Title)));
        foreach (var client in new[] { author, methodist })
            Assert.Null((await DetailsAsync(client, created)).Actions.ForceTargets);

        // Не администратор — 403.
        foreach (var client in new[] { author, methodist, approver })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(url, new ForceStatusRequest(version, S.Returned, "Нужно"))).StatusCode);

        // Недопустимая цель — 422: вперёд и в «Черновик» не переводят; пустой комментарий — 422.
        foreach (var forbidden in new[] { S.Draft, S.Accepted, S.Approval, S.Approved, S.Archived, S.Deleted, "bogus", "", null })
            Assert.Equal(HttpStatusCode.UnprocessableEntity,
                (await admin.PostAsJsonAsync(url, new ForceStatusRequest(version, forbidden, "Нужно"))).StatusCode);
        foreach (var comment in new[] { null, "", "  " })
            Assert.Equal(HttpStatusCode.UnprocessableEntity,
                (await admin.PostAsJsonAsync(url, new ForceStatusRequest(version, S.Returned, comment))).StatusCode);

        // Тот же статус — 409; чужая версия — 409.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync(url, new ForceStatusRequest(version, S.UnderReview, "Нужно"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync(url, new ForceStatusRequest(version - 1, S.Returned, "Нужно"))).StatusCode);

        // Ничего не записано, документ не менялся.
        Assert.Equal((S.UnderReview, version), ((await DetailsAsync(admin, created)).Document.Status, (await DetailsAsync(admin, created)).Document.Version));
        await app.WithDbAsync(async db =>
        {
            Assert.Equal(0, await db.AuditLog.CountAsync(a => a.EntityId == created.Document.Id && a.EventType == AuditEvents.StatusForced));
            Assert.Equal(0, await db.DocumentStatusHistory.CountAsync(h => h.DocumentId == created.Document.Id && h.ToStatus != S.Draft));
        });

        // Удалённый документ администратор не видит: 404, как у остальных.
        var deleted = await DocumentData.CreateAsync(app, author, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.13");
        Assert.Equal(HttpStatusCode.NoContent,
            (await author.DeleteAsync($"/api/documents/{deleted.Document.Id}?version={deleted.Document.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PostAsJsonAsync(Url(deleted, "force-status"), new ForceStatusRequest(deleted.Document.Version + 1, S.Returned, "Нужно"))).StatusCode);
    }

    [Fact]
    public async Task Forced_status_from_approval_interrupts_the_round_and_tells_the_author_and_methodists()
    {
        var c = await UnderReviewAsync("Б1.О.10");
        await ExpectAsync(c.Methodist.PostAsJsonAsync(Url(c.Created, "accept"), new DocumentVersionRequest(c.Version)), S.Approval);
        await ExpectAsync(c.Head.PostAsync(Url(c.Created, "approval/approve"), null), S.Approval);
        var version = (await DetailsAsync(c.Admin, c.Created)).Document.Version;

        version = await ExpectAsync(
            c.Admin.PostAsJsonAsync(Url(c.Created, "force-status"), new ForceStatusRequest(version, S.UnderReview, " Согласующий в отпуске ")),
            S.UnderReview);

        // Круг прерван, как при возврате: документ ушёл из очереди, ни согласовать, ни вернуть его уже нельзя.
        Assert.DoesNotContain(await QueueAsync(c.Umu), q => q.Id == c.Created.Document.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await c.Umu.PostAsync(Url(c.Created, "approval/approve"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await c.Umu.PostAsJsonAsync(Url(c.Created, "approval/reject"), new RejectRequest("Нет литературы"))).StatusCode);
        Assert.Contains(await ReviewQueueAsync(c.Methodist), d => d.Id == c.Created.Document.Id);

        // Автору и методистам — «статус изменён принудительно»; согласующим и самому администратору — нет.
        foreach (var client in new[] { c.Author, c.Methodist, c.Other })
        {
            var item = Assert.Single(await FeedAsync(client, c.Created, NotificationEvents.StatusForced));
            Assert.StartsWith($"Статус документа изменён принудительно: {c.Created.Document.Title}. Было: «На согласовании», стало: «На проверке».", item.Text);
            Assert.EndsWith("Комментарий: Согласующий в отпуске.", item.Text);
        }
        foreach (var client in new[] { c.Head, c.Umu, c.Admin })
            Assert.Empty(await FeedAsync(client, c.Created, NotificationEvents.StatusForced));
        var payload = await PayloadAsync(Assert.Single(await FeedAsync(c.Author, c.Created, NotificationEvents.StatusForced)));
        Assert.Equal((c.AdminRecord.ShortName, S.Approval, S.UnderReview, "Согласующий в отпуске"),
            (payload.GetProperty("by").GetString(), payload.GetProperty("from").GetString(), payload.GetProperty("to").GetString(),
                payload.GetProperty("comment").GetString()));

        await app.WithDbAsync(async db =>
        {
            var audit = await db.AuditLog.SingleAsync(a => a.EntityId == c.Created.Document.Id && a.EventType == AuditEvents.StatusForced);
            var logged = JsonDocument.Parse(audit.Payload).RootElement;
            Assert.Equal((c.AdminRecord.Id, true), (audit.UserId, logged.GetProperty("approval_interrupted").GetBoolean()));
            var change = await db.DocumentStatusHistory.Where(h => h.DocumentId == c.Created.Document.Id).OrderBy(h => h.ChangedAt).LastAsync();
            Assert.Equal((S.Approval, S.UnderReview, c.AdminRecord.Id, "Согласующий в отпуске"),
                (change.FromStatus, change.ToStatus, change.ChangedBy, change.Comment));
        });

        // Следующий приём начинает новый круг: отметка Head из прерванного сброшена, оба снова в очереди.
        await ExpectAsync(c.Methodist.PostAsJsonAsync(Url(c.Created, "accept"), new DocumentVersionRequest(version)), S.Approval);
        var round = (await DetailsAsync(c.Methodist, c.Created)).Approval!;
        Assert.All(round.Seats, s => Assert.Equal((SignatureStatuses.Pending, null), (s.Status, s.MarkedAt)));
        foreach (var approver in new[] { c.Head, c.Umu })
            Assert.Contains(await QueueAsync(approver), q => q.Id == c.Created.Document.Id);
        await ExpectAsync(c.Umu.PostAsync(Url(c.Created, "approval/approve"), null), S.Approval);
        await ExpectAsync(c.Head.PostAsync(Url(c.Created, "approval/approve"), null), S.Approved);
    }

    private sealed record Circle(
        HttpClient Author, Employee AuthorRecord, HttpClient Methodist, Employee MethodistRecord, HttpClient Other,
        HttpClient Admin, Employee AdminRecord, HttpClient Head, Employee HeadRecord, HttpClient Umu, Employee UmuRecord,
        DocumentDetails Created, int Version);

    // Документ на проверке и все участники круга: автор, два методиста, администратор и оба согласующих.
    private async Task<Circle> UnderReviewAsync(string code)
    {
        var (author, authorRecord) = await DocumentData.UserAsync(app);
        var (methodist, methodistRecord) = await AccountAsync(AppRoles.Methodist);
        var (other, _) = await AccountAsync(AppRoles.Methodist);
        var (admin, adminRecord) = await AccountAsync(AppRoles.Admin);
        var created = await DocumentData.CreateAsync(app, author, (await DocumentData.PublishedTemplateAsync(app)).Id, code);
        var (head, headRecord) = await DocumentData.SignatoryAsync(app, created.Document.DisciplineId, SignatoryRoles.DepartmentHead);
        var (umu, umuRecord) = await DocumentData.SignatoryAsync(app, created.Document.DisciplineId, SignatoryRoles.UmuHead);
        var version = await DocumentData.FillForSubmissionAsync(author, created);
        version = await ExpectAsync(author.PostAsJsonAsync(Url(created, "submit"), new DocumentVersionRequest(version)), S.UnderReview);
        return new(author, authorRecord, methodist, methodistRecord, other, admin, adminRecord, head, headRecord, umu, umuRecord, created, version);
    }

    // Согласованный документ: приём запускает круг сам, согласующие отмечают в любом порядке.
    private async Task<Circle> ApprovedAsync(string code)
    {
        var c = await UnderReviewAsync(code);
        await ExpectAsync(c.Methodist.PostAsJsonAsync(Url(c.Created, "accept"), new DocumentVersionRequest(c.Version)), S.Approval);
        await ExpectAsync(c.Head.PostAsync(Url(c.Created, "approval/approve"), null), S.Approval);
        var version = await ExpectAsync(c.Umu.PostAsync(Url(c.Created, "approval/approve"), null), S.Approved);
        return c with { Version = version };
    }

    // Только созданный документ автора: содержимое не нужно — принудительная смена зависит от статуса.
    private async Task<(HttpClient Author, DocumentDetails Created)> DraftAsync(string code)
    {
        var (author, _) = await DocumentData.UserAsync(app);
        return (author, await DocumentData.CreateAsync(app, author, (await DocumentData.PublishedTemplateAsync(app)).Id, code));
    }

    // Состояние, до которого через интерфейс долго идти: статус и время согласования выставляются напрямую.
    private async Task SetStatusAsync(Guid documentId, string status, DateTimeOffset? approvedAt = null) =>
        await app.WithDbAsync(async db => await db.Documents.Where(d => d.Id == documentId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, status).SetProperty(d => d.ApprovedAt, approvedAt)));

    private async Task<DateTimeOffset?> ApprovedAtAsync(Guid documentId)
    {
        DateTimeOffset? at = null;
        await app.WithDbAsync(async db => at = await db.Documents.Where(d => d.Id == documentId).Select(d => d.ApprovedAt).SingleAsync());
        return at;
    }

    // Учётная запись с ролью — через страницу администратора, как в жизни: получатели «всем методистам» берутся из ролей
    // учётных записей в базе.
    private async Task<(HttpClient Client, Employee Employee)> AccountAsync(string role)
    {
        var admin = app.CreateClientFor(AppRoles.Admin);
        var created = await admin.PostAsJsonAsync("/api/admin/users", new CreateUserRequest(
            $"reopen-{Guid.NewGuid():N}@test.local", "Получатель Уведомлений Обезличенный", "Test-Passw0rd", [role]));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var account = (await created.Content.ReadFromJsonAsync<UserItem>())!;
        Employee? employee = null;
        await app.WithDbAsync(async db => employee = await db.Employees.SingleAsync(e => e.Id == account.DirectoryUserId));
        return (app.CreateClientFor(role, employee!.IdentitySubject), employee);
    }

    private static async Task<List<NotificationItem>> FeedAsync(HttpClient client, DocumentDetails document, string eventType) =>
        (await client.GetFromJsonAsync<List<NotificationItem>>($"/api/notifications?limit={NotificationsModule.MaxLimit}"))!
            .Where(n => n.DocumentId == document.Document.Id && n.EventType == eventType).ToList();

    private async Task<JsonElement> PayloadAsync(NotificationItem item)
    {
        var payload = default(JsonElement);
        await app.WithDbAsync(async db =>
            payload = JsonDocument.Parse((await db.Notifications.SingleAsync(n => n.Id == item.Id)).Payload).RootElement.Clone());
        return payload;
    }

    private static async Task<List<ApprovalQueueItem>> QueueAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<ApprovalQueueItem>>("/api/approvals"))!;

    private static async Task<List<DocumentListItem>> ReviewQueueAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<DocumentListItem>>("/api/documents?status=under_review"))!;

    private static async Task<DocumentDetails> DetailsAsync(HttpClient client, DocumentDetails document) =>
        (await client.GetFromJsonAsync<DocumentDetails>(Url(document, "")))!;

    private static string Url(DocumentDetails document, string action) =>
        $"/api/documents/{document.Document.Id}" + (action.Length > 0 ? $"/{action}" : "");

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
