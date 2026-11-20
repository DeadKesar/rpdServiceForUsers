using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Documents;

/// <summary>
/// Правило «один незавершённый документ на дисциплину и линию редакций шаблона» (уточнение планировщика, этап 7):
/// незавершённый — не «Согласован», не «Архивный», не «Удалённый» (<see cref="DocumentTransitions.IsUnfinished"/>). Нарушающее
/// его создание или переход — 409 (A8, A9, A11, B3); запрос один на все эти места.
/// </summary>
public static class UnfinishedDocuments
{
    /// <summary>
    /// Другой незавершённый документ той же дисциплины и линии редакций шаблона; null — такого нет. Тип документа
    /// отдельно не сверяется: линия шаблона принадлежит одному типу (РПД или ФОС).
    /// </summary>
    public static async Task<Guid?> OtherAsync(AppDbContext db, Document document, CancellationToken ct = default)
    {
        // Шаблон документа мог быть снят с публикации и удалён из выборок — линия берётся без фильтров.
        var lineageId = await db.DocumentTemplates.IgnoreQueryFilters().Where(t => t.Id == document.TemplateId)
            .Select(t => t.LineageId).SingleAsync(ct);
        var finished = DocumentTransitions.FinishedStatuses.ToArray();
        return await (
                from d in db.Documents
                join t in db.DocumentTemplates.IgnoreQueryFilters() on d.TemplateId equals t.Id
                where d.Id != document.Id && d.DisciplineId == document.DisciplineId && t.LineageId == lineageId &&
                      !finished.Contains(d.Status)
                select (Guid?)d.Id)
            .FirstOrDefaultAsync(ct);
    }
}
