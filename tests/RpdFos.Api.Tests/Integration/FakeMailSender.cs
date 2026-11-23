using RpdFos.Core.Mail;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Почтовый отправитель для тестов: настоящий (SMTP) нужен почтовому серверу, которого в тестах нет. Запоминает
/// попытки и принятые письма; <see cref="FailFor"/> заставляет отправку по адресу отказывать — как недоступный сервер или
/// как письмо, которое сервер не принял; <see cref="Down"/> — сервер не принимает соединение. Считает открытые соединения
/// (<see cref="Connections"/>): задача рассылки открывает одно на запуск. Отправитель общий на хост, а база общая для
/// тестов коллекции, поэтому тесты отбирают письма по своим адресам.
/// </summary>
public sealed class FakeMailSender : IMailSender
{
    private readonly object _lock = new();
    private readonly List<OutgoingMail> _sent = [];
    private readonly List<string> _attempts = [];
    private readonly Dictionary<string, Exception> _failures = new(StringComparer.OrdinalIgnoreCase);
    private int _connections;
    private int _disposed;
    private bool _down;

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

    /// <summary>Сколько соединений открыто за всё время; закрытых из них — <see cref="Closed"/>.</summary>
    public int Connections
    {
        get { lock (_lock) return _connections; }
    }

    public int Closed
    {
        get { lock (_lock) return _disposed; }
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

    /// <summary>Сервер не принимает соединение — пока не вызван <c>Down = false</c>.</summary>
    public bool Down
    {
        get { lock (_lock) return _down; }
        set { lock (_lock) _down = value; }
    }

    public Task<IMailConnection> OpenAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_down) throw new MailServerUnavailableException("Почтовый сервер не принимает соединение (тест)");
            _connections++;
        }
        return Task.FromResult<IMailConnection>(new Connection(this));
    }

    private void Deliver(OutgoingMail mail)
    {
        lock (_lock)
        {
            _attempts.Add(mail.To);
            if (_failures.TryGetValue(mail.To, out var error)) throw error;
            _sent.Add(mail);
        }
    }

    private void Closing()
    {
        lock (_lock) _disposed++;
    }

    private sealed class Connection(FakeMailSender owner) : IMailConnection
    {
        public Task SendAsync(OutgoingMail mail, CancellationToken ct = default)
        {
            owner.Deliver(mail);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            owner.Closing();
            return ValueTask.CompletedTask;
        }
    }
}
