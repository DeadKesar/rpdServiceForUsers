using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.DocumentTypes;

namespace RpdFos.Core.Templates;

/// <summary>
/// Переходы шаблона, которым нужна база: проверка с содержимым файла оформления, публикация с архивированием
/// прежней редакции, новая редакция. Правила переходов — <see cref="TemplateTransitions"/>.
/// </summary>
public sealed class TemplateLifecycle(AppDbContext db, DocumentTypeRegistry registry, TimeProvider clock)
{
    public async Task<TemplateInspection> InspectAsync(DocumentTemplate template, CancellationToken ct = default)
    {
        var content = template.PrintTemplateFileId is Guid fileId
            ? await db.UploadedFiles.Where(f => f.Id == fileId).Select(f => f.Content).SingleAsync(ct)
            : null;
        return TemplateCheck.Inspect(template, content, registry);
    }

    /// <summary>
    /// Публикует шаблон, если проверка прошла, и архивирует прежнюю опубликованную редакцию той же линии.
    /// Возвращает ошибки проверки; пустой список — опубликован.
    /// </summary>
    public async Task<IReadOnlyList<string>> PublishAsync(DocumentTemplate template, CancellationToken ct = default)
    {
        var inspection = await InspectAsync(template, ct);
        if (!inspection.CanPublish) return inspection.Errors;

        // Опубликованная редакция в линии — одна (частичный уникальный индекс): прежняя уходит в архив
        // отдельным сохранением раньше, чем публикуется новая. Порядок команд одного сохранения EF не гарантирует.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var previous = await db.DocumentTemplates.SingleOrDefaultAsync(t =>
            t.LineageId == template.LineageId && t.Id != template.Id && t.Status == TemplateStatuses.Published, ct);
        if (previous is not null)
        {
            previous.Archive();
            await db.SaveChangesAsync(ct);
        }

        template.Publish(clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return [];
    }

    /// <summary>Есть ли в линии редакций неопубликованная редакция — вторую параллельно не создаём.</summary>
    public Task<bool> HasRevisionInWorkAsync(Guid lineageId, CancellationToken ct = default) =>
        db.DocumentTemplates.AnyAsync(t => t.LineageId == lineageId &&
            (t.Status == TemplateStatuses.Empty || t.Status == TemplateStatuses.Draft || t.Status == TemplateStatuses.SavedDraft), ct);

    /// <summary>Новая редакция опубликованного шаблона; номер — следующий в линии, удалённые тоже занимают номер.</summary>
    public async Task<DocumentTemplate> CreateRevisionAsync(DocumentTemplate published, Guid createdBy, CancellationToken ct = default)
    {
        var last = await db.DocumentTemplates.IgnoreQueryFilters()
            .Where(t => t.LineageId == published.LineageId).MaxAsync(t => t.Revision, ct);
        var revision = published.CreateRevision(last + 1, createdBy);
        db.DocumentTemplates.Add(revision);
        await db.SaveChangesAsync(ct);
        return revision;
    }
}
