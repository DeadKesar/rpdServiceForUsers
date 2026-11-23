using System.Net;
using System.Net.Http.Json;
using RpdFos.Api.Features.Approvals;
using RpdFos.Api.Features.Documents;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using Xunit;
using static RpdFos.Api.Tests.Integration.DocumentCircles;
using S = RpdFos.Core.Documents.DocumentStatuses;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// История документа (этап 8, A10; FR-ADM-02): лента переходов статусов и записей аудита для методиста и администратора.
/// Участники — настоящие учётные записи (<see cref="DocumentCircles"/>), согласующие — обезличенные записи файла должностей.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class DocumentHistoryApiTests(DatabaseFixture app)
{
    [Fact]
    public async Task Full_cycle_of_a_document_is_in_the_history_with_authors_comments_and_statuses()
    {
        var c = await UnderReviewAsync(app, "Б1.О.10");
        var id = c.Created.Document.Id;
        async Task<int> VersionAsync() => (await DetailsAsync(c.Methodist, id)).Document.Version;

        // Возврат на доработку с общим комментарием, повторная отправка, приём.
        var added = await c.Methodist.PostAsJsonAsync(Url(id, "comments"), new CommentRequest("/thematic_plan", "Тема не раскрыта"));
        var comment = (await added.Content.ReadFromJsonAsync<CommentItem>())!;
        await ExpectAsync(c.Methodist.PostAsJsonAsync(Url(id, "return"), new ReturnRequest(c.Version, "Нужны правки")), S.Returned);
        Assert.Equal(HttpStatusCode.OK, (await c.Author.PostAsync(Url(id, $"comments/{comment.Id}/resolve"), null)).StatusCode);
        await ExpectAsync(c.Author.PostAsJsonAsync(Url(id, "submit"), new DocumentVersionRequest(await VersionAsync())), S.UnderReview);
        await ExpectAsync(c.Methodist.PostAsJsonAsync(Url(id, "accept"), new DocumentVersionRequest(await VersionAsync())), S.Approval);

        // Согласующий возвращает документ, методист принимает снова, оба согласуют.
        await ExpectAsync(c.Head.PostAsJsonAsync(Url(id, "approval/reject"), new RejectRequest("Нет литературы")), S.UnderReview);
        await ExpectAsync(c.Methodist.PostAsJsonAsync(Url(id, "accept"), new DocumentVersionRequest(await VersionAsync())), S.Approval);
        await ExpectAsync(c.Head.PostAsync(Url(id, "approval/approve"), null), S.Approval);
        await ExpectAsync(c.Umu.PostAsync(Url(id, "approval/approve"), null), S.Approved);

        // Возврат согласованного на исправление, новый круг, замена согласующего и обратно, принудительная смена статуса.
        await ExpectAsync(c.Methodist.PostAsJsonAsync(Url(id, "return-for-correction"),
            new CorrectionRequest(await VersionAsync(), "Ошибка в часах")), S.UnderReview);
        await ExpectAsync(c.Methodist.PostAsJsonAsync(Url(id, "accept"), new DocumentVersionRequest(await VersionAsync())), S.Approval);
        var seat = SignatoryRoles.DepartmentHead;
        foreach (var employee in new[] { c.OtherMethodistRecord.Id, c.HeadRecord.Id })
            Assert.True((await c.Methodist.PostAsJsonAsync(Url(id, $"approval/seats/{seat}/assign"), new AssignRequest(employee)))
                .IsSuccessStatusCode);
        await ExpectAsync(c.Admin.PostAsJsonAsync(Url(id, "force-status"),
            new ForceStatusRequest(await VersionAsync(), S.UnderReview, "Сбой в круге")), S.UnderReview);

        // Третий круг до согласования, передача другому автору, архив.
        await ExpectAsync(c.Methodist.PostAsJsonAsync(Url(id, "accept"), new DocumentVersionRequest(await VersionAsync())), S.Approval);
        await ExpectAsync(c.Head.PostAsync(Url(id, "approval/approve"), null), S.Approval);
        await ExpectAsync(c.Umu.PostAsync(Url(id, "approval/approve"), null), S.Approved);
        Assert.True((await c.Methodist.PostAsJsonAsync(Url(id, "transfer"),
            new TransferRequest(await VersionAsync(), c.StrangerRecord.Id, "Автор ушёл"))).IsSuccessStatusCode);
        await ExpectAsync(c.Methodist.PostAsJsonAsync(Url(id, "archive"), new DocumentVersionRequest(await VersionAsync())), S.Archived);

        var all = await HistoryAsync(c.Methodist, id);
        Assert.True(all.Total < 50);
        var chronological = all.Items.AsEnumerable().Reverse().ToList();
        // Один и тот же результат у методиста и администратора.
        Assert.Equal(all.Items.Select(e => (e.At, e.Event)), (await HistoryAsync(c.Admin, id)).Items.Select(e => (e.At, e.Event)));

        // Действия цикла по порядку; события одного шага (приём и старт круга) — в любом порядке внутри группы.
        string[] accepted = ["accepted", "approval_started_status", AuditEvents.ApprovalStarted];
        Group[] expected =
        [
            new(["submitted"], c.AuthorRecord.Id),
            new(["returned"], c.MethodistRecord.Id),
            new(["resubmitted"], c.AuthorRecord.Id),
            new(accepted, c.MethodistRecord.Id),
            new([AuditEvents.SignatureRejected, "returned_from_approval"], c.HeadRecord.Id),
            new(accepted, c.MethodistRecord.Id),
            new([AuditEvents.SignatureApproved], c.HeadRecord.Id),
            new([AuditEvents.SignatureApproved, "approved"], c.UmuRecord.Id),
            new([AuditEvents.ReturnedForCorrection], c.MethodistRecord.Id),
            new(accepted, c.MethodistRecord.Id),
            new([AuditEvents.SignatoryAssigned], c.MethodistRecord.Id),
            new([AuditEvents.SignatoryAssigned], c.MethodistRecord.Id),
            new([AuditEvents.StatusForced], c.AdminRecord.Id),
            new(accepted, c.MethodistRecord.Id),
            new([AuditEvents.SignatureApproved], c.HeadRecord.Id),
            new([AuditEvents.SignatureApproved, "approved"], c.UmuRecord.Id),
            new([AuditEvents.DocumentTransferred], c.MethodistRecord.Id),
            new([AuditEvents.DocumentArchived], c.MethodistRecord.Id)
        ];
        var cursor = chronological.FindIndex(e => e.Event == "submitted");
        Assert.True(cursor >= 0);
        foreach (var group in expected)
        {
            var taken = chronological.Skip(cursor).Take(group.Events.Length).ToList();
            Assert.Equal(group.Events.Order(), taken.Select(e => e.Event).Order());
            Assert.All(taken, e => Assert.Equal(group.By, e.ById));
            cursor += group.Events.Length;
        }
        Assert.Equal(chronological.Count, cursor);
        Assert.Equal(chronological.OrderBy(e => e.At).Select(e => e.At), chronological.Select(e => e.At));
        Assert.All(chronological, e => Assert.False(string.IsNullOrEmpty(e.ByName)));
        Assert.DoesNotContain(chronological, e => e.Event == "status_changed");

        HistoryEntry One(string kind) => chronological.First(e => e.Event == kind);

        // Возврат — с общим комментарием и статусами; возврат согласующим — со своим; возврат на исправление, принудительная
        // смена и архив — одной строкой каждый, с причиной и статусами (переход статуса не повторяется).
        var returned = One("returned");
        Assert.Equal(("Нужны правки", "На проверке", "На доработке"), (returned.Comment, returned.FromStatus, returned.ToStatus));
        var rejected = One("returned_from_approval");
        Assert.Equal(("Нет литературы", "На согласовании", "На проверке"), (rejected.Comment, rejected.FromStatus, rejected.ToStatus));
        var correction = One(AuditEvents.ReturnedForCorrection);
        Assert.Equal(("Ошибка в часах", "Согласован", "На проверке"), (correction.Comment, correction.FromStatus, correction.ToStatus));
        var forced = One(AuditEvents.StatusForced);
        Assert.Equal(("Сбой в круге", "На согласовании", "На проверке"), (forced.Comment, forced.FromStatus, forced.ToStatus));
        Assert.Contains("Круг согласования прерван", forced.Details);
        var archived = One(AuditEvents.DocumentArchived);
        Assert.Equal(("Согласован", "Архивный"), (archived.FromStatus, archived.ToStatus));
        Assert.Single(chronological, e => e.Event == AuditEvents.DocumentArchived);

        // Передача: причина — комментарием, прежний и новый автор — в подробностях.
        var transfer = One(AuditEvents.DocumentTransferred);
        Assert.Equal("Автор ушёл", transfer.Comment);
        var line = Assert.Single(transfer.Details);
        Assert.Contains(c.AuthorRecord.ShortName, line);
        Assert.Contains(c.StrangerRecord.ShortName, line);

        // Старт круга называет места и найденных подписантов; замена — кто был и кто стал.
        Assert.Contains(One(AuditEvents.ApprovalStarted).Details, d => d.Contains(c.HeadRecord.ShortName));
        var assigned = chronological.Where(e => e.Event == AuditEvents.SignatoryAssigned).ToList();
        Assert.Contains(c.HeadRecord.ShortName, Assert.Single(assigned[0].Details));
        Assert.Contains(c.OtherMethodistRecord.ShortName, assigned[0].Details[0]);
        Assert.Contains(c.OtherMethodistRecord.ShortName, Assert.Single(assigned[1].Details));
    }

    [Fact]
    public async Task History_comes_in_parts_from_new_to_old_and_is_closed_to_the_author_and_the_approver()
    {
        var c = await UnderReviewAsync(app, "Б1.О.12");
        var id = c.Created.Document.Id;
        await ExpectAsync(c.Methodist.PostAsJsonAsync(Url(id, "accept"), new DocumentVersionRequest(c.Version)), S.Approval);

        var full = await HistoryAsync(c.Methodist, id);
        Assert.True(full.Total >= 4);
        Assert.Equal(full.Items.OrderByDescending(e => e.At).Select(e => e.At), full.Items.Select(e => e.At));

        // Части склеиваются в ту же ленту без пропусков и повторов; take не меньше одного, skip за концом даёт пустую часть.
        var parts = new List<HistoryEntry>();
        for (var skip = 0; skip < full.Total; skip += 2)
        {
            var part = await HistoryAsync(c.Admin, id, skip, 2);
            Assert.Equal(full.Total, part.Total);
            Assert.True(part.Items.Count <= 2);
            parts.AddRange(part.Items);
        }
        Assert.Equal(full.Items.Select(e => (e.At, e.Event)), parts.Select(e => (e.At, e.Event)));
        Assert.Empty((await HistoryAsync(c.Methodist, id, full.Total + 5, 10)).Items);
        Assert.Single((await HistoryAsync(c.Methodist, id, 0, 0)).Items);

        // Автору, согласующему, постороннему преподавателю — 403; несуществующий документ — 404; без входа — 401.
        foreach (var client in new[] { c.Author, c.Head, c.Umu, c.Stranger })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Url(id, "history"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Methodist.GetAsync(Url(Guid.NewGuid(), "history"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().GetAsync(Url(id, "history"))).StatusCode);
    }

    [Fact]
    public async Task Taking_for_review_shows_its_reason()
    {
        var (author, _) = await AccountAsync(app, AppRoles.Teacher);
        var (methodist, methodistRecord) = await AccountAsync(app, AppRoles.Methodist);
        var created = await DocumentData.CreateAsync(app, author, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.12");
        var id = created.Document.Id;

        await ExpectAsync(methodist.PostAsJsonAsync(Url(id, "take"), new TakeRequest(created.Document.Version, "Автор в отпуске")),
            S.UnderReview);
        var items = (await HistoryAsync(methodist, id)).Items;
        Assert.Equal(["taken_for_review", "created"], items.Select(e => e.Event));
        var taken = items[0];
        Assert.Equal(("taken_for_review", methodistRecord.Id, "Автор в отпуске", "Черновик", "На проверке"),
            (taken.Event, taken.ById, taken.Comment, taken.FromStatus, taken.ToStatus));
    }

    private sealed record Group(string[] Events, Guid By);

    private static async Task<HistoryPage> HistoryAsync(HttpClient client, Guid id, int skip = 0, int take = 50)
    {
        var response = await client.GetAsync($"{Url(id, "history")}?skip={skip}&take={take}");
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<HistoryPage>())!;
    }
}
