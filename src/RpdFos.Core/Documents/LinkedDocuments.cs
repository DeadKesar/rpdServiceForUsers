using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Documents;

/// <summary>
/// Связь ФОС и РПД (FR-DOC-19; этап 7, B3). Ссылка на РПД лежит в содержимом ФОС — <c>linked_rpd.rpd_document_id</c>, как в
/// проекте; отдельной колонки в <c>documents</c> нет. У ФОС и РПД одна дисциплина, поэтому ФОС РПД ищутся среди ФОС её
/// дисциплины — их единицы, и ссылка читается из содержимого. Удалённые документы скрыты фильтром запросов; где нужна и
/// удалённая РПД (ФОС показывает, что связанная РПД удалена), фильтр отключён явно.
/// </summary>
public sealed class LinkedDocuments(AppDbContext db)
{
    /// <summary>ФОС, созданные по этой РПД: неудалённые, любого статуса, новейшие первыми.</summary>
    public async Task<List<Document>> FosOfAsync(Document rpd, CancellationToken ct = default) =>
        Linked(await db.Documents.AsNoTracking()
            .Where(d => d.Type == TemplateTypes.Fos && d.DisciplineId == rpd.DisciplineId)
            .OrderByDescending(d => d.CreatedAt).ToListAsync(ct), rpd.Id);

    /// <summary>Автор ли сотрудник хотя бы одного ФОС, созданного по этой РПД: ему РПД открыта для чтения.</summary>
    public async Task<bool> IsFosAuthorAsync(Document rpd, Guid employeeId, CancellationToken ct = default) =>
        Linked(await db.Documents.AsNoTracking()
            .Where(d => d.Type == TemplateTypes.Fos && d.DisciplineId == rpd.DisciplineId && d.AuthorId == employeeId)
            .ToListAsync(ct), rpd.Id).Count > 0;

    /// <summary>
    /// РПД, по которой создан ФОС, — и удалённая: у ФОС есть что показать («связанная РПД удалена»). null — связи нет или записи
    /// физически нет.
    /// </summary>
    public async Task<Document?> RpdOfAsync(Document fos, CancellationToken ct = default) =>
        FosSnapshot.RpdIdOf(fos.Payload) is { } rpdId
            ? await db.Documents.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(d => d.Id == rpdId, ct)
            : null;

    /// <summary>
    /// РПД, по которой ФОС дисциплины создаётся, если автор её не назвал: незавершённая, иначе новейшая из согласованных
    /// (после согласования следующую редакцию ведут отдельным документом, A11). Удалённые и архивные не берутся (решение 8).
    /// </summary>
    public async Task<Document?> DefaultRpdAsync(Guid disciplineId, CancellationToken ct = default) =>
        (await DefaultRpdsAsync([disciplineId], ct)).GetValueOrDefault(disciplineId);

    /// <summary>То же для нескольких дисциплин сразу — экран создания документа.</summary>
    public async Task<Dictionary<Guid, Document>> DefaultRpdsAsync(IReadOnlyCollection<Guid> disciplineIds, CancellationToken ct = default) =>
        (await db.Documents.AsNoTracking()
            .Where(d => d.Type == TemplateTypes.Rpd && d.Status != DocumentStatuses.Archived && disciplineIds.Contains(d.DisciplineId))
            .ToListAsync(ct))
        .OrderBy(d => DocumentTransitions.IsUnfinished(d.Status) ? 0 : 1).ThenByDescending(d => d.CreatedAt)
        .GroupBy(d => d.DisciplineId).ToDictionary(g => g.Key, g => g.First());

    private static List<Document> Linked(IEnumerable<Document> candidates, Guid rpdId) =>
        candidates.Where(d => FosSnapshot.RpdIdOf(d.Payload) == rpdId).ToList();
}
