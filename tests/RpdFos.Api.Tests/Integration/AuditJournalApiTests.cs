using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using Xunit;
using static RpdFos.Api.Tests.Integration.DocumentCircles;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Журнал аудита администратора (этап 8, A11; FR-ADM-02, S-53): отбор по пользователю, периоду и типу события, выдача частями,
/// доступ только администратору. Записи вставляются напрямую, с заданным временем, у свежих сотрудников: база общая для всех
/// тестов коллекции, поэтому отбор по пользователю отделяет эти записи от чужих.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class AuditJournalApiTests(DatabaseFixture app)
{
    // Полдень по UTC: попадает в те же календарные сутки в любом поясе от −11 до +11, как бы ни был настроен сервер.
    private static DateTimeOffset Noon(int month, int day) => new(2020, month, day, 12, 0, 0, TimeSpan.Zero);

    private static string Url(Guid? user = null, string? from = null, string? to = null, string? type = null, int skip = 0, int take = 50) =>
        "/api/audit?" + string.Join('&', new[]
        {
            user is { } u ? $"userId={u}" : null, from is null ? null : $"from={from}", to is null ? null : $"to={to}",
            type is null ? null : $"eventType={type}", $"skip={skip}", $"take={take}"
        }.Where(p => p is not null));

    private async Task<AuditPage> PageAsync(string url, HttpClient? client = null)
    {
        var response = await (client ?? app.CreateClientFor(AppRoles.Admin)).GetAsync(url);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<AuditPage>())!;
    }

    private async Task AddAsync(Guid? user, string type, DateTimeOffset at, string? entityType = null, Guid? entityId = null, string payload = "{}")
    {
        await app.WithDbAsync(async db =>
        {
            db.AuditLog.Add(new AuditLogEntry
            {
                UserId = user, EventType = type, EntityType = entityType, EntityId = entityId, OccurredAt = at, Payload = payload
            });
            await db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task Filters_by_user_period_and_event_type_work_alone_and_together()
    {
        var (_, first) = await AccountAsync(app, AppRoles.Methodist);
        var (_, second) = await AccountAsync(app, AppRoles.Methodist);
        // Первый: 10 марта — удаление черновика, 20 марта — изменение записи справочника. Второй: 10 марта — архив, 30 марта — изменение.
        await AddAsync(first.Id, AuditEvents.DocumentDeleted, Noon(3, 10), AuditEntities.Document, Guid.NewGuid(), """{"title":"Черновик","status":"draft"}""");
        await AddAsync(first.Id, AuditEvents.EmployeeUpdated, Noon(3, 20), AuditEntities.Employee, Guid.NewGuid());
        await AddAsync(second.Id, AuditEvents.DocumentArchived, Noon(3, 10), AuditEntities.Document, Guid.NewGuid());
        await AddAsync(second.Id, AuditEvents.EmployeeUpdated, Noon(3, 30), AuditEntities.Employee, Guid.NewGuid());

        // По пользователю — только его записи, от новых к старым.
        var byUser = await PageAsync(Url(user: first.Id));
        Assert.Equal([AuditEvents.EmployeeUpdated, AuditEvents.DocumentDeleted], byUser.Items.Select(i => i.EventType));
        Assert.Equal(2, byUser.Total);
        Assert.All(byUser.Items, i => Assert.Equal(first.ShortName, i.UserName));

        // По типу — у обоих; тип без записей — пустая лента.
        var byType = await PageAsync(Url(type: AuditEvents.EmployeeUpdated, from: "2020-03-01", to: "2020-03-31"));
        Assert.Contains(byType.Items, i => i.UserId == first.Id);
        Assert.Contains(byType.Items, i => i.UserId == second.Id);
        Assert.All(byType.Items, i => Assert.Equal(AuditEvents.EmployeeUpdated, i.EventType));
        Assert.Empty((await PageAsync(Url(user: first.Id, type: AuditEvents.DocumentArchived))).Items);

        // По периоду: границы включительно, у одного дня «с» и «по» совпадают; день без записей пуст; запись вне периода не попадает.
        var oneDay = await PageAsync(Url(from: "2020-03-10", to: "2020-03-10"));
        Assert.Equal(new[] { first.Id, second.Id }.Order(), oneDay.Items.Where(i => i.UserId == first.Id || i.UserId == second.Id).Select(i => i.UserId!.Value).Order());
        Assert.All(oneDay.Items, i => Assert.Equal(new DateOnly(2020, 3, 10), DateOnly.FromDateTime(i.At.ToLocalTime().DateTime)));
        var fromOnly = await PageAsync(Url(user: second.Id, from: "2020-03-11"));
        Assert.Equal([AuditEvents.EmployeeUpdated], fromOnly.Items.Select(i => i.EventType));
        var toOnly = await PageAsync(Url(user: second.Id, to: "2020-03-29"));
        Assert.Equal([AuditEvents.DocumentArchived], toOnly.Items.Select(i => i.EventType));
        Assert.Empty((await PageAsync(Url(user: first.Id, from: "2020-03-11", to: "2020-03-19"))).Items);

        // Все три условия вместе.
        var together = await PageAsync(Url(user: first.Id, from: "2020-03-15", to: "2020-03-25", type: AuditEvents.EmployeeUpdated));
        Assert.Equal(AuditEvents.EmployeeUpdated, Assert.Single(together.Items).EventType);
        Assert.Empty((await PageAsync(Url(user: first.Id, from: "2020-03-15", to: "2020-03-25", type: AuditEvents.DocumentDeleted))).Items);
        Assert.Empty((await PageAsync(Url(user: second.Id, from: "2020-03-15", to: "2020-03-25", type: AuditEvents.EmployeeUpdated))).Items);

        // Дата «по» раньше даты «с» — ошибка с пояснением, а не пустая лента.
        var wrong = await app.CreateClientFor(AppRoles.Admin).GetAsync(Url(from: "2020-03-20", to: "2020-03-10"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrong.StatusCode);
    }

    [Fact]
    public async Task Journal_comes_in_parts_without_gaps_or_repeats_even_for_events_of_one_moment()
    {
        var (_, who) = await AccountAsync(app, AppRoles.Methodist);
        // Пять событий, два из них в один и тот же момент: порядок внутри момента устойчив между страницами.
        foreach (var at in new[] { Noon(4, 1), Noon(4, 2), Noon(4, 3), Noon(4, 3), Noon(4, 4) })
            await AddAsync(who.Id, AuditEvents.EmployeeUpdated, at, AuditEntities.Employee, Guid.NewGuid());

        var full = await PageAsync(Url(user: who.Id));
        Assert.Equal(5, full.Total);
        Assert.Equal(full.Items.OrderByDescending(i => i.At).Select(i => i.At), full.Items.Select(i => i.At));

        var parts = new List<AuditRecord>();
        for (var skip = 0; skip < full.Total; skip += 2)
        {
            var part = await PageAsync(Url(user: who.Id, skip: skip, take: 2));
            Assert.Equal(5, part.Total);
            Assert.True(part.Items.Count <= 2);
            parts.AddRange(part.Items);
        }
        Assert.Equal(full.Items.Select(i => i.Id), parts.Select(i => i.Id));
        Assert.Equal(5, parts.Select(i => i.Id).Distinct().Count());
        Assert.Empty((await PageAsync(Url(user: who.Id, skip: 50))).Items);
        Assert.Single((await PageAsync(Url(user: who.Id, take: 0))).Items);
    }

    [Fact]
    public async Task Events_are_described_in_words_and_document_events_link_to_the_document()
    {
        var (author, authorRecord) = await AccountAsync(app, AppRoles.Teacher);
        var created = await DocumentData.CreateAsync(app, author, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.10");
        var id = created.Document.Id;
        var (_, actor) = await AccountAsync(app, AppRoles.Admin);
        var gone = Guid.NewGuid();
        await AddAsync(actor.Id, AuditEvents.ReturnedForCorrection, Noon(5, 1), AuditEntities.Document, id,
            """{"from":"approved","to":"under_review","comment":"Ошибка в часах"}""");
        await AddAsync(actor.Id, AuditEvents.DocumentDeleted, Noon(5, 2), AuditEntities.Document, gone,
            """{"title":"Удалённый черновик","status":"draft"}""");
        await AddAsync(actor.Id, AuditEvents.SignatureRejected, Noon(5, 3), AuditEntities.Document, id,
            """{"role":"department_head","comment":"Нет литературы"}""");
        await AddAsync(actor.Id, AuditEvents.DisciplineAssigned, Noon(5, 4), AuditEntities.Discipline, Guid.NewGuid(),
            JsonSerializer.Serialize(new { employee = authorRecord.Id, document = id }));
        await AddAsync(actor.Id, AuditEvents.EmployeeUpdated, Noon(5, 5), AuditEntities.Employee, authorRecord.Id,
            """{"before":{"short_name":"Было Б. Б.","position":"доцент","is_active":true,"department_id":null},"after":{"short_name":"Стало С. С.","position":"доцент","is_active":false,"department_id":"3f9c0c1e-0000-0000-0000-000000000001"}}""");
        await AddAsync(actor.Id, AuditEvents.MatrixRowLinked, Noon(5, 6), AuditEntities.MatrixRow, Guid.NewGuid(),
            """{"row":"Проектная деятельность","status":"ignored","added":[]}""");
        await AddAsync(actor.Id, "document.unknown_future_event", Noon(5, 7));

        var items = (await PageAsync(Url(user: actor.Id))).Items.OrderBy(i => i.At).ToList();
        Assert.Equal(7, items.Count);

        // Возврат на исправление: статусы по-русски, причина, ссылка на документ и его название.
        var returned = items[0];
        Assert.Equal(("Возвращён на исправление", "Согласован", "На проверке", "Ошибка в часах"),
            (returned.Title, returned.FromStatus, returned.ToStatus, returned.Comment));
        Assert.Equal((id, id, created.Document.Title), (returned.DocumentId, returned.EntityId, returned.EntityTitle));

        // Удалённый черновик: название — из содержимого записи, ссылки нет.
        var deleted = items[1];
        Assert.Equal("Удалённый черновик", deleted.EntityTitle);
        Assert.Null(deleted.DocumentId);
        Assert.Contains("Черновик", Assert.Single(deleted.Details));

        // Отказ согласующего: причина видна в журнале, где перехода статуса нет.
        Assert.Equal("Нет литературы", items[2].Comment);
        Assert.Contains("Заведующий кафедрой", Assert.Single(items[2].Details));

        // Закрепление при передаче документа: сотрудник назван по справочнику, есть ссылка на документ.
        var assigned = items[3];
        Assert.Contains(assigned.Details, d => d.Contains(authorRecord.ShortName));
        Assert.Equal(id, assigned.DocumentId);

        // Запись справочника: изменились только названные поля; подразделение — словом «изменено».
        var updated = items[4];
        Assert.Equal(authorRecord.ShortName, updated.EntityTitle);
        Assert.Contains("Фамилия И. О.: было «Было Б. Б.», стало «Стало С. С.»", updated.Details);
        Assert.Contains("Действует: было «да», стало «нет»", updated.Details);
        Assert.Contains("Кафедра: изменено", updated.Details);
        Assert.DoesNotContain(updated.Details, d => d.StartsWith("Должность"));

        // Строка матрицы с «Не учитывать» называется по-своему; неизвестное событие не ломает ленту.
        Assert.Equal(("Строка матрицы отмечена «Не учитывать»", "Проектная деятельность"), (items[5].Title, items[5].EntityTitle));
        Assert.Equal("document.unknown_future_event", items[6].Title);

        // Автор удаляет черновик: запись об удалении — в журнале, а у прежних записей о документе название остаётся без ссылки.
        // Заодно черновик не остаётся на дисциплине для других тестов, которые считают её документы.
        Assert.Equal(HttpStatusCode.NoContent, (await author.DeleteAsync($"/api/documents/{id}?version={created.Document.Version}")).StatusCode);
        var afterDelete = (await PageAsync(Url(user: actor.Id))).Items.OrderBy(i => i.At).First();
        Assert.Equal((created.Document.Title, null), (afterDelete.EntityTitle, afterDelete.DocumentId));
        var deletion = Assert.Single((await PageAsync(Url(user: authorRecord.Id, type: AuditEvents.DocumentDeleted))).Items);
        Assert.Equal((created.Document.Title, null), (deletion.EntityTitle, deletion.DocumentId));
    }

    [Fact]
    public async Task Filter_options_list_authors_of_events_and_every_known_event_with_a_title()
    {
        var (_, who) = await AccountAsync(app, AppRoles.Methodist);
        var (_, silent) = await AccountAsync(app, AppRoles.Methodist);
        await AddAsync(who.Id, AuditEvents.EmployeeUpdated, Noon(6, 1), AuditEntities.Employee, Guid.NewGuid());

        var response = await app.CreateClientFor(AppRoles.Admin).GetAsync("/api/audit/filters");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var options = (await response.Content.ReadFromJsonAsync<AuditFilterOptions>())!;
        Assert.Contains(options.Users, u => u.Id == who.Id && u.Name == who.ShortName);
        Assert.DoesNotContain(options.Users, u => u.Id == silent.Id);
        Assert.Equal(AuditDescriber.KnownEvents.Order(), options.Events.Select(e => e.Code).Order());
        Assert.All(options.Events, e => Assert.NotEqual(e.Code, e.Title));
    }

    [Fact]
    public async Task Journal_is_open_to_the_administrator_only()
    {
        foreach (var url in new[] { "/api/audit", "/api/audit/filters" })
        {
            foreach (var role in new[] { AppRoles.Teacher, AppRoles.Methodist, AppRoles.Approver })
                Assert.Equal(HttpStatusCode.Forbidden, (await app.CreateClientFor(role).GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await app.CreateClientFor(AppRoles.Admin).GetAsync(url)).StatusCode);
        }
    }

    // Ответы API — те же поля, что у записей ядра; отдельных типов в API нет, поэтому разбираем в зеркальные записи теста.
    private sealed record AuditPage(List<AuditRecord> Items, int Total);

    private sealed record AuditRecord(
        Guid Id, DateTimeOffset At, Guid? UserId, string? UserName, string EventType, string Title, string? EntityType, Guid? EntityId,
        string? EntityTitle, Guid? DocumentId, string? FromStatus, string? ToStatus, string? Comment, List<string> Details);

    private sealed record AuditFilterOptions(List<UserOption> Users, List<EventOption> Events);

    private sealed record UserOption(Guid Id, string Name);

    private sealed record EventOption(string Code, string Title);
}
