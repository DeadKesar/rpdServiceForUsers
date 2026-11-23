using MailKit; // SendAsync(MimeMessage) — метод расширения IMailTransportExtensions
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace RpdFos.Core.Mail;

/// <summary>
/// Отправка по SMTP (MailKit, MIT). Соединение — на запуск задачи рассылки, а не на письмо (этап 8, A5): подключиться, войти,
/// отправить все письма, отключиться. Шифрование — <see cref="MailSettings.Security"/>: по умолчанию его выбирает MailKit по
/// серверу (ловушка без TLS принимает письма как есть, настоящий сервер — по SSL или STARTTLS). Вход — только если задан
/// <see cref="MailSettings.User"/>.
/// </summary>
public sealed class SmtpMailSender(IOptions<MailSettings> settings) : IMailSender
{
    // Дольше сервер на команду не отвечает — он недоступен; письма ждут следующего запуска.
    private const int TimeoutMilliseconds = 30_000;

    public async Task<IMailConnection> OpenAsync(CancellationToken ct = default)
    {
        var options = settings.Value;
        var from = MailboxAddress.Parse(options.From);
        var client = new SmtpClient { Timeout = TimeoutMilliseconds };
        try
        {
            await client.ConnectAsync(options.Host, options.Port, SocketOptions(options.Security), ct);
            if (!string.IsNullOrEmpty(options.User))
                await client.AuthenticateAsync(options.User, options.Password ?? "", ct);
        }
        catch (Exception e)
        {
            client.Dispose();
            if (e is OperationCanceledException) throw;
            // Пароль в текст не попадает: сообщение уходит в журнал хоста.
            var what = e is AuthenticationException
                ? $"Почтовый сервер {options.Host}:{options.Port} не принял вход пользователя {options.User}"
                : $"Почтовый сервер {options.Host}:{options.Port} недоступен";
            throw new MailServerUnavailableException($"{what}: {e.Message}", e);
        }

        return new Connection(client, from);
    }

    // Auto оставляет выбор MailKit: порт 465 — SSL сразу, остальные — STARTTLS, если сервер его предлагает.
    public static SecureSocketOptions SocketOptions(MailSecurity security) => security switch
    {
        MailSecurity.None => SecureSocketOptions.None,
        MailSecurity.StartTls => SecureSocketOptions.StartTls,
        MailSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        _ => SecureSocketOptions.Auto
    };

    private sealed class Connection(SmtpClient client, MailboxAddress from) : IMailConnection
    {
        public async Task SendAsync(OutgoingMail mail, CancellationToken ct = default)
        {
            var message = new MimeMessage();
            message.From.Add(from);
            message.To.Add(string.IsNullOrWhiteSpace(mail.ToName)
                ? MailboxAddress.Parse(mail.To)
                : new MailboxAddress(mail.ToName, mail.To));
            message.Subject = mail.Subject;
            message.Body = new TextPart("plain") { Text = mail.Body }; // кодировка — UTF-8

            try
            {
                await client.SendAsync(message, ct);
            }
            // Сервер не принял письмо (адрес): MailKit сбросил сеанс, соединение пригодно для следующего письма.
            // Оборванное соединение — другое дело: дальше пробовать незачем.
            catch (Exception e) when (e is not OperationCanceledException && !client.IsConnected)
            {
                throw new MailServerUnavailableException($"Соединение с почтовым сервером оборвано: {e.Message}", e);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (client.IsConnected) await client.DisconnectAsync(quit: true);
            }
            catch (Exception)
            {
                // Письма уже отправлены; сервер, не ответивший на QUIT, их не отменяет.
            }
            client.Dispose();
        }
    }
}
