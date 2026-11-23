namespace RpdFos.Core.Mail;

/// <summary>Текстовое письмо одному получателю. <see cref="ToName"/> — как подписать адрес в заголовке «Кому».</summary>
public sealed record OutgoingMail(string To, string? ToName, string Subject, string Body);

/// <summary>
/// Отправка писем. Настоящая реализация — SMTP (<see cref="SmtpMailSender"/>); в тестах она подменяется, как конвертер PDF.
/// Соединение открывается явно (<see cref="OpenAsync"/>), чтобы задача рассылки отправила все письма запуска по одному
/// соединению: настоящий сервер на каждое подключение входит заново и может ограничивать их частоту.
/// </summary>
public interface IMailSender
{
    /// <summary>Подключается к серверу (и входит, если задан <see cref="MailSettings.User"/>).</summary>
    /// <exception cref="MailServerUnavailableException">Сервер не принял соединение или не принял вход.</exception>
    Task<IMailConnection> OpenAsync(CancellationToken ct = default);
}

/// <summary>Открытое соединение с почтовым сервером. Закрывается <see cref="IAsyncDisposable.DisposeAsync"/>.</summary>
public interface IMailConnection : IAsyncDisposable
{
    /// <exception cref="MailServerUnavailableException">
    /// Соединение оборвалось: пробовать остальные письма этого запуска незачем. Любая другая ошибка — сервер не принял
    /// именно это письмо (неверный адрес), соединение остаётся рабочим.
    /// </exception>
    Task SendAsync(OutgoingMail mail, CancellationToken ct = default);
}

public static class MailSenderExtensions
{
    /// <summary>Одно письмо по своему соединению — для служебных писем, которые уходят сразу и поодиночке.</summary>
    public static async Task SendAsync(this IMailSender sender, OutgoingMail mail, CancellationToken ct = default)
    {
        await using var connection = await sender.OpenAsync(ct);
        await connection.SendAsync(mail, ct);
    }
}

/// <summary>Почтовый сервер недоступен — не принял соединение или вход, не ответил или оборвал соединение.</summary>
public sealed class MailServerUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
