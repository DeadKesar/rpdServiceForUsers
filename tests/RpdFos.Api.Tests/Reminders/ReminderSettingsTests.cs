using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RpdFos.Core;
using RpdFos.Core.Documents;
using RpdFos.Core.Mail;
using RpdFos.Core.Reminders;
using Xunit;

namespace RpdFos.Api.Tests.Reminders;

/// <summary>
/// Сроки напоминаний о доработке (этап 7, A6, решение 6): умолчания — как в проекте, неверные сроки не принимаются при
/// старте хоста, слово «дня» / «дней» в тексте согласовано с числом. Базы не нужно.
/// </summary>
public class ReminderSettingsTests
{
    [Fact]
    public void Defaults_are_the_terms_of_the_design()
    {
        var settings = new RemindersSettings();

        Assert.Equal((true, 14, 3), (settings.Enabled, settings.ReworkDays, settings.WarnDaysBefore));
    }

    [Theory]
    [InlineData(14, 3)]
    [InlineData(2, 1)]
    [InlineData(30, 29)]
    public void Terms_that_leave_room_for_the_warning_are_accepted(int reworkDays, int warnDaysBefore)
    {
        using var provider = Provider(reworkDays, warnDaysBefore);

        var settings = provider.GetRequiredService<IOptions<RemindersSettings>>().Value;

        Assert.Equal((reworkDays, warnDaysBefore), (settings.ReworkDays, settings.WarnDaysBefore));
    }

    // Предупреждение должно прийти раньше срока: иначе оно шло бы вместе с эскалацией или после неё.
    [Theory]
    [InlineData(1, 1)]
    [InlineData(0, 0)]
    [InlineData(-14, 3)]
    [InlineData(14, 0)]
    [InlineData(14, -1)]
    [InlineData(14, 14)]
    [InlineData(14, 20)]
    public void Terms_without_room_for_the_warning_are_refused_at_start(int reworkDays, int warnDaysBefore)
    {
        using var provider = Provider(reworkDays, warnDaysBefore);

        var failure = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<RemindersSettings>>().Value);

        Assert.Contains("Reminders:ReworkDays", failure.Message);
        Assert.Contains("Reminders:WarnDaysBefore", failure.Message);
    }

    // «Больше 21 дня», но «больше 14 дней», «больше 22 дней», «больше 11 дней» и «больше 111 дней».
    [Theory]
    [InlineData(14, "дней")]
    [InlineData(21, "дня")]
    [InlineData(22, "дней")]
    [InlineData(11, "дней")]
    [InlineData(111, "дней")]
    [InlineData(101, "дня")]
    [InlineData(31, "дня")]
    [InlineData(2, "дней")]
    [InlineData(5, "дней")]
    [InlineData(30, "дней")]
    public void The_word_after_more_than_agrees_with_the_number(int days, string unit) =>
        Assert.Equal(unit, DocumentNotifications.DaysUnit(days));

    // Час напоминаний (этап 8, A5): утром, до часа дайджеста, — иначе «раз в день» приносит их только на следующее утро.
    [Fact]
    public void The_reminders_run_in_the_morning_before_the_digest_by_default()
    {
        Assert.Equal(7, new RemindersSettings().Hour);
        Assert.True(new RemindersSettings().Hour < new MailSettings().DigestHour);
    }

    [Theory]
    [InlineData(7, 8)]
    [InlineData(0, 8)]
    [InlineData(3, 4)]
    public void An_hour_before_the_digest_is_accepted(int hour, int digestHour)
    {
        using var provider = Provider(14, 3, hour, digestHour);

        Assert.Equal(hour, provider.GetRequiredService<IOptions<RemindersSettings>>().Value.Hour);
    }

    [Theory]
    [InlineData(8, 8)]
    [InlineData(9, 8)]
    [InlineData(23, 0)]
    public void An_hour_not_before_the_digest_is_refused_at_start_with_a_clear_text(int hour, int digestHour)
    {
        using var provider = Provider(14, 3, hour, digestHour);

        var failure = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<RemindersSettings>>().Value);

        Assert.Contains("Reminders:Hour", failure.Message);
        Assert.Contains("Mail:DigestHour", failure.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(24)]
    public void An_hour_outside_the_day_is_refused_at_start(int hour)
    {
        using var provider = Provider(14, 3, hour, digestHour: 23, mailEnabled: false);

        var failure = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<RemindersSettings>>().Value);

        Assert.Contains("Reminders:Hour", failure.Message);
    }

    // Без одной из задач часы друг с другом не связаны: выключенные напоминания не мешают настройкам почты, и наоборот.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void The_order_of_hours_is_not_checked_when_one_of_the_jobs_is_off(bool remindersEnabled, bool mailEnabled)
    {
        using var provider = Provider(14, 3, hour: 9, digestHour: 8, mailEnabled: mailEnabled, remindersEnabled: remindersEnabled);

        Assert.Equal(9, provider.GetRequiredService<IOptions<RemindersSettings>>().Value.Hour);
    }

    private static ServiceProvider Provider(
        int reworkDays, int warnDaysBefore, int hour = 7, int digestHour = 8, bool mailEnabled = true, bool remindersEnabled = true)
    {
        var services = new ServiceCollection();
        services.AddCore("Host=localhost");
        services.Configure<RemindersSettings>(o =>
            (o.ReworkDays, o.WarnDaysBefore, o.Hour, o.Enabled) = (reworkDays, warnDaysBefore, hour, remindersEnabled));
        services.Configure<MailSettings>(o => (o.DigestHour, o.Enabled) = (digestHour, mailEnabled));
        return services.BuildServiceProvider();
    }
}
