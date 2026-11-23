using System.Globalization;
using RpdFos.Core.Mail;

namespace RpdFos.Core.Accounts;

/// <summary>
/// Служебные письма об учётной записи (этап 8, A6): восстановление пароля и приглашение. Это не уведомления — у них нет
/// настроек каналов, письмо уходит сразу. Срок указан датой и временем, а не «сутками»: так точнее и без склонений.
/// </summary>
public static class AccountLetters
{
    private const string Signature = "\n\n-- \nАвтоматическое сообщение системы создания РПД и ФОС. Отвечать на него не нужно.";

    public static OutgoingMail Reset(string to, string? name, string link, DateTimeOffset validUntil, TimeZoneInfo zone) =>
        new(to, name, "Восстановление пароля — система создания РПД и ФОС",
            $"{Greeting(name)}Для вашей учётной записи запрошена смена пароля. Чтобы задать новый пароль, откройте ссылку:\n\n{link}\n\n" +
            $"Ссылка сработает один раз и действует до {Until(validUntil, zone)}.\n\n" +
            "Если вы не запрашивали смену пароля, ничего делать не нужно: прежний пароль продолжает действовать." + Signature);

    public static OutgoingMail Invitation(string to, string? name, string link, DateTimeOffset validUntil, TimeZoneInfo zone) =>
        new(to, name, "Приглашение в систему создания РПД и ФОС — задайте пароль",
            $"{Greeting(name)}Для вас создана учётная запись в системе создания рабочих программ дисциплин и фондов оценочных " +
            $"средств. Вход — по адресу {to}. Чтобы задать пароль, откройте ссылку:\n\n{link}\n\n" +
            $"Ссылка сработает один раз и действует до {Until(validUntil, zone)}. " +
            "Если срок истёк, на странице входа есть «Забыли пароль?» — или попросите администратора отправить приглашение снова." + Signature);

    private static string Greeting(string? name) => string.IsNullOrWhiteSpace(name) ? "Здравствуйте!\n\n" : $"Здравствуйте, {name}!\n\n";

    // Формат не зависит от региональных настроек сервера.
    private static string Until(DateTimeOffset moment, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(moment, zone).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
}
