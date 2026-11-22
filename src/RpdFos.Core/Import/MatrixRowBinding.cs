using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using Parsed = DisciplineDataExtractor;

namespace RpdFos.Core.Import;

/// <summary>Дисциплина плана в выборе для строки матрицы: кандидат неоднозначной строки, без связей с матрицей, прочая.</summary>
public sealed record MatrixRowOption(Guid Id, string Code, string Name, bool Candidate, bool WithoutMatrix);

/// <summary>
/// Строка блока «Строки матрицы без дисциплины плана»: одна строка матрицы или группа одинаковых (этап 7.1) — что в ней,
/// как решена (кто, когда, к чему) и варианты выбора. <see cref="Id"/> — первая строка группы: по ней привязывают и
/// отменяют всю группу; <see cref="Places"/> — места в файле матрицы («Таблица 1, строка 49»); <see cref="Codes"/> —
/// коды всех строк группы.
/// </summary>
public sealed record MatrixRowState(
    Guid Id, string Name, IReadOnlyList<string> Places, string Semesters, IReadOnlyList<string> Codes, string Status,
    Guid? DisciplineId, string? Discipline, string? ResolvedBy, DateTimeOffset? ResolvedAt, IReadOnlyList<MatrixRowOption> Options);

public enum MatrixRowProblem { NotFound, AlreadyResolved, NotResolved, WrongDiscipline }

