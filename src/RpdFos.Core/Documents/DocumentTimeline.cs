using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Documents;

/// <summary>
/// Строка истории документа (этап 8, A10; FR-ADM-02). <paramref name="Kind"/> — <c>status</c> (переход статуса) или <c>event</c>
/// (запись аудита); <paramref name="Event"/> — код события для интерфейса и тестов. Названия статусов — по-русски, как их видит
/// пользователь.
/// </summary>
public sealed record HistoryEntry(
    DateTimeOffset At, string Kind, string Event, string Title, Guid? ById, string? ByName, string? FromStatus, string? ToStatus,
    string? Comment, IReadOnlyList<string> Details);

public sealed record HistoryPage(IReadOnlyList<HistoryEntry> Items, int Total);

/// <summary>
/// История документа для методиста и администратора (этап 8, A10): переходы статусов и записи аудита документа в одной ленте,
/// от новых к старым. Ничего не хранит: лента собирается из <c>document_status_history</c> и <c>audit_log</c>.
/// <para>
/// Возврат на исправление, принудительная смена статуса и отправка в архив пишут и переход статуса, и запись аудита в один
/// момент: в ленте они одной строкой (запись аудита), иначе одно действие считалось бы дважды. Удаление черновика — запись
/// аудита только повторяет переход, её в ленте нет. Создание документа — переход «без статуса» в «Черновик» — первая строка
/// ленты.
/// </para>
/// <para>
/// Лента собирается целиком, а страница вырезается из неё: у документа переходов и записей аудита — десятки, не тысячи, а
/// склейка двух источников с общим порядком по страницам иначе пропускала бы строки на границах.
/// </para>
/// </summary>
public sealed class DocumentTimeline(AppDbContext db)
{
    public const int DefaultPage = 50, MaxPage = 200;

    // Записи аудита, которые идут вместе с переходом статуса в один момент и заменяют его в ленте.
    private static readonly string[] StatusBearing =
        [AuditEvents.ReturnedForCorrection, AuditEvents.StatusForced, AuditEvents.DocumentArchived];

    public async Task<HistoryPage> PageAsync(Document document, int skip, int take, CancellationToken ct = default)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, MaxPage);

        var statuses = await db.DocumentStatusHistory.AsNoTracking().Where(h => h.DocumentId == document.Id).ToListAsync(ct);
        var audits = (await db.AuditLog.AsNoTracking()
                .Where(a => a.EntityType == AuditEntities.Document && a.EntityId == document.Id).ToListAsync(ct))
            .Where(a => a.EventType != AuditEvents.DocumentDeleted)
            .Select(a => (Entry: a, Payload: Parse(a.Payload)))
            .ToList();

        var folded = audits.Where(a => StatusBearing.Contains(a.Entry.EventType))
            .Select(a => (a.Entry.UserId, a.Entry.OccurredAt)).ToHashSet();
        statuses = statuses.Where(h => !folded.Contains((h.ChangedBy, h.ChangedAt))).ToList();

        var names = await NamesAsync(statuses, audits.Select(a => a.Entry), audits.Select(a => a.Payload), ct);
        string? NameOf(Guid? id) => id is { } v && names.TryGetValue(v, out var name) ? name : null;

        var items = new List<HistoryEntry>();
        foreach (var h in statuses)
        {
            var (code, title) = StatusEvent(h);
            items.Add(new(h.ChangedAt, "status", code, title, h.ChangedBy, NameOf(h.ChangedBy), StatusTitle(h.FromStatus),
                DocumentStatuses.Title(h.ToStatus), h.Comment, []));
        }
        foreach (var (entry, payload) in audits) items.Add(AuditDescriber.Describe(entry, payload, NameOf));

        // OrderBy устойчив: строки одного момента остаются в порядке источников, а после разворота идут от новых к старым.
        var ordered = items.OrderBy(i => i.At).Reverse().ToList();
        return new(ordered.Skip(skip).Take(take).ToList(), ordered.Count);
    }

    private static string? StatusTitle(string? status) => status is null ? null : DocumentStatuses.Title(status);

    private static (string Code, string Title) StatusEvent(DocumentStatusChange h) => h.ToStatus switch
    {
        DocumentStatuses.UnderReview when h.FromStatus == DocumentStatuses.Approval => ("returned_from_approval", "Возврат с согласования"),
        // Причина обязательна только у «Взять на проверку»: по ней его и отличает от отправки автором.
        DocumentStatuses.UnderReview when h.Comment is not null => ("taken_for_review", "Взят на проверку методистом"),
        DocumentStatuses.UnderReview when h.FromStatus == DocumentStatuses.Returned => ("resubmitted", "Повторно отправлен на проверку"),
        DocumentStatuses.UnderReview => ("submitted", "Отправлен на проверку"),
        DocumentStatuses.Returned when h.FromStatus == DocumentStatuses.Accepted =>
            ("returned_after_plan_correction", "Возвращён автору после исправления данных плана"),
        DocumentStatuses.Returned => ("returned", "Возвращён на доработку"),
        DocumentStatuses.Accepted => ("accepted", "Принят"),
        DocumentStatuses.Approval => ("approval_started_status", "Передан на согласование"),
        DocumentStatuses.Approved => ("approved", "Согласован"),
        DocumentStatuses.SavedDraft => ("saved", "Черновик сохранён"),
        DocumentStatuses.Draft when h.FromStatus is null => ("created", "Документ создан"),
        DocumentStatuses.Draft => ("edited", "Документ изменён после сохранения"),
        DocumentStatuses.Deleted => ("deleted", "Документ удалён"),
        DocumentStatuses.Archived => ("archived_status", "Документ в архиве"),
        _ => ("status_changed", "Статус изменён")
    };

    private async Task<Dictionary<Guid, string>> NamesAsync(
        IEnumerable<DocumentStatusChange> statuses, IEnumerable<AuditLogEntry> audits, IEnumerable<JsonNode?> payloads, CancellationToken ct)
    {
        var ids = statuses.Select(h => h.ChangedBy).Concat(audits.Where(a => a.UserId is not null).Select(a => a.UserId!.Value)).ToHashSet();
        foreach (var p in payloads) ids.UnionWith(AuditDescriber.PeopleIn(p));
        return await db.Employees.AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.ShortName, ct);
    }

    private static JsonNode? Parse(string json)
    {
        try { return JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
