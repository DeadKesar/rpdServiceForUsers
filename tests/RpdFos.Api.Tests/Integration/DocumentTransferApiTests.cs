using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Features.Directory;
using RpdFos.Api.Features.Documents;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using Xunit;
using static RpdFos.Api.Tests.Integration.DocumentCircles;
using S = RpdFos.Core.Documents.DocumentStatuses;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// «Передать документ» (этап 7, A9; FR-DOC-15, решение 5): методист или администратор передаёт документ другому автору —
/// записи справочника с действующей учётной записью преподавателя. Учётные записи прежнего и нового автора, методиста и
/// администратора — настоящие, созданные через страницу администратора: уведомления и проверка нового автора смотрят в
/// роли учётных записей в базе.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class DocumentTransferApiTests(DatabaseFixture app)
{
    private const string Reason = "Преподаватель ушёл с кафедры";

    private sealed record Team(
        HttpClient Author, Employee AuthorRecord, HttpClient Next, Employee NextRecord, HttpClient Methodist, Employee MethodistRecord,
        HttpClient Admin, Employee AdminRecord, DocumentDetails Created);

    [Fact]
    public async Task Methodist_transfers_a_document_and_the_new_author_edits_it_while_the_former_one_is_refused()
    {
        var t = await TeamAsync("Б1.О.10");
        var id = t.Created.Document.Id;
        var goals = JsonNode.Parse("""{"goals":"<p>Цели нового автора</p>","objectives":["Задача"]}""");
        var version = (await DocumentData.PatchAsync(t.Author, id, "goals_and_objectives", t.Created.Document.Version, goals)).Version;

        // Кнопка «Передать документ» — методисту и администратору; автору и другому преподавателю её нет.
        Assert.True((await DetailsAsync(t.Methodist, id)).Actions.Transfer);
        Assert.True((await DetailsAsync(t.Admin, id)).Actions.Transfer);
        Assert.False((await DetailsAsync(t.Author, id)).Actions.Transfer);
        var payloadBefore = (await DetailsAsync(t.Methodist, id)).Payload.GetRawText();
        var historyBefore = await HistoryCountAsync(id);

        var transferred = await ExpectAsync(
            t.Methodist.PostAsJsonAsync(Url(id, "transfer"), new TransferRequest(version, t.NextRecord.Id, $"  {Reason}  ")), S.Draft);
        Assert.Equal(version + 1, transferred);

        // Меняется только автор: статус, содержимое и составители прежние, в истории статусов ничего не добавилось.
        var after = await DetailsAsync(t.Methodist, id);
        Assert.Equal((S.Draft, t.NextRecord.Id, payloadBefore), (after.Document.Status, after.Document.AuthorId, after.Payload.GetRawText()));
        Assert.Equal(historyBefore, await HistoryCountAsync(id));

        // Прежний автор получает отказ и документа больше не видит; правит новый.
        var edit = new SectionUpdateRequest(transferred, JsonDocument.Parse(goals!.ToJsonString()).RootElement);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Author.PatchAsJsonAsync(Url(id, "sections/goals_and_objectives"), edit)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Author.PostAsJsonAsync(Url(id, "save"), new DocumentVersionRequest(transferred))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Author.GetAsync(Url(id, ""))).StatusCode);
        Assert.DoesNotContain((await t.Author.GetFromJsonAsync<List<DocumentListItem>>("/api/documents"))!, d => d.Id == id);

        var forNext = await DetailsAsync(t.Next, id);
        Assert.True(forNext.Editable);
        Assert.Contains((await t.Next.GetFromJsonAsync<List<DocumentListItem>>("/api/documents"))!, d => d.Id == id);
        var edited = await DocumentData.PatchAsync(t.Next, id, "goals_and_objectives", transferred,
            JsonNode.Parse("""{"goals":"<p>Цели после передачи</p>","objectives":["Задача"]}"""));
        Assert.Equal(transferred + 1, edited.Version);

        await app.WithDbAsync(async db =>
        {
            // Запись в журнале аудита (NFR-SEC-07): кто, от кого, кому, причина без лишних пробелов.
            var audit = await db.AuditLog.SingleAsync(a => a.EntityId == id && a.EventType == AuditEvents.DocumentTransferred);
            Assert.Equal((t.MethodistRecord.Id, AuditEntities.Document), (audit.UserId, audit.EntityType));
            var logged = JsonDocument.Parse(audit.Payload).RootElement;
            Assert.Equal((t.AuthorRecord.Id, t.NextRecord.Id, Reason),
                (logged.GetProperty("from").GetGuid(), logged.GetProperty("to").GetGuid(), logged.GetProperty("reason").GetString()));
        });

        // Прежнему автору — уведомление без ссылки: документ ему недоступен; новому — со ссылкой. Передавшему и остальным — нет.
        var title = t.Created.Document.Title;
        var gone = Assert.Single(await FeedAsync(t.Author, t.Created, NotificationEvents.TransferredOut));
        Assert.Null(gone.DocumentId);
        Assert.Equal($"Документ передан другому автору: {title}. Новый автор: {t.NextRecord.ShortName} " +
                     $"Методист: {t.MethodistRecord.ShortName} Причина: {Reason}.", gone.Text);
        var goneParameters = await PayloadAsync(app, gone);
        Assert.Equal((t.MethodistRecord.ShortName, Reason, t.NextRecord.ShortName),
            (goneParameters.GetProperty("by").GetString(), goneParameters.GetProperty("comment").GetString(),
                goneParameters.GetProperty("new_author").GetString()));

        var received = Assert.Single(await FeedAsync(t.Next, t.Created, NotificationEvents.TransferredIn));
        Assert.Equal(id, received.DocumentId);
        Assert.Equal($"Вам передан документ: {title}. Прежний автор: {t.AuthorRecord.ShortName} " +
                     $"Методист: {t.MethodistRecord.ShortName} Причина: {Reason}.", received.Text);
        var receivedParameters = await PayloadAsync(app, received);
        Assert.Equal((t.MethodistRecord.ShortName, Reason, t.AuthorRecord.ShortName),
            (receivedParameters.GetProperty("by").GetString(), receivedParameters.GetProperty("comment").GetString(),
                receivedParameters.GetProperty("previous_author").GetString()));
        foreach (var other in new[] { t.Methodist, t.Admin })
        {
            Assert.Empty(await FeedAsync(other, t.Created, NotificationEvents.TransferredOut));
            Assert.Empty(await FeedAsync(other, t.Created, NotificationEvents.TransferredIn));
        }
    }

    [Fact]
    public async Task A_document_in_any_status_but_deleted_is_transferred_without_changing_the_status()
    {
        var t = await TeamAsync("Б1.О.12");
        var (_, third) = await AccountAsync(app, AppRoles.Teacher, "Третий Автор Обезличенный");
        var id = t.Created.Document.Id;
        Employee[] holders = [t.NextRecord, third];

        // Администратор передаёт документ в каждом статусе: новый автор — по очереди второй и третий преподаватель.
        string[] statuses = [S.Draft, S.SavedDraft, S.UnderReview, S.Returned, S.Accepted, S.Approval, S.Approved, S.Archived];
        for (var i = 0; i < statuses.Length; i++)
        {
            await SetStatusAsync(app, id, statuses[i]);
            var version = (await DetailsAsync(t.Admin, id)).Document.Version;
            await ExpectAsync(
                t.Admin.PostAsJsonAsync(Url(id, "transfer"), new TransferRequest(version, holders[i % 2].Id, $"Из «{statuses[i]}»")), statuses[i]);
            Assert.Equal(holders[i % 2].Id, (await DetailsAsync(t.Methodist, id)).Document.AuthorId);
        }
        await app.WithDbAsync(async db =>
        {
            var reasons = await db.AuditLog.Where(a => a.EntityId == id && a.EventType == AuditEvents.DocumentTransferred)
                .OrderBy(a => a.OccurredAt).Select(a => a.Payload).ToListAsync();
            Assert.Equal(statuses.Select(s => $"Из «{s}»"), reasons.Select(p => JsonDocument.Parse(p).RootElement.GetProperty("reason").GetString()!));
        });

        // Удалённого документа администратор не видит: 404.
        var draft = await DocumentData.CreateAsync(app, t.Author, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.13");
        Assert.Equal(HttpStatusCode.NoContent,
            (await t.Author.DeleteAsync($"/api/documents/{draft.Document.Id}?version={draft.Document.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await t.Admin.PostAsJsonAsync(Url(draft, "transfer"), new TransferRequest(draft.Document.Version, t.NextRecord.Id, Reason))).StatusCode);
    }

    [Fact]
    public async Task Transfer_is_refused_to_other_roles_without_a_reason_to_a_wrong_new_author_to_the_same_author_and_with_a_stale_version()
    {
        var t = await TeamAsync("Б1.О.03");
        var id = t.Created.Document.Id;
        var url = Url(id, "transfer");
        var version = t.Created.Document.Version;

        // Передают методист и администратор; прежний автор, другой преподаватель и согласующий — 403.
        var (approver, _) = await AccountAsync(app, AppRoles.Approver);
        foreach (var client in new[] { t.Author, t.Next, approver })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(url, new TransferRequest(version, t.NextRecord.Id, Reason))).StatusCode);

        // Причина обязательна — 422.
        foreach (var empty in new[] { null, "", "   " })
            Assert.Equal(HttpStatusCode.UnprocessableEntity,
                (await t.Methodist.PostAsJsonAsync(url, new TransferRequest(version, t.NextRecord.Id, empty))).StatusCode);

        // Новый автор — запись справочника с действующей учётной записью преподавателя; иначе 422.
        var (_, methodistOnly) = await AccountAsync(app, AppRoles.Methodist);
        var (_, disabled, disabledAccount) = await AccountAsync(app, [AppRoles.Teacher]);
        Assert.True((await t.Admin.PostAsync($"/api/admin/users/{disabledAccount}/deactivate", null)).IsSuccessStatusCode);
        var (_, inactive) = await AccountAsync(app, AppRoles.Teacher);
        var noAccount = new Employee
        {
            ShortName = "Без Учётной З. З.", NameForDocument = "З. З. Без Учётной", CanLogin = false, CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await app.WithDbAsync(async db =>
        {
            db.Employees.Add(noAccount);
            await db.SaveChangesAsync();
            await db.Employees.Where(e => e.Id == inactive.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.IsActive, false));
        });
        foreach (var (employee, title) in new[]
                 {
                     (Guid.NewGuid(), "Сотрудник не найден"), (Guid.Empty, "Сотрудник не найден"), (inactive.Id, "Сотрудник не найден"),
                     (noAccount.Id, "У сотрудника нет учётной записи преподавателя"),
                     (methodistOnly.Id, "У сотрудника нет учётной записи преподавателя"),
                     (disabled.Id, "У сотрудника нет учётной записи преподавателя")
                 })
        {
            var response = await t.Methodist.PostAsJsonAsync(url, new TransferRequest(version, employee, Reason));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal(title, (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
        }

        // Тот же автор и чужая версия — 409.
        var same = await t.Methodist.PostAsJsonAsync(url, new TransferRequest(version, t.AuthorRecord.Id, Reason));
        Assert.Equal(HttpStatusCode.Conflict, same.StatusCode);
        Assert.Equal("Автор не изменится", (await same.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
        Assert.Equal(HttpStatusCode.Conflict,
            (await t.Methodist.PostAsJsonAsync(url, new TransferRequest(version - 1, t.NextRecord.Id, Reason))).StatusCode);

        // Документ не менялся, в журнале аудита и в уведомлениях ничего нет.
        Assert.Equal((t.AuthorRecord.Id, version), ((await DetailsAsync(t.Methodist, id)).Document.AuthorId, (await DetailsAsync(t.Methodist, id)).Document.Version));
        await app.WithDbAsync(async db =>
        {
            Assert.Equal(0, await db.AuditLog.CountAsync(a => a.EntityId == id && a.EventType == AuditEvents.DocumentTransferred));
            Assert.Equal(0, await db.Notifications.CountAsync(n => n.DocumentId == id &&
                (n.EventType == NotificationEvents.TransferredIn || n.EventType == NotificationEvents.TransferredOut)));
        });
        Assert.Empty(await FeedAsync(t.Next, t.Created, NotificationEvents.TransferredIn));
        Assert.Equal(HttpStatusCode.NotFound,
            (await t.Methodist.PostAsJsonAsync(Url(Guid.NewGuid(), "transfer"), new TransferRequest(1, t.NextRecord.Id, Reason))).StatusCode);
    }

    [Fact]
    public async Task The_link_goes_to_the_former_author_only_while_they_can_open_the_document_and_the_actor_is_not_notified()
    {
        // (а) Прежний автор — ещё и методист: документ ему доступен, ссылка есть.
        var (both, bothRecord, _) = await AccountAsync(app, [AppRoles.Teacher, AppRoles.Methodist], "Двойная Роль Обезличенный");
        var (admin, adminRecord) = await AccountAsync(app, AppRoles.Admin);
        var (next, nextRecord) = await AccountAsync(app, AppRoles.Teacher, "Новый Автор Обезличенный");
        var owned = await DocumentData.CreateAsync(app, both, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.10");
        await ExpectAsync(admin.PostAsJsonAsync(Url(owned, "transfer"), new TransferRequest(owned.Document.Version, nextRecord.Id, Reason)), S.Draft);
        var out1 = Assert.Single(await FeedAsync(both, owned, NotificationEvents.TransferredOut));
        Assert.Equal(owned.Document.Id, out1.DocumentId);
        Assert.StartsWith($"Документ передан другому автору: {owned.Document.Title}. Новый автор: {nextRecord.ShortName} Администратор: {adminRecord.ShortName}", out1.Text);
        Assert.Equal(200, (int)(await both.GetAsync(Url(owned, ""))).StatusCode);

        // (б) Методист с ролью преподавателя передаёт собственный документ: ему самому уведомление не нужно, новому автору — идёт.
        var own = await DocumentData.CreateAsync(app, both, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.12");
        await ExpectAsync(both.PostAsJsonAsync(Url(own, "transfer"), new TransferRequest(own.Document.Version, nextRecord.Id, Reason)), S.Draft);
        Assert.Empty(await FeedAsync(both, own, NotificationEvents.TransferredOut));
        var in2 = Assert.Single(await FeedAsync(next, own, NotificationEvents.TransferredIn));
        Assert.Contains($"Методист: {bothRecord.ShortName}", in2.Text);

        // (в) Он же назначает себя автором чужого документа: уведомление «вам передан» ему не создаётся, прежнему автору — без ссылки.
        var (teacher, teacherRecord) = await AccountAsync(app, AppRoles.Teacher, "Прежний Автор Обезличенный");
        var theirs = await DocumentData.CreateAsync(app, teacher, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.13");
        await ExpectAsync(both.PostAsJsonAsync(Url(theirs, "transfer"), new TransferRequest(theirs.Document.Version, bothRecord.Id, Reason)), S.Draft);
        Assert.Empty(await FeedAsync(both, theirs, NotificationEvents.TransferredIn));
        Assert.Null(Assert.Single(await FeedAsync(teacher, theirs, NotificationEvents.TransferredOut)).DocumentId);
        Assert.Equal(teacherRecord.Id, (await ReadAuditAsync(theirs.Document.Id)).From);

        // (г) Прежний автор без действующей учётной записи (преподаватель ушёл): уведомление некому читать, оно не создаётся.
        var (leaver, leaverRecord, leaverAccount) = await AccountAsync(app, [AppRoles.Teacher], "Ушедший Автор Обезличенный");
        var left = await DocumentData.CreateAsync(app, leaver, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.05");
        Assert.True((await admin.PostAsync($"/api/admin/users/{leaverAccount}/deactivate", null)).IsSuccessStatusCode);
        await ExpectAsync(admin.PostAsJsonAsync(Url(left, "transfer"), new TransferRequest(left.Document.Version, nextRecord.Id, Reason)), S.Draft);
        await app.WithDbAsync(async db =>
            Assert.Equal(0, await db.Notifications.CountAsync(n => n.RecipientId == leaverRecord.Id && n.EventType == NotificationEvents.TransferredOut)));
        Assert.Single(await FeedAsync(next, left, NotificationEvents.TransferredIn));
    }

    [Fact]
    public async Task Candidates_are_active_teacher_accounts_found_by_surname_and_only_methodists_and_admins_search()
    {
        var token = $"Кандидат{Guid.NewGuid():N}";
        async Task<(HttpClient Client, Employee Employee, Guid AccountId)> NamedAsync(string kind, params string[] roles) =>
            await AccountAsync(app, roles, $"{token}-{kind} Иван Иванович");

        var teacher = await NamedAsync("teacher", AppRoles.Teacher);
        var both = await NamedAsync("both", AppRoles.Teacher, AppRoles.Methodist);
        await NamedAsync("methodist", AppRoles.Methodist);
        await NamedAsync("approver", AppRoles.Approver);
        var disabled = await NamedAsync("disabled", AppRoles.Teacher);
        var inactive = await NamedAsync("inactive", AppRoles.Teacher);
        var (admin, _) = await AccountAsync(app, AppRoles.Admin);
        Assert.True((await admin.PostAsync($"/api/admin/users/{disabled.AccountId}/deactivate", null)).IsSuccessStatusCode);
        await app.WithDbAsync(async db =>
        {
            db.Employees.Add(new Employee
            {
                ShortName = $"{token}-noaccount И. И.", NameForDocument = $"И. И. {token}-noaccount", CanLogin = false,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            await db.Employees.Where(e => e.Id == inactive.Employee.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.IsActive, false));
        });

        // Методисту и администратору — только действующие учётные записи преподавателей.
        var (methodist, _) = await AccountAsync(app, AppRoles.Methodist);
        var query = Uri.EscapeDataString(token);
        foreach (var client in new[] { methodist, admin })
        {
            var found = (await client.GetFromJsonAsync<List<DirectoryPersonItem>>($"/api/directory/teachers?surname={query}"))!;
            Assert.Equal(new[] { both.Employee.ShortName, teacher.Employee.ShortName }.Order(), found.Select(p => p.ShortName).Order());
            Assert.Equal(teacher.Employee.Id,
                Assert.Single((await client.GetFromJsonAsync<List<DirectoryPersonItem>>($"/api/directory/teachers?surname={query}-t"))!).Id);
            Assert.Empty((await client.GetFromJsonAsync<List<DirectoryPersonItem>>($"/api/directory/teachers?surname={query}-m"))!);
        }

        // Преподавателю и согласующему поиск закрыт.
        var (approverClient, _) = await AccountAsync(app, AppRoles.Approver);
        foreach (var client in new[] { teacher.Client, approverClient })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/directory/teachers?surname={query}")).StatusCode);
    }

    // Черновик у настоящего преподавателя; второй преподаватель, методист и администратор — настоящие учётные записи.
    private async Task<Team> TeamAsync(string code)
    {
        var (author, authorRecord) = await AccountAsync(app, AppRoles.Teacher, "Прежний Автор Обезличенный");
        var (next, nextRecord) = await AccountAsync(app, AppRoles.Teacher, "Новый Автор Обезличенный");
        var (methodist, methodistRecord) = await AccountAsync(app, AppRoles.Methodist, "Передающий Методист Обезличенный");
        var (admin, adminRecord) = await AccountAsync(app, AppRoles.Admin, "Передающий Администратор Обезличенный");
        var created = await DocumentData.CreateAsync(app, author, (await DocumentData.PublishedTemplateAsync(app)).Id, code);
        return new(author, authorRecord, next, nextRecord, methodist, methodistRecord, admin, adminRecord, created);
    }

    private async Task<int> HistoryCountAsync(Guid documentId)
    {
        var count = 0;
        await app.WithDbAsync(async db => count = await db.DocumentStatusHistory.CountAsync(h => h.DocumentId == documentId));
        return count;
    }

    private async Task<(Guid From, Guid To)> ReadAuditAsync(Guid documentId)
    {
        var payload = "";
        await app.WithDbAsync(async db => payload = await db.AuditLog
            .Where(a => a.EntityId == documentId && a.EventType == AuditEvents.DocumentTransferred).Select(a => a.Payload).SingleAsync());
        var logged = JsonDocument.Parse(payload).RootElement;
        return (logged.GetProperty("from").GetGuid(), logged.GetProperty("to").GetGuid());
    }
}
