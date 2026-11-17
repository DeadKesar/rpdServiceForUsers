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

/// <summary>Как событие уходит на почту получателю (настройка «почта» в профиле, этап 7, A5).</summary>
public enum MailMode
{
    /// <summary>Письма нет.</summary>
    Off,

    /// <summary>Письмо уходит сразу — фоновой задачей в пределах минуты после события.</summary>
    Immediately,

    /// <summary>Событие входит в ежедневный дайджест — одно письмо на получателя в час <c>Mail:DigestHour</c>.</summary>
    Daily
}

/// <summary>
/// Событие уведомлений: название для таблицы настроек и умолчание почты — пока пользователь не менял настройки.
/// В интерфейсе все события показываются по умолчанию.
/// </summary>
public sealed record NotificationEventInfo(string Type, string Title, MailMode DefaultMail);

/// <summary>
/// События, о которых уведомляют (этап 6.1, C3; этап 7, A3, A6). Из FR-REV-08 в прототип вошли смены статуса, возвраты,
/// события печати и согласования и напоминания о сроке доработки («приближение дедлайна», FR-REV-09). «Новый комментарий»
/// отдельным событием не делается (отступление от FR-REV-08): замечания к полям приходят вместе с возвратом — в
/// уведомлении их число.
/// <para>
/// Каждое событие записывается и в <see cref="All"/> — единый перечень с названием и умолчанием почты (этап 7, A4);
/// из него же строится таблица настроек уведомлений в профиле (A5): событие, которое добавили в перечень, появляется в
/// таблице само. Тест сверяет перечень с константами: событие, которого в нём нет, не пройдёт.
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
    /// Автору: срок доработки истекает — за <c>Reminders:WarnDaysBefore</c> дней до срока; один раз за одно пребывание
    /// документа на доработке (этап 7, A6).
    /// </summary>
    public const string ReworkDeadlineNear = "document.rework_deadline_near";

    /// <summary>
    /// Заведующему кафедрой дисциплины и методистам: документ на доработке дольше срока (<c>Reminders:ReworkDays</c>);
    /// один раз за одно пребывание на доработке (этап 7, A6, FR-REV-09).
    /// </summary>
    public const string ReworkOverdue = "document.rework_overdue";

    /// <summary>
    /// Все события с названиями и умолчаниями почты — в порядке таблицы настроек. «Сразу» — у событий, которые требуют
    /// действия получателя: документ вернулся на доработку (в том числе после исправления данных плана), ждёт
    /// согласования, возвращён с согласования, срок доработки истекает или вышел. У остальных почта выключена: они
    /// сообщают, а не просят действовать, и лента интерфейса их показывает. Настройка пользователя (A5) перекрывает
    /// умолчание. Названия — без родовых форм и без указания адресата: пол получателя системе неизвестен, а одно событие
    /// приходит и автору, и методистам. Срок доработки настраивается, поэтому в названии события числа дней нет.
    /// </summary>
    public static IReadOnlyList<NotificationEventInfo> All { get; } =
    [
        new(Submitted, "Документ отправлен на проверку", MailMode.Off),
        new(TakenForReview, "Документ взят на проверку", MailMode.Off),
        new(ReturnedForRework, "Документ возвращён на доработку", MailMode.Immediately),
        new(ReturnedAfterPlanCorrection, "Документ возвращён после исправления данных учебного плана", MailMode.Immediately),
        new(ReworkDeadlineNear, "Срок доработки документа истекает", MailMode.Immediately),
        new(ReworkOverdue, "Документ на доработке дольше срока", MailMode.Immediately),
        new(Accepted, "Документ принят", MailMode.Off),
        new(ApprovalStarted, "Согласование начато", MailMode.Off),
        new(AwaitingMark, "Документ ждёт вашего согласования", MailMode.Immediately),
        new(SignatoryReplaced, "Согласующий заменён", MailMode.Off),
        new(ReturnedFromApproval, "Документ возвращён с согласования", MailMode.Immediately),
        new(Approved, "Документ согласован", MailMode.Off),
        new(PrintReady, "Печатная форма готова", MailMode.Off),
        new(PrintFailed, "Печатная форма не сформирована", MailMode.Off)
    ];

    /// <summary>Событие по типу; неизвестное — <c>null</c>.</summary>
    public static NotificationEventInfo? Find(string eventType) => All.FirstOrDefault(e => e.Type == eventType);

    /// <summary>Умолчание почты для события; неизвестное событие — без почты.</summary>
    public static MailMode DefaultMailOf(string eventType) => Find(eventType)?.DefaultMail ?? MailMode.Off;
}
