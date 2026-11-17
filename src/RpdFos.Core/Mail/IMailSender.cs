namespace RpdFos.Core.Mail;

/// <summary>Текстовое письмо одному получателю. <see cref="ToName"/> — как подписать адрес в заголовке «Кому».</summary>
public sealed record OutgoingMail(string To, string? ToName, string Subject, string Body);

/// <summary>
/// Отправка письма. Настоящая реализация — SMTP (<see cref="SmtpMailSender"/>); в тестах она подменяется, как конвертер PDF.
/// </summary>
public interface IMailSender
{
    /// <exception cref="MailServerUnavailableException">
    /// Сервер не принял соединение: пробовать остальные письма этого запуска незачем.
    /// </exception>
    Task SendAsync(OutgoingMail mail, CancellationToken ct = default);
}

/// <summary>Почтовый сервер недоступен — не принял соединение или не ответил.</summary>
public sealed class MailServerUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
