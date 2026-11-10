namespace RpdFos.Core.Data.Entities;

/// <summary>
/// Уведомление пользователю о событии документа (таблица notifications, правка по ревизии архитектуры, FR-REV-08).
/// В прототипе — только канал «в интерфейсе»: <see cref="EmailedAt"/> остаётся пустым (решение автора, этап 6).
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
