using MailKit; // SendAsync(MimeMessage) — метод расширения IMailTransportExtensions
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace RpdFos.Core.Mail;

/// <summary>
/// Отправка по SMTP (MailKit, MIT). Соединение — на каждое письмо: писем немного, а долгоживущее соединение пришлось бы
/// следить за обрывами. Шифрование выбирает MailKit по серверу (<see cref="SecureSocketOptions.Auto"/>): ловушка без TLS
/// принимает письма как есть, настоящий сервер — по SSL или STARTTLS. Входа по логину нет: он нужен настоящему серверу —
/// этап 8.
/// </summary>
public sealed class SmtpMailSender(IOptions<MailSettings> settings) : IMailSender
{
    // Дольше сервер на команду не отвечает — он недоступен; письма ждут следующего запуска.
    private const int TimeoutMilliseconds = 30_000;

    public async Task SendAsync(OutgoingMail mail, CancellationToken ct = default)
    {
        var options = settings.Value;

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(options.From));
        message.To.Add(string.IsNullOrWhiteSpace(mail.ToName)
            ? MailboxAddress.Parse(mail.To)
            : new MailboxAddress(mail.ToName, mail.To));
        message.Subject = mail.Subject;
        message.Body = new TextPart("plain") { Text = mail.Body }; // кодировка — UTF-8

        using var client = new SmtpClient { Timeout = TimeoutMilliseconds };
        try
        {
            await client.ConnectAsync(options.Host, options.Port, SecureSocketOptions.Auto, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new MailServerUnavailableException($"Почтовый сервер {options.Host}:{options.Port} недоступен: {e.Message}", e);
        }

        await client.SendAsync(message, ct);
        await client.DisconnectAsync(quit: true, ct);
    }
}
