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
/// к полям — методиста и согласующих (этап 7, A2). Каждая смена статуса — запись в истории и снимок версии (<c>status_change</c>). Права пользователя
/// проверяет вызывающий; сохраняет тоже он — с версией клиента. Уведомления о событиях создаются в том же сохранении
/// (этап 6.1, C3).
/// </summary>
public sealed class DocumentReview(
    AppDbContext db, DocumentTypeRegistry registry, IDocumentValidator validator, ValidationRuleSet rules, DocumentHistory history,
    DocumentApproval approval, DocumentNotifications notifications, TimeProvider clock)
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

        var from = document.Submit(clock.GetUtcNow());
        var at = history.StatusChanged(document, from, authorId);
        await history.SnapshotAsync(document, authorId, VersionTriggers.StatusChange, ct);
        await notifications.SubmittedAsync(document, authorId, again: from == DocumentStatuses.Returned, at, ct);
        return new(true, issues);
    }

    /// <summary>
    /// «Взять на проверку» методистом из статусов автора (этап 6, A1): причина обязательна и пишется в историю.
    /// Полной проверки нет — принять можно только документ без ошибок.
    /// </summary>
    public async Task<ReviewProblem?> TakeAsync(Document document, Guid reviewerId, string? reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) return ReviewProblem.EmptyText;
        var text = reason.Trim();
        var at = history.StatusChanged(document, document.TakeForReview(clock.GetUtcNow()), reviewerId, text);
        await history.SnapshotAsync(document, reviewerId, VersionTriggers.StatusChange, ct);
        await notifications.TakenForReviewAsync(document, reviewerId, text, at, ct);
        return null;
    }

    /// <summary>
    /// Документ на проверку взял методист: последний переход в «На проверке» — из статуса автора и сделан не автором.
    /// По нему автору, у которого документ был открыт, — понятный отказ, а не «нельзя изменить» (этап 6, A1).
    /// Возврат с согласования — тоже переход не автора, но из «На согласовании»: это не «взят методистом».
    /// </summary>
    public async Task<bool> TakenByReviewerAsync(Document document, CancellationToken ct = default)
    {
        if (document.Status != DocumentStatuses.UnderReview) return false;
        var last = await db.DocumentStatusHistory.Where(h => h.DocumentId == document.Id && h.ToStatus == DocumentStatuses.UnderReview)
            .OrderByDescending(h => h.ChangedAt).Select(h => new { h.FromStatus, h.ChangedBy }).FirstOrDefaultAsync(ct);
        return last is { FromStatus: { } from } && DocumentTransitions.CanTakeForReview(from) && last.ChangedBy != document.AuthorId;
    }

    /// <summary>Возврат на доработку — только при неразрешённых замечаниях (FR-REV-03).</summary>
    public async Task<ReviewProblem?> ReturnAsync(Document document, Guid reviewerId, string? comment, CancellationToken ct = default)
    {
        var open = await UnresolvedAsync(document, ct);
        if (open == 0) return ReviewProblem.NoUnresolvedComments;

        var from = document.ReturnForRework();
        var general = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        var at = history.StatusChanged(document, from, reviewerId, general ?? string.Format(ReturnedWithComments, open));
        await history.SnapshotAsync(document, reviewerId, VersionTriggers.StatusChange, ct);
        await notifications.ReturnedForReworkAsync(document, reviewerId, open, general, at, ct);
        return null;
    }

    /// <summary>
    /// Приём: замечаний к документу не осталось (предусловие UC-07) и полная проверка не нашла ошибок — исправление
    /// методистом на проверке могло сделать документ неполным (этап 5.2). Ошибки — вместе с отказом.
    /// Приём сам запускает согласование (UC-07, шаг 3): подписанты всех электронных мест найдены — документ сразу
    /// «На согласовании»; иначе остаётся «Принят», пока их не назначат вручную (этап 6, B2).
    /// </summary>
    public async Task<(ReviewProblem? Problem, IReadOnlyList<DocumentValidationError> Issues, ApprovalStart? Approval)> AcceptAsync(
        Document document, Guid reviewerId, CancellationToken ct = default)
    {
        if (await UnresolvedAsync(document, ct) > 0) return (ReviewProblem.UnresolvedComments, [], null);
        var issues = await CheckAsync(document, ct);
        if (DocumentSubmission.HasErrors(issues)) return (ReviewProblem.CheckFailed, issues, null);

        var acceptedAt = history.StatusChanged(document, document.Accept(), reviewerId);
        var start = await approval.BeginAsync(document, reviewerId, ct);
        // Один снимок на приём и старт: содержимое между ними не меняется.
        await history.SnapshotAsync(document, reviewerId, VersionTriggers.StatusChange, ct);
        // Автору — после старта: в уведомлении сказано, чем он кончился. Время — приёма, раньше уведомлений старта.
        await notifications.AcceptedAsync(document, reviewerId, start, acceptedAt, ct);
        return (null, issues, start);
    }

    /// <summary>
    /// Замечание к полю — методиста на проверке или согласующего на согласовании (этап 7, A2; кто может, проверяет
    /// вызывающий): место должно быть в схеме документа, текст — непустым.
    /// </summary>
    public async Task<(DocumentComment? Comment, ReviewProblem? Problem)> CommentAsync(
        Document document, Guid authorId, string fieldPath, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, ReviewProblem.EmptyText);
        var schema = JsonDocument.Parse(await SchemaAsync(document, ct)).RootElement;
        if (!fieldPath.StartsWith('/') || SchemaInspector.SubschemaAt(schema, fieldPath, registry.FindSchema) is null)
            return (null, ReviewProblem.UnknownField);

        var comment = db.DocumentComments.Add(new DocumentComment
        {
            DocumentId = document.Id, AuthorId = authorId, FieldPath = fieldPath, Text = text.Trim(), CreatedAt = clock.GetUtcNow()
        }).Entity;
        return (comment, null);
    }

    /// <summary>
    /// Замечания, оставленные на согласовании, — согласующими (этап 7, A2): последний переход документа до замечания —
    /// в «На согласовании». Других замечаний в этом статусе не бывает, поэтому отдельный признак в document_comments не
    /// нужен; замечание согласующего, которого потом заменили (FR-REV-13), остаётся замечанием согласования.
    /// </summary>
    public static IQueryable<Guid> FromApproval(AppDbContext db) =>
        from c in db.DocumentComments
        where db.DocumentStatusHistory.Where(h => h.DocumentId == c.DocumentId && h.ChangedAt <= c.CreatedAt)
            .OrderByDescending(h => h.ChangedAt).Select(h => h.ToStatus).FirstOrDefault() == DocumentStatuses.Approval
        select c.Id;

    private Task<int> UnresolvedAsync(Document document, CancellationToken ct) =>
        db.DocumentComments.CountAsync(c => c.DocumentId == document.Id && !c.IsResolved, ct);

    private Task<string> SchemaAsync(Document document, CancellationToken ct) =>
        db.DocumentTemplates.IgnoreQueryFilters().Where(t => t.Id == document.TemplateId).Select(t => t.Schema).SingleAsync(ct);
}
