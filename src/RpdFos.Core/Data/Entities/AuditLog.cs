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

    /// <summary>
    /// Методист вернул согласованный документ на исправление и администратор принудительно сменил статус (этап 7, A8;
    /// NFR-SEC-07): <c>{ "from", "to", "comment" }</c>. Принудительная смена с согласования — ещё
    /// <c>"approval_interrupted": true</c>: круг прерван, как при возврате.
    /// </summary>
    public const string ReturnedForCorrection = "document.returned_for_correction", StatusForced = "document.status_forced";

    /// <summary>
    /// Согласованный документ отправлен в архив (этап 7, A9; NFR-SEC-07): <c>{ "from", "to" }</c> — статусы. Передача
    /// документа другому автору (FR-DOC-15): <c>{ "from", "to", "reason" }</c> — записи справочника прежнего и нового автора.
    /// </summary>
    public const string DocumentArchived = "document.archived", DocumentTransferred = "document.transferred";

    /// <summary>
    /// Документ создан на основе другого (этап 7, A11; FR-DOC-07): объект — новый документ, в содержимом
    /// <c>{ "base", "title", "not_transferred" }</c> — основа, её название и список неперенесённого. Из этой записи редактор
    /// берёт список, который показывает после создания. Основа устарела (A11; расширение FR-DOC-12): согласован документ
    /// на её основе для той же дисциплины — объект основа, в содержимом <c>{ "successor" }</c>.
    /// </summary>
    public const string DocumentBasedOn = "document.based_on", DocumentOutdated = "document.outdated";

    /// <summary>
    /// «Обновить из РПД» в ФОС (этап 7, B3; NFR-SEC-07): объект — ФОС, в содержимом <c>{ "rpd", "from_version", "to_version",
    /// "added_indicators", "removed_indicators", "added_sections", "removed_sections" }</c> — РПД, версии, с которой снят прежний
    /// снимок и нынешней, и что в снимке добавилось и исчезло.
    /// </summary>
    public const string RpdSnapshotRefreshed = "document.rpd_snapshot_refreshed";

    /// <summary>
    /// Закрепление дисциплины за сотрудником и его снятие (этап 7, A10; NFR-SEC-07): объект — дисциплина, в содержимом
    /// <c>{ "employee" }</c> — закреплённый. Закрепление при передаче документа (A9) — ещё <c>"document"</c>: документ, из-за
    /// которого оно появилось.
    /// </summary>
    public const string DisciplineAssigned = "discipline.assigned", DisciplineUnassigned = "discipline.unassigned";

    /// <summary>
    /// Согласование (этап 6, часть B): старт круга — <c>{ "seats": [{ "role", "resolver", "signatory" }] }</c>, отметки
    /// согласующих — <c>{ "role" }</c> и <c>{ "role", "comment" }</c>, назначение или замена подписанта —
    /// <c>{ "role", "before", "after" }</c>. По ним виден и прежний круг: строки согласования новый круг сбрасывает.
    /// </summary>
    public const string ApprovalStarted = "document.approval_started", SignatureApproved = "document.signature_approved",
        SignatureRejected = "document.signature_rejected", SignatoryAssigned = "document.signatory_assigned";

    /// <summary>Справочник сотрудников со страницы администратора (этап 5.2): <c>{ "before", "after" }</c>.</summary>
    public const string EmployeeCreated = "employee.created", EmployeeUpdated = "employee.updated",
        EmployeeDeactivated = "employee.deactivated", EmployeeReactivated = "employee.reactivated",
        EmployeeReplaced = "employee.replaced";

    /// <summary>Привязка строки матрицы к дисциплине плана и её отмена (этап 5.2).</summary>
    public const string MatrixRowLinked = "matrix_row.linked", MatrixRowUnlinked = "matrix_row.unlinked";
}

public static class AuditEntities
{
    public const string Document = "document", Employee = "employee", MatrixRow = "matrix_row", Discipline = "discipline";
}
