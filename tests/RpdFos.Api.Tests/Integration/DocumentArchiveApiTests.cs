using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Features.Approvals;
using RpdFos.Api.Features.Documents;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using Xunit;
using static RpdFos.Api.Tests.Integration.DocumentCircles;
using S = RpdFos.Core.Documents.DocumentStatuses;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// «В архив» (этап 7, A9; FR-DOC-11, решение 3): согласованный документ отправляет в архив автор, методист или
/// администратор — только явно. Архивный документ открыт только для чтения, печатают его те же участники, что и
/// согласованный, а дисциплину он не занимает — по ней создаётся новый.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class DocumentArchiveApiTests(DatabaseFixture app)
{
    [Theory]
    [InlineData("author")]
    [InlineData("methodist")]
    [InlineData("admin")]
    public async Task Author_methodist_and_admin_send_an_approved_document_to_the_archive_and_the_author_is_told_unless_they_did_it(string who)
    {
        var c = await ApprovedAsync("Б1.О.10");
        var (actor, record, label) = who switch
        {
            "author" => (c.Author, c.AuthorRecord, "Автор"),
            "methodist" => (c.Methodist, c.MethodistRecord, "Методист"),
            _ => (c.Admin, c.AdminRecord, "Администратор")
        };

        // Кнопка «В архив» — автору, методисту и администратору; согласующему и постороннему её нет.
        Assert.True((await DetailsAsync(actor, c.Created.Document.Id)).Actions.Archive);
        Assert.False((await DetailsAsync(c.Head, c.Created.Document.Id)).Actions.Archive);

        var version = await ExpectAsync(actor.PostAsJsonAsync(Url(c.Created, "archive"), new DocumentVersionRequest(c.Version)), S.Archived);
        Assert.Equal(c.Version + 1, version);

        await app.WithDbAsync(async db =>
        {
            var document = await db.Documents.SingleAsync(d => d.Id == c.Created.Document.Id);
            // Время согласования остаётся: архивный документ был согласован.
            Assert.Equal((S.Archived, false), (document.Status, document.ApprovedAt is null));

            // История статусов, снимок версии и журнал аудита (NFR-SEC-07).
            var change = await db.DocumentStatusHistory.Where(h => h.DocumentId == document.Id).OrderBy(h => h.ChangedAt).LastAsync();
            Assert.Equal((S.Approved, S.Archived, record.Id, null), (change.FromStatus, change.ToStatus, change.ChangedBy, change.Comment));
            var last = await db.DocumentVersions.Where(v => v.DocumentId == document.Id).OrderBy(v => v.VersionNumber).LastAsync();
            Assert.Equal(VersionTriggers.StatusChange, last.TriggerEvent);

            var audit = await db.AuditLog.SingleAsync(a => a.EntityId == document.Id && a.EventType == AuditEvents.DocumentArchived);
            Assert.Equal((record.Id, AuditEntities.Document, change.ChangedAt), (audit.UserId, audit.EntityType, audit.OccurredAt));
            var logged = JsonDocument.Parse(audit.Payload).RootElement;
            Assert.Equal((S.Approved, S.Archived), (logged.GetProperty("from").GetString(), logged.GetProperty("to").GetString()));
        });

        // Автору — уведомление, если в архив отправил не он; другим участникам — ничего.
        var told = await FeedAsync(c.Author, c.Created, NotificationEvents.Archived);
        if (who == "author")
            Assert.Empty(told);
        else
        {
            var item = Assert.Single(told);
            Assert.Equal(c.Created.Document.Id, item.DocumentId);
            Assert.Equal($"Отправлен в архив: {c.Created.Document.Title}. {label}: {record.ShortName}", item.Text);
        }
        foreach (var other in new[] { c.OtherMethodist, c.Head, c.Umu, c.Admin, c.Methodist }.Where(client => client != actor))
            Assert.Empty(await FeedAsync(other, c.Created, NotificationEvents.Archived));
    }

    [Fact]
    public async Task Archive_is_refused_to_strangers_outside_the_approved_status_and_with_a_stale_version()
    {
        var c = await ApprovedAsync("Б1.О.12");
        var url = Url(c.Created, "archive");

        // Посторонний преподаватель — 403, согласующий — 403: отказ раньше проверки статуса. Чужая версия — 409.
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Stranger.PostAsJsonAsync(url, new DocumentVersionRequest(c.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Head.PostAsJsonAsync(url, new DocumentVersionRequest(c.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.Methodist.PostAsJsonAsync(url, new DocumentVersionRequest(c.Version - 1))).StatusCode);
        Assert.Equal(S.Approved, (await DetailsAsync(c.Methodist, c.Created.Document.Id)).Document.Status);

        // Из любого другого статуса — 409, в том числе из архивного: документ в архив не отправить дважды.
        foreach (var status in new[] { S.Draft, S.SavedDraft, S.UnderReview, S.Returned, S.Accepted, S.Approval, S.Archived })
        {
            await SetStatusAsync(app, c.Created.Document.Id, status);
            foreach (var client in new[] { c.Author, c.Methodist, c.Admin })
            {
                var response = await client.PostAsJsonAsync(url, new DocumentVersionRequest(c.Version));
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                Assert.Equal("Действие недоступно в этом статусе", (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
            }
            // Посторонний и там получает 403, а не 409: чужой документ не сообщает, в каком он состоянии.
            Assert.Equal(HttpStatusCode.Forbidden, (await c.Stranger.PostAsJsonAsync(url, new DocumentVersionRequest(c.Version))).StatusCode);
            var details = await DetailsAsync(c.Admin, c.Created.Document.Id);
            Assert.False(details.Actions.Archive);
        }

        // Ничего не записано, а документ, вернувшийся в «Согласован», по-прежнему уходит в архив.
        await app.WithDbAsync(async db =>
        {
            Assert.Equal(0, await db.AuditLog.CountAsync(a => a.EntityId == c.Created.Document.Id && a.EventType == AuditEvents.DocumentArchived));
            Assert.Equal(0, await db.DocumentStatusHistory.CountAsync(h => h.DocumentId == c.Created.Document.Id && h.ToStatus == S.Archived));
        });
        await SetStatusAsync(app, c.Created.Document.Id, S.Approved);
        await ExpectAsync(c.Author.PostAsJsonAsync(url, new DocumentVersionRequest(c.Version)), S.Archived);

        // Документа нет — 404; удалённого — тоже.
        Assert.Equal(HttpStatusCode.NotFound,
            (await c.Author.PostAsJsonAsync(Url(Guid.NewGuid(), "archive"), new DocumentVersionRequest(1))).StatusCode);
        var draft = await DocumentData.CreateAsync(app, c.Author, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.13");
        Assert.Equal(HttpStatusCode.NoContent,
            (await c.Author.DeleteAsync($"/api/documents/{draft.Document.Id}?version={draft.Document.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await c.Author.PostAsJsonAsync(Url(draft, "archive"), new DocumentVersionRequest(draft.Document.Version))).StatusCode);
    }

    [Fact]
    public async Task Archived_document_is_read_only_printed_by_the_same_participants_as_an_approved_one_and_listed_with_its_status()
    {
        var c = await ApprovedAsync("Б1.О.13");
        var id = c.Created.Document.Id;
        var print = $"/api/documents/{id}/print/docx";

        // Печатают согласованный документ автор, методисты и согласующие; администратор и посторонний — нет.
        await AssertPrintingAsync();
        await ExpectAsync(c.Author.PostAsJsonAsync(Url(c.Created, "archive"), new DocumentVersionRequest(c.Version)), S.Archived);
        // Архивный — те же участники.
        await AssertPrintingAsync();

        async Task AssertPrintingAsync()
        {
            foreach (var client in new[] { c.Author, c.Methodist, c.OtherMethodist, c.Head, c.Umu })
                Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync(print, null)).StatusCode);
            foreach (var client in new[] { c.Admin, c.Stranger })
                Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(print, null)).StatusCode);
        }

        // Только чтение: ни правки, ни отправки; из действий автору остаётся печать, методисту — печать и передача.
        var forAuthor = await DetailsAsync(c.Author, id);
        Assert.Equal((S.Archived, false), (forAuthor.Document.Status, forAuthor.Editable));
        Assert.Equal(new DocumentActions(Submit: false, Review: false, ResolveComments: false, Print: true), forAuthor.Actions);
        var forMethodist = await DetailsAsync(c.Methodist, id);
        Assert.Equal((false, new DocumentActions(false, false, false, Print: true, Transfer: true)), (forMethodist.Editable, forMethodist.Actions));
        var forAdmin = await DetailsAsync(c.Admin, id);
        Assert.False(forAdmin.Actions.Print);
        Assert.Equal([S.SavedDraft, S.Returned, S.UnderReview], forAdmin.Actions.ForceTargets!.Select(t => t.Status));
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Stranger.GetAsync(Url(id, ""))).StatusCode);

        var section = new SectionUpdateRequest(c.Version + 1, JsonDocument.Parse("""{"goals":"<p>Новые цели</p>","objectives":["Задача"]}""").RootElement);
        Assert.Equal(HttpStatusCode.Conflict, (await c.Author.PatchAsJsonAsync(Url(id, "sections/goals_and_objectives"), section)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.Author.PostAsJsonAsync(Url(id, "submit"), new DocumentVersionRequest(c.Version + 1))).StatusCode);

        // Круг согласования остаётся виден: места, отметки, без плашек возврата.
        foreach (var client in new[] { c.Author, c.Methodist, c.Head })
        {
            var panel = (await DetailsAsync(client, id)).Approval!;
            Assert.Equal(2, panel.Seats.Count);
            Assert.All(panel.Seats, s => Assert.Equal(SignatureStatuses.Approved, s.Status));
            Assert.Equal((null, null, false, false), (panel.Returned, panel.Corrected, panel.CanAssign, panel.CanStart));
        }

        // В списках виден со своим статусом: у автора и методиста, по фильтру и без; у согласующего — в «Отмеченных мной», но не в очереди.
        foreach (var client in new[] { c.Author, c.Methodist })
        {
            var all = (await client.GetFromJsonAsync<List<DocumentListItem>>("/api/documents"))!;
            Assert.Equal(("archived", "Архивный"), all.Where(d => d.Id == id).Select(d => (d.Status, d.StatusTitle)).Single());
            Assert.Equal(id, Assert.Single((await client.GetFromJsonAsync<List<DocumentListItem>>("/api/documents?status=archived"))!,
                d => d.Id == id).Id);
        }
        var marked = (await c.Head.GetFromJsonAsync<List<MarkedApprovalItem>>("/api/approvals/marked"))!.Single(d => d.Id == id);
        Assert.Equal(("archived", "Архивный"), (marked.Status, marked.StatusTitle));
        Assert.DoesNotContain((await c.Head.GetFromJsonAsync<List<ApprovalQueueItem>>("/api/approvals"))!, q => q.Id == id);
    }

    [Fact]
    public async Task Archived_document_does_not_occupy_the_discipline_so_a_new_one_is_created_for_it()
    {
        var c = await ApprovedAsync("Б1.О.10");
        var id = c.Created.Document.Id;
        var (templateId, disciplineId) = (c.Created.Document.TemplateId, c.Created.Document.DisciplineId);
        var create = new CreateDocumentRequest(templateId, disciplineId);

        // Согласованный документ дисциплину занимает: новый — 409 со ссылкой на него (до «Создать на основе», A11).
        var blocked = await c.Stranger.PostAsJsonAsync("/api/documents", create);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal(id.ToString(), (await blocked.Content.ReadFromJsonAsync<ProblemDetails>())!.Extensions["documentId"]!.ToString());
        Assert.Equal(id, (await OptionAsync(c.Methodist)).DocumentId);

        await ExpectAsync(c.Methodist.PostAsJsonAsync(Url(c.Created, "archive"), new DocumentVersionRequest(c.Version)), S.Archived);

        // Архивный дисциплину не занимает: на экране выбора она свободна, документ по ней создаёт любой преподаватель.
        Assert.Null((await OptionAsync(c.Methodist)).DocumentId);
        var created = await c.Stranger.PostAsJsonAsync("/api/documents", create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var fresh = (await created.Content.ReadFromJsonAsync<DocumentDetails>())!;
        Assert.NotEqual(id, fresh.Document.Id);
        Assert.Equal((S.Draft, c.StrangerRecord.Id), (fresh.Document.Status, fresh.Document.AuthorId));

        // Теперь на экране выбора — новый документ (архивный не мешает экрану, как и созданию), второй новый — 409 со ссылкой на него.
        var option = await OptionAsync(c.Methodist);
        Assert.Equal((fresh.Document.Id, c.StrangerRecord.ShortName), (option.DocumentId, option.DocumentAuthor));
        var again = await c.Author.PostAsJsonAsync("/api/documents", create);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(fresh.Document.Id.ToString(), (await again.Content.ReadFromJsonAsync<ProblemDetails>())!.Extensions["documentId"]!.ToString());

        // Оба документа — в списке методиста, каждый со своим статусом.
        var list = (await c.Methodist.GetFromJsonAsync<List<DocumentListItem>>("/api/documents"))!;
        Assert.Equal(("archived", "draft"), (list.Single(d => d.Id == id).Status, list.Single(d => d.Id == fresh.Document.Id).Status));

        // Один незавершённый документ на дисциплину: архивный в работу вернуть нельзя, пока новый не согласован (A8).
        var back = await c.Admin.PostAsJsonAsync(Url(id, "force-status"),
            new ForceStatusRequest((await DetailsAsync(c.Admin, id)).Document.Version, S.UnderReview, "Проверка"));
        Assert.Equal(HttpStatusCode.Conflict, back.StatusCode);
        Assert.Equal("Есть незавершённый документ", (await back.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);

        async Task<DisciplineOption> OptionAsync(HttpClient client) =>
            (await client.GetFromJsonAsync<List<DisciplineOption>>($"/api/documents/disciplines?templateId={templateId}"))!
                .Single(o => o.Id == disciplineId);
    }

    private Task<DocumentCircle> ApprovedAsync(string code) => DocumentCircles.ApprovedAsync(app, code);
}
