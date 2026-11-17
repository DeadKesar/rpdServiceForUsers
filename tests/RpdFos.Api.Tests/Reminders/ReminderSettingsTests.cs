using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RpdFos.Core;
using RpdFos.Core.Documents;
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

    private static ServiceProvider Provider(int reworkDays, int warnDaysBefore)
    {
        var services = new ServiceCollection();
        services.AddCore("Host=localhost");
        services.Configure<RemindersSettings>(o => (o.ReworkDays, o.WarnDaysBefore) = (reworkDays, warnDaysBefore));
        return services.BuildServiceProvider();
    }
}
