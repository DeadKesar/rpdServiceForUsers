namespace RpdFos.Core.Mail;

/// <summary>
/// Настройки почты — раздел <c>Mail</c> конфигурации (этап 7, A4). В разработке письма уходят в ловушку Mailpit из
/// docker-compose (порт 1025), настоящий почтовый сервер подключается этими же настройками (этап 8, A5): адрес, вход и
/// режим шифрования.
/// </summary>
public sealed class MailSettings
{
    /// <summary>
    /// false — письма не отправляются, повторяющаяся задача не ставится (сервер без почты; тесты, которые запускают
    /// отправку сами). Уведомления в интерфейсе от этого не зависят.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Почтовый сервер.</summary>
    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 1025;

    /// <summary>
    /// Логин на почтовом сервере. Пусто — подключение без входа (ловушка Mailpit). Пароль не хранится в репозитории:
    /// user-secrets или переменные окружения (<c>Mail__User</c>, <c>Mail__Password</c>).
    /// </summary>
    public string User { get; set; } = "";

    public string Password { get; set; } = "";

    /// <summary>
    /// Режим шифрования. Вход без шифрования (<see cref="MailSecurity.None"/> с <see cref="User"/>) отправил бы пароль по
    /// сети открытым текстом; хост этого не запрещает — это выбор настроек, а в тестах на ловушке вход без TLS нужен.
    /// </summary>
    public MailSecurity Security { get; set; } = MailSecurity.Auto;

    /// <summary>Отправитель: «Имя &lt;адрес&gt;» или просто адрес.</summary>
    public string From { get; set; } = "АС РПД и ФОС <rpdfos@rpdfos.local>";

    /// <summary>
    /// Адрес системы для ссылок в письмах — тот, по которому пользователи открывают клиент. Пусто — письма уходят без
    /// ссылок.
    /// </summary>
    public string PublicUrl { get; set; } = "";

    /// <summary>
    /// Сколько часов после срока отправки письмо о событии ещё отправляется. Срок «сразу» — время события, срок дайджеста —
    /// ближайший час <see cref="DigestHour"/> после события. Повтор после сбоя идёт при каждом запуске задачи, и без
    /// предела письмо, которое сервер не принимает (неверный адрес), повторялось бы вечно, а после долгого простоя почты
    /// ушли бы устаревшие письма о давних событиях. Событие остаётся в ленте интерфейса в любом случае.
    /// </summary>
    public int MaxAgeHours { get; set; } = 24;

    /// <summary>
    /// Час (0–23) по времени сервера, в который уходит дайджест — одно письмо на получателя со всеми неотправленными
    /// уведомлениями тех событий, у которых в настройках профиля почта «раз в день» (этап 7, A5). Часовой пояс —
    /// системный пояс сервера (<see cref="TimeProvider.LocalTimeZone"/>): на сервере он задаётся его настройками.
    /// </summary>
    public int DigestHour { get; set; } = 8;
}

/// <summary>Режим шифрования соединения с почтовым сервером (<c>Mail:Security</c>).</summary>
public enum MailSecurity
{
    /// <summary>Выбирает MailKit по серверу: порт 465 — SSL сразу, иначе STARTTLS, если сервер его предлагает.</summary>
    Auto,

    /// <summary>Без шифрования — ловушка Mailpit в разработке.</summary>
    None,

    /// <summary>STARTTLS обязателен: без него сервер отвергается.</summary>
    StartTls,

    /// <summary>SSL с первого байта (обычно порт 465).</summary>
    SslOnConnect
}
