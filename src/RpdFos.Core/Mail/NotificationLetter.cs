using System.Text;
using System.Text.Json;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Mail;

/// <summary>
/// Письмо по уведомлению (этап 7, A4): тема и текст — из <c>payload</c> (<c>subject</c> и <c>text</c>), ниже — ссылка на
/// документ, если уведомление к нему привязано. У уведомлений, созданных до появления темы, темой служит начало текста.
/// </summary>
public static class NotificationLetter
{
    // Тема в списке писем должна читаться целиком; названия документов длинные.
    private const int SubjectLimit = 150;

    /// <param name="publicUrl">Адрес системы (<see cref="MailSettings.PublicUrl"/>); пусто — письмо без ссылки.</param>
    public static OutgoingMail Compose(Notification notification, string to, string? toName, string publicUrl)
    {
        string? text = null, subject = null;
        using (var payload = JsonDocument.Parse(notification.Payload))
        {
            var root = payload.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                text = StringOf(root, "text");
                subject = StringOf(root, "subject");
            }
        }

        text = (text ?? "").Trim();
        // Заголовок письма — одна строка: переводы строк в теме (из комментария автора, например) недопустимы.
        var line = OneLine(string.IsNullOrWhiteSpace(subject) ? text : subject);
        if (line.Length == 0) line = "Уведомление системы создания РПД и ФОС";
        else if (line.Length > SubjectLimit) line = line[..(SubjectLimit - 1)] + "…";

        var body = new StringBuilder(text);
        var address = publicUrl.Trim().TrimEnd('/');
        if (notification.DocumentId is Guid documentId && address.Length > 0)
            body.Append($"\n\nОткрыть документ: {address}/documents/{documentId}");
        body.Append("\n\n-- \nАвтоматическое сообщение системы создания РПД и ФОС.");
        return new OutgoingMail(to, toName, line, body.ToString());
    }

    private static string? StringOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string OneLine(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
