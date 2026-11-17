using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Features.Profile;
using RpdFos.Api.Features.Users;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Notifications;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Профиль и настройки уведомлений (этап 7, A5; S-02, FR-REV-08): данные только для чтения, таблица настроек с умолчаниями,
/// сохранение отклонений и отказы. Как настройки меняют ленту, счётчик и почту — в <c>NotificationPreferencesTests</c>.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ProfileApiTests(DatabaseFixture app)
{
    private const string Settings = "/api/profile/notification-settings";

    [Fact]
    public async Task Profile_combines_the_directory_record_with_the_account_and_shows_the_roles_of_the_account_not_of_the_token()
    {
        // Запись справочника из файла должностей — с должностью и кафедрой; администратор привязывает к ней учётную запись.
        var suffix = Guid.NewGuid().ToString("N")[..8];
        Department department = new() { Code = $"T-{suffix}", Name = $"Кафедра проверочная {suffix}", ShortName = $"КП-{suffix}" };
        Employee record = new()
        {
            ShortName = "Профилев П. П.", NameForDocument = "П. П. Профилев", Position = "Доцент", DepartmentId = department.Id,
            CanLogin = false, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        await app.WithDbAsync(async db =>
        {
            db.Departments.Add(department);
            db.Employees.Add(record);
            await db.SaveChangesAsync();
        });
        var (client, _, email, accountId) =
            await AccountData.CreateAsync(app, [AppRoles.Teacher, AppRoles.Approver], record.Id, "Профилев Павел Петрович");

        var profile = (await client.GetFromJsonAsync<ProfileItem>("/api/profile"))!;

        // ФИО — полное, из записи справочника (оно взято из учётной записи при привязке); email — учётной записи.
        Assert.Equal(("Профилев Павел Петрович", email, "Доцент", department.Name, true),
            (profile.FullName, profile.Email, profile.Position, profile.Unit, profile.HasDirectoryRecord));
        Assert.Equal([("teacher", "Преподаватель"), ("approver", "Утверждающий")], profile.Roles.Select(r => (r.Code, r.Title)));

        // Роли администратор сменил, а токен пользователя обновится только при следующем входе: профиль показывает роли
        // учётной записи — то, что будет в токене после входа.
        var changed = await app.CreateClientFor(AppRoles.Admin).PutAsJsonAsync($"/api/admin/users/{accountId}/roles",
            new RolesRequest([AppRoles.Methodist]));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        profile = (await client.GetFromJsonAsync<ProfileItem>("/api/profile"))!;
        Assert.Equal([("methodist", "Методист")], profile.Roles.Select(r => (r.Code, r.Title)));
    }

    [Fact]
    public async Task A_user_without_a_directory_record_has_a_profile_from_the_token_but_no_settings()
    {
        var stranger = app.CreateClientFor(AppRoles.Teacher);

        var profile = (await stranger.GetFromJsonAsync<ProfileItem>("/api/profile"))!;

        Assert.Equal(("Тестовый teacher", false), (profile.FullName, profile.HasDirectoryRecord));
        Assert.Null(profile.Email);
        Assert.Null(profile.Position);
        Assert.Null(profile.Unit);
        Assert.Equal([("teacher", "Преподаватель")], profile.Roles.Select(r => (r.Code, r.Title)));

        // Уведомления создаются только записям справочника: настраивать нечего — ни читать, ни писать.
        var read = await stranger.GetAsync(Settings);
        var write = await stranger.PutAsJsonAsync(Settings, new[] { new NotificationSettingRequest(NotificationEvents.Approved, false, "off") });
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Equal("Нет записи в справочнике сотрудников", (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Settings_default_to_every_event_in_the_interface_and_the_mail_of_the_events_default()
    {
        var (client, _, _, _) = await AccountData.CreateAsync(app, [AppRoles.Teacher]);

        var table = (await client.GetFromJsonAsync<List<NotificationSettingItem>>(Settings))!;

        // Таблица — весь перечень событий в его порядке; ничего, кроме перечня, в ней нет.
        Assert.Equal(NotificationEvents.All.Select(e => e.Type), table.Select(r => r.EventType));
        Assert.Equal(NotificationEvents.All.Select(e => e.Title), table.Select(r => r.Title));
        Assert.All(table, row => Assert.True(row.InApp));
        Assert.Equal(NotificationEvents.All.Select(e => MailModeCodes.Code(e.DefaultMail)), table.Select(r => r.Mail));
        // Умолчания почты — «сразу» у событий, которые просят действия, «выключено» у остальных.
        Assert.Equal("immediate", table.Single(r => r.EventType == NotificationEvents.AwaitingMark).Mail);
        Assert.Equal("off", table.Single(r => r.EventType == NotificationEvents.Approved).Mail);
        // Пока пользователь ничего не менял, в базе для него ничего нет.
        Assert.Empty(await StoredAsync(client));
    }

    [Fact]
    public async Task Saving_changes_only_the_named_events_and_stores_only_what_differs_from_the_defaults()
    {
        var (client, employee, _, _) = await AccountData.CreateAsync(app, [AppRoles.Teacher]);

        // Одно отклонение, один «выбор» умолчания (возврат на доработку — «сразу» и так) и ещё одно отклонение.
        var saved = await AccountData.SaveSettingsAsync(client,
            (NotificationEvents.Submitted, false, "daily"),
            (NotificationEvents.ReturnedForRework, true, "immediate"),
            (NotificationEvents.Approved, true, "immediate"));

        Assert.Equal(("daily", false), (Of(saved, NotificationEvents.Submitted).Mail, Of(saved, NotificationEvents.Submitted).InApp));
        Assert.Equal(("immediate", true), (Of(saved, NotificationEvents.Approved).Mail, Of(saved, NotificationEvents.Approved).InApp));
        Assert.Equal(("immediate", true), (Of(saved, NotificationEvents.ReturnedForRework).Mail, Of(saved, NotificationEvents.ReturnedForRework).InApp));
        // Остальные события остались умолчаниями.
        Assert.All(saved.Where(r => r.EventType is not (NotificationEvents.Submitted or NotificationEvents.Approved)),
            row => Assert.Equal(new EventSetting(true, NotificationEvents.DefaultMailOf(row.EventType)),
                new EventSetting(row.InApp, MailModeCodes.TryParse(row.Mail, out var mode) ? mode : MailMode.Off)));
        // В базе — только отклонения от умолчаний, по модели данных: каналы и частота.
        var stored = await StoredAsync(client);
        Assert.Equal([NotificationEvents.Approved, NotificationEvents.Submitted], stored.Keys.Order());
        Assert.Equal(new EventSetting(false, MailMode.Daily), stored[NotificationEvents.Submitted]);

        // Повторный вход на страницу показывает то же; сохранение другого события первые не трогает.
        Assert.Equal(saved, (await client.GetFromJsonAsync<List<NotificationSettingItem>>(Settings))!);
        saved = await AccountData.SaveSettingsAsync(client, (NotificationEvents.PrintReady, true, "daily"));
        Assert.Equal(("daily", false), (Of(saved, NotificationEvents.Submitted).Mail, Of(saved, NotificationEvents.Submitted).InApp));
        Assert.Equal(3, (await StoredAsync(client)).Count);

        // Вернуть всё к умолчаниям — значит не хранить ничего: строка настроек исчезает.
        await AccountData.SaveSettingsAsync(client,
            (NotificationEvents.Submitted, true, "off"), (NotificationEvents.Approved, true, "off"), (NotificationEvents.PrintReady, true, "off"));
        Assert.Empty(await StoredAsync(client));
        await app.WithDbAsync(async db => Assert.False(await db.NotificationPreferences.AnyAsync(p => p.UserId == employee.Id)));
    }

    [Fact]
    public async Task A_wrong_event_or_mail_mode_is_refused_with_422_and_nothing_is_saved()
    {
        var (client, _, _, _) = await AccountData.CreateAsync(app, [AppRoles.Teacher]);
        var good = new NotificationSettingRequest(NotificationEvents.Submitted, false, "daily");

        var unknownEvent = await client.PutAsJsonAsync(Settings, new[] { good, new NotificationSettingRequest("нет.такого.события", true, "off") });
        var unknownMode = await client.PutAsJsonAsync(Settings, new[] { good, new NotificationSettingRequest(NotificationEvents.Approved, true, "weekly") });
        var noMode = await client.PutAsJsonAsync(Settings, new[] { new NotificationSettingRequest(NotificationEvents.Approved, true, null) });

        Assert.All([unknownEvent, unknownMode, noMode], response => Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode));
        Assert.Equal("Неизвестное событие", (await unknownEvent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        Assert.Equal("Неизвестный режим почты", (await unknownMode.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        // Верная первая строка запроса тоже не сохранена: запрос принимается целиком или не принимается.
        Assert.Empty(await StoredAsync(client));
    }

    [Fact]
    public async Task Profile_and_settings_need_a_signed_in_user()
    {
        var anonymous = app.CreateClientFor(null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Settings)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(Settings, Array.Empty<NotificationSettingRequest>())).StatusCode);
    }

    private static NotificationSettingItem Of(IEnumerable<NotificationSettingItem> table, string eventType) =>
        table.Single(r => r.EventType == eventType);

    // Что лежит в базе у пользователя этого клиента: отклонения от умолчаний. Клиент выбирает запись справочника по sub.
    private async Task<Dictionary<string, EventSetting>> StoredAsync(HttpClient client)
    {
        var subject = client.DefaultRequestHeaders.GetValues(TestAuthHandler.SubjectHeader).Single();
        var stored = new Dictionary<string, EventSetting>();
        await app.WithDbAsync(async db =>
        {
            var json = await db.NotificationPreferences.AsNoTracking()
                .Where(p => db.Employees.Any(e => e.Id == p.UserId && e.IdentitySubject == subject))
                .Select(p => p.Preferences).SingleOrDefaultAsync();
            stored = NotificationSettings.Parse(json);
        });
        return stored;
    }
}
