using Microsoft.Extensions.Options;
using RpdFos.Core.Mail;

namespace RpdFos.Core.Reminders;

/// <summary>
/// Проверка часа напоминаний при старте хоста (этап 8, A5). Ему нужны и настройки почты, поэтому отдельный класс, а не
/// <c>Validate</c> у самих настроек.
/// <para>
/// Час напоминаний — строго раньше часа дайджеста, а не «не позже»: при равенстве задача создаёт уведомления в 07:00:xx,
/// когда час дайджеста этого дня уже наступил, и «раз в день» принёс бы их только на следующее утро. Проверка нужна лишь
/// когда идут обе задачи: без почты или без напоминаний часы друг с другом не связаны.
/// </para>
/// </summary>
public sealed class RemindersSettingsValidator(IOptions<MailSettings> mail) : IValidateOptions<RemindersSettings>
{
    public ValidateOptionsResult Validate(string? name, RemindersSettings options)
    {
        if (options.Hour is < 0 or > 23)
            return ValidateOptionsResult.Fail("Reminders:Hour — час от 0 до 23.");

        var digest = mail.Value;
        if (options.Enabled && digest.Enabled && options.Hour >= digest.DigestHour)
            return ValidateOptionsResult.Fail(
                $"Reminders:Hour ({options.Hour}) должен быть раньше Mail:DigestHour ({digest.DigestHour}): иначе напоминание, " +
                "которое в профиле получают «раз в день», придёт только на следующее утро.");

        return ValidateOptionsResult.Success;
    }
}
