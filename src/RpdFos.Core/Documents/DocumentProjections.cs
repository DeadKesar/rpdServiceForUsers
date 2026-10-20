using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Documents;

/// <summary>Привязка к компетенции и индикатору — для снимка версии.</summary>
public sealed record CompetenceLinkState(Guid? CompetenceId, Guid? IndicatorId);

/// <summary>Нормализованные копии содержимого после сохранения — то, что попадает в снимок версии.</summary>
public sealed record DocumentProjectionState(IReadOnlyList<DocumentHours> Hours, IReadOnlyList<CompetenceLinkState> Links);

/// <summary>
/// Нормализованные копии содержимого документа: часы по семестрам (document_hours) и привязки к компетенциям
/// и индикаторам (document_competence_links). Пересобираются по payload при каждом сохранении — изменения
/// попадают в то же SaveChanges, что и payload, то есть в ту же транзакцию.
/// </summary>
public sealed class DocumentProjections(AppDbContext db)
{
    /// <summary>Пересобирает нормализованные таблицы; возвращает их итоговое содержимое — для снимка версии.</summary>
    public async Task<DocumentProjectionState> SyncAsync(Document document, CancellationToken ct = default)
    {
        var payload = JsonDocument.Parse(document.Payload).RootElement;
        var hours = Hours(payload);
        await SyncHoursAsync(document.Id, hours, ct);
        var links = await SyncCompetencesAsync(document, payload, ct);
        return new DocumentProjectionState(hours, links);
    }

    /// <summary>Часы из раздела hours_distribution: по семестру, первый повтор семестра выигрывает.</summary>
    public static IReadOnlyList<DocumentHours> Hours(JsonElement payload)
    {
        var result = new Dictionary<int, DocumentHours>();
        if (payload.TryGetProperty("hours_distribution", out var hours) && hours.ValueKind == JsonValueKind.Object &&
            hours.TryGetProperty("semesters", out var semesters) && semesters.ValueKind == JsonValueKind.Array)
            foreach (var s in semesters.EnumerateArray())
                if (Int(s, "semester") is { } semester && !result.ContainsKey(semester))
                    result[semester] = new DocumentHours
                    {
                        Semester = semester, Lectures = Int(s, "lectures") ?? 0, Practice = Int(s, "practice") ?? 0,
                        LabHours = Int(s, "lab_hours") ?? 0, SelfStudy = Int(s, "self_study") ?? 0, Control = Int(s, "control") ?? 0
                    };
        return result.Values.OrderBy(h => h.Semester).ToList();
    }

    // Строки обновляются на месте по семестру: удаление и вставка одного семестра в одном сохранении
    // столкнулись бы на уникальном индексе (document_id, semester).
    private async Task SyncHoursAsync(Guid documentId, IReadOnlyList<DocumentHours> hours, CancellationToken ct)
    {
        var existing = await db.DocumentHours.Where(h => h.DocumentId == documentId).ToDictionaryAsync(h => h.Semester, ct);
        foreach (var h in hours)
        {
            if (!existing.Remove(h.Semester, out var row))
            {
                row = new DocumentHours { DocumentId = documentId, Semester = h.Semester };
                db.DocumentHours.Add(row);
            }
            (row.Lectures, row.Practice, row.LabHours, row.SelfStudy, row.Control) =
                (Math.Max(0, h.Lectures), Math.Max(0, h.Practice), Math.Max(0, h.LabHours), Math.Max(0, h.SelfStudy), Math.Max(0, h.Control));
        }
        db.DocumentHours.RemoveRange(existing.Values);
    }

    // Коды из payload — в id по матрице компетенций дисциплины. Выбранный индикатор даёт строку
    // (компетенция, индикатор); компетенция без выбранных индикаторов — (компетенция, null).
    // Кода нет в матрице — привязки нет: такие расхождения проверит правило этапа 5.
    private async Task<IReadOnlyList<CompetenceLinkState>> SyncCompetencesAsync(Document document, JsonElement payload, CancellationToken ct)
    {
        var candidates = await (
                from m in db.CompetenceMatrix
                where m.DisciplineId == document.DisciplineId
                join c in db.Competences on m.CompetenceId equals c.Id
                select new { c.Id, c.Code })
            .ToDictionaryAsync(c => c.Code, c => c.Id, ct);
        var competenceIds = candidates.Values.ToList();
        var indicators = await db.CompetenceIndicators.Where(i => competenceIds.Contains(i.CompetenceId))
            .ToDictionaryAsync(i => (i.CompetenceId, i.Code), i => i.Id, ct);

        var wanted = new HashSet<(Guid? Competence, Guid? Indicator)>();
        if (payload.TryGetProperty("competences", out var competences) && competences.ValueKind == JsonValueKind.Array)
            foreach (var c in competences.EnumerateArray())
            {
                if (Text(c, "code") is not { } code || !candidates.TryGetValue(code, out var competenceId)) continue;
                var chosen = c.TryGetProperty("indicators", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Select(i => Text(i, "code"))
                        .Select(i => i is not null && indicators.TryGetValue((competenceId, i), out var id) ? id : (Guid?)null)
                        .OfType<Guid>().ToList()
                    : [];
                if (chosen.Count == 0) wanted.Add((competenceId, null));
                foreach (var indicatorId in chosen) wanted.Add((competenceId, indicatorId));
            }

        var result = wanted.Select(w => new CompetenceLinkState(w.Competence, w.Indicator)).ToList();
        var existing = await db.DocumentCompetenceLinks.Where(l => l.DocumentId == document.Id).ToListAsync(ct);
        db.DocumentCompetenceLinks.RemoveRange(existing.Where(l => !wanted.Remove((l.CompetenceId, l.IndicatorId))));
        db.DocumentCompetenceLinks.AddRange(wanted.Select(w =>
            new DocumentCompetenceLink { DocumentId = document.Id, CompetenceId = w.Competence, IndicatorId = w.Indicator }));
        return result;
    }

    private static int? Int(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number &&
        v.TryGetInt32(out var i) ? i : null;

    private static string? Text(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
