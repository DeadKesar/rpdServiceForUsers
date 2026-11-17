using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RpdFos.Api.Features.Documents;
using RpdFos.Api.Features.Notifications;
using RpdFos.Api.Features.Users;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Mail;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Почта уведомлений (этап 7, A4, FR-REV-08): кому, когда и как письмо уходит. Отправитель подменён
/// (<see cref="FakeMailSender"/>), задачу тесты запускают сами — расписание в тестовом хосте выключено (Mail:Enabled).
/// База и отправитель общие для тестов коллекции, а задача берёт все неотправленные уведомления базы: письма
/// отбираются по адресам учётных записей, созданных в самом тесте.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class NotificationMailTests(DatabaseFixture app)
{
    private FakeMailSender Mail => app.Services.GetRequiredService<FakeMailSender>();

    [Fact]
    public async Task Return_for_rework_is_mailed_to_the_author_once_with_the_link_and_the_events_without_mail_are_not()
    {
        var (author, authorRecord, authorEmail) = await AccountAsync(AppRoles.Teacher);
        var (reviewer, reviewerRecord, reviewerEmail) = await AccountAsync(AppRoles.Methodist);
        var created = await DocumentData.CreateAsync(app, author, (await DocumentData.PublishedTemplateAsync(app)).Id, "Б1.О.10");
        var url = $"/api/documents/{created.Document.Id}";
        var version = await DocumentData.FillForSubmissionAsync(author, created);
        version = await PostAsync(author, $"{url}/submit", new DocumentVersionRequest(version));
        Assert.Equal(HttpStatusCode.Created, (await reviewer.PostAsJsonAsync($"{url}/comments",
            new CommentRequest("/thematic_plan/0/hours", "Разбейте часы по темам"))).StatusCode);
        await PostAsync(reviewer, $"{url}/return", new ReturnRequest(version, " Исправьте до пятницы "));

        await RunJobAsync();

        // Письмо автору: тема — первое предложение уведомления, текст — всё уведомление, ниже — ссылка на документ.
        var headline = $"Возвращён на доработку: {created.Document.Title}";
        var letter = Assert.Single(Mail.SentTo(authorEmail));
        Assert.Equal((headline, authorRecord.ShortName), (letter.Subject, letter.ToName));
        Assert.StartsWith($"{headline}. Замечаний: 1. Методист: {reviewerRecord.ShortName} Комментарий: Исправьте до пятницы.", letter.Body);
        Assert.Contains($"Открыть документ: https://rpdfos.test/documents/{created.Document.Id}", letter.Body);
        // Отправка методисту («Отправлен на проверку») по умолчанию выключена: уведомление есть, письма нет.
        Assert.Empty(Mail.SentTo(reviewerEmail));

        var notifications = await NotificationsOfAsync(created.Document.Id);
        var returned = Assert.Single(notifications, n => n.EventType == NotificationEvents.ReturnedForRework);
        Assert.NotNull(returned.EmailedAt);
        Assert.Contains(notifications, n => n.EventType == NotificationEvents.Submitted);
        Assert.All(notifications.Where(n => n.EventType != NotificationEvents.ReturnedForRework), n => Assert.Null(n.EmailedAt));

        // Второй запуск ничего не повторяет, время отправки остаётся прежним.
        await RunJobAsync();
        Assert.Single(Mail.SentTo(authorEmail));
        Assert.Equal(returned.EmailedAt,
            Assert.Single(await NotificationsOfAsync(created.Document.Id), n => n.EventType == NotificationEvents.ReturnedForRework).EmailedAt);
    }

    [Fact]
    public async Task By_default_only_events_that_require_the_recipients_action_go_by_mail_at_once()
    {
        var (_, recipient, email) = await AccountAsync(AppRoles.Teacher);
        var ids = new Dictionary<string, Guid>();
        foreach (var info in NotificationEvents.All)
            ids[info.Type] = await AddNotificationAsync(recipient.Id, info.Type, subject: info.Type);

        await RunJobAsync();

        // Список — из задания: возвращён на доработку (в том числе после исправления плана), ждёт согласования,
        // возвращён с согласования, напоминания о сроке доработки (A6). Остальные события письма не получают.
        string[] expected =
        [
            NotificationEvents.ReturnedForRework, NotificationEvents.ReturnedAfterPlanCorrection,
            NotificationEvents.AwaitingMark, NotificationEvents.ReturnedFromApproval,
            NotificationEvents.ReworkDeadlineNear, NotificationEvents.ReworkOverdue
        ];
        Assert.Equal(expected.Order(), Mail.SentTo(email).Select(m => m.Subject).Order());
        foreach (var (type, id) in ids)
            Assert.Equal(expected.Contains(type), await EmailedAtAsync(id) is not null);
    }

    [Fact]
    public async Task Recipient_without_an_account_gets_no_mail_even_when_the_directory_has_an_address()
    {
        // Запись справочника, чей sub не указывает ни на одну учётную запись (адрес — в справочнике), и запись без входа:
        // подписант из файла должностей. Адрес справочника для писем не используется — только адрес учётной записи.
        var (_, withoutAccount) = await DocumentData.UserAsync(app);
        Employee signatory = new()
        {
            ShortName = "Подписант П. П.", NameForDocument = "П. П. Подписант", Email = $"signatory-{Guid.NewGuid():N}@test.local",
            CanLogin = false, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        await app.WithDbAsync(async db =>
        {
            db.Employees.Add(signatory);
            await db.SaveChangesAsync();
        });
        var ids = new[] { await AddNotificationAsync(withoutAccount.Id), await AddNotificationAsync(signatory.Id) };

        await RunJobAsync();

        Assert.Empty(Mail.SentTo(withoutAccount.Email!));
        Assert.Empty(Mail.SentTo(signatory.Email!));
        Assert.DoesNotContain(Mail.Attempts, a => a == withoutAccount.Email || a == signatory.Email);
        foreach (var id in ids) Assert.Null(await EmailedAtAsync(id));
    }

    [Fact]
    public async Task Disabled_account_and_inactive_record_get_no_mail()
    {
        // Учётную запись отключил администратор; запись справочника стала неактивной (человек ушёл с должности).
        var (_, disabled, disabledEmail) = await CreateAccountAsync([AppRoles.Teacher], disabled: true);
        var (_, inactive, inactiveEmail) = await AccountAsync(AppRoles.Teacher);
        await app.WithDbAsync(async db =>
        {
            (await db.Employees.SingleAsync(e => e.Id == inactive.Id)).IsActive = false;
            await db.SaveChangesAsync();
        });
        var ids = new[] { await AddNotificationAsync(disabled.Id), await AddNotificationAsync(inactive.Id) };

        await RunJobAsync();

        Assert.Empty(Mail.SentTo(disabledEmail));
        Assert.Empty(Mail.SentTo(inactiveEmail));
        foreach (var id in ids) Assert.Null(await EmailedAtAsync(id));
    }

    [Fact]
    public async Task A_failed_send_neither_cancels_the_event_nor_stops_the_retries()
    {
        var (client, recipient, email) = await AccountAsync(AppRoles.Teacher);
        var id = await AddNotificationAsync(recipient.Id);

        // Сервер недоступен: запуск не падает, письма нет, время отправки пусто, а событие на месте — в ленте и в счётчике.
        Mail.FailFor(email, new MailServerUnavailableException("Почтовый сервер не отвечает (тест)"));
        await RunJobAsync();
        Assert.Empty(Mail.SentTo(email));
        Assert.Null(await EmailedAtAsync(id));
        Assert.Equal(1, (await client.GetFromJsonAsync<UnreadCount>("/api/notifications/unread-count"))!.Unread);
        Assert.Contains(await AllAsync(client), n => n.Id == id && n.ReadAt is null);

        // Сервер не принял письмо: то же самое.
        Mail.FailFor(email, new InvalidOperationException("550 Адрес не принят (тест)"));
        await RunJobAsync();
        Assert.Empty(Mail.SentTo(email));
        Assert.Null(await EmailedAtAsync(id));

        // Сервер снова отвечает: следующий запуск отправляет письмо, и больше оно не повторяется.
        Mail.Recover(email);
        await RunJobAsync();
        Assert.Single(Mail.SentTo(email));
        Assert.NotNull(await EmailedAtAsync(id));
        await RunJobAsync();
        Assert.Single(Mail.SentTo(email));
    }

    [Fact]
    public async Task A_rejected_letter_does_not_hold_back_the_next_one_but_an_unavailable_server_stops_the_run()
    {
        var (_, first, firstEmail) = await AccountAsync(AppRoles.Teacher);
        var (_, second, secondEmail) = await AccountAsync(AppRoles.Teacher);
        var now = DateTimeOffset.UtcNow;
        await AddNotificationAsync(first.Id, createdAt: now.AddMinutes(-2));
        await AddNotificationAsync(second.Id, createdAt: now.AddMinutes(-1));

        // Первое письмо сервер не принял — второе уходит в том же запуске: порядок очереди не блокирует остальных.
        Mail.FailFor(firstEmail, new InvalidOperationException("550 Адрес не принят (тест)"));
        await RunJobAsync();
        Assert.Empty(Mail.SentTo(firstEmail));
        Assert.Single(Mail.SentTo(secondEmail));

        // Сервер недоступен на первом письме — до следующих не доходит: пробовать каждое незачем, они уйдут следующим запуском.
        var later = await AddNotificationAsync(second.Id, createdAt: now);
        Mail.FailFor(firstEmail, new MailServerUnavailableException("Почтовый сервер не отвечает (тест)"));
        var attempts = Mail.Attempts.Count;
        await RunJobAsync();
        Assert.DoesNotContain(secondEmail, Mail.Attempts.Skip(attempts));
        Assert.Null(await EmailedAtAsync(later));

        Mail.Recover(firstEmail);
        await RunJobAsync();
        Assert.Single(Mail.SentTo(firstEmail));
        Assert.Equal(2, Mail.SentTo(secondEmail).Count);
        Assert.NotNull(await EmailedAtAsync(later));
    }

    [Fact]
    public async Task Read_and_outdated_notifications_are_not_mailed()
    {
        var maxAge = TimeSpan.FromHours(app.Services.GetRequiredService<IOptions<MailSettings>>().Value.MaxAgeHours);
        var (_, recipient, email) = await AccountAsync(AppRoles.Teacher);
        // Прочитанное в интерфейсе (в том числе отмеченное системой при замене согласующего) письмом не дублируется;
        // о событии старше срока письмо не уходит — событие остаётся в ленте.
        var read = await AddNotificationAsync(recipient.Id, subject: "Прочитано", read: true);
        var outdated = await AddNotificationAsync(recipient.Id, subject: "Давно", createdAt: DateTimeOffset.UtcNow - maxAge - TimeSpan.FromMinutes(5));
        var fresh = await AddNotificationAsync(recipient.Id, subject: "Свежее");

        await RunJobAsync();

        Assert.Equal(["Свежее"], Mail.SentTo(email).Select(m => m.Subject));
        Assert.Null(await EmailedAtAsync(read));
        Assert.Null(await EmailedAtAsync(outdated));
        Assert.NotNull(await EmailedAtAsync(fresh));
    }

    // Учётная запись с ролями — через страницу администратора, как в жизни; связанная запись справочника создаётся вместе с ней.
    // Адрес письма — email этой учётной записи.
    private Task<(HttpClient Client, Employee Employee, string Email)> AccountAsync(params string[] roles) =>
        CreateAccountAsync(roles, disabled: false);

    private async Task<(HttpClient Client, Employee Employee, string Email)> CreateAccountAsync(string[] roles, bool disabled)
    {
        var admin = app.CreateClientFor(AppRoles.Admin);
        var email = $"mail-{Guid.NewGuid():N}@test.local";
        var created = await admin.PostAsJsonAsync("/api/admin/users",
            new CreateUserRequest(email, "Получатель Писем Обезличенный", "Test-Passw0rd", roles));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var account = (await created.Content.ReadFromJsonAsync<UserItem>())!;
        if (disabled)
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/admin/users/{account.Id}/deactivate", null)).StatusCode);
        Employee? employee = null;
        await app.WithDbAsync(async db => employee = await db.Employees.SingleAsync(e => e.Id == account.DirectoryUserId));
        return (app.CreateClientFor(string.Join(',', roles), employee!.IdentitySubject), employee, email);
    }

    // Уведомление в базе напрямую: события без сложной подготовки документа. Тема и текст — как у настоящих уведомлений.
    private async Task<Guid> AddNotificationAsync(
        Guid recipient, string eventType = NotificationEvents.ReturnedForRework, string? subject = null, DateTimeOffset? createdAt = null,
        bool read = false)
    {
        var now = DateTimeOffset.UtcNow;
        var headline = subject ?? eventType;
        var notification = new Notification
        {
            RecipientId = recipient, EventType = eventType, CreatedAt = createdAt ?? now, ReadAt = read ? now : null,
            Payload = JsonSerializer.Serialize(new { text = $"{headline}. Текст уведомления.", subject = headline })
        };
        await app.WithDbAsync(async db =>
        {
            db.Notifications.Add(notification);
            await db.SaveChangesAsync();
        });
        return notification.Id;
    }

    private async Task<int> RunJobAsync()
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NotificationMailJob>().RunAsync(CancellationToken.None);
    }

    private async Task<DateTimeOffset?> EmailedAtAsync(Guid notificationId)
    {
        DateTimeOffset? at = null;
        await app.WithDbAsync(async db => at = (await db.Notifications.AsNoTracking().SingleAsync(n => n.Id == notificationId)).EmailedAt);
        return at;
    }

    private async Task<List<Notification>> NotificationsOfAsync(Guid documentId)
    {
        var found = new List<Notification>();
        await app.WithDbAsync(async db => found = await db.Notifications.AsNoTracking().Where(n => n.DocumentId == documentId).ToListAsync());
        return found;
    }

    private static async Task<List<NotificationItem>> AllAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<NotificationItem>>($"/api/notifications?limit={NotificationsModule.MaxLimit}"))!;

    // Действие над документом: новая версия из ответа.
    private static async Task<int> PostAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<StatusAnswer>())!.Version;
    }

    private sealed record StatusAnswer(int Version, string Status);
}
