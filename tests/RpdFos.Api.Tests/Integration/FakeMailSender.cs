using RpdFos.Core.Mail;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Почтовый отправитель для тестов: настоящий (SMTP) нужен почтовому серверу, которого в тестах нет. Запоминает
/// попытки и принятые письма; <see cref="FailFor"/> заставляет отправку по адресу отказывать — как недоступный сервер или
/// как письмо, которое сервер не принял. Отправитель общий на хост, а база общая для тестов коллекции, поэтому тесты
/// отбирают письма по своим адресам.
/// </summary>
public sealed class FakeMailSender : IMailSender
{
    private readonly object _lock = new();
    private readonly List<OutgoingMail> _sent = [];
    private readonly List<string> _attempts = [];
    private readonly Dictionary<string, Exception> _failures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Принятые письма в порядке отправки.</summary>
    public IReadOnlyList<OutgoingMail> Sent
    {
        get { lock (_lock) return [.. _sent]; }
    }

    /// <summary>Адреса всех попыток, в том числе неудачных, в порядке попыток.</summary>
    public IReadOnlyList<string> Attempts
    {
        get { lock (_lock) return [.. _attempts]; }
    }

    /// <summary>Письма на адрес.</summary>
    public IReadOnlyList<OutgoingMail> SentTo(string address) => Sent.Where(m => m.To.Equals(address, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Отправка на адрес завершается ошибкой — пока не вызван <see cref="Recover"/>.</summary>
    public void FailFor(string address, Exception error)
    {
        lock (_lock) _failures[address] = error;
    }

    public void Recover(string address)
    {
        lock (_lock) _failures.Remove(address);
    }

    public Task SendAsync(OutgoingMail mail, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _attempts.Add(mail.To);
            if (_failures.TryGetValue(mail.To, out var error)) throw error;
            _sent.Add(mail);
        }
        return Task.CompletedTask;
    }
}
