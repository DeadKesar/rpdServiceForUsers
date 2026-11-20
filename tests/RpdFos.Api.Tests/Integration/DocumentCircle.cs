using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Features.Documents;
using RpdFos.Api.Features.Notifications;
using RpdFos.Api.Features.Users;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using Xunit;
using S = RpdFos.Core.Documents.DocumentStatuses;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Участники документа и сам документ для тестов жизненного цикла после согласования (этап 7, A9): автор, два методиста,
/// администратор, посторонний преподаватель и оба согласующих листа согласования. Все, кроме согласующих, — настоящие
/// учётные записи, созданные через страницу администратора: получатели рассылки и новые авторы берутся из ролей в базе.
/// </summary>
public sealed record DocumentCircle(
    HttpClient Author, Employee AuthorRecord, HttpClient Methodist, Employee MethodistRecord, HttpClient OtherMethodist,
    Employee OtherMethodistRecord, HttpClient Admin, Employee AdminRecord, HttpClient Stranger, Employee StrangerRecord,
    HttpClient Head, Employee HeadRecord, HttpClient Umu, Employee UmuRecord, DocumentDetails Created, int Version);

public static class DocumentCircles
{
    /// <summary>Имя, под которым создаются учётные записи: у всех одно, различать их нужно по записи справочника.</summary>
    public const string DefaultName = "Получатель Уведомлений Обезличенный";

    /// <summary>
    /// Учётная запись с ролями — через страницу администратора, как в жизни. Клиент действует от имени этой записи.
    /// </summary>
    public static async Task<(HttpClient Client, Employee Employee, Guid AccountId)> AccountAsync(
        DatabaseFixture app, string[] roles, string fullName = DefaultName)
    {
        var created = await app.CreateClientFor(AppRoles.Admin).PostAsJsonAsync("/api/admin/users", new CreateUserRequest(
            $"circle-{Guid.NewGuid():N}@test.local", fullName, "Test-Passw0rd", roles));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var account = (await created.Content.ReadFromJsonAsync<UserItem>())!;
        Employee? employee = null;
        await app.WithDbAsync(async db => employee = await db.Employees.SingleAsync(e => e.Id == account.DirectoryUserId));
        return (app.CreateClientFor(string.Join(',', roles), employee!.IdentitySubject), employee, account.Id);
    }

    public static async Task<(HttpClient Client, Employee Employee)> AccountAsync(
        DatabaseFixture app, string role, string fullName = DefaultName)
    {
        var (client, employee, _) = await AccountAsync(app, [role], fullName);
        return (client, employee);
    }

    /// <summary>Документ на проверке: заполнен, отправлен автором, все участники круга созданы.</summary>
    public static async Task<DocumentCircle> UnderReviewAsync(DatabaseFixture app, string code)
    {
        var (author, authorRecord) = await AccountAsync(app, AppRoles.Teacher);
        var (methodist, methodistRecord) = await AccountAsync(app, AppRoles.Methodist);
        var (other, otherRecord) = await AccountAsync(app, AppRoles.Methodist);
        var (admin, adminRecord) = await AccountAsync(app, AppRoles.Admin);
        var (stranger, strangerRecord) = await AccountAsync(app, AppRoles.Teacher);
        var created = await DocumentData.CreateAsync(app, author, (await DocumentData.PublishedTemplateAsync(app)).Id, code);
        var (head, headRecord) = await DocumentData.SignatoryAsync(app, created.Document.DisciplineId, SignatoryRoles.DepartmentHead);
        var (umu, umuRecord) = await DocumentData.SignatoryAsync(app, created.Document.DisciplineId, SignatoryRoles.UmuHead);
        var version = await DocumentData.FillForSubmissionAsync(author, created);
        version = await ExpectAsync(author.PostAsJsonAsync(Url(created, "submit"), new DocumentVersionRequest(version)), S.UnderReview);
        return new(author, authorRecord, methodist, methodistRecord, other, otherRecord, admin, adminRecord, stranger, strangerRecord,
            head, headRecord, umu, umuRecord, created, version);
    }

    /// <summary>Согласованный документ: приём запускает круг сам, согласующие отмечают в любом порядке.</summary>
    public static async Task<DocumentCircle> ApprovedAsync(DatabaseFixture app, string code)
    {
        var c = await UnderReviewAsync(app, code);
        await ExpectAsync(c.Methodist.PostAsJsonAsync(Url(c.Created, "accept"), new DocumentVersionRequest(c.Version)), S.Approval);
        await ExpectAsync(c.Head.PostAsync(Url(c.Created, "approval/approve"), null), S.Approval);
        var version = await ExpectAsync(c.Umu.PostAsync(Url(c.Created, "approval/approve"), null), S.Approved);
        return c with { Version = version };
    }

    /// <summary>Состояние, до которого через интерфейс долго идти: статус выставляется напрямую.</summary>
    public static async Task SetStatusAsync(DatabaseFixture app, Guid documentId, string status) =>
        await app.WithDbAsync(async db => await db.Documents.Where(d => d.Id == documentId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, status)));

    public static async Task<DocumentDetails> DetailsAsync(HttpClient client, Guid documentId) =>
        (await client.GetFromJsonAsync<DocumentDetails>($"/api/documents/{documentId}"))!;

    public static string Url(DocumentDetails document, string action) => Url(document.Document.Id, action);

    public static string Url(Guid documentId, string action) =>
        $"/api/documents/{documentId}" + (action.Length > 0 ? $"/{action}" : "");

    /// <summary>Ответ на смену статуса или действие согласования: ожидаемый статус; возвращает новую версию.</summary>
    public static async Task<int> ExpectAsync(Task<HttpResponseMessage> request, string status)
    {
        var response = await request;
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        var changed = (await response.Content.ReadFromJsonAsync<StatusAnswer>())!;
        Assert.Equal(status, changed.Status);
        return changed.Version;
    }

    /// <summary>Уведомления события в ленте пользователя: со ссылкой на документ и без неё (по названию документа в тексте).</summary>
    public static async Task<List<NotificationItem>> FeedAsync(HttpClient client, DocumentDetails document, string eventType) =>
        (await client.GetFromJsonAsync<List<NotificationItem>>($"/api/notifications?limit={NotificationsModule.MaxLimit}"))!
            .Where(n => n.EventType == eventType && (n.DocumentId == document.Document.Id || n.Text.Contains(document.Document.Title)))
            .ToList();

    public static async Task<JsonElement> PayloadAsync(DatabaseFixture app, NotificationItem item)
    {
        var payload = default(JsonElement);
        await app.WithDbAsync(async db =>
            payload = JsonDocument.Parse((await db.Notifications.SingleAsync(n => n.Id == item.Id)).Payload).RootElement.Clone());
        return payload;
    }

    public sealed record StatusAnswer(int Version, string Status);
}
