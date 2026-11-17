using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Features.Profile;
using RpdFos.Api.Features.Users;
using RpdFos.Core.Data.Entities;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>Учётные записи и уведомления напрямую в базе — для тестов настроек уведомлений (этап 7, A5).</summary>
public static class AccountData
{
    /// <summary>
    /// Учётная запись с ролями — через страницу администратора, как в жизни: запись справочника создаётся вместе с ней или
    /// берётся существующая (<paramref name="directoryUserId"/>, например из файла должностей). Клиент действует от имени
    /// этой записи.
    /// </summary>
    public static async Task<(HttpClient Client, Employee Employee, string Email, Guid AccountId)> CreateAsync(
        DatabaseFixture app, string[] roles, Guid? directoryUserId = null, string fullName = "Получатель Писем Обезличенный")
    {
        var email = $"prefs-{Guid.NewGuid():N}@test.local";
        var created = await app.CreateClientFor(AppRoles.Admin).PostAsJsonAsync("/api/admin/users",
            new CreateUserRequest(email, fullName, "Test-Passw0rd", roles, directoryUserId));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var account = (await created.Content.ReadFromJsonAsync<UserItem>())!;
        Employee? employee = null;
        await app.WithDbAsync(async db => employee = await db.Employees.SingleAsync(e => e.Id == account.DirectoryUserId));
        return (app.CreateClientFor(string.Join(',', roles), employee!.IdentitySubject), employee, email, account.Id);
    }

    /// <summary>
    /// Сохраняет настройки уведомлений через профиль, как страница: события — кортежами «тип, в интерфейсе, режим почты».
    /// Возвращает таблицу настроек из ответа.
    /// </summary>
    public static async Task<List<NotificationSettingItem>> SaveSettingsAsync(
        HttpClient client, params (string EventType, bool InApp, string Mail)[] changes)
    {
        var response = await client.PutAsJsonAsync("/api/profile/notification-settings",
            changes.Select(c => new NotificationSettingRequest(c.EventType, c.InApp, c.Mail)).ToList());
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<List<NotificationSettingItem>>())!;
    }

    /// <summary>
    /// Уведомление в базе в обход событий документа. Тема и текст — как у настоящих уведомлений; у уведомления нет документа,
    /// поэтому в письме нет ссылки.
    /// </summary>
    public static async Task<Guid> AddNotificationAsync(
        DatabaseFixture app, Guid recipient, string eventType, DateTimeOffset? createdAt = null, bool read = false, string? subject = null)
    {
        var now = DateTimeOffset.UtcNow;
        var headline = subject ?? eventType;
        var notification = new Notification
        {
            RecipientId = recipient, EventType = eventType, CreatedAt = createdAt ?? now, ReadAt = read ? createdAt ?? now : null,
            Payload = JsonSerializer.Serialize(new { text = $"{headline}. Текст уведомления.", subject = headline })
        };
        await app.WithDbAsync(async db =>
        {
            db.Notifications.Add(notification);
            await db.SaveChangesAsync();
        });
        return notification.Id;
    }

    public static async Task<Notification> NotificationAsync(DatabaseFixture app, Guid id)
    {
        Notification? found = null;
        await app.WithDbAsync(async db => found = await db.Notifications.AsNoTracking().SingleAsync(n => n.Id == id));
        return found!;
    }
}
