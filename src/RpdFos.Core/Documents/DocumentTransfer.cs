using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Directory;

namespace RpdFos.Core.Documents;

public enum TransferProblem
{
    /// <summary>Причина не написана.</summary>
    EmptyReason,

    /// <summary>Нового автора нет в справочнике или его запись неактивна.</summary>
    EmployeeNotFound,

    /// <summary>У нового автора нет действующей учётной записи преподавателя: войти и править документ он не сможет.</summary>
    NoTeacherAccount,

    /// <summary>Новый автор — тот же, что нынешний: передача ничего бы не изменила.</summary>
    SameAuthor
}

public sealed record TransferResult(TransferProblem? Problem);

/// <summary>
/// «Передать документ» (этап 7, A9; FR-DOC-15, решение 5): методист или администратор передаёт документ другому автору, когда
/// ответственный преподаватель сменился. Новый автор — запись справочника с действующей учётной записью преподавателя.
/// Статус документа любой, кроме «Удалённый»; составители в содержимом не меняются (FR-DOC-14: автор остаётся единственным
/// редактором, а составители — отдельный список для титула и листа согласования). Причина обязательна.
/// <para>
/// Передача — запись в журнале аудита (NFR-SEC-07) и уведомления: новому автору — со ссылкой, прежнему — со ссылкой только
/// если документ ему остался доступен (как методисту, администратору или согласующему): иначе она привела бы его на 403.
/// Прежнему автору без действующей учётной записи, а он обычно такой — преподаватель ушёл, уведомление не создаётся: его
/// некому прочесть. В статусах документа передача ничего не меняет, поэтому в истории статусов её нет. Если у дисциплины есть
/// закрепления, новый автор закрепляется за ней (A10): иначе он правил бы документ, но не мог бы создать по дисциплине ничего
/// нового. Всё добавляется в контекст, сохраняет вызывающий — с версией клиента.
/// </para>
/// </summary>
public sealed class DocumentTransfer(
    AppDbContext db, DocumentHistory history, DocumentNotifications notifications, DocumentApproval approval, DirectoryAccounts directory,
    DisciplineAssignments assignments, LinkedDocuments links)
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Порядок отказов: данные запроса — причина и новый автор (422), затем состояние документа — тот же автор (409).
    /// Чужую версию отклоняет общий обработчик (409).
    /// </summary>
    /// <param name="byRole">Роль совершившего (<see cref="AppRoles"/>): в уведомлениях она названа.</param>
    public async Task<TransferResult> TransferAsync(
        Document document, Guid by, string byRole, Guid newAuthorId, string? reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) return new(TransferProblem.EmptyReason);
        if (!await db.Employees.AnyAsync(e => e.Id == newAuthorId && e.IsActive, ct)) return new(TransferProblem.EmployeeNotFound);
        if (await directory.RolesOfActiveAsync(newAuthorId, ct) is not { } roles || !roles.Contains(AppRoles.Teacher))
            return new(TransferProblem.NoTeacherAccount);
        if (document.AuthorId == newAuthorId) return new(TransferProblem.SameAuthor);

        var text = reason.Trim();
        var at = history.NextMoment();
        var previous = document.TransferTo(newAuthorId);
        db.AuditLog.Add(new AuditLogEntry
        {
            UserId = by, EventType = AuditEvents.DocumentTransferred, EntityType = AuditEntities.Document, EntityId = document.Id,
            OccurredAt = at, Payload = new JsonObject { ["from"] = previous, ["to"] = newAuthorId, ["reason"] = text }.ToJsonString(Json)
        });

        await assignments.EnsureAssignedAsync(document, newAuthorId, by, ct);

        // Документ уже у нового автора: открытость для прежнего считается без права автора (закреплённым он остаётся, если был).
        if (await directory.RolesOfActiveAsync(previous, ct) is { } previousRoles)
            await notifications.TransferredOutAsync(document, previous, newAuthorId, by, byRole, text,
                await DocumentVisibility.CanOpenAsync(approval, assignments, document, previous, previousRoles, ct, links), at, ct);
        await notifications.TransferredInAsync(document, previous, newAuthorId, by, byRole, text, at, ct);
        return new(null);
    }
}
