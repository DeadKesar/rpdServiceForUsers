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

    /// <summary>Автор удалил свой черновик (этап 6, A5): <c>{ "title", "status" }</c>.</summary>
    public const string DocumentDeleted = "document.deleted";

    /// <summary>Справочник сотрудников со страницы администратора (этап 5.2): <c>{ "before", "after" }</c>.</summary>
    public const string EmployeeCreated = "employee.created", EmployeeUpdated = "employee.updated",
        EmployeeDeactivated = "employee.deactivated", EmployeeReactivated = "employee.reactivated",
        EmployeeReplaced = "employee.replaced";

    /// <summary>Привязка строки матрицы к дисциплине плана и её отмена (этап 5.2).</summary>
    public const string MatrixRowLinked = "matrix_row.linked", MatrixRowUnlinked = "matrix_row.unlinked";
}

public static class AuditEntities
{
    public const string Document = "document", Employee = "employee", MatrixRow = "matrix_row";
}
