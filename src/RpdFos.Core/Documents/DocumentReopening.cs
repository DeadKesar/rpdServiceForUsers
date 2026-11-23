using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Documents;

public enum ReopeningProblem
{
    /// <summary>Причина или комментарий не написаны.</summary>
    EmptyText,

    /// <summary>В этот статус принудительно не переводят (<see cref="DocumentTransitions.ForceTargets"/>).</summary>
    TargetNotAllowed,

    /// <summary>Документ уже в этом статусе.</summary>
    SameStatus,

    /// <summary>У дисциплины уже есть другой незавершённый документ той же линии шаблона.</summary>
    UnfinishedExists
}

/// <summary>Итог перехода: что помешало; <paramref name="OtherId"/> — незавершённый документ, из-за которого переход отклонён.</summary>
public sealed record ReopeningResult(ReopeningProblem? Problem, Guid? OtherId = null);

/// <summary>
/// Обратные переходы документа, которые совершает не автор и не круг согласования (этап 7, A8): методист возвращает
/// согласованный документ на исправление, администратор принудительно меняет статус (FR-ADM-04, только назад). Правила
/// переходов — в <see cref="DocumentTransitions"/>. Каждый переход — запись в истории статусов, снимок версии, запись в
/// журнале аудита (NFR-SEC-07) и уведомления; всё добавляется в контекст, а сохраняет вызывающий — с версией клиента, вместе.
/// <para>
/// Круг согласования не сбрасывается: строки <c>document_signatures</c> остаются до следующего приёма, как после возврата
/// с согласования, а приём начинает круг заново. Принудительная смена с согласования прерывает круг тем же способом —
/// документ уходит из очереди согласующих, отметить его они больше не могут.
/// </para>
/// </summary>
public sealed class DocumentReopening(
    AppDbContext db, DocumentHistory history, DocumentNotifications notifications, DocumentApproval approval, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// «Вернуть на исправление» (решение 2): причина обязательна — она пишется в историю статусов и уходит в уведомлении.
    /// Согласованный документ — завершённый, возврат делает его незавершённым: другого незавершённого документа дисциплины
    /// и линии шаблона при этом быть не должно. Статус «Согласован» проверяет вызывающий.
    /// </summary>
    public async Task<ReopeningResult> ReturnForCorrectionAsync(
        Document document, Guid reviewerId, string? reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) return new(ReopeningProblem.EmptyText);
        if (await OtherUnfinishedAsync(document, DocumentStatuses.UnderReview, ct) is { } other)
            return new(ReopeningProblem.UnfinishedExists, other);

        var text = reason.Trim();
        var from = document.ReturnForCorrection();
        var at = history.StatusChanged(document, from, reviewerId, text);
        await history.SnapshotAsync(document, reviewerId, VersionTriggers.StatusChange, ct);
        db.AuditLog.Add(Audit(document, reviewerId, AuditEvents.ReturnedForCorrection, at,
            new JsonObject { ["from"] = from, ["to"] = document.Status, ["comment"] = text }));
        await notifications.ReturnedForCorrectionAsync(document, reviewerId, text, at, ct);
        return new(null);
    }

    /// <summary>
    /// «Сменить статус» администратором (FR-ADM-04): комментарий обязателен. Сначала проверяются данные запроса — цель и
    /// комментарий, затем состояние документа — тот же ли это статус и нет ли другого незавершённого документа. С согласования
    /// круг прерывается, как при возврате: об этом — признак в записи аудита.
    /// </summary>
    public async Task<ReopeningResult> ForceStatusAsync(
        Document document, Guid adminId, string? target, string? comment, CancellationToken ct = default)
    {
        if (target is null || !DocumentTransitions.ForceTargets.Contains(target)) return new(ReopeningProblem.TargetNotAllowed);
        if (string.IsNullOrWhiteSpace(comment)) return new(ReopeningProblem.EmptyText);
        if (document.Status == target) return new(ReopeningProblem.SameStatus);
        if (await OtherUnfinishedAsync(document, target, ct) is { } other) return new(ReopeningProblem.UnfinishedExists, other);

        var text = comment.Trim();
        var from = document.ForceStatus(target, clock.GetUtcNow());
        var at = history.StatusChanged(document, from, adminId, text);
        await history.SnapshotAsync(document, adminId, VersionTriggers.StatusChange, ct);
        var payload = new JsonObject { ["from"] = from, ["to"] = document.Status, ["comment"] = text };
        if (from == DocumentStatuses.Approval) payload["approval_interrupted"] = true;
        db.AuditLog.Add(Audit(document, adminId, AuditEvents.StatusForced, at, payload));
        await notifications.StatusForcedAsync(document, adminId, from, text, at, ct);
        // Автору и методистам сказало «статус изменён»; согласующим, чьи отметки ждали, — что круг прерван (этап 8, A1).
        if (from == DocumentStatuses.Approval) await approval.InterruptedAsync(document, adminId, "Администратор", text, at, ct);
        return new(null);
    }

    // Переход из завершённого документа в незавершённый (согласованный и архивный возвращаются в работу) не должен давать
    // двух незавершённых у одной дисциплины и линии шаблона. Документ, который и так незавершён, их число не меняет.
    private async Task<Guid?> OtherUnfinishedAsync(Document document, string target, CancellationToken ct) =>
        DocumentTransitions.IsUnfinished(document.Status) || !DocumentTransitions.IsUnfinished(target)
            ? null
            : await UnfinishedDocuments.OtherAsync(db, document, ct);

    private static AuditLogEntry Audit(Document document, Guid by, string eventType, DateTimeOffset at, JsonObject payload) => new()
    {
        UserId = by, EventType = eventType, EntityType = AuditEntities.Document, EntityId = document.Id, OccurredAt = at,
        Payload = payload.ToJsonString(Json)
    };
}
