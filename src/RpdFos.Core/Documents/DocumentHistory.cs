using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Documents;

/// <summary>
/// Следы изменений документа — в том же сохранении, что и содержимое: история статусов и снимки версий вместе
/// с нормализованными часами и привязками к компетенциям.
/// </summary>
public sealed class DocumentHistory(AppDbContext db, DocumentProjections projections, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private DateTimeOffset lastChange;

    public void StatusChanged(Document document, string from, Guid by, string? comment = null) =>
        db.DocumentStatusHistory.Add(new DocumentStatusChange
        {
            DocumentId = document.Id, FromStatus = from, ToStatus = document.Status, ChangedBy = by, Comment = comment,
            ChangedAt = NextMoment()
        });

    // Два перехода одного сохранения (приём и старт согласования) стоят в истории в порядке событий: время второго —
    // позже первого не меньше чем на 2 мкс, чтобы различаться и после округления до точности timestamptz.
    private DateTimeOffset NextMoment()
    {
        var now = clock.GetUtcNow();
        var earliest = lastChange.AddTicks(2 * TimeSpan.TicksPerMicrosecond);
        return lastChange = now < earliest ? earliest : now;
    }

    /// <summary>
    /// Снимок версии: пересобирает нормализованные таблицы и сохраняет их состояние вместе с содержимым.
    /// Автосохранение перезаписывает последний рабочий снимок того же человека; правка методиста на проверке даёт
    /// свой снимок — так в истории версий видно, кто изменил документ (FR-REV-11).
    /// </summary>
    public async Task SnapshotAsync(Document document, Guid by, string trigger, CancellationToken ct = default)
    {
        var state = await projections.SyncAsync(document, ct);
        var last = await db.DocumentVersions.Where(v => v.DocumentId == document.Id)
            .OrderByDescending(v => v.VersionNumber).FirstOrDefaultAsync(ct);
        var version = trigger == VersionTriggers.AutoSave && last is { TriggerEvent: VersionTriggers.AutoSave } && last.SavedBy == by
            ? last
            : db.DocumentVersions.Add(new DocumentVersion
            {
                DocumentId = document.Id, VersionNumber = (last?.VersionNumber ?? 0) + 1, TriggerEvent = trigger
            }).Entity;

        version.Snapshot = document.Payload;
        version.HoursSnapshot = JsonSerializer.Serialize(state.Hours.Select(h => new
        {
            semester = h.Semester, lectures = h.Lectures, practice = h.Practice, lab_hours = h.LabHours,
            self_study = h.SelfStudy, control = h.Control
        }), Json);
        version.CompetencesSnapshot = JsonSerializer.Serialize(
            state.Links.Select(l => new { competence_id = l.CompetenceId, indicator_id = l.IndicatorId }), Json);
        version.SavedBy = by;
        version.SavedAt = clock.GetUtcNow();
    }
}
