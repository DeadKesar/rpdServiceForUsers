using System.Text;
using System.Text.Json;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Mail;

/// <summary>
/// Письма по уведомлениям (этап 7, A4, A5): письмо об одном событии и дайджест. Тема и текст — из <c>payload</c>
/// (<c>subject</c> и <c>text</c>), ниже — ссылка на документ, если уведомление к нему привязано. У уведомлений, созданных
/// до появления темы, темой служит начало текста.
/// </summary>
public static class NotificationLetter
{
    // Тема в списке писем должна читаться целиком; названия документов длинные.
    private const int SubjectLimit = 150;

    private const string Signature = "\n\n-- \nАвтоматическое сообщение системы создания РПД и ФОС.";

    /// <param name="publicUrl">Адрес системы (<see cref="MailSettings.PublicUrl"/>); пусто — письмо без ссылки.</param>
    public static OutgoingMail Compose(Notification notification, string to, string? toName, string publicUrl)
    {
        var (text, subject) = ReadPayload(notification);
        // Заголовок письма — одна строка: переводы строк в теме (из комментария автора, например) недопустимы.
        var line = OneLine(string.IsNullOrWhiteSpace(subject) ? text : subject);
        if (line.Length == 0) line = "Уведомление системы создания РПД и ФОС";
        else if (line.Length > SubjectLimit) line = line[..(SubjectLimit - 1)] + "…";

        var body = new StringBuilder(text);
        if (LinkOf(notification, publicUrl) is { } link) body.Append($"\n\nОткрыть документ: {link}");
        body.Append(Signature);
        return new OutgoingMail(to, toName, line, body.ToString());
    }

    /// <summary>
    /// Дайджест: одно письмо получателю со всеми <paramref name="notifications"/> (в порядке событий). Тема — дата отправки
    /// по времени <paramref name="zone"/>; в тексте — число уведомлений и по абзацу на каждое: время события, текст
    /// одной строкой и ссылка на документ. Время событий — в том же поясе, что и дата в теме.
    /// </summary>
    public static OutgoingMail ComposeDigest(
        IReadOnlyList<Notification> notifications, string to, string? toName, string publicUrl, DateTimeOffset at, TimeZoneInfo zone)
    {
        var body = new StringBuilder($"Новых уведомлений: {notifications.Count}.");
        for (var i = 0; i < notifications.Count; i++)
        {
            var notification = notifications[i];
            var (text, subject) = ReadPayload(notification);
            var line = OneLine(text.Length > 0 ? text : subject ?? "");
            if (line.Length == 0) line = "Уведомление системы создания РПД и ФОС";
            body.Append($"\n\n{i + 1}. {Local(notification.CreatedAt, zone, "dd.MM.yyyy HH:mm")} — {line}");
            if (LinkOf(notification, publicUrl) is { } link) body.Append($"\n   Открыть документ: {link}");
        }
        body.Append(Signature);
        return new OutgoingMail(to, toName, $"Дайджест уведомлений от {Local(at, zone, "dd.MM.yyyy")}", body.ToString());
    }

    // Дата и время — одинаково на любом сервере: формат не зависит от региональных настроек системы.
    private static string Local(DateTimeOffset moment, TimeZoneInfo zone, string format) =>
        TimeZoneInfo.ConvertTime(moment, zone).ToString(format, System.Globalization.CultureInfo.InvariantCulture);

    // Ссылка на документ — только у уведомления, привязанного к документу, и при заданном адресе системы.
    private static string? LinkOf(Notification notification, string publicUrl)
    {
        var address = publicUrl.Trim().TrimEnd('/');
        return notification.DocumentId is Guid documentId && address.Length > 0 ? $"{address}/documents/{documentId}" : null;
    }

    private static (string Text, string? Subject) ReadPayload(Notification notification)
    {
        string? text = null, subject = null;
        using var payload = JsonDocument.Parse(notification.Payload);
        var root = payload.RootElement;
        if (root.ValueKind == JsonValueKind.Object)
        {
            text = StringOf(root, "text");
            subject = StringOf(root, "subject");
        }
        return ((text ?? "").Trim(), subject);
    }

    private static string? StringOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string OneLine(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
