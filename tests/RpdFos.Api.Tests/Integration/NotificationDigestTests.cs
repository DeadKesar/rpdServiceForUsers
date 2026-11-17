using System.Globalization;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RpdFos.Api.Features.Notifications;
using RpdFos.Api.Features.Profile;
using RpdFos.Core;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Mail;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Ежедневный дайджест (этап 7, A5, FR-REV-08): одно письмо на получателя в час <c>Mail:DigestHour</c> со всеми
/// неотправленными уведомлениями событий «раз в день». Время подменено (<see cref="TestClock"/>), часовой пояс сервера — UTC+3,
/// час дайджеста — 8:00 по местному, то есть 5:00 UTC.
/// <para>
/// База общая для тестов коллекции, а задача берёт все неотправленные уведомления базы в окне вокруг «сейчас». Поэтому каждый
/// тест живёт в своих сутках — не ближе десяти дней от настоящих и через пять дней друг от друга: уведомления других тестов в
/// его окно не попадают (старше окна или ещё не наступили), а его уведомления для настоящих часов других тестов лежат в
/// будущем и ждут.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class NotificationDigestTests(DatabaseFixture app)
{
    private const string Settings = "/api/profile/notification-settings";

    private static readonly TimeZoneInfo Zone = TestClock.ZoneWithOffset(3);
    private static int _days;

    // Полночь UTC суток D этого теста; уведомления и «сейчас» заданы смещением от неё.
    private readonly DateTimeOffset _day =
        new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(10 + 5 * Interlocked.Increment(ref _days));

    private FakeMailSender Mail => app.Services.GetRequiredService<FakeMailSender>();

    private DateTimeOffset At(int dayShift, int hour, int minute = 0) => _day.AddDays(dayShift).AddHours(hour).AddMinutes(minute);

    [Fact]
    public async Task Daily_events_wait_for_the_digest_hour_and_go_in_one_letter_once_while_immediate_ones_do_not_wait()
    {
        var (client, recipient, email, _) = await AccountData.CreateAsync(app, [AppRoles.Teacher]);
        await AccountData.SaveSettingsAsync(client, (NotificationEvents.ReturnedForRework, true, "daily"), (NotificationEvents.Submitted, true, "daily"));

        // Накануне: два уведомления «раз в день» (одно — после полуночи по местному), одно уже прочитанное, одно без почты
        // и одно «сразу». Время в UTC; по местному (UTC+3) первое — 10:00 накануне, второе — 0:30 того же дня D.
        var first = await AccountData.AddNotificationAsync(app, recipient.Id, NotificationEvents.ReturnedForRework, At(-1, 7), subject: "Первое");
        var second = await AccountData.AddNotificationAsync(app, recipient.Id, NotificationEvents.Submitted, At(-1, 21, 30), subject: "Второе");
        var read = await AccountData.AddNotificationAsync(app, recipient.Id, NotificationEvents.Submitted, At(-1, 12), read: true, subject: "Прочитано");
        var silent = await AccountData.AddNotificationAsync(app, recipient.Id, NotificationEvents.Approved, At(-1, 12), subject: "Без почты");
        var immediate = await AccountData.AddNotificationAsync(app, recipient.Id, NotificationEvents.AwaitingMark, At(-1, 12), subject: "Сразу");

        // 7:59 по местному: час дайджеста не наступил — уходит только письмо «сразу».
        var clock = new TestClock(At(0, 4, 59), Zone);
        Assert.Equal(1, await RunJobAsync(clock));
        Assert.Equal(["Сразу"], Mail.SentTo(email).Select(m => m.Subject));
        Assert.Null((await AccountData.NotificationAsync(app, first)).EmailedAt);
        Assert.Null((await AccountData.NotificationAsync(app, second)).EmailedAt);

        // 8:00: одно письмо-дайджест со всеми неотправленными уведомлениями «раз в день», в порядке событий.
        clock.Now = At(0, 5);
        Assert.Equal(1, await RunJobAsync(clock));
        var digest = Mail.SentTo(email).Last();
        Assert.Equal(($"Дайджест уведомлений от {Local(clock.Now, "dd.MM.yyyy")}", 2), (digest.Subject, Mail.SentTo(email).Count));
        Assert.StartsWith("Новых уведомлений: 2.\n\n", digest.Body);
        Assert.Contains($"1. {Local(At(-1, 7), "dd.MM.yyyy HH:mm")} — Первое. Текст уведомления.", digest.Body);
        Assert.Contains($"2. {Local(At(-1, 21, 30), "dd.MM.yyyy HH:mm")} — Второе. Текст уведомления.", digest.Body);
        Assert.DoesNotContain("Прочитано", digest.Body);
        Assert.DoesNotContain("Без почты", digest.Body);
        Assert.DoesNotContain("Сразу", digest.Body);
        Assert.Equal(clock.Now, (await AccountData.NotificationAsync(app, first)).EmailedAt);
        Assert.Equal(clock.Now, (await AccountData.NotificationAsync(app, second)).EmailedAt);
        Assert.Null((await AccountData.NotificationAsync(app, read)).EmailedAt);
        Assert.Null((await AccountData.NotificationAsync(app, silent)).EmailedAt);
        Assert.NotNull((await AccountData.NotificationAsync(app, immediate)).EmailedAt);

        // Следующий запуск уведомление в дайджест второй раз не включает.
        clock.Now = At(0, 5, 1);
        Assert.Equal(0, await RunJobAsync(clock));
        Assert.Equal(2, Mail.SentTo(email).Count);

        // Новое уведомление после часа дайджеста ждёт следующего дня и уходит одним письмом один раз.
        var third = await AccountData.AddNotificationAsync(app, recipient.Id, NotificationEvents.ReturnedForRework, At(0, 6), subject: "Третье");
        clock.Now = At(0, 6, 1);
        Assert.Equal(0, await RunJobAsync(clock));
        clock.Now = At(1, 4, 59);
        Assert.Equal(0, await RunJobAsync(clock));
        clock.Now = At(1, 5);
        Assert.Equal(1, await RunJobAsync(clock));
        Assert.StartsWith("Новых уведомлений: 1.\n\n", Mail.SentTo(email).Last().Body);
        Assert.Contains("Третье", Mail.SentTo(email).Last().Body);
        Assert.NotNull((await AccountData.NotificationAsync(app, third)).EmailedAt);
        clock.Now = At(1, 5, 1);
        Assert.Equal(0, await RunJobAsync(clock));
        Assert.Equal(3, Mail.SentTo(email).Count);
    }

    [Fact]
    public async Task Each_recipient_gets_his_own_digest_and_only_when_he_chose_the_daily_mode()
    {
        var (daily, dailyRecord, dailyEmail, _) = await AccountData.CreateAsync(app, [AppRoles.Teacher]);
        var (other, otherRecord, otherEmail, _) = await AccountData.CreateAsync(app, [AppRoles.Teacher]);
        var (_, plainRecord, plainEmail, _) = await AccountData.CreateAsync(app, [AppRoles.Teacher]);
        await AccountData.SaveSettingsAsync(daily, (NotificationEvents.Approved, true, "daily"));
        await AccountData.SaveSettingsAsync(other, (NotificationEvents.Approved, false, "daily"));
        await AccountData.AddNotificationAsync(app, dailyRecord.Id, NotificationEvents.Approved, At(-1, 9), subject: "Согласован первый");
        await AccountData.AddNotificationAsync(app, dailyRecord.Id, NotificationEvents.Approved, At(-1, 10), subject: "Согласован второй");
        await AccountData.AddNotificationAsync(app, otherRecord.Id, NotificationEvents.Approved, At(-1, 9), subject: "Согласован у другого");
        await AccountData.AddNotificationAsync(app, plainRecord.Id, NotificationEvents.Approved, At(-1, 9), subject: "Согласован у третьего");

        Assert.Equal(2, await RunJobAsync(new TestClock(At(0, 5), Zone)));

        // Дайджест у каждого свой; событие, скрытое в интерфейсе, всё равно приходит на почту; у не выбравшего «раз в день» — нет.
        var mine = Assert.Single(Mail.SentTo(dailyEmail));
        Assert.StartsWith("Новых уведомлений: 2.", mine.Body);
        Assert.Contains("Согласован первый", mine.Body);
        Assert.Contains("Согласован второй", mine.Body);
        Assert.DoesNotContain("другого", mine.Body);
        var theirs = Assert.Single(Mail.SentTo(otherEmail));
        Assert.StartsWith("Новых уведомлений: 1.", theirs.Body);
        Assert.Empty(Mail.SentTo(plainEmail));
    }

    [Fact]
    public async Task The_term_of_a_letter_runs_from_its_digest_hour_not_from_the_event()
    {
        var (late, lateRecord, lateEmail, _) = await AccountData.CreateAsync(app, [AppRoles.Teacher]);
        var (stale, staleRecord, staleEmail, _) = await AccountData.CreateAsync(app, [AppRoles.Teacher]);
        await AccountData.SaveSettingsAsync(late, (NotificationEvents.Approved, true, "daily"));
        await AccountData.SaveSettingsAsync(stale, (NotificationEvents.Approved, true, "daily"));
        // Событие в 9:00 по местному ждёт дайджеста следующего дня — почти сутки. Срок письма — 8:00 суток D (5:00 UTC).
        var missed = await AccountData.AddNotificationAsync(app, lateRecord.Id, NotificationEvents.Approved, At(-1, 6), subject: "Опоздало");
        // А это событие ждало дайджеста накануне — срок вышел вчера в 8:00.
        var expired = await AccountData.AddNotificationAsync(app, staleRecord.Id, NotificationEvents.Approved, At(-2, 6), subject: "Просрочено");

        // Сервер не работал в час дайджеста и поднялся в 10:00 по местному: событию уже 25 часов, а от срока письма — 2 часа,
        // и оно уходит. У просроченного от срока больше суток (MaxAgeHours): письма нет, событие остаётся в ленте.
        var clock = new TestClock(At(0, 7), Zone);
        Assert.Equal(1, await RunJobAsync(clock));

        Assert.Single(Mail.SentTo(lateEmail));
        Assert.Equal(clock.Now, (await AccountData.NotificationAsync(app, missed)).EmailedAt);
        Assert.Empty(Mail.SentTo(staleEmail));
        Assert.Null((await AccountData.NotificationAsync(app, expired)).EmailedAt);
        Assert.Single(await FeedAsync(stale));
    }

    [Fact]
    public async Task The_digest_hour_must_be_an_hour_of_the_day()
    {
        foreach (var wrong in new[] { -1, 24, 99 })
        {
            var services = new ServiceCollection();
            services.AddCore("Host=localhost");
            services.Configure<MailSettings>(o => o.DigestHour = wrong);
            await using var provider = services.BuildServiceProvider();

            var failure = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<MailSettings>>().Value);
            Assert.Contains("Mail:DigestHour", failure.Message);
        }

        var ok = new ServiceCollection();
        ok.AddCore("Host=localhost");
        ok.Configure<MailSettings>(o => o.DigestHour = 23);
        await using var okProvider = ok.BuildServiceProvider();
        Assert.Equal(23, okProvider.GetRequiredService<IOptions<MailSettings>>().Value.DigestHour);
    }

    // Дата и время — по поясу сервера, в формате письма (не зависит от региональных настроек тестирующей машины).
    private static string Local(DateTimeOffset moment, string format) =>
        TimeZoneInfo.ConvertTime(moment, Zone).ToString(format, CultureInfo.InvariantCulture);

    private static async Task<List<NotificationItem>> FeedAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<NotificationItem>>("/api/notifications"))!;

    // Задача на подменённых часах; настройки почты — не из конфигурации хоста: тест задаёт их сам (час дайджеста, срок письма).
    private async Task<int> RunJobAsync(TestClock clock)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var job = new NotificationMailJob(
            scope.ServiceProvider.GetRequiredService<AppDbContext>(), scope.ServiceProvider.GetRequiredService<IMailSender>(),
            Options.Create(new MailSettings { DigestHour = 8, MaxAgeHours = 24, PublicUrl = "https://rpdfos.test" }), clock,
            NullLogger<NotificationMailJob>.Instance);
        return await job.RunAsync(CancellationToken.None);
    }
}
