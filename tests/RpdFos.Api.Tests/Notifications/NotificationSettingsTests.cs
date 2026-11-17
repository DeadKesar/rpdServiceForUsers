using RpdFos.Api.Tests.Integration;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Mail;
using RpdFos.Core.Notifications;
using Xunit;

namespace RpdFos.Api.Tests.Notifications;

/// <summary>
/// Настройки уведомлений (этап 7, A5, FR-REV-08) без базы и сервера: формат jsonb по модели данных, действующая настройка
/// и расписание дайджеста.
/// </summary>
public class NotificationSettingsTests
{
    [Fact]
    public void Settings_are_stored_as_channels_and_frequency_by_the_data_model()
    {
        var json = NotificationSettings.Serialize(new Dictionary<string, EventSetting>
        {
            ["document.submitted"] = new(true, MailMode.Daily),
            ["approval.started"] = new(false, MailMode.Off),
            ["document.approved"] = new(false, MailMode.Immediately)
        });

        // {тип события: {channels: [...], frequency: ...}}: частота есть только у включённой почты; типы — по алфавиту.
        Assert.Equal(
            """{"approval.started":{"channels":[]},"document.approved":{"channels":["email"],"frequency":"immediate"},"document.submitted":{"channels":["ui","email"],"frequency":"daily"}}""",
            json);
    }

    [Theory]
    [InlineData(true, MailMode.Off)]
    [InlineData(true, MailMode.Immediately)]
    [InlineData(true, MailMode.Daily)]
    [InlineData(false, MailMode.Off)]
    [InlineData(false, MailMode.Immediately)]
    [InlineData(false, MailMode.Daily)]
    public void Every_combination_survives_writing_and_reading(bool inApp, MailMode mail)
    {
        var setting = new EventSetting(inApp, mail);

        var read = NotificationSettings.Parse(NotificationSettings.Serialize(new Dictionary<string, EventSetting> { ["e"] = setting }));

        Assert.Equal(setting, Assert.Single(read).Value);
    }

    [Fact]
    public void Reading_tolerates_missing_parts_and_ignores_what_it_cannot_understand()
    {
        var read = NotificationSettings.Parse("""
            {
              "email.without.frequency": {"channels": ["email"]},
              "frequency.without.email": {"channels": ["ui"], "frequency": "daily"},
              "no.channels": {},
              "not.an.object": 5,
              "unknown.channel": {"channels": ["sms", "ui"]}
            }
            """);

        // Почта без указанной частоты — «сразу»; частота без почты ничего не значит; запись без каналов — всё выключено.
        Assert.Equal(new EventSetting(false, MailMode.Immediately), read["email.without.frequency"]);
        Assert.Equal(new EventSetting(true, MailMode.Off), read["frequency.without.email"]);
        Assert.Equal(new EventSetting(false, MailMode.Off), read["no.channels"]);
        Assert.Equal(new EventSetting(true, MailMode.Off), read["unknown.channel"]);
        Assert.DoesNotContain("not.an.object", read.Keys);

        Assert.Empty(NotificationSettings.Parse(null));
        Assert.Empty(NotificationSettings.Parse(" "));
        Assert.Empty(NotificationSettings.Parse("[]"));
    }

    [Fact]
    public void Without_a_saved_setting_an_event_follows_its_default_and_the_saved_one_wins()
    {
        var stored = new Dictionary<string, EventSetting> { [NotificationEvents.Approved] = new(false, MailMode.Daily) };

        // Умолчание: в интерфейсе показывается всегда, почта — по событию.
        Assert.Equal(new EventSetting(true, MailMode.Immediately), NotificationSettings.Effective(NotificationEvents.AwaitingMark, stored));
        Assert.Equal(new EventSetting(true, MailMode.Off), NotificationSettings.Effective(NotificationEvents.Submitted, stored));
        Assert.Equal(new EventSetting(false, MailMode.Daily), NotificationSettings.Effective(NotificationEvents.Approved, stored));
        // Событие, которого нет в перечне (прежнее), — только интерфейс.
        Assert.Equal(new EventSetting(true, MailMode.Off), NotificationSettings.Effective("старое.событие", stored));
    }

    [Fact]
    public void Every_event_has_a_distinct_title_for_the_table_of_settings()
    {
        var titles = NotificationEvents.All.Select(e => e.Title).ToList();

        Assert.All(titles, title => Assert.False(string.IsNullOrWhiteSpace(title)));
        Assert.Equal(titles.Count, titles.Distinct().Count());
        Assert.Equal(NotificationEvents.AwaitingMark, NotificationEvents.Find(NotificationEvents.AwaitingMark)!.Type);
        Assert.Null(NotificationEvents.Find("нет.такого"));
    }

    [Fact]
    public void Mail_modes_have_the_codes_of_the_api()
    {
        Assert.Equal(["off", "immediate", "daily"], new[] { MailMode.Off, MailMode.Immediately, MailMode.Daily }.Select(MailModeCodes.Code));
        Assert.True(MailModeCodes.TryParse("daily", out var daily));
        Assert.Equal(MailMode.Daily, daily);
        Assert.True(MailModeCodes.TryParse("off", out var off));
        Assert.Equal(MailMode.Off, off);
        Assert.False(MailModeCodes.TryParse("weekly", out _));
        Assert.False(MailModeCodes.TryParse(null, out _));
    }

    [Fact]
    public void The_digest_hour_is_the_nearest_one_strictly_after_the_event_in_the_time_of_the_server()
    {
        var utc = TimeZoneInfo.Utc;
        var moscow = TestClock.ZoneWithOffset(3);
        DateTimeOffset At(int day, int hour, int minute) => new(2026, 11, day, hour, minute, 0, TimeSpan.Zero);

        // UTC, 8:00: до часа — в этот же день, с часа и позже — на следующий; ровно в час — тоже на следующий.
        Assert.Equal(At(17, 8, 0), DigestSchedule.NextAfter(At(17, 7, 59), 8, utc));
        Assert.Equal(At(18, 8, 0), DigestSchedule.NextAfter(At(17, 8, 0), 8, utc));
        Assert.Equal(At(18, 8, 0), DigestSchedule.NextAfter(At(17, 23, 59), 8, utc));

        // UTC+3: 8:00 по местному — 5:00 UTC; календарный день считается по местному времени.
        Assert.Equal(At(17, 5, 0), DigestSchedule.NextAfter(At(17, 4, 59), 8, moscow));
        Assert.Equal(At(18, 5, 0), DigestSchedule.NextAfter(At(17, 5, 0), 8, moscow));
        // 21:30 UTC — уже 0:30 следующего дня по местному: дайджест того же местного дня, в 5:00 UTC.
        Assert.Equal(At(18, 5, 0), DigestSchedule.NextAfter(At(17, 21, 30), 8, moscow));
        // Полночь как час рассылки.
        Assert.Equal(At(17, 21, 0), DigestSchedule.NextAfter(At(17, 20, 59), 0, moscow));
    }
}
