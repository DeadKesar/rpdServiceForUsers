using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.DocumentTypes;

namespace RpdFos.Core.Documents;

/// <summary>Итог отправки: отправлен ли документ и что нашла полная проверка.</summary>
public sealed record SubmissionResult(bool Submitted, IReadOnlyList<DocumentValidationError> Issues);

public enum ReviewProblem { NoUnresolvedComments, UnresolvedComments, UnknownField, EmptyText, CheckFailed }

/// <summary>
/// Проверка документа: отправка автором с полной проверкой, возврат на доработку и приём методистом, замечания
/// к полям. Каждая смена статуса — запись в истории и снимок версии (<c>status_change</c>). Права пользователя
/// проверяет вызывающий; сохраняет тоже он — с версией клиента.
/// </summary>
public sealed class DocumentReview(
    AppDbContext db, DocumentTypeRegistry registry, IDocumentValidator validator, ValidationRuleSet rules, DocumentHistory history,
    TimeProvider clock)
{
    public const string ReturnedWithComments = "Замечаний: {0}";

    /// <summary>Полная проверка документа к отправке: схема в режиме отправки, условные разделы, правила.</summary>
    public async Task<IReadOnlyList<DocumentValidationError>> CheckAsync(Document document, CancellationToken ct = default)
    {
        var schemaJson = await SchemaAsync(document, ct);
        var facts = (await DisciplineFacts.LoadAsync(db, document.DisciplineId, ct))!;
        return DocumentSubmission.Check(schemaJson, JsonDocument.Parse(document.Payload).RootElement, facts, validator, rules,
            registry.FindSchema);
    }

    /// <summary>Отправка (и повторная — с доработки): только если полная проверка не нашла ошибок.</summary>
    public async Task<SubmissionResult> SubmitAsync(Document document, Guid authorId, CancellationToken ct = default)
    {
        var issues = await CheckAsync(document, ct);
        if (DocumentSubmission.HasErrors(issues)) return new(false, issues);

        history.StatusChanged(document, document.Submit(clock.GetUtcNow()), authorId);
        await history.SnapshotAsync(document, authorId, VersionTriggers.StatusChange, ct);
        return new(true, issues);
    }

    /// <summary>Возврат на доработку — только при неразрешённых замечаниях (FR-REV-03).</summary>
    public async Task<ReviewProblem?> ReturnAsync(Document document, Guid reviewerId, string? comment, CancellationToken ct = default)
    {
        var open = await UnresolvedAsync(document, ct);
        if (open == 0) return ReviewProblem.NoUnresolvedComments;

        var from = document.ReturnForRework();
        history.StatusChanged(document, from, reviewerId,
            string.IsNullOrWhiteSpace(comment) ? string.Format(ReturnedWithComments, open) : comment.Trim());
        await history.SnapshotAsync(document, reviewerId, VersionTriggers.StatusChange, ct);
        return null;
    }

    /// <summary>
    /// Приём: замечаний к документу не осталось (предусловие UC-07) и полная проверка не нашла ошибок — исправление
    /// методистом на проверке могло сделать документ неполным (этап 5.2). Ошибки — вместе с отказом.
    /// </summary>
    public async Task<(ReviewProblem? Problem, IReadOnlyList<DocumentValidationError> Issues)> AcceptAsync(
        Document document, Guid reviewerId, CancellationToken ct = default)
    {
        if (await UnresolvedAsync(document, ct) > 0) return (ReviewProblem.UnresolvedComments, []);
        var issues = await CheckAsync(document, ct);
        if (DocumentSubmission.HasErrors(issues)) return (ReviewProblem.CheckFailed, issues);

        history.StatusChanged(document, document.Accept(), reviewerId);
        await history.SnapshotAsync(document, reviewerId, VersionTriggers.StatusChange, ct);
        return (null, issues);
    }

    /// <summary>Замечание к полю: место должно быть в схеме документа, текст — непустым.</summary>
    public async Task<(DocumentComment? Comment, ReviewProblem? Problem)> CommentAsync(
        Document document, Guid reviewerId, string fieldPath, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, ReviewProblem.EmptyText);
        var schema = JsonDocument.Parse(await SchemaAsync(document, ct)).RootElement;
        if (!fieldPath.StartsWith('/') || SchemaInspector.SubschemaAt(schema, fieldPath, registry.FindSchema) is null)
            return (null, ReviewProblem.UnknownField);

        var comment = db.DocumentComments.Add(new DocumentComment
        {
            DocumentId = document.Id, AuthorId = reviewerId, FieldPath = fieldPath, Text = text.Trim(), CreatedAt = clock.GetUtcNow()
        }).Entity;
        return (comment, null);
    }

    private Task<int> UnresolvedAsync(Document document, CancellationToken ct) =>
        db.DocumentComments.CountAsync(c => c.DocumentId == document.Id && !c.IsResolved, ct);

    private Task<string> SchemaAsync(Document document, CancellationToken ct) =>
        db.DocumentTemplates.IgnoreQueryFilters().Where(t => t.Id == document.TemplateId).Select(t => t.Schema).SingleAsync(ct);
}
