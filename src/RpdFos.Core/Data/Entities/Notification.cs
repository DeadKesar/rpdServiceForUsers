namespace RpdFos.Core.Data.Entities;

/// <summary>
/// Уведомление пользователю о событии документа (таблица notifications, правка по ревизии архитектуры, FR-REV-08).
/// В прототипе — только канал «в интерфейсе»: <see cref="EmailedAt"/> остаётся пустым (решение автора, этап 6).
/// Создаётся в том же сохранении, что и событие, — <c>DocumentNotifications</c>; <see cref="Payload"/> — готовый текст
/// (<c>text</c>) и параметры события: документ, дисциплина, кто, комментарий, число замечаний.
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

    public DateTimeOffset? EmailedAt { get; set; }
}

/// <summary>
/// События, о которых уведомляют (этап 6.1, C3). Из FR-REV-08 в прототип вошли смены статуса и возвраты; «приближение
/// дедлайна» и «новый комментарий» не делаются.
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
}
