using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using RpdFos.Api.Features.Notifications;
using RpdFos.Api.Features.Profile;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Mail;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Как настройки уведомлений меняют ленту, счётчик и почту (этап 7, A5, FR-REV-08). Уведомления кладутся в базу напрямую
/// (<see cref="AccountData.AddNotificationAsync"/>): здесь проверяется выдача по настройкам, а не создание событий.
/// Письма отправляет сама задача (<see cref="NotificationMailJob"/>) — расписание в тестовом хосте выключено. Пользователи —
/// учётные записи, созданные в самом тесте: база и почтовый отправитель общие для тестов коллекции.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class NotificationPreferencesTests(DatabaseFixture app)
{
    private const string Settings = "/api/profile/notification-settings";

    private FakeMailSender Mail => app.Services.GetRequiredService<FakeMailSender>();

    [Fact]
    public async Task An_event_hidden_in_the_interface_is_neither_in_the_feed_nor_in_the_counter_and_comes_back_with_the_channel()
    {
        var (client, recipient, _, _) = await AccountData.CreateAsync(app, [AppRoles.Teacher]);
        var now = DateTimeOffset.UtcNow;
        var hidden = await AccountData.AddNotificationAsync(app, recipient.Id, NotificationEvents.Submitted, now.AddMinutes(-3));
        var approved = await AccountData.AddNotificationAsync(app, recipient.Id, NotificationEvents.Approved, now.AddMinutes(-2));
        var returned = await AccountData.AddNotificationAsync(app, recipient.Id, NotificationEvents.ReturnedForRework, now.AddMinutes(-1));
        Assert.Equal([returned, approved, hidden], (await FeedAsync(client)).Select(n => n.Id));
        Assert.Equal(3, await UnreadAsync(client));

        await AccountData.SaveSettingsAsync(client, (NotificationEvents.Submitted, false, "off"));

        // Выключенный канал «в интерфейсе»: события нет ни в списке, ни в числе непрочитанных — и давнего тоже.
        Assert.Equal([returned, approved], (await FeedAsync(client)).Select(n => n.Id));
        Assert.Equal(2, await UnreadAsync(client));

        // «Прочитать все» касается только того, что пользователь видит: скрытое остаётся непрочитанным — иначе оно потеряло
        // бы своё письмо (прочитанное письмом не дублируется). Отметить скрытое по одному нельзя: его для пользователя нет.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/notifications/read-all", null)).StatusCode);
        Assert.All(await FeedAsync(client), n => Assert.NotNull(n.ReadAt));
        Assert.Equal(0, await UnreadAsync(client));
        Assert.Null((await AccountData.NotificationAsync(app, hidden)).ReadAt);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/notifications/{hidden}/read", null)).StatusCode);
        Assert.Null((await AccountData.NotificationAsync(app, hidden)).ReadAt);

        // Канал включён обратно — событие снова в ленте и в счётчике, как непрочитанное.
        await AccountData.SaveSettingsAsync(client, (NotificationEvents.Submitted, true, "off"));
        Assert.Equal([returned, approved, hidden], (await FeedAsync(client)).Select(n => n.Id));
        Assert.Equal(1, await UnreadAsync(client));
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/notifications/{hidden}/read", null)).StatusCode);
        Assert.Equal(0, await UnreadAsync(client));
    }

    [Fact]
    public async Task The_channels_are_independent_a_hidden_event_still_goes_by_mail_and_read_all_does_not_take_its_letter_away()
    {
        var (client, recipient, email, _) = await AccountData.CreateAsync(app, [AppRoles.Teacher]);
        await AccountData.SaveSettingsAsync(client, (NotificationEvents.Submitted, false, "immediate"));
        var hidden = await AccountData.AddNotificationAsync(app, recipient.Id, NotificationEvents.Submitted, subject: "Отправлен на проверку");
        Assert.Empty(await FeedAsync(client));
        Assert.Equal(0, await UnreadAsync(client));

        await client.PostAsync("/api/notifications/read-all", null);
        await RunJobAsync();

        Assert.Equal(["Отправлен на проверку"], Mail.SentTo(email).Select(m => m.Subject));
        Assert.NotNull((await AccountData.NotificationAsync(app, hidden)).EmailedAt);
    }

    [Fact]
    public async Task The_mail_mode_replaces_the_default_in_both_directions_and_off_leaves_the_event_in_the_interface_only()
    {
        var (client, recipient, email, _) = await AccountData.CreateAsync(app, [AppRoles.Teacher]);
        // По умолчанию «сразу» у возврата на доработку и «выключено» у отправки на проверку; ждёт согласования не менялось.
        await AccountData.SaveSettingsAsync(client, (NotificationEvents.ReturnedForRework, true, "off"), (NotificationEvents.Submitted, true, "immediate"));
        var returned = await AccountData.AddNotificationAsync(app, recipient.Id, NotificationEvents.ReturnedForRework, subject: "Возвращён");
        var submitted = await AccountData.AddNotificationAsync(app, recipient.Id, NotificationEvents.Submitted, subject: "Отправлен");
        var awaiting = await AccountData.AddNotificationAsync(app, recipient.Id, NotificationEvents.AwaitingMark, subject: "Ждёт");
        var approved = await AccountData.AddNotificationAsync(app, recipient.Id, NotificationEvents.Approved, subject: "Согласован");

        await RunJobAsync();

        Assert.Equal(["Ждёт", "Отправлен"], Mail.SentTo(email).Select(m => m.Subject).Order());
        Assert.NotNull((await AccountData.NotificationAsync(app, submitted)).EmailedAt);
        Assert.NotNull((await AccountData.NotificationAsync(app, awaiting)).EmailedAt);
        // «Выключено» — письма нет, а событие в ленте на месте и непрочитано; у не менявшегося события с умолчанием «выключено» так же.
        Assert.Null((await AccountData.NotificationAsync(app, returned)).EmailedAt);
        Assert.Null((await AccountData.NotificationAsync(app, approved)).EmailedAt);
        Assert.Equal(4, (await FeedAsync(client)).Count);
        Assert.Equal(4, await UnreadAsync(client));

        // Повторный запуск писем не добавляет.
        await RunJobAsync();
        Assert.Equal(2, Mail.SentTo(email).Count);
    }

    [Fact]
    public async Task Settings_are_personal_another_user_keeps_the_defaults()
    {
        var (first, firstRecord, firstEmail, _) = await AccountData.CreateAsync(app, [AppRoles.Teacher]);
        var (second, secondRecord, secondEmail, _) = await AccountData.CreateAsync(app, [AppRoles.Teacher]);
        await AccountData.SaveSettingsAsync(first, (NotificationEvents.ReturnedForRework, false, "off"));
        await AccountData.AddNotificationAsync(app, firstRecord.Id, NotificationEvents.ReturnedForRework, subject: "Возвращён первому");
        await AccountData.AddNotificationAsync(app, secondRecord.Id, NotificationEvents.ReturnedForRework, subject: "Возвращён второму");

        await RunJobAsync();

        Assert.Empty(await FeedAsync(first));
        Assert.Single(await FeedAsync(second));
        Assert.Empty(Mail.SentTo(firstEmail));
        Assert.Equal(["Возвращён второму"], Mail.SentTo(secondEmail).Select(m => m.Subject));
    }

    private static async Task<List<NotificationItem>> FeedAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<NotificationItem>>($"/api/notifications?limit={NotificationsModule.MaxLimit}"))!;

    private static async Task<int> UnreadAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<UnreadCount>("/api/notifications/unread-count"))!.Unread;

    private async Task RunJobAsync()
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<NotificationMailJob>().RunAsync(CancellationToken.None);
    }
}