/// <summary>
/// Ручная привязка строк матрицы к дисциплинам плана (этап 5.2, ЛР Т1). Привязка добавляет дисциплине связи с
/// компетенциями строки так же, как импорт, — на уровне компетенций; коды, которых нет в списке компетенций плана,
/// пропускаются с замечанием. «Не учитывать» — строка решена без связей. Отмена удаляет только связи, добавленные этой
/// привязкой. Документы, уже созданные по дисциплине, не меняются. Привязка и отмена — в журнале аудита.
/// Одинаковые строки решаются вместе (этап 7.1): строки одной версии плана, а значит одного запуска и одного файла
/// матрицы, с тем же названием по правилу сопоставления с планом (этап 5.4) и теми же семестрами, в одном состоянии
/// и с одной дисциплиной. Строки, решённые по-разному, остаются отдельными. Аудит — по каждой строке. Сохраняет вызывающий.
/// </summary>
public sealed class MatrixRowBinding(AppDbContext db, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Строки версии плана и варианты выбора: дисциплины, практики и факультативы — без модулей, групп элективов и ГИА;
    /// сначала кандидаты неоднозначной строки, затем дисциплины без связей с матрицей, затем остальные.
    /// </summary>
    public async Task<IReadOnlyList<MatrixRowState>> ListAsync(Guid planId, CancellationToken ct)
    {
        var rows = await db.MatrixUnmatchedRows.AsNoTracking().Where(r => r.CurriculumPlanId == planId)
            .OrderBy(r => r.Sheet).ThenBy(r => r.RowNumber).ToListAsync(ct);
        if (rows.Count == 0) return [];

        var disciplines = await db.Disciplines.AsNoTracking()
            .Where(d => d.CurriculumPlanId == planId && d.EntryKind != EntryKinds.Module && d.EntryKind != EntryKinds.Gia &&
                        d.PartType != PartTypes.ElectiveGroup)
            .OrderBy(d => d.Code).Select(d => new { d.Id, d.Code, d.Name }).ToListAsync(ct);
        var ids = disciplines.Select(d => d.Id).ToList();
        var withMatrix = (await db.CompetenceMatrix.Where(m => ids.Contains(m.DisciplineId)).Select(m => m.DisciplineId)
            .Distinct().ToListAsync(ct)).ToHashSet();
        var people = await PeopleAsync(rows.Select(r => r.ResolvedBy), ct);

        // Порядок групп — по первой строке группы (лист, строка), как был порядок строк.
        return rows.GroupBy(GroupOf).Select(g =>
        {
            var first = g.First();
            var candidates = g.SelectMany(r => r.Candidates).ToHashSet();
            return new MatrixRowState(
                first.Id, first.Name, g.Select(r => $"{r.Sheet}, строка {r.RowNumber}").ToList(), first.Semesters,
                g.SelectMany(r => r.Codes).Distinct().ToList(), first.Status, first.DisciplineId,
                disciplines.FirstOrDefault(d => d.Id == first.DisciplineId) is { } chosen ? $"{chosen.Code} {chosen.Name}" : null,
                first.ResolvedBy is Guid by ? people.GetValueOrDefault(by) : null, first.ResolvedAt,
                disciplines
                    .Select(d => new MatrixRowOption(d.Id, d.Code, d.Name, candidates.Contains(d.Code), !withMatrix.Contains(d.Id)))
                    .OrderBy(o => o.Candidate ? 0 : o.WithoutMatrix ? 1 : 2).ThenBy(o => o.Code, StringComparer.Ordinal)
                    .ToList());
        }).ToList();
    }

    /// <summary>
    /// Привязать строку и все одинаковые с ней к дисциплине (null — «Не учитывать»). Замечания — пропущенные коды,
    /// без повторов по строкам группы.
    /// </summary>
    public async Task<(MatrixRowProblem? Problem, IReadOnlyList<string> Warnings)> ApplyAsync(
        Guid rowId, Guid? disciplineId, Guid by, CancellationToken ct)
    {
        var row = await db.MatrixUnmatchedRows.SingleOrDefaultAsync(r => r.Id == rowId, ct);
        if (row is null) return (MatrixRowProblem.NotFound, []);
        if (row.Status != MatrixRowStatuses.Pending) return (MatrixRowProblem.AlreadyResolved, []);
        var group = await GroupAsync(row, ct);

        var warnings = new List<string>();
        var skipped = group.ToDictionary(m => m.Id, _ => new List<string>());
        if (disciplineId is Guid id)
        {
            var discipline = await db.Disciplines.SingleOrDefaultAsync(d => d.Id == id && d.CurriculumPlanId == row.CurriculumPlanId &&
                d.EntryKind != EntryKinds.Module && d.EntryKind != EntryKinds.Gia && d.PartType != PartTypes.ElectiveGroup, ct);
            if (discipline is null) return (MatrixRowProblem.WrongDiscipline, []);

            var competences = await CompetencesAsync(row.CurriculumPlanId, ct);
            var existing = (await db.CompetenceMatrix.Where(m => m.DisciplineId == id).Select(m => m.CompetenceId).ToListAsync(ct))
                .ToHashSet();
            foreach (var member in group)
            {
                foreach (var code in member.CompetenceCodes)
                {
                    if (!competences.TryGetValue(code, out var competenceId))
                    {
                        var warning = $"Компетенции {code} нет в списке компетенций плана — её связь пропущена";
                        if (!warnings.Contains(warning)) warnings.Add(warning);
                        skipped[member.Id].Add(code);
                        continue;
                    }
                    // Связь уже есть (из матрицы, другой привязки или соседней строки группы) — не дублируется и при
                    // отмене остаётся.
                    if (!existing.Add(competenceId)) continue;
                    db.CompetenceMatrix.Add(new CompetenceMatrixEntry { DisciplineId = id, CompetenceId = competenceId });
                    member.AddedCompetenceIds.Add(competenceId);
                }
                member.Status = MatrixRowStatuses.Linked;
                member.DisciplineId = id;
            }
        }
        else
            foreach (var member in group) member.Status = MatrixRowStatuses.Ignored;

        var now = clock.GetUtcNow();
        foreach (var member in group)
        {
            member.ResolvedBy = by;
            member.ResolvedAt = now;
            Audit(AuditEvents.MatrixRowLinked, member, by, new
            {
                row = member.Name, status = member.Status, discipline_id = member.DisciplineId, added = member.AddedCompetenceIds,
                skipped = skipped[member.Id]
            });
        }
        return (null, warnings);
    }

    /// <summary>
    /// Отменить привязку или «Не учитывать» строки и одинаковых с ней строк в том же состоянии: удаляются только связи,
    /// добавленные этой привязкой.
    /// </summary>
    public async Task<MatrixRowProblem?> UndoAsync(Guid rowId, Guid by, CancellationToken ct)
    {
        var row = await db.MatrixUnmatchedRows.SingleOrDefaultAsync(r => r.Id == rowId, ct);
        if (row is null) return MatrixRowProblem.NotFound;
        if (row.Status == MatrixRowStatuses.Pending) return MatrixRowProblem.NotResolved;

        foreach (var member in await GroupAsync(row, ct))
        {
            if (member.DisciplineId is Guid id && member.AddedCompetenceIds.Count > 0)
            {
                var added = member.AddedCompetenceIds;
                db.CompetenceMatrix.RemoveRange(await db.CompetenceMatrix
                    .Where(m => m.DisciplineId == id && added.Contains(m.CompetenceId)).ToListAsync(ct));
            }
            Audit(AuditEvents.MatrixRowUnlinked, member, by, new
            {
                row = member.Name, status = member.Status, discipline_id = member.DisciplineId, removed = member.AddedCompetenceIds
            });
            member.Status = MatrixRowStatuses.Pending;
            member.DisciplineId = null;
            member.AddedCompetenceIds = [];
            member.ResolvedBy = null;
            member.ResolvedAt = null;
        }
        return null;
    }

    // Группа строки: те же название и семестры, то же состояние и та же дисциплина. Строки версии плана читаются целиком —
    // их десятки, а ключ считается кодом (правило названий — в расширителе, не в SQL).
    private static string GroupOf(MatrixUnmatchedRow r) =>
        $"{Parsed.UnmatchedMatrixRow.GroupKey(r.Name, r.Semesters)}|{r.Status}|{r.DisciplineId}";

    private async Task<List<MatrixUnmatchedRow>> GroupAsync(MatrixUnmatchedRow row, CancellationToken ct)
    {
        var key = GroupOf(row);
        return (await db.MatrixUnmatchedRows.Where(r => r.CurriculumPlanId == row.CurriculumPlanId).ToListAsync(ct))
            .Where(r => GroupOf(r) == key).OrderBy(r => r.Sheet).ThenBy(r => r.RowNumber).ToList();
    }

    // Компетенции версии плана по коду — как при импорте: УК и ОПК — общие для ФГОС, ПК — свои у профиля. ФГОС —
    // тот, к которому уже относятся связи матрицы плана; связей нет — все ФГОС направления.
    private async Task<Dictionary<string, Guid>> CompetencesAsync(Guid planId, CancellationToken ct)
    {
        var plan = await db.CurriculumPlans.AsNoTracking().SingleAsync(p => p.Id == planId, ct);
        var fgos = await (
                from m in db.CompetenceMatrix
                join d in db.Disciplines on m.DisciplineId equals d.Id
                join c in db.Competences on m.CompetenceId equals c.Id
                where d.CurriculumPlanId == planId
                select c.FgosId)
            .Distinct().ToListAsync(ct);
        if (fgos.Count == 0) fgos = await db.Fgos.Where(f => f.DirectionId == plan.DirectionId).Select(f => f.Id).ToListAsync(ct);

        return (await db.Competences.AsNoTracking()
                .Where(c => fgos.Contains(c.FgosId) && (c.ProfileId == null || c.ProfileId == plan.ProfileId))
                .Select(c => new { c.Code, c.Id, c.ProfileId }).ToListAsync(ct))
            .GroupBy(c => c.Code)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.ProfileId == plan.ProfileId).First().Id);
    }

    private async Task<Dictionary<Guid, string>> PeopleAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.OfType<Guid>().Distinct().ToList();
        return await db.Employees.Where(e => list.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.ShortName, ct);
    }

    private void Audit(string type, MatrixUnmatchedRow row, Guid by, object payload) =>
        db.AuditLog.Add(new AuditLogEntry
        {
            UserId = by, EventType = type, EntityType = AuditEntities.MatrixRow, EntityId = row.Id, OccurredAt = clock.GetUtcNow(),
            Payload = JsonSerializer.Serialize(payload, Json)
        });
}
