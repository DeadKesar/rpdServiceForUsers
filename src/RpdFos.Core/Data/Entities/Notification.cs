namespace RpdFos.Core.Data.Entities;

/// <summary>
/// Уведомление пользователю о событии документа (таблица notifications, правка по ревизии архитектуры, FR-REV-08).
/// Создаётся в том же сохранении, что и событие, — <c>DocumentNotifications</c>; <see cref="Payload"/> — готовый текст
/// (<c>text</c>), тема письма (<c>subject</c>) и параметры события: документ, дисциплина, кто, комментарий, число
/// замечаний. В ленте интерфейса уведомление видно сразу; на почту его отправляет фоновая задача <c>NotificationMailJob</c>
/// (этап 7, A4) — время отправки в <see cref="EmailedAt"/>.
/// </summary>
public class Notification
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Получатель — запись справочника сотрудников.</summary>
    public Guid RecipientId { get; set; }

    public string EventType { get; set; } = "";
    public Guid? DocumentId { get; set; }

    /// <summary>Текст и параметры уведомления (jsonb).</summary>
    public string Payload { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Время прочтения; null — не прочитано.</summary>
    public DateTimeOffset? ReadAt { get; set; }

    /// <summary>
    /// Время отправки по почте; null — не отправлено (почта выключена для события, у получателя нет учётной записи или
    /// отправка ещё не прошла).
    /// </summary>
    public DateTimeOffset? EmailedAt { get; set; }
}

/// <summary>Как событие уходит на почту получателю.</summary>
public enum MailMode
{
    /// <summary>Письма нет.</summary>
    Off,

    /// <summary>Письмо уходит сразу — фоновой задачей в пределах минуты после события.</summary>
    Immediately
}

/// <summary>Событие уведомлений и его умолчание для почты — пока пользователь не менял настройки.</summary>
public sealed record NotificationEventInfo(string Type, MailMode DefaultMail);

/// <summary>
/// События, о которых уведомляют (этап 6.1, C3; этап 7, A3). Из FR-REV-08 в прототип вошли смены статуса, возвраты и
/// события печати и согласования. «Новый комментарий» отдельным событием не делается (отступление от FR-REV-08):
/// замечания к полям приходят вместе с возвратом — в уведомлении их число. Напоминания о сроке — этап 7, A6.
/// <para>
/// Каждое событие записывается и в <see cref="All"/> — единый перечень с умолчаниями почты (этап 7, A4); из него же
/// строится таблица настроек уведомлений (A5). Тест сверяет перечень с константами: событие, которого в нём нет, не пройдёт.
/// </para>
/// </summary>
public static class NotificationEvents
{
    /// <summary>Автору: методист взял документ на проверку из его статуса (этап 6, A1).</summary>
    public const string TakenForReview = "document.taken_for_review";

    /// <summary>Автору: возвращён на доработку — с числом замечаний.</summary>
    public const string ReturnedForRework = "document.returned_for_rework";

    /// <summary>Автору: принятый документ вернулся после исправления данных плана, которое сделало его неполным (этап 5.2).</summary>
    public const string ReturnedAfterPlanCorrection = "document.returned_after_plan_correction";

    /// <summary>Автору: принят — согласование начато или ждёт назначения согласующих.</summary>
    public const string Accepted = "document.accepted";

    /// <summary>Методистам: отправлен на проверку, в том числе повторно — с доработки.</summary>
    public const string Submitted = "document.submitted";

    /// <summary>Методистам: возвращён с согласования — кто и комментарий.</summary>
    public const string ReturnedFromApproval = "document.returned_from_approval";

    /// <summary>Автору и методистам: согласован.</summary>
    public const string Approved = "document.approved";

    /// <summary>Согласующему: документ ждёт его отметки — при старте круга и при назначении на идущем круге.</summary>
    public const string AwaitingMark = "approval.awaiting_mark";

    /// <summary>Автору: методист запустил согласование принятого документа вручную — после назначения согласующих (этап 7, A3).</summary>
    public const string ApprovalStarted = "approval.started";

    /// <summary>
    /// Прежнему согласующему: на идущем круге его место занял другой (этап 7, A3). Без ссылки на документ, если он ему
    /// больше недоступен.
    /// </summary>
    public const string SignatoryReplaced = "approval.signatory_replaced";

    /// <summary>Тому, кто запустил формирование: печатная форма готова — со ссылкой на документ (этап 7, A3).</summary>
    public const string PrintReady = "document.print_ready";

    /// <summary>Тому, кто запустил формирование: печатная форма не сформирована — с причиной (этап 7, A3).</summary>
    public const string PrintFailed = "document.print_failed";

    /// <summary>
    /// Все события с умолчаниями почты. «Сразу» — у событий, которые требуют действия получателя: документ вернулся на
    /// доработку (в том числе после исправления данных плана), ждёт согласования, возвращён с согласования; напоминания
    /// (A6) пополнят этот ряд. У остальных почта выключена: они сообщают, а не просят действовать, и лента интерфейса
    /// их показывает. Настройка пользователя (A5) перекрывает умолчание.
    /// </summary>
    public static IReadOnlyList<NotificationEventInfo> All { get; } =
    [
        new(TakenForReview, MailMode.Off),
        new(ReturnedForRework, MailMode.Immediately),
        new(ReturnedAfterPlanCorrection, MailMode.Immediately),
        new(Accepted, MailMode.Off),
        new(Submitted, MailMode.Off),
        new(ReturnedFromApproval, MailMode.Immediately),
        new(Approved, MailMode.Off),
        new(AwaitingMark, MailMode.Immediately),
        new(ApprovalStarted, MailMode.Off),
        new(SignatoryReplaced, MailMode.Off),
        new(PrintReady, MailMode.Off),
        new(PrintFailed, MailMode.Off)
    ];

    /// <summary>Умолчание почты для события; неизвестное событие — без почты.</summary>
    public static MailMode DefaultMailOf(string eventType) =>
        All.FirstOrDefault(e => e.Type == eventType)?.DefaultMail ?? MailMode.Off;
}
