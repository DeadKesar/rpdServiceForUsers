using System.Net;

namespace RpdFos.Core.Data.Entities;

/// <summary>
/// Журнал аудита (таблица audit_log, раздел 2.4 ВКР, FR-ADM-02, FR-AUD-01): кто, когда и что сделал. Только
/// добавление. Первое событие — исправление методистом поля из учебного плана (этап 5.1).
/// </summary>
public class AuditLogEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Кто — запись справочника сотрудников; null — системное событие.</summary>
    public Guid? UserId { get; set; }

    public string EventType { get; set; } = "";
    public string? EntityType { get; set; }
    public Guid? EntityId { get; set; }

    /// <summary>Контекст события (jsonb): для исправления поля — место, было, стало.</summary>
    public string Payload { get; set; } = "{}";

    public IPAddress? IpAddress { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public static class AuditEvents
{
    /// <summary>Исправлено поле из учебного плана: <c>{ "path", "before", "after" }</c>.</summary>
    public const string PlanFieldCorrected = "document.plan_field_corrected";
}

public static class AuditEntities
{
    public const string Document = "document";
}
