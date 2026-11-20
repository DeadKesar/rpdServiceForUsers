using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Documents;

/// <summary>
/// «В архив» (этап 7, A9; FR-DOC-11, решение 3): согласованный документ отправляет в архив автор, методист или
/// администратор — только явно. Сам документ в архив не уходит: согласованный преемник лишь помечает прежний «устарел» (A11).
/// Архивный документ открыт только для чтения, печатают его те же участники, что и согласованный, а дисциплину он не
/// занимает — по ней можно создать новый (<see cref="DocumentCreation"/>).
/// <para>
/// Переход — запись в истории статусов, снимок версии, запись в журнале аудита (NFR-SEC-07) и уведомление автору, если в
/// архив отправил не он. Всё добавляется в контекст, а сохраняет вызывающий — с версией клиента, вместе с переходом.
/// </para>
/// </summary>
public sealed class DocumentArchiving(AppDbContext db, DocumentHistory history, DocumentNotifications notifications)
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <param name="byRole">Роль совершившего (<see cref="AppRoles"/>): автору, получившему уведомление, она названа в тексте.</param>
    public async Task ArchiveAsync(Document document, Guid by, string byRole, CancellationToken ct = default)
    {
        var from = document.Archive();
        var at = history.StatusChanged(document, from, by);
        await history.SnapshotAsync(document, by, VersionTriggers.StatusChange, ct);
        db.AuditLog.Add(new AuditLogEntry
        {
            UserId = by, EventType = AuditEvents.DocumentArchived, EntityType = AuditEntities.Document, EntityId = document.Id,
            OccurredAt = at, Payload = new JsonObject { ["from"] = from, ["to"] = document.Status }.ToJsonString(Json)
        });
        await notifications.ArchivedAsync(document, by, byRole, at, ct);
    }
}
